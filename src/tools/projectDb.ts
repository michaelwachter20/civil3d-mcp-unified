import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, statSync } from "node:fs";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import type { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";
import { backupsOf, localStamp, rotateBackups } from "../utils/atomicFiles.js";
import { knowledgeError, sharedRoot } from "./knowledgeBase.js";

/**
 * The suite's project database: one SQLite file per job, <shared folder>\projects\<slug>.db, shared with Project
 * Analyzer (which fills it from the job folder) and TBC MCP. The contract is project-analyzer's
 * docs/suite-knowledge.md; the table layout is project-db.sql next to this file (a copy of project-analyzer's
 * schema/project-db.sql, checked by a test).
 *
 *   openProjectDb / withProjectDb    open a job's database: read-only to read; read-write (creating it, adding the
 *                                    readers' indexes, a daily backup of a database this tool made) only to write
 *   immediate, nextId                short BEGIN IMMEDIATE writes and the next free fact-N / rule-N id
 *   fileText, partText, searchText   read the analyzer's compressed text (zstd blocks) and its FTS5 index
 *   jobIndex, jobForPath, jobByName  which job a drawing belongs to (source_root + folders, deepest wins), cached
 *   importJsonNotes                  one-time move of a pre-2.3.0 <slug>.json notes file into the database
 *
 * A busy database (Project Analyzer writing) is waited for without blocking the server: a short busy timeout, then
 * retries on a timer, then a clear error. A damaged one is never written: the error says how to restore a backup.
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

let schemaText: string | undefined;

export function schemaSql(): string {
  if (schemaText === undefined) {
    const file = [path.join(moduleDirectory, "project-db.sql"), path.resolve(moduleDirectory, "..", "..", "src", "tools", "project-db.sql")]
      .find((candidate) => existsSync(candidate));
    if (!file) throw new Error("project-db.sql is missing from the installation.");
    schemaText = readFileSync(file, "utf8");
  }
  return schemaText;
}

/** The schema's "CREATE INDEX IF NOT EXISTS" lines (the indexes readers rely on): name, table and the statement. */
export function schemaIndexes(): Array<{ name: string; table: string; sql: string }> {
  return schemaSql().split(/\r?\n/).flatMap((line) => {
    const match = /^CREATE INDEX IF NOT EXISTS (\w+) ON (\w+)\s*\(.*\);\s*$/i.exec(line.trim());
    return match ? [{ name: match[1], table: match[2], sql: line.trim() }] : [];
  });
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

// ---------------------------------------------------------------- errors

const messageOf = (error: unknown) => String((error as Error | undefined)?.message ?? error);
const sqliteCode = (error: unknown) => {
  const code = (error as { errcode?: unknown } | undefined)?.errcode;
  return typeof code === "number" ? code & 0xff : undefined;
};

/** SQLITE_BUSY: another connection (Project Analyzer writing) holds a lock longer than the busy timeout. */
export const isBusy = (error: unknown) => sqliteCode(error) === 5 || /database is locked|SQLITE_BUSY/i.test(messageOf(error));

/** SQLITE_CORRUPT / SQLITE_NOTADB: the file is damaged or isn't a database. */
export const isCorrupt = (error: unknown) =>
  sqliteCode(error) === 11 || sqliteCode(error) === 26 || /database disk image is malformed|file is not a database|SQLITE_(CORRUPT|NOTADB)/i.test(messageOf(error));

/** A table or column this database doesn't have (an older or partial layout; not damage). */
export const isMissingTableOrColumn = (error: unknown) => /no such (table|column)/i.test(messageOf(error));

const isSqliteError = (error: unknown) => sqliteCode(error) !== undefined || (error as { code?: unknown } | undefined)?.code === "ERR_SQLITE_ERROR";

export const BUSY_MESSAGE = "The job's database is busy (Project Analyzer is probably writing to it); try again in a moment.";

const busyError = () => knowledgeError("CIVIL3D.CONFLICT", BUSY_MESSAGE);

/** 'ok', what PRAGMA quick_check found wrong (first lines), or undefined when it couldn't run because the file is busy. */
export function quickCheck(file: string): string | undefined {
  let db: DatabaseSync | undefined;
  try {
    db = new (sqliteModule().DatabaseSync)(file, { readOnly: true });
    db.exec(`PRAGMA busy_timeout = ${timing.busyTimeoutMs}`);
    const rows = db.prepare("PRAGMA quick_check").all() as Array<Record<string, unknown>>;
    const found = rows.slice(0, 5).map((row) => String(Object.values(row)[0])).join("; ");
    return found === "ok" ? "ok" : found || "no answer";
  } catch (error) {
    if (isBusy(error)) return undefined;
    return `can't be read: ${messageOf(error)}`;
  } finally {
    db?.close();
  }
}

/** The job's name and whether Project Analyzer made the database, from the cached job list (its meta may be unreadable). */
function knownJob(file: string): Job | undefined {
  return index?.jobs.find((job) => pathKey(job.file) === pathKey(file));
}

/** How to get a good copy back: Project Analyzer's restore when it made the database, else the newest backup. */
export function restoreAdvice(file: string): string {
  const stem = path.parse(file).name;
  const job = knownJob(file);
  const backups = backupsOf(file);
  const folder = path.join(path.dirname(file), `${stem}.backups`);
  const copy = backups.length
    ? `copy the newest backup in ${folder} (${path.basename(backups[backups.length - 1])}) over ${path.basename(file)} with the AI apps and Project Analyzer closed`
    : `there are no backups in ${folder} yet`;
  const restore = `python analyze.py restore --project "${job?.name ?? stem}"`;
  return job?.sourceRoot
    ? `Restore a backup with Project Analyzer: ${restore} (it lists the checked backups and puts one back). Without Project Analyzer: ${copy}.`
    : `Restore a backup: ${backups.length ? copy : `${copy}; if Project Analyzer is installed, ${restore} lists the ones it has`}.`;
}

function damagedError(file: string, detail: string) {
  return knowledgeError(
    "CIVIL3D.CONFLICT",
    `The job's database ${path.basename(file)} is damaged (${detail}); it was not changed, and nothing can be read from or written to it as it is. ` +
    restoreAdvice(file),
  );
}

// Databases found damaged, with their size and modified time then. Damage in pages a write doesn't touch goes
// unnoticed by that write, and writing into a damaged file can make it worse: a write is refused while the file is
// as it was when the damage was found. A file that changed since (a backup put back) is looked at afresh.
const damaged = new Map<string, { size: number; mtimeMs: number; detail: string }>();

const stampOf = (file: string) => {
  try {
    const stat = statSync(file);
    return { size: stat.size, mtimeMs: stat.mtimeMs };
  } catch {
    return undefined;
  }
};

/** Remembers that `file` is damaged (as it is now) and returns the damaged error. */
function recordDamage(file: string, detail: string) {
  const stamp = stampOf(file);
  if (stamp) damaged.set(pathKey(file), { ...stamp, detail });
  return damagedError(file, detail);
}

/** The damage found earlier in `file` when it hasn't changed since; a record of a file that changed is forgotten. */
function knownDamage(file: string): string | undefined {
  const record = damaged.get(pathKey(file));
  if (!record) return undefined;
  const stamp = stampOf(file);
  if (stamp && stamp.size === record.size && stamp.mtimeMs === record.mtimeMs) return record.detail;
  damaged.delete(pathKey(file));
  return undefined;
}

/** For tests: forget every database found damaged. */
export function forgetDamageForTesting() {
  damaged.clear();
}

// SQLITE_IOERR and SQLITE_FORMAT can also mean a damaged file; PRAGMA quick_check decides.
const MAYBE_DAMAGED = new Set([10, 24]);

/**
 * What to tell the caller about an error from SQLite: busy stays busy (it is retried); a damaged file (SQLite says so,
 * or PRAGMA quick_check isn't ok after a read error) becomes the "damaged; restore a backup" error; anything else (a
 * constraint, a missing table) is passed on without checking the whole file.
 */
function explain(file: string, error: unknown): unknown {
  if (!isSqliteError(error) || isBusy(error) || isMissingTableOrColumn(error)) return error;
  if (isCorrupt(error)) {
    const check = quickCheck(file);
    return recordDamage(file, check && check !== "ok" ? check : messageOf(error));
  }
  if (!MAYBE_DAMAGED.has(sqliteCode(error) ?? -1)) return error;
  const check = quickCheck(file);
  return check && check !== "ok" ? recordDamage(file, check) : error;
}

// ---------------------------------------------------------------- opening

export interface ProjectDb {
  db: DatabaseSync;
  file: string;
  /** False when a newer suite tool wrote the database (meta.schema_version > SCHEMA_VERSION): read it, never write. */
  writable: boolean;
  /** True when the connection was opened read-only (every read action). */
  readOnly: boolean;
  schemaVersion: number;
}

export interface OpenOptions {
  /** Create the database when it is missing (from project-db.sql). Implies write. */
  create?: boolean;
  /** The job's name, set in meta when the database has none. */
  name?: string;
  /** Open read-write: link and note-writing actions only. Without it the database is opened read-only. */
  write?: boolean;
}

const DEFAULT_TIMING = { busyTimeoutMs: 2_000, retryWaitMs: 500, retryTotalMs: 10_000 };
let timing = { ...DEFAULT_TIMING };

/** For tests: SQLite's busy timeout per try, the wait between tries and the whole budget (default 2 s, 0.5 s, 10 s). */
export function setProjectDbTimingForTesting(value?: Partial<typeof DEFAULT_TIMING>) {
  timing = { ...DEFAULT_TIMING, ...value };
}

/** meta[key]; undefined when there is no such row or no meta table yet. Any other error (damage, busy) is thrown. */
export function metaValue(db: DatabaseSync, key: string): string | undefined {
  try {
    const row = db.prepare("SELECT value FROM meta WHERE key = ?").get(key) as { value: string | null } | undefined;
    return row?.value ?? undefined;
  } catch (error) {
    if (/no such table: meta\b/i.test(messageOf(error))) return undefined;
    throw error;
  }
}

/** meta.schema_version: a number, 0 when the meta table has no version, undefined when there is no meta table. */
function schemaVersionOf(db: DatabaseSync): number | undefined {
  const hasMeta = !!db.prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta'").get();
  if (!hasMeta) return undefined;
  return Number(metaValue(db, "schema_version") ?? 0) || 0;
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
 * Adds the readers' indexes (the schema's "CREATE INDEX IF NOT EXISTS" lines) a database doesn't have yet, on tables
 * it has: an older database made before they were in the schema. They only ever add an index. Busy is passed on
 * (the write would wait for it anyway); any other failure leaves that index out.
 */
export function ensureIndexes(db: DatabaseSync): string[] {
  const existing = db.prepare("SELECT type, name FROM sqlite_master WHERE type IN ('table', 'index')").all() as Array<{ type: string; name: string }>;
  const tables = new Set(existing.filter((row) => row.type === "table").map((row) => row.name.toLowerCase()));
  const indexes = new Set(existing.filter((row) => row.type === "index").map((row) => row.name.toLowerCase()));
  const added: string[] = [];
  for (const wanted of schemaIndexes()) {
    if (indexes.has(wanted.name.toLowerCase()) || !tables.has(wanted.table.toLowerCase())) continue;
    try {
      db.exec(wanted.sql);
      added.push(wanted.name);
    } catch (error) {
      if (isBusy(error) || isCorrupt(error)) throw error;
    }
  }
  return added;
}

/**
 * A checked copy of a database this tool made (meta.source_root empty: Project Analyzer backs up its own) before the
 * first write of each day: VACUUM INTO <slug>.backups\<slug> <YYYY-MM-DD> <HHMMSS>.db (Project Analyzer's naming,
 * local time), PRAGMA quick_check on the copy (deleted when not ok), the newest 3 kept. A copy that fails (its check,
 * or VACUUM INTO on a damaged page) means the database itself may be damaged, as Project Analyzer's backups take it:
 * when the database's own quick_check isn't ok either, the damage is remembered and thrown, so the write doesn't
 * happen. Any other failure (disk full, no permission, busy) never stops the write.
 */
export function dailyBackup(db: DatabaseSync, file: string): string | undefined {
  const stem = path.parse(file).name;
  const folder = path.join(path.dirname(file), `${stem}.backups`);
  const { date, time } = localStamp();
  if (backupsOf(file).some((copy) => path.basename(copy).startsWith(`${stem} ${date} `))) return undefined;
  const target = path.join(folder, `${stem} ${date} ${time}.db`);
  const partial = path.join(folder, `.${path.basename(target)}.partial`);
  const removePartial = () => { try { rmSync(partial, { force: true }); } catch { /* removed next time */ } };
  const sourceDamage = () => {
    const check = quickCheck(file);
    return check && check !== "ok" ? recordDamage(file, check) : undefined;
  };
  try {
    mkdirSync(folder, { recursive: true });
    rmSync(partial, { force: true });
    db.prepare("VACUUM INTO ?").run(partial);
  } catch (error) {
    removePartial();
    const damage = isCorrupt(error) || sqliteCode(error) === 10 ? sourceDamage() : undefined;
    if (damage) throw damage;
    return undefined;
  }
  try {
    if (quickCheck(partial) !== "ok") {
      removePartial();
      const damage = sourceDamage();
      if (damage) throw damage;
      return undefined;
    }
    renameSync(partial, target);
    rotateBackups(file, 3);
    return target;
  } catch (error) {
    removePartial();
    if ((error as { code?: unknown })?.code === "CIVIL3D.CONFLICT") throw error;
    return undefined;
  }
}

/**
 * Opens a job's database with the suite's settings. Read (the default): read-only, nothing created, changed or
 * imported. Write: read-write; a missing database is created (with `create`) from project-db.sql in one transaction
 * and given schema_version, project_id, created and project_name; WAL is set when the database isn't in WAL yet;
 * the readers' indexes are added when missing; a database this tool made is backed up before the day's first write;
 * an old <slug>.json notes file next to it is imported once. A database written by a newer tool opens with
 * writable = false. An unreadable meta is never treated as a new database.
 */
export function openProjectDb(file: string, options: OpenOptions = {}): ProjectDb {
  const write = !!(options.write || options.create);
  if (!existsSync(file) && !options.create) {
    throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project database at ${file}.`);
  }
  if (write) {
    const detail = knownDamage(file);
    if (detail) throw damagedError(file, detail);
    mkdirSync(path.dirname(file), { recursive: true });
  }
  const { DatabaseSync } = sqliteModule();
  const db = write ? new DatabaseSync(file) : new DatabaseSync(file, { readOnly: true });
  try {
    db.exec(`PRAGMA busy_timeout = ${timing.busyTimeoutMs}`);
    const version = schemaVersionOf(db);
    if (version !== undefined && version > SCHEMA_VERSION) return { db, file, writable: false, readOnly: !write, schemaVersion: version };
    if (!write) {
      if (!version) {
        throw knowledgeError("CIVIL3D.CONFLICT", `${path.basename(file)} has no schema_version in its meta table; it isn't a project database yet (nothing was changed).`);
      }
      return { db, file, writable: true, readOnly: true, schemaVersion: version };
    }
    if (!version) {
      // No meta table (a new file, or one a crash left half made) or no version in it: made from the schema, only
      // when asked to create it; the whole schema and the meta rows go in one transaction.
      if (!options.create) {
        throw knowledgeError("CIVIL3D.CONFLICT", `${path.basename(file)} has no schema_version in its meta table; it isn't a project database this tool can write.`);
      }
      if (journalMode(db) !== "wal") db.exec("PRAGMA journal_mode = WAL");
      immediate(db, () => {
        db.exec(schemaSql());
        if (!metaValue(db, "schema_version")) {
          setMeta(db, "schema_version", String(SCHEMA_VERSION));
          setMeta(db, "project_id", randomBytes(6).toString("hex"));
          setMeta(db, "created", nowIso());
        }
      });
    } else {
      if (!metaValue(db, "source_root")) dailyBackup(db, file);
      if (journalMode(db) !== "wal") db.exec("PRAGMA journal_mode = WAL");
      ensureIndexes(db);
    }
    const name = options.name?.replace(/\s+/g, " ").trim();
    if (name && !metaValue(db, "project_name")) immediate(db, () => { if (!metaValue(db, "project_name")) setMeta(db, "project_name", name); });
    const legacy = file.replace(/\.db$/i, ".json");
    if (legacy !== file && existsSync(legacy)) importJsonNotes(db, legacy);
    return { db, file, writable: true, readOnly: false, schemaVersion: SCHEMA_VERSION };
  } catch (error) {
    db.close();
    throw error;
  }
}

const journalMode = (db: DatabaseSync) =>
  String((db.prepare("PRAGMA journal_mode").get() as { journal_mode?: string } | undefined)?.journal_mode ?? "").toLowerCase();

/** One try: open, refuse a write to a newer database, run `work`, close. SQLite errors come back explained. */
function withProjectDbOnce<T>(file: string, work: (project: ProjectDb) => T, options: OpenOptions): T {
  let project: ProjectDb;
  try {
    project = openProjectDb(file, options);
  } catch (error) {
    throw explain(file, error);
  }
  try {
    if ((options.write || options.create) && !project.writable) {
      throw knowledgeError(
        "CIVIL3D.CONFLICT",
        `${path.basename(file)} was written by a newer suite tool (schema ${project.schemaVersion}); it can be read but not changed. Update Civil 3D MCP.`,
      );
    }
    return work(project);
  } catch (error) {
    throw explain(file, error);
  } finally {
    project.db.close();
  }
}

/** withProjectDb without waiting: a busy database fails at once with the busy error. For the job list. */
export function withProjectDbSync<T>(file: string, work: (project: ProjectDb) => T, options: OpenOptions = {}): T {
  try {
    return withProjectDbOnce(file, work, options);
  } catch (error) {
    throw isBusy(error) ? busyError() : error;
  }
}

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Opens the database (read-only unless `write` or `create`), runs `work` and closes it. While the database is busy
 * (SQLITE_BUSY after the 2 s busy timeout) it tries again after a timer, so the server keeps answering, for about
 * 10 s in all, then fails with the busy error. `work` runs again on each try: keep it to the database (writes in
 * immediate(), which rolls back).
 */
export async function withProjectDb<T>(file: string, work: (project: ProjectDb) => T, options: OpenOptions = {}): Promise<T> {
  const started = Date.now();
  for (;;) {
    try {
      return withProjectDbOnce(file, work, options);
    } catch (error) {
      if (!isBusy(error)) throw error;
      if (Date.now() - started + timing.retryWaitMs + timing.busyTimeoutMs > timing.retryTotalMs) throw busyError();
      await delay(timing.retryWaitMs);
    }
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

/** Decompressed blocks by id, kept while one answer reads several parts (the contract: cache a block). */
export type BlockCache = Map<number, Buffer>;

function block(db: DatabaseSync, blockId: number, blocks: BlockCache): Buffer {
  let found = blocks.get(blockId);
  if (!found) {
    const row = db.prepare("SELECT codec, data FROM text_blocks WHERE id = ?").get(blockId) as { codec: string; data: Uint8Array };
    found = decompress(row.codec, row.data);
    blocks.set(blockId, found);
  }
  return found;
}

/** A file's text as [{label, text}] in order; each compressed block is decompressed once. */
export function fileText(db: DatabaseSync, fileId: string): Array<{ label: string | null; text: string }> {
  const blocks: BlockCache = new Map();
  const parts = db.prepare("SELECT id, file_id, label, block_id, offset, length FROM text_parts WHERE file_id = ? ORDER BY seq").all(fileId) as unknown as PartRow[];
  return parts.map((part) => ({ label: part.label, text: block(db, part.block_id, blocks).subarray(part.offset, part.offset + part.length).toString("utf8") }));
}

/** A file's whole text, parts separated by a blank line and a '## <label>' heading when labelled. */
export const fullText = (db: DatabaseSync, fileId: string) =>
  fileText(db, fileId).map((part) => (part.label ? `## ${part.label}\n\n${part.text}` : part.text)).join("\n\n");

/** One part's text; `blocks` keeps the decompressed blocks for the next part of the same answer. */
export function partText(db: DatabaseSync, partId: number, blocks: BlockCache = new Map()): { fileId: string; label: string | null; text: string } {
  const part = db.prepare("SELECT id, file_id, label, block_id, offset, length FROM text_parts WHERE id = ?").get(partId) as unknown as PartRow;
  const text = block(db, part.block_id, blocks).subarray(part.offset, part.offset + part.length).toString("utf8");
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

// Decompressed blocks one search keeps: small parts of many files share a block, but a block can be megabytes.
const SEARCH_BLOCK_CACHE = 8;

/** Best matching parts for `text`, best first, each with a ~240-character snippet around the first match. */
export function searchText(db: DatabaseSync, text: string, limit = 20): TextHit[] {
  const query = ftsQuery(text);
  if (!query) return [];
  const rows = db.prepare("SELECT rowid FROM text_search WHERE text_search MATCH ? ORDER BY rank LIMIT ?").all(query, limit) as Array<{ rowid: number }>;
  const words = (text.toLowerCase().match(PIECE) ?? []).filter((word) => word.length > 1);
  const blocks: BlockCache = new Map();
  return rows.map(({ rowid }) => {
    if (blocks.size >= SEARCH_BLOCK_CACHE) blocks.delete(blocks.keys().next().value!);
    const part = partText(db, rowid, blocks);
    const lower = part.text.toLowerCase();
    const found = (words.length ? words : [text.toLowerCase()]).map((word) => lower.indexOf(word)).filter((at) => at >= 0);
    const start = Math.max((found.length ? Math.min(...found) : 0) - 100, 0);
    const snippet = part.text.slice(start, start + 240).replace(/\s+/g, " ").trim();
    return { fileId: part.fileId, partId: rowid, label: part.label, snippet: `${start ? "…" : ""}${snippet}` };
  });
}

// ---------------------------------------------------------------- finding the job of a drawing

export interface JobRoot { path: string; tool: string; key: string }
export interface Job {
  file: string; slug: string; name: string; sourceRoot?: string; roots: JobRoot[];
  /** Why the database couldn't be read (damaged, busy, not a project database); the job is still listed. */
  problem?: string;
}

const INDEX_TTL_MS = 60_000;
// A job that couldn't be read is looked at again sooner (a busy database usually is free a moment later).
const INDEX_RETRY_MS = 5_000;
let index: { at: number; directory: string; listing: string; jobs: Job[]; errors: string[]; ttl: number } | undefined;

/** Forget the cached job list (after linking or creating a job). */
export function invalidateJobIndex() {
  index = undefined;
}

/**
 * The job's other folders (contract: "More than one folder per job"): meta job_folders, [{name, path}]. Their files'
 * paths start with `<job folder: NAME>\\`; onDisk gives any stored path's place on this computer.
 */
export const FOLDER_PREFIX = "<job folder: ";

export function jobFolders(db: DatabaseSync): Array<{ name: string; path: string }> {
  try {
    const folders = JSON.parse(metaValue(db, "job_folders") ?? "[]") as unknown;
    return Array.isArray(folders)
      ? folders.filter((folder): folder is { name: string; path: string } =>
        typeof folder?.name === "string" && !!folder.name && typeof folder?.path === "string" && !!folder.path)
      : [];
  } catch {
    return [];
  }
}

/**
 * Where a top-level file (its files.path) is on this computer: an extra job folder's path + the rest for
 * `<job folder: NAME>\\rest`, else source_root + the path; undefined for user knowledge, Outlook emails, or a folder the
 * database doesn't know.
 */
export function onDisk(db: DatabaseSync, relative: string): string | undefined {
  const stored = relative.replace(/\//g, "\\");
  if (stored.startsWith(FOLDER_PREFIX)) {
    const end = stored.indexOf(">\\", FOLDER_PREFIX.length);
    if (end < 0) return undefined;
    const name = stored.slice(FOLDER_PREFIX.length, end).toLowerCase();
    const folder = jobFolders(db).find((candidate) => candidate.name.toLowerCase() === name);
    return folder ? path.win32.join(folder.path, stored.slice(end + 2)) : undefined;
  }
  const sourceRoot = metaValue(db, "source_root");
  if (stored.startsWith("<") || !sourceRoot) return undefined;
  return stored ? path.win32.join(sourceRoot, stored) : sourceRoot;
}

/** A path on this computer as the job's database stores it (relative to the job folder or `<job folder: NAME>\\...`). */
export function storedPath(db: DatabaseSync, absolute: string): string | undefined {
  const key = pathKey(absolute);
  const sourceRoot = metaValue(db, "source_root");
  const folders = [...(sourceRoot ? [{ prefix: "", path: sourceRoot }] : []),
    ...jobFolders(db).map((folder) => ({ prefix: `${FOLDER_PREFIX}${folder.name}>\\`, path: folder.path }))];
  for (const folder of folders) {
    const root = pathKey(folder.path);
    if (key.startsWith(`${root}\\`)) return folder.prefix + path.win32.normalize(absolute).slice(path.win32.normalize(folder.path).replace(/\\+$/, "").length + 1);
  }
  return undefined;
}

function readJob(file: string): Job {
  return withProjectDbSync(file, ({ db }) => {
    const name = metaValue(db, "project_name") ?? path.basename(file, ".db");
    const sourceRoot = metaValue(db, "source_root") || undefined;
    const roots: JobRoot[] = [];
    if (sourceRoot) roots.push({ path: sourceRoot, tool: "project-analyzer", key: pathKey(sourceRoot) });
    for (const row of db.prepare("SELECT path, tool FROM folders").all() as Array<{ path: string; tool: string }>) {
      if (row.path?.trim()) roots.push({ path: row.path, tool: row.tool, key: pathKey(row.path) });
    }
    for (const folder of jobFolders(db)) roots.push({ path: folder.path, tool: "project-analyzer", key: pathKey(folder.path) });
    return { file, slug: path.basename(file, ".db"), name, sourceRoot, roots };
  });
}

/**
 * Every job database in <shared folder>\projects, with the folders that belong to it; cached for a minute (or until
 * the set of files changes). Databases are opened read-only. A job whose database can't be read (damaged, busy) is
 * still listed, with `problem` saying why (its folders from the last good read, when there was one). A pre-2.3.0
 * <slug>.json is moved into its database here, once (the only write the job list makes).
 */
export function jobIndex(force = false): { jobs: Job[]; errors: string[] } {
  const directory = projectsDirectory();
  const names = existsSync(directory) ? readdirSync(directory) : [];
  const listing = names.filter((name) => /\.(db|json)$/i.test(name)).sort().join("|");
  if (!force && index && index.directory === directory && index.listing === listing && Date.now() - index.at < index.ttl) {
    return index;
  }
  const previous = index?.directory === directory ? index.jobs : [];
  const errors: string[] = [];
  const present = new Set(names.map((name) => name.toLowerCase()));
  for (const json of names.filter((name) => /\.json$/i.test(name))) {
    const db = json.replace(/\.json$/i, ".db");
    try {
      if (present.has(db.toLowerCase())) {
        // An old notes file next to its database: imported by a write open (once; then renamed).
        withProjectDbSync(path.join(directory, db), () => undefined, { write: true });
        continue;
      }
      const parsed = JSON.parse(readFileSync(path.join(directory, json), "utf8").replace(/^﻿/, "")) as { name?: unknown };
      withProjectDbSync(path.join(directory, db), () => undefined, { create: true, name: typeof parsed?.name === "string" ? parsed.name : json.replace(/\.json$/i, "") });
    } catch (error) {
      errors.push(`${json}: ${messageOf(error)}`);
    }
  }
  const jobs: Job[] = [];
  const databases = existsSync(directory) ? readdirSync(directory).filter((name) => /\.db$/i.test(name)) : [];
  for (const name of databases) {
    const file = path.join(directory, name);
    try {
      jobs.push(readJob(file));
    } catch (error) {
      const problem = messageOf(error);
      errors.push(`${name}: ${problem}`);
      const before = previous.find((job) => job.file === file);
      jobs.push({ ...(before ?? { file, slug: path.basename(name, ".db"), name: path.basename(name, ".db"), roots: [] }), problem });
    }
  }
  jobs.sort((left, right) => left.name.localeCompare(right.name));
  const finalListing = (existsSync(directory) ? readdirSync(directory) : []).filter((entry) => /\.(db|json)$/i.test(entry)).sort().join("|");
  index = { at: Date.now(), directory, listing: finalListing, jobs, errors, ttl: jobs.some((job) => job.problem) ? INDEX_RETRY_MS : INDEX_TTL_MS };
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

type Identity = { name: string; slug: string; folders: string[] };
// Read again only when a file changed: every save of know-how checks against the jobs.
const identityCache = new Map<string, { stamp: string; identity: Identity | undefined }>();

const fileStamp = (file: string) => {
  const stat = statSync(file);
  const wal = `${file}-wal`;
  return `${stat.size}:${stat.mtimeMs}:${existsSync(wal) ? statSync(wal).mtimeMs : 0}`;
};

/**
 * What names each job in a projects folder (its name, its file's slug and its folders), read without changing
 * anything: databases are opened read-only, nothing is created or imported; a file is read again only after it
 * changed. Used to keep job data out of general know-how (generalKnowledge.ts). Unreadable files are skipped; never
 * throws.
 */
export function jobIdentities(directory = projectsDirectory()): Identity[] {
  let names: string[];
  try {
    names = readdirSync(directory);
  } catch {
    return [];
  }
  const jobs: Identity[] = [];
  for (const entry of names) {
    const file = path.join(directory, entry);
    if (!/\.db$|\.json(\.imported)?$/i.test(entry)) continue;
    let stamp: string;
    try {
      stamp = fileStamp(file);
    } catch {
      continue;
    }
    const cached = identityCache.get(file);
    if (cached?.stamp === stamp) {
      if (cached.identity) jobs.push(cached.identity);
      continue;
    }
    let identity: Identity | undefined;
    try {
      if (/\.db$/i.test(entry)) {
        const db = new (sqliteModule().DatabaseSync)(file, { readOnly: true });
        try {
          db.exec(`PRAGMA busy_timeout = ${timing.busyTimeoutMs}`);
          const folders = (db.prepare("SELECT path FROM folders").all() as Array<{ path: string }>).map((row) => row.path);
          const sourceRoot = metaValue(db, "source_root");
          const extra = jobFolders(db).map((folder) => folder.path);
          identity = { name: metaValue(db, "project_name") ?? "", slug: entry.replace(/\.db$/i, ""), folders: [...(sourceRoot ? [sourceRoot] : []), ...folders, ...extra] };
        } finally {
          db.close();
        }
      } else {
        const data = JSON.parse(readFileSync(file, "utf8").replace(/^﻿/, "")) as { name?: unknown; folders?: unknown };
        const folders = (Array.isArray(data.folders) ? data.folders : [])
          .map((folder) => (typeof folder === "string" ? folder : (folder as { path?: unknown })?.path))
          .filter((folder): folder is string => typeof folder === "string");
        identity = { name: typeof data.name === "string" ? data.name : "", slug: entry.replace(/\.json(\.imported)?$/i, ""), folders };
      }
    } catch {
      // Not a job file, or in use: nothing to compare against from it (looked at again next time).
      continue;
    }
    identityCache.set(file, { stamp, identity });
    if (identity) jobs.push(identity);
  }
  return jobs;
}
