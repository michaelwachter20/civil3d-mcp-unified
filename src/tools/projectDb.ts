import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync } from "node:fs";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import type { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";
import { knowledgeError, sharedRoot } from "./knowledgeBase.js";

/**
 * The suite's project database: one SQLite file per job, <shared folder>\projects\<slug>.db, shared with Project
 * Analyzer (which fills it from the job folder) and TBC MCP. The contract is project-analyzer's
 * docs/suite-knowledge.md; the table layout is project-db.sql next to this file (a copy of project-analyzer's
 * schema/project-db.sql, checked by a test).
 *
 *   openProjectDb / withProjectDb    open (or create) a job's database with the suite's connection settings
 *   immediate, nextId                short BEGIN IMMEDIATE writes and the next free fact-N / rule-N id
 *   fileText, partText, searchText   read the analyzer's compressed text (zstd blocks) and its FTS5 index
 *   jobIndex, jobForPath, jobByName  which job a drawing belongs to (source_root + folders, deepest wins), cached
 *   importJsonNotes                  one-time move of a pre-2.3.0 <slug>.json notes file into the database
 *
 * Uses Node's built-in node:sqlite and zlib zstd (Node.js 22.15 or newer); no native packages.
 */

export const SCHEMA_VERSION = 1;
export const TOOL = "civil3d";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));

type Sqlite = typeof import("node:sqlite");
let sqlite: Sqlite | undefined;

/** node:sqlite, loaded on first use so the server starts (and the other tools work) on an older Node.js. */
function sqliteModule(): Sqlite {
  if (!sqlite) {
    try {
      sqlite = createRequire(import.meta.url)("node:sqlite") as Sqlite;
    } catch {
      throw knowledgeError("CIVIL3D.CONFLICT", `Project notes need Node.js 22.15 or newer (running ${process.version}). Update Node.js.`);
    }
  }
  return sqlite;
}

export const nowIso = () => new Date().toISOString();

/** "Elm Street Drainage" -> "elm-street-drainage" (the same in every suite tool). */
export const slug = (name: string) => name.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 64);

export function projectsDirectory(): string {
  return path.join(sharedRoot(), "projects");
}

/** <shared folder>\projects\<slug>.db for a job name. */
export function databaseFileFor(name: string): string {
  const key = slug(name);
  if (!key) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give the project a name with letters or digits.");
  return path.join(projectsDirectory(), `${key}.db`);
}

export function schemaSql(): string {
  const file = [path.join(moduleDirectory, "project-db.sql"), path.resolve(moduleDirectory, "..", "..", "src", "tools", "project-db.sql")]
    .find((candidate) => existsSync(candidate));
  if (!file) throw new Error("project-db.sql is missing from the installation.");
  return readFileSync(file, "utf8");
}

// ---------------------------------------------------------------- paths

/** Civil 3D runs on Windows, but tests (and CI) may not: Windows-looking paths are always handled as Windows paths. */
const looksWindows = (value: string) => /^[a-zA-Z]:([\\/]|$)|^[\\/]{2}[^\\/]/.test(value) || value.includes("\\");
const pathApi = (value: string) => (process.platform === "win32" || looksWindows(value) ? path.win32 : path.posix);

/** Absolute path without a trailing separator. */
export function normalizePath(value: string): string {
  const resolved = pathApi(value).resolve(value);
  return /^([a-zA-Z]:)?[\\/]$/.test(resolved) ? resolved : resolved.replace(/[\\/]+$/, "");
}

/** Comparison key: normalized, one kind of separator, lower case. */
export const pathKey = (value: string) => normalizePath(value).replace(/[\\/]+/g, "\\").toLowerCase();

export const directoryOf = (file: string) => pathApi(file).dirname(file);
export const baseName = (file: string) => pathApi(file).basename(file);

/** True when `child` is `root` or inside it (both comparison keys). */
const within = (child: string, root: string) => child === root || child.startsWith(root.endsWith("\\") ? root : `${root}\\`);

/** `file` relative to `root` when it is inside it ("Drawings\C-104.dwg"), else undefined. */
export function relativeTo(root: string, file: string): string | undefined {
  const rootKey = pathKey(root);
  if (!within(pathKey(file), rootKey) || pathKey(file) === rootKey) return undefined;
  return normalizePath(file).slice(normalizePath(root).length).replace(/^[\\/]+/, "");
}

/** A path to store or show without the user's profile folder (privacy rule of the contract). */
export function withoutProfile(value: string): string {
  const home = os.homedir();
  if (!home) return value;
  const key = pathKey(home);
  return within(pathKey(value), key) ? `%USERPROFILE%${normalizePath(value).slice(normalizePath(home).length)}` : value;
}

// ---------------------------------------------------------------- opening

export interface ProjectDb {
  db: DatabaseSync;
  file: string;
  /** False when a newer suite tool wrote the database (meta.schema_version > SCHEMA_VERSION): read it, never write. */
  writable: boolean;
  schemaVersion: number;
}

export function metaValue(db: DatabaseSync, key: string): string | undefined {
  try {
    const row = db.prepare("SELECT value FROM meta WHERE key = ?").get(key) as { value: string | null } | undefined;
    return row?.value ?? undefined;
  } catch {
    return undefined;   // a brand-new file: no meta table yet
  }
}

export function setMeta(db: DatabaseSync, key: string, value: string) {
  db.prepare("INSERT INTO meta(key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value").run(key, value);
}

/** Runs `work` in a BEGIN IMMEDIATE transaction (committed on success, rolled back on any error). */
export function immediate<T>(db: DatabaseSync, work: () => T): T {
  db.exec("BEGIN IMMEDIATE");
  try {
    const result = work();
    db.exec("COMMIT");
    return result;
  } catch (error) {
    try { db.exec("ROLLBACK"); } catch { /* already rolled back */ }
    throw error;
  }
}

/** The next free '<prefix>-N' id (N = highest + 1). Call inside the write transaction that inserts it. */
export function nextId(db: DatabaseSync, table: "facts" | "rules" | "questions", prefix: string): string {
  const row = db.prepare(`SELECT MAX(CAST(SUBSTR(id, ${prefix.length + 2}) AS INTEGER)) AS n FROM ${table} WHERE id GLOB '${prefix}-[0-9]*'`)
    .get() as { n: number | null } | undefined;
  return `${prefix}-${Number(row?.n ?? 0) + 1}`;
}

/**
 * Opens a job's database with the suite's settings (WAL, 30 s busy timeout). With `create`, a missing database is
 * created from project-db.sql and given schema_version, project_id, created and project_name. An old <slug>.json
 * notes file next to it is imported once. A database written by a newer tool opens read-only (writable = false).
 */
export function openProjectDb(file: string, options: { create?: boolean; name?: string } = {}): ProjectDb {
  if (!existsSync(file) && !options.create) {
    throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project database at ${file}.`);
  }
  mkdirSync(path.dirname(file), { recursive: true });
  const { DatabaseSync } = sqliteModule();
  const db = new DatabaseSync(file);
  try {
    db.exec("PRAGMA busy_timeout = 30000");
    const version = Number(metaValue(db, "schema_version") ?? 0) || 0;
    if (version > SCHEMA_VERSION) return { db, file, writable: false, schemaVersion: version };
    db.exec("PRAGMA journal_mode = WAL");
    if (!version) db.exec(schemaSql());
    const name = options.name?.replace(/\s+/g, " ").trim();
    if (!version || (name && !metaValue(db, "project_name"))) {
      immediate(db, () => {
        if (!metaValue(db, "schema_version")) {
          setMeta(db, "schema_version", String(SCHEMA_VERSION));
          setMeta(db, "project_id", randomBytes(6).toString("hex"));
          setMeta(db, "created", nowIso());
        }
        if (name && !metaValue(db, "project_name")) setMeta(db, "project_name", name);
      });
    }
    const legacy = file.replace(/\.db$/i, ".json");
    if (legacy !== file && existsSync(legacy)) importJsonNotes(db, legacy);
    return { db, file, writable: true, schemaVersion: SCHEMA_VERSION };
  } catch (error) {
    db.close();
    throw error;
  }
}

export function withProjectDb<T>(file: string, work: (project: ProjectDb) => T, options: { create?: boolean; name?: string; write?: boolean } = {}): T {
  const project = openProjectDb(file, options);
  try {
    if (options.write && !project.writable) {
      throw knowledgeError(
        "CIVIL3D.CONFLICT",
        `${path.basename(file)} was written by a newer suite tool (schema ${project.schemaVersion}); it can be read but not changed. Update Civil 3D MCP.`,
      );
    }
    return work(project);
  } finally {
    project.db.close();
  }
}

// ---------------------------------------------------------------- moving from the JSON notes files

/**
 * Imports an old <slug>.json ({name, folders, notes}) once, in one transaction, then renames it to
 * <slug>.json.imported. Each note becomes a fact of the tool that wrote it (its "source"; none = civil3d) with its
 * times kept. A file that doesn't parse is left alone. Returns the number of notes imported.
 */
export function importJsonNotes(db: DatabaseSync, jsonFile: string): number {
  const done = `${jsonFile}.imported`;
  const rename = () => { try { renameSync(jsonFile, done); } catch { /* in use: retried on the next open */ } };
  if (metaValue(db, "json_notes_imported")) {
    rename();
    return 0;
  }
  let data: { name?: unknown; folders?: unknown; notes?: unknown };
  try {
    data = JSON.parse(readFileSync(jsonFile, "utf8").replace(/^﻿/, ""));
  } catch {
    return 0;
  }
  if (!data || typeof data !== "object" || Array.isArray(data)) return 0;
  let count = 0;
  immediate(db, () => {
    setMeta(db, "json_notes_imported", nowIso());
    if (data.name && !metaValue(db, "project_name")) setMeta(db, "project_name", String(data.name));
    for (const folder of Array.isArray(data.folders) ? data.folders : []) {
      if (typeof folder === "string" && folder.trim()) {
        db.prepare("INSERT OR IGNORE INTO folders(path, tool, added) VALUES (?, 'civil3d', ?)").run(folder, nowIso());
      }
    }
    for (const note of Array.isArray(data.notes) ? data.notes as Array<Record<string, unknown>> : []) {
      const text = String(note?.note ?? "").split(/\s+/).filter(Boolean).join(" ");
      if (!text) continue;
      db.prepare("INSERT INTO facts(id, fact, sources, tool, origin, added, updated) VALUES (?, ?, ?, ?, ?, ?, ?)").run(
        nextId(db, "facts", "fact"), text, JSON.stringify([{ file: "user", path: "project note" }]),
        String(note.source || TOOL), JSON.stringify({ note_id: note.id ?? null }),
        typeof note.added === "string" && note.added ? note.added : nowIso(),
        typeof note.updated === "string" && note.updated ? note.updated : null,
      );
      count += 1;
    }
  });
  rename();
  return count;
}

// ---------------------------------------------------------------- text

function decompress(codec: string, data: Uint8Array): Buffer {
  if (codec === "zstd") return zlib.zstdDecompressSync(data);
  if (codec === "zlib") return zlib.inflateSync(data);
  if (codec === "none") return Buffer.from(data);
  throw new Error(`unknown text codec '${codec}'`);
}

interface PartRow { id: number; file_id: string; label: string | null; block_id: number; offset: number; length: number }

/** A file's text as [{label, text}] in order; each compressed block is decompressed once. */
export function fileText(db: DatabaseSync, fileId: string): Array<{ label: string | null; text: string }> {
  const blocks = new Map<number, Buffer>();
  const parts = db.prepare("SELECT id, file_id, label, block_id, offset, length FROM text_parts WHERE file_id = ? ORDER BY seq").all(fileId) as unknown as PartRow[];
  return parts.map((part) => {
    let block = blocks.get(part.block_id);
    if (!block) {
      const row = db.prepare("SELECT codec, data FROM text_blocks WHERE id = ?").get(part.block_id) as { codec: string; data: Uint8Array };
      block = decompress(row.codec, row.data);
      blocks.set(part.block_id, block);
    }
    return { label: part.label, text: block.subarray(part.offset, part.offset + part.length).toString("utf8") };
  });
}

/** A file's whole text, parts separated by a blank line and a '## <label>' heading when labelled. */
export const fullText = (db: DatabaseSync, fileId: string) =>
  fileText(db, fileId).map((part) => (part.label ? `## ${part.label}\n\n${part.text}` : part.text)).join("\n\n");

export function partText(db: DatabaseSync, partId: number): { fileId: string; label: string | null; text: string } {
  const part = db.prepare("SELECT p.file_id, p.label, p.offset, p.length, b.codec, b.data FROM text_parts p JOIN text_blocks b ON b.id = p.block_id WHERE p.id = ?")
    .get(partId) as { file_id: string; label: string | null; offset: number; length: number; codec: string; data: Uint8Array };
  const text = decompress(part.codec, part.data).subarray(part.offset, part.offset + part.length).toString("utf8");
  return { fileId: part.file_id, label: part.label, text };
}

// Words, and codes that keep their joining characters: MH-3, 12+50, C-104, 2026-10-03.
const TOKEN = /[\p{L}\p{N}\p{M}]+(?:[-+./][\p{L}\p{N}\p{M}]+)*/gu;
const PIECE = /[\p{L}\p{N}\p{M}]+/gu;

/** The words and codes someone typed: "MH-3 culvert" -> ["MH-3", "culvert"]. */
export const searchTokens = (text: string) => text.match(TOKEN) ?? [];

/** An FTS5 query finding every word in any order; a code becomes a phrase of its pieces: 'MH-3' -> '"MH 3"'. */
export function ftsQuery(text: string): string {
  return searchTokens(text).map((token) => `"${(token.match(PIECE) ?? []).join(" ")}"`).join(" ");
}

export interface TextHit { fileId: string; partId: number; label: string | null; snippet: string }

/** Best matching parts for `text`, best first, each with a ~240-character snippet around the first match. */
export function searchText(db: DatabaseSync, text: string, limit = 20): TextHit[] {
  const query = ftsQuery(text);
  if (!query) return [];
  const rows = db.prepare("SELECT rowid FROM text_search WHERE text_search MATCH ? ORDER BY rank LIMIT ?").all(query, limit) as Array<{ rowid: number }>;
  const words = (text.toLowerCase().match(PIECE) ?? []).filter((word) => word.length > 1);
  return rows.map(({ rowid }) => {
    const part = partText(db, rowid);
    const lower = part.text.toLowerCase();
    const found = (words.length ? words : [text.toLowerCase()]).map((word) => lower.indexOf(word)).filter((at) => at >= 0);
    const start = Math.max((found.length ? Math.min(...found) : 0) - 100, 0);
    const snippet = part.text.slice(start, start + 240).replace(/\s+/g, " ").trim();
    return { fileId: part.fileId, partId: rowid, label: part.label, snippet: `${start ? "…" : ""}${snippet}` };
  });
}

// ---------------------------------------------------------------- finding the job of a drawing

export interface JobRoot { path: string; tool: string; key: string }
export interface Job { file: string; slug: string; name: string; sourceRoot?: string; roots: JobRoot[] }

const INDEX_TTL_MS = 60_000;
let index: { at: number; directory: string; listing: string; jobs: Job[]; errors: string[] } | undefined;

/** Forget the cached job list (after linking or creating a job). */
export function invalidateJobIndex() {
  index = undefined;
}

function readJob(file: string): Job {
  return withProjectDb(file, ({ db }) => {
    const name = metaValue(db, "project_name") ?? path.basename(file, ".db");
    const sourceRoot = metaValue(db, "source_root") || undefined;
    const roots: JobRoot[] = [];
    if (sourceRoot) roots.push({ path: sourceRoot, tool: "project-analyzer", key: pathKey(sourceRoot) });
    for (const row of db.prepare("SELECT path, tool FROM folders").all() as Array<{ path: string; tool: string }>) {
      if (row.path?.trim()) roots.push({ path: row.path, tool: row.tool, key: pathKey(row.path) });
    }
    return { file, slug: path.basename(file, ".db"), name, sourceRoot, roots };
  });
}

/**
 * Every job database in <shared folder>\projects, with the folders that belong to it; cached for a minute (or until
 * the set of files changes). A pre-2.3.0 <slug>.json without a database gets its database here (imported once).
 */
export function jobIndex(force = false): { jobs: Job[]; errors: string[] } {
  const directory = projectsDirectory();
  const names = existsSync(directory) ? readdirSync(directory) : [];
  const listing = names.filter((name) => /\.(db|json)$/i.test(name)).sort().join("|");
  if (!force && index && index.directory === directory && index.listing === listing && Date.now() - index.at < INDEX_TTL_MS) {
    return index;
  }
  const errors: string[] = [];
  const present = new Set(names.map((name) => name.toLowerCase()));
  for (const json of names.filter((name) => /\.json$/i.test(name))) {
    const db = json.replace(/\.json$/i, ".db");
    if (present.has(db.toLowerCase())) continue;
    try {
      const parsed = JSON.parse(readFileSync(path.join(directory, json), "utf8").replace(/^﻿/, "")) as { name?: unknown };
      withProjectDb(path.join(directory, db), () => undefined, { create: true, name: typeof parsed?.name === "string" ? parsed.name : json.replace(/\.json$/i, "") });
    } catch (error) {
      errors.push(`${json}: ${(error as Error).message}`);
    }
  }
  const jobs: Job[] = [];
  const databases = existsSync(directory) ? readdirSync(directory).filter((name) => /\.db$/i.test(name)) : [];
  for (const name of databases) {
    try {
      jobs.push(readJob(path.join(directory, name)));
    } catch (error) {
      errors.push(`${name}: ${(error as Error).message}`);
    }
  }
  jobs.sort((left, right) => left.name.localeCompare(right.name));
  const finalListing = (existsSync(directory) ? readdirSync(directory) : []).filter((entry) => /\.(db|json)$/i.test(entry)).sort().join("|");
  index = { at: Date.now(), directory, listing: finalListing, jobs, errors };
  return index;
}

/** The job whose folder (source_root or a folders row) contains the file; the deepest folder wins. */
export function jobForPath(file: string | undefined): { job: Job; root: JobRoot } | undefined {
  if (!file) return undefined;
  const directory = pathKey(directoryOf(file));
  let best: { job: Job; root: JobRoot } | undefined;
  for (const job of jobIndex().jobs) {
    for (const root of job.roots) {
      if (within(directory, root.key) && (!best || root.key.length > best.root.key.length)) best = { job, root };
    }
  }
  return best;
}

/** The job with this name (same slug), if its database exists. */
export function jobByName(name: string): Job | undefined {
  const key = slug(name);
  if (!key) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give the project a name with letters or digits.");
  return jobIndex().jobs.find((job) => job.slug === key || slug(job.name) === key);
}
