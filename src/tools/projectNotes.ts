import { existsSync, mkdirSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import type { DatabaseSync } from "node:sqlite";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { knowledgeError } from "./knowledgeBase.js";
import { listPractices } from "./practices.js";
import {
  baseName, databaseFileFor, directoryOf, fullText, immediate, invalidateJobIndex, jobByName, jobForPath, jobIndex, metaValue, nextId, normalizePath,
  nowIso, pathKey, relativeTo, searchText, searchTokens, TOOL, withoutProfile, withProjectDb, type Job,
} from "./projectDb.js";

export { projectsDirectory } from "./projectDb.js";

/**
 * Project notes: what is known about ONE job, kept in the suite's project database
 * <knowledge dir>\projects\<slug>.db (see projectDb.ts and project-analyzer's docs/suite-knowledge.md). Project
 * Analyzer fills it from the job folder; Civil 3D MCP and TBC MCP read it and add notes. A note is a row of `facts`
 * with tool = 'civil3d'; this tool changes only its own facts and folders. A job is matched by folder: the active
 * drawing belongs to the job whose source_root or linked folder contains it (deepest wins).
 * Reference files the user adds for the job (standards, specs) go in <knowledge dir>\projects\<slug>\.
 */

const oneLine = (value: string) => value.replace(/\s+/g, " ").trim();
const parseJson = <T>(text: string | null | undefined, fallback: T): T => {
  try { return text ? (JSON.parse(text) as T) : fallback; } catch { return fallback; }
};

/** The job's files folder <knowledge dir>\projects\<slug> (next to <slug>.db), for material the user puts there. */
export function projectFilesDirectory(name: string): string {
  return databaseFileFor(name).replace(/\.db$/, "");
}

const filesDirectoryOf = (job: Job) => job.file.replace(/\.db$/i, "");

function ensureFilesDirectory(job: Job): string {
  const dir = filesDirectoryOf(job);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  return dir;
}

export interface ProjectFileEntry { path: string; size: number; modified: string }

/** Every file in the project's files folder (relative paths, subfolders included). */
export function listProjectFilesIn(dir: string, limit = 500): { files: ProjectFileEntry[]; truncated: boolean } {
  const files: ProjectFileEntry[] = [];
  const walk = (current: string) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (files.length >= limit) return;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) { walk(full); continue; }
      if (!entry.isFile()) continue;
      const stat = statSync(full);
      files.push({ path: path.relative(dir, full), size: stat.size, modified: stat.mtime.toISOString() });
    }
  };
  if (existsSync(dir)) walk(dir);
  files.sort((a, b) => a.path.localeCompare(b.path));
  return { files, truncated: files.length >= limit };
}

/** Every known job: name, database and folders. */
export function listProjects() {
  return jobIndex().jobs.map((job) => ({ name: job.name, path: job.file, folders: job.roots.map((root) => root.path) }));
}

/** The job whose folder contains the drawing (deepest folder wins). */
export function projectForDrawing(drawingPath: string | undefined): Job | undefined {
  return jobForPath(drawingPath)?.job;
}

// ---------------------------------------------------------------- the active drawing

let drawingResolver: (() => Promise<string | undefined>) | undefined;

/** For tests: where the "active drawing" comes from (undefined = ask Civil 3D). */
export function setActiveDrawingResolverForTesting(resolver: (() => Promise<string | undefined>) | undefined) {
  drawingResolver = resolver;
}

/** Path of the active drawing, asked from the plugin (undefined when Civil 3D can't be reached). */
export async function activeDrawingPath(): Promise<string | undefined> {
  if (drawingResolver) return drawingResolver();
  try {
    const open = await withApplicationConnection(async (client) => await client.sendCommand("listOpenDrawings", {})) as
      { drawings?: Array<{ path?: string; active?: boolean }> } | undefined;
    const active = open?.drawings?.find((drawing) => drawing.active)?.path;
    if (active) return active;
  } catch {
    // Older plugin without listOpenDrawings: fall back to drawing info.
  }
  try {
    const info = await withApplicationConnection(async (client) => await client.sendCommand("getDrawingInfo", {})) as
      { filePath?: string } | undefined;
    return info?.filePath || undefined;
  } catch {
    return undefined;
  }
}

async function resolveProject(name: string | undefined): Promise<{ job: Job; drawing?: string }> {
  if (name) {
    const job = jobByName(name);
    if (!job) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project named '${name}'. Use action=link to create it.`);
    return { job };
  }
  const drawing = await activeDrawingPath();
  const job = projectForDrawing(drawing);
  if (!job) {
    throw knowledgeError(
      "CIVIL3D.OBJECT_NOT_FOUND",
      `The active drawing${drawing ? ` (${baseName(drawing)})` : ""} isn't in a known project folder. ` +
      "Ask the user which job it is, then use action=link (project, optional folder; default: the active drawing's folder).",
    );
  }
  return { job, drawing };
}

// ---------------------------------------------------------------- facts

interface FactRow {
  id: string; fact: string; topics: string; date: string | null; confidence: string; key: number; supersedes: string | null;
  sources: string; tool: string; author: string | null; added: string; updated: string | null; retired: string | null;
}

const FACT_COLUMNS = "f.id, f.fact, f.topics, f.date, f.confidence, f.key, f.supersedes, f.sources, f.tool, f.author, f.added, f.updated, f.retired";
const CURRENT = "f.retired IS NULL";
const NOT_SUPERSEDED = "NOT EXISTS (SELECT 1 FROM facts s WHERE s.supersedes = f.id AND s.retired IS NULL)";

function presentFact(row: FactRow) {
  const topics = parseJson<string[]>(row.topics, []);
  const sources = parseJson<unknown[]>(row.sources, []);
  return {
    id: row.id,
    fact: row.fact,
    tool: row.tool,
    ...(topics.length ? { topics } : {}),
    ...(row.date ? { date: row.date } : {}),
    ...(row.confidence && row.confidence !== "high" ? { confidence: row.confidence } : {}),
    ...(row.key ? { key: true } : {}),
    ...(row.supersedes ? { supersedes: row.supersedes } : {}),
    ...(sources.length ? { sources } : {}),
    ...(row.author ? { author: row.author } : {}),
    added: row.added,
    ...(row.updated ? { updated: row.updated } : {}),
    ...(row.retired ? { retired: row.retired } : {}),
  };
}

const facts = (db: DatabaseSync, where: string, ...params: Array<string | number | null>) =>
  (db.prepare(`SELECT ${FACT_COLUMNS} FROM facts f WHERE ${where}`).all(...params) as unknown as FactRow[]).map(presentFact);

function describe(job: Job) {
  const filesFolder = ensureFilesDirectory(job);
  const { files } = listProjectFilesIn(filesFolder);
  return withProjectDb(job.file, ({ db }) => {
    const others = db.prepare(`SELECT tool, COUNT(*) AS n FROM facts f WHERE ${CURRENT} AND tool <> ? GROUP BY tool`).all(TOOL) as Array<{ tool: string; n: number }>;
    return {
      project: job.name,
      path: job.file,
      folders: job.roots.map((root) => ({ path: root.path, tool: root.tool })),
      notes: facts(db, `${CURRENT} AND f.tool = ? ORDER BY f.added, f.id`, TOOL),
      otherFacts: Object.fromEntries(others.map((row) => [row.tool, row.n])),
      filesFolder,
      files: files.length,
      hint: "notes are this tool's own facts. action=overview shows what the whole suite knows about the job; search, file, entity, timeline and locate read more.",
    };
  });
}

export async function listProjectNotes(name?: string) {
  if (!name) {
    const drawing = await activeDrawingPath();
    const job = projectForDrawing(drawing);
    if (!job) {
      return {
        project: null,
        activeDrawing: drawing ? baseName(drawing) : null,
        knownProjects: listProjects(),
        hint: "No project linked to this drawing's folder yet. Ask the user which job it is and use action=link.",
      };
    }
    return describe(job);
  }
  const { job } = await resolveProject(name);
  return describe(job);
}

/** The project's files folder and what is in it (action=files). */
export async function listProjectFiles(name?: string) {
  const { job } = await resolveProject(name);
  const filesFolder = ensureFilesDirectory(job);
  const { files, truncated } = listProjectFilesIn(filesFolder);
  return {
    project: job.name, filesFolder, files, truncated,
    hint: files.length === 0
      ? "Empty. The user can put standards, specs or other reference files for this job here (subfolders are fine)."
      : "Read files from this folder when they help. Keep what you learn about the job in project notes, never in general lessons or skills.",
  };
}

/** Creates the project database if needed and links a folder to it (default: the active drawing's folder). */
export async function linkProjectFolder(name: string, folder?: string) {
  const target = folder ?? (await activeDrawingPath().then((drawing) => (drawing ? directoryOf(drawing) : undefined)));
  if (!target) throw knowledgeError("CIVIL3D.INVALID_INPUT", "No folder given and the active drawing's folder isn't known.");
  const existing = jobByName(name);
  const file = existing?.file ?? databaseFileFor(name);
  const normalized = normalizePath(target);
  const result = withProjectDb(file, ({ db }) => immediate(db, () => {
    const roots = [metaValue(db, "source_root"), ...(db.prepare("SELECT path FROM folders").all() as Array<{ path: string }>).map((row) => row.path)]
      .filter((root): root is string => !!root);
    if (roots.some((root) => pathKey(root) === pathKey(normalized))) return "unchanged";
    db.prepare("INSERT INTO folders(path, tool, added) VALUES (?, ?, ?)").run(normalized, TOOL, nowIso());
    return existing ? "linked" : "created";
  }), { create: true, name: oneLine(name), write: true });
  invalidateJobIndex();
  const job = jobByName(name)!;
  const filesFolder = ensureFilesDirectory(job);
  return { success: true, action: result, project: job.name, folders: job.roots.map((root) => root.path), path: job.file, filesFolder };
}

/** Removes a folder this tool linked (notes are kept; other tools' folders and source_root stay). */
export function unlinkProjectFolder(name: string, folder: string) {
  const job = jobByName(name);
  if (!job) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project named '${name}'.`);
  const key = pathKey(folder);
  withProjectDb(job.file, ({ db }) => immediate(db, () => {
    if (job.sourceRoot && pathKey(job.sourceRoot) === key) {
      throw knowledgeError("CIVIL3D.CONFLICT", `'${folder}' is the job folder Project Analyzer read (source_root); it can't be unlinked here.`);
    }
    const row = (db.prepare("SELECT path, tool FROM folders").all() as Array<{ path: string; tool: string }>).find((candidate) => pathKey(candidate.path) === key);
    if (!row) {
      throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `'${folder}' isn't linked to '${job.name}'. Linked: ${job.roots.map((root) => root.path).join("; ") || "(none)"}.`);
    }
    if (row.tool !== TOOL) throw knowledgeError("CIVIL3D.CONFLICT", `'${row.path}' was linked by ${row.tool}; only that tool unlinks it.`);
    db.prepare("DELETE FROM folders WHERE path = ? AND tool = ?").run(row.path, TOOL);
  }), { write: true });
  invalidateJobIndex();
  const updated = jobByName(name)!;
  return { success: true, action: "unlinked", project: updated.name, folders: updated.roots.map((root) => root.path) };
}

/** Where a note came from: the active drawing when it belongs to the job, else the user. */
async function noteSource(job: Job, drawing: string | undefined, fromUser: boolean) {
  if (!fromUser) {
    const active = drawing ?? await activeDrawingPath();
    const match = jobForPath(active);
    if (active && match?.job.file === job.file) {
      return { file: `drawing:${withoutProfile(normalizePath(active))}`, path: relativeTo(match.root.path, active) ?? baseName(active) };
    }
  }
  return { file: "user", path: "said by the user" };
}

export interface NoteOptions { topics?: string[]; supersedes?: string; source?: "drawing" | "user" }

const cleanTopics = (topics: string[] | undefined) => [...new Set((topics ?? []).map(oneLine).filter(Boolean))];

export async function addProjectNote(note: string, name?: string, options: NoteOptions = {}) {
  const text = oneLine(note);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The note is empty.");
  const { job, drawing } = await resolveProject(name);
  const source = await noteSource(job, drawing, options.source === "user");
  return withProjectDb(job.file, ({ db }) => immediate(db, () => {
    const duplicate = (db.prepare(`SELECT ${FACT_COLUMNS} FROM facts f WHERE ${CURRENT} AND lower(f.fact) = lower(?) ORDER BY f.tool = ? DESC`).all(text, TOOL) as unknown as FactRow[])[0];
    if (duplicate) {
      const sources = parseJson<Array<{ file?: string; path?: string }>>(duplicate.sources, []);
      if (duplicate.tool === TOOL && !sources.some((existing) => existing.file === source.file && existing.path === source.path)) {
        sources.push(source);
        db.prepare("UPDATE facts SET sources = ?, updated = ? WHERE id = ?").run(JSON.stringify(sources), nowIso(), duplicate.id);
        return { success: true, action: "source_added", project: job.name, note: presentFact({ ...duplicate, sources: JSON.stringify(sources) }) };
      }
      return { success: true, action: "unchanged", project: job.name, note: presentFact(duplicate), hint: "The same fact is already recorded." };
    }
    if (options.supersedes) {
      const target = db.prepare("SELECT id FROM facts WHERE id = ?").get(options.supersedes);
      if (!target) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No fact '${options.supersedes}' in project '${job.name}' to supersede.`);
    }
    const id = nextId(db, "facts", "fact");
    db.prepare("INSERT INTO facts(id, fact, topics, supersedes, sources, tool, added) VALUES (?, ?, ?, ?, ?, ?, ?)").run(
      id, text, JSON.stringify(cleanTopics(options.topics)), options.supersedes ?? null, JSON.stringify([source]), TOOL, nowIso(),
    );
    const added = (db.prepare(`SELECT ${FACT_COLUMNS} FROM facts f WHERE f.id = ?`).get(id) as unknown as FactRow);
    return { success: true, action: "added", project: job.name, note: presentFact(added), path: job.file };
  }), { write: true });
}

function ownFact(db: DatabaseSync, job: Job, id: string): FactRow {
  const row = db.prepare(`SELECT ${FACT_COLUMNS} FROM facts f WHERE f.id = ?`).get(id) as unknown as FactRow | undefined;
  if (!row) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No note '${id}' in project '${job.name}'.`);
  if (row.tool !== TOOL) {
    throw knowledgeError("CIVIL3D.CONFLICT", `${id} belongs to ${row.tool}; only that tool changes it. To correct it, add a note with supersedes='${id}'.`);
  }
  if (row.retired) throw knowledgeError("CIVIL3D.CONFLICT", `${id} was removed on ${row.retired}. Add a new note instead.`);
  return row;
}

export async function updateProjectNote(id: string, note: string, name?: string, options: Pick<NoteOptions, "topics"> = {}) {
  const text = oneLine(note);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The note is empty.");
  const { job } = await resolveProject(name);
  return withProjectDb(job.file, ({ db }) => immediate(db, () => {
    const row = ownFact(db, job, id);
    const topics = options.topics ? JSON.stringify(cleanTopics(options.topics)) : row.topics;
    const updated = nowIso();
    db.prepare("UPDATE facts SET fact = ?, topics = ?, updated = ? WHERE id = ?").run(text, topics, updated, id);
    return { success: true, action: "updated", project: job.name, note: presentFact({ ...row, fact: text, topics, updated }) };
  }), { write: true });
}

/** Retires one of this tool's notes: hidden from now on, kept in the database, its id never reused. */
export async function removeProjectNote(id: string, name?: string) {
  const { job } = await resolveProject(name);
  return withProjectDb(job.file, ({ db }) => immediate(db, () => {
    const row = ownFact(db, job, id);
    const retired = nowIso();
    db.prepare("UPDATE facts SET retired = ? WHERE id = ?").run(retired, id);
    return { success: true, action: "retired", project: job.name, note: presentFact({ ...row, retired }) };
  }), { write: true });
}

// ---------------------------------------------------------------- reading what the suite knows

const all = <T>(db: DatabaseSync, sql: string, ...params: Array<string | number | null>): T[] => {
  try { return db.prepare(sql).all(...params) as unknown as T[]; } catch { return []; }
};

const filePath = (db: DatabaseSync, fileId: string | null | undefined) =>
  fileId ? (db.prepare("SELECT path FROM files WHERE id = ?").get(fileId) as { path: string } | undefined)?.path ?? fileId : undefined;

function copyInfo(db: DatabaseSync) {
  return {
    sourceRoot: metaValue(db, "source_root") ?? null,
    copyStatus: metaValue(db, "copy_status") ?? null,
    ...(metaValue(db, "original_roots") ? { originalRoots: parseJson<string[]>(metaValue(db, "original_roots"), []) } : {}),
  };
}

/** Everything that matters about the job at a glance (contract: "What to tell the AI", overview). */
export async function projectOverview(name?: string) {
  const { job } = await resolveProject(name);
  return withProjectDb(job.file, ({ db }) => {
    const statuses = all<{ status: string; n: number }>(db, "SELECT status, COUNT(*) AS n FROM files GROUP BY status ORDER BY n DESC");
    const lastRun = metaValue(db, "last_run_finished");
    return {
      project: job.name,
      path: job.file,
      ...copyInfo(db),
      ...(lastRun ? { analyzed: lastRun, sourceUnchanged: metaValue(db, "source_unchanged") ?? null } : { analyzed: null }),
      folders: job.roots.map((root) => ({ path: root.path, tool: root.tool })),
      files: { total: statuses.reduce((sum, row) => sum + row.n, 0), byStatus: Object.fromEntries(statuses.map((row) => [row.status, row.n])) },
      keyFacts: facts(db, `${CURRENT} AND f.key = 1 AND ${NOT_SUPERSEDED} ORDER BY f.added, f.id`),
      newestFacts: facts(db, `${CURRENT} ORDER BY f.added DESC, CAST(SUBSTR(f.id, 6) AS INTEGER) DESC LIMIT 10`),
      openQuestions: all<{ id: string; question: string; tool: string }>(db, "SELECT id, question, tool FROM questions WHERE status = 'open' ORDER BY added, id"),
      rules: all<{ id: string; rule: string; tool: string }>(db, "SELECT id, rule, tool FROM rules WHERE retired IS NULL ORDER BY added, id"),
      highChecks: all<{ id: string; kind: string; title: string; detail: string | null }>(db, "SELECT id, kind, title, detail FROM intel_checks WHERE severity = 'high' ORDER BY id"),
      people: all<{ name: string; aliases: string; files: number }>(db, "SELECT name, aliases, files FROM intel_entities WHERE type = 'person' ORDER BY files DESC, name LIMIT 10")
        .map((row) => ({ name: row.name, ...(parseJson<string[]>(row.aliases, []).length ? { aliases: parseJson<string[]>(row.aliases, []) } : {}), files: row.files })),
      practices: safePractices(),
      filesFolder: filesDirectoryOf(job),
      hint: "search (query), file (file id or path), entity (name), timeline (from, to) and locate (path) read more. " +
        "Paths in the job's files point at the original location when the job folder is a copy: translate them with action=locate.",
    };
  });
}

function safePractices() {
  try {
    return listPractices().map((practice) => ({ id: practice.id, practice: practice.practice, source: practice.source ?? "unknown" }));
  } catch {
    return [];
  }
}

/** Full-text search of the job's files plus a search of the facts. */
export async function searchProject(query: string, name?: string, limit = 20) {
  const { job } = await resolveProject(name);
  const tokens = searchTokens(query).map((token) => token.toLowerCase());
  if (tokens.length === 0) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give words or codes to search for (e.g. 'MH-3 culvert').");
  return withProjectDb(job.file, ({ db }) => {
    let hits: Array<{ file: string; path?: string; part: string | null; snippet: string }> = [];
    let note: string | undefined;
    try {
      hits = searchText(db, query, Math.min(Math.max(limit, 1), 50))
        .map((hit) => ({ file: hit.fileId, path: filePath(db, hit.fileId), part: hit.label, snippet: hit.snippet }));
    } catch (error) {
      note = `Text search failed: ${(error as Error).message}`;
    }
    const like = tokens.map(() => "lower(f.fact || ' ' || f.topics) LIKE ?").join(" AND ");
    const matching = facts(db, `${CURRENT} AND ${like} ORDER BY f.key DESC, f.added DESC LIMIT 50`, ...tokens.map((token) => `%${token}%`));
    return { project: job.name, query, facts: matching, hits, ...(note ? { note } : {}) };
  });
}

interface FileRow {
  id: string; path: string; name: string; kind: string; size: number | null; status: string; duplicate_of: string | null;
  summary: string | null; doc_date: string | null; author: string | null; error: string | null; chars: number | null; parent_id: string | null;
}

function findFile(db: DatabaseSync, file: string): FileRow {
  const columns = "id, path, name, kind, size, status, duplicate_of, summary, doc_date, author, error, chars, parent_id";
  const wanted = file.replace(/\//g, "\\");
  const byId = db.prepare(`SELECT ${columns} FROM files WHERE id = ?`).get(file) as unknown as FileRow | undefined;
  if (byId) return byId;
  const byPath = db.prepare(`SELECT ${columns} FROM files WHERE lower(path) = lower(?)`).get(wanted) as unknown as FileRow | undefined;
  if (byPath) return byPath;
  const candidates = db.prepare(`SELECT ${columns} FROM files WHERE lower(name) = lower(?) OR lower(path) LIKE lower(?) LIMIT 11`)
    .all(baseName(wanted), `%\\${wanted}`) as unknown as FileRow[];
  if (candidates.length === 1) return candidates[0];
  if (candidates.length > 1) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", `Several files match '${file}': ${candidates.slice(0, 10).map((row) => row.path).join("; ")}. Give the path.`);
  }
  throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No file '${file}' in the project database. Use action=search to find it.`);
}

const TEXT_PREVIEW_CHARS = 4_000;

/** One file of the job: details, summary, references from and to it, facts citing it, the start of its text. */
export async function projectFileDetails(file: string, name?: string) {
  const { job } = await resolveProject(name);
  return withProjectDb(job.file, ({ db }) => {
    const row = findFile(db, file);
    const textOf = row.status === "duplicate" && row.duplicate_of ? row.duplicate_of : row.id;
    let text = "";
    try { text = fullText(db, textOf); } catch { /* no text stored */ }
    const summary = all<{ summary: string; points: string; people: string; tool: string; added: string }>(
      db, "SELECT summary, points, people, tool, added FROM summaries WHERE file_id = ? ORDER BY added DESC, id DESC LIMIT 1", row.id)[0];
    return {
      project: job.name,
      file: Object.fromEntries(Object.entries(row).filter(([, value]) => value !== null)),
      props: Object.fromEntries(all<{ key: string; value: string }>(db, "SELECT key, value FROM props WHERE file_id = ? ORDER BY key", row.id).map((prop) => [prop.key, prop.value])),
      ...(summary ? { summary: { ...summary, points: parseJson(summary.points, []), people: parseJson(summary.people, []) } } : {}),
      refsFrom: all<Record<string, unknown>>(db, "SELECT kind, raw, location, status, target_path, expected_path FROM refs WHERE file_id = ?", row.id),
      refsTo: all<{ file_id: string; kind: string; raw: string; status: string }>(
        db, "SELECT file_id, kind, raw, status FROM refs WHERE target_id = ? OR lower(target_path) = lower(?)", row.id, row.path)
        .map((ref) => ({ ...ref, from: filePath(db, ref.file_id) })),
      facts: facts(db, `${CURRENT} AND EXISTS (SELECT 1 FROM json_each(f.sources) WHERE json_extract(value, '$.file') = ?) ORDER BY f.added`, row.id),
      text: text.length > TEXT_PREVIEW_CHARS ? `${text.slice(0, TEXT_PREVIEW_CHARS)}\n[... ${text.length - TEXT_PREVIEW_CHARS} more characters]` : text,
    };
  });
}

/** A person, organization, structure, sheet or document the analyzer found, with its files, events, stories and facts. */
export async function projectEntity(entity: string, name?: string) {
  const { job } = await resolveProject(name);
  const wanted = oneLine(entity).toLowerCase();
  if (!wanted) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give the name to look up (a person, organization, structure, sheet or document).");
  return withProjectDb(job.file, ({ db }) => {
    type EntityRow = { id: string; type: string; name: string; aliases: string; files: number; mentions: number; first_date: string | null; last_date: string | null };
    const rows = all<EntityRow>(db, "SELECT id, type, name, aliases, files, mentions, first_date, last_date FROM intel_entities");
    const names = (row: EntityRow) => [row.name, ...parseJson<string[]>(row.aliases, [])].map((value) => String(value).toLowerCase());
    const found = rows.find((row) => row.id.toLowerCase() === wanted || names(row).includes(wanted));
    if (!found) {
      const similar = rows.filter((row) => names(row).some((value) => value.includes(wanted) || wanted.includes(value))).slice(0, 10).map((row) => row.name);
      const matchingFacts = facts(db, `${CURRENT} AND lower(f.topics || ' ' || f.fact) LIKE ? ORDER BY f.added LIMIT 50`, `%${wanted}%`);
      return {
        project: job.name, entity: null, query: entity, ...(similar.length ? { similar } : {}), facts: matchingFacts,
        hint: similar.length ? "Not found by that exact name; try one of the similar names." : "Not a known name; try action=search.",
      };
    }
    const allNames = names(found);
    const entityFiles = all<{ file_id: string; count: number }>(db, "SELECT file_id, count FROM intel_entity_files WHERE entity_id = ? ORDER BY count DESC", found.id);
    const fileIds = new Set(entityFiles.map((row) => row.file_id));
    const events = all<{ date: string; kind: string; title: string; file_id: string | null }>(db, "SELECT date, kind, title, file_id FROM intel_events ORDER BY date")
      .filter((event) => (event.file_id && fileIds.has(event.file_id)) || allNames.some((value) => event.title?.toLowerCase().includes(value)))
      .slice(0, 50)
      .map((event) => ({ date: event.date, kind: event.kind, title: event.title, file: filePath(db, event.file_id) }));
    const stories = all<{ id: string; title: string; subject: string | null; refs: string; people: string; first_date: string; last_date: string }>(
      db, "SELECT id, title, subject, refs, people, first_date, last_date FROM intel_stories")
      .filter((story) => story.subject === found.id || allNames.some((value) =>
        story.title?.toLowerCase().includes(value) || story.refs?.toLowerCase().includes(value) || story.people?.toLowerCase().includes(value)))
      .map((story) => ({
        id: story.id, title: story.title, first: story.first_date, last: story.last_date,
        steps: all<{ date: string; kind: string; who: string; what: string; file_id: string }>(
          db, "SELECT date, kind, who, what, file_id FROM intel_story_steps WHERE story_id = ? ORDER BY position", story.id)
          .map((step) => ({ date: step.date, kind: step.kind, who: step.who, what: step.what, file: filePath(db, step.file_id) })),
      }));
    const like = allNames.map(() => "lower(f.topics) LIKE ?").join(" OR ");
    return {
      project: job.name,
      entity: {
        id: found.id, type: found.type, name: found.name, aliases: parseJson<string[]>(found.aliases, []),
        files: found.files, mentions: found.mentions, first: found.first_date, last: found.last_date,
      },
      files: entityFiles.slice(0, 50).map((row) => ({ file: row.file_id, path: filePath(db, row.file_id), count: row.count })),
      events,
      stories,
      facts: facts(db, `${CURRENT} AND (${like}) ORDER BY f.added`, ...allNames.map((value) => `%${value}%`)),
    };
  });
}

/** The job's events (from the analyzer) and dated facts in date order, optionally between two dates (inclusive). */
export async function projectTimeline(name?: string, from?: string, to?: string, limit = 200) {
  const { job } = await resolveProject(name);
  for (const value of [from, to]) {
    if (value && !/^\d{4}(-\d{2}(-\d{2})?)?$/.test(value)) throw knowledgeError("CIVIL3D.INVALID_INPUT", `Dates are YYYY, YYYY-MM or YYYY-MM-DD, not '${value}'.`);
  }
  const inRange = (date: string | null) => !!date && (!from || date >= from || date.startsWith(from)) && (!to || date.slice(0, to.length) <= to);
  return withProjectDb(job.file, ({ db }) => {
    const events = all<{ date: string; kind: string; title: string; confidence: string; file_id: string | null }>(
      db, "SELECT date, kind, title, confidence, file_id FROM intel_events")
      .filter((event) => inRange(event.date))
      .map((event) => ({ date: event.date, kind: event.kind, title: event.title, ...(event.confidence && event.confidence !== "high" ? { confidence: event.confidence } : {}), file: filePath(db, event.file_id) }));
    const dated = facts(db, `${CURRENT} AND f.date IS NOT NULL`).filter((fact) => inRange(fact.date ?? null))
      .map((fact) => ({ date: fact.date!, kind: "fact", title: fact.fact, fact: fact.id, tool: fact.tool }));
    const timeline = [...events, ...dated].sort((left, right) => left.date.localeCompare(right.date));
    return {
      project: job.name, from: from ?? null, to: to ?? null, count: timeline.length,
      timeline: timeline.slice(0, Math.max(limit, 1)), ...(timeline.length > limit ? { truncated: true } : {}),
    };
  });
}

/**
 * Translates a path written in one of the job's files (an xref, image, hyperlink) to where it is in this copy of the
 * job (contract: "Copied job folders"): refs first, then meta.reference_roots; otherwise says it isn't known.
 */
export async function locatePath(written: string, name?: string) {
  const { job } = await resolveProject(name);
  const wanted = written.trim().replace(/\//g, "\\");
  if (!wanted) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give the path as written in the file.");
  return withProjectDb(job.file, ({ db }) => {
    const sourceRoot = metaValue(db, "source_root");
    const inCopy = (relative: string | null | undefined) => (relative && sourceRoot ? path.win32.join(sourceRoot, relative) : undefined);
    const refs = all<{ file_id: string; kind: string; status: string; target_path: string | null; expected_path: string | null; candidates: string | null; location: string | null }>(
      db, "SELECT file_id, kind, status, target_path, expected_path, candidates, location FROM refs WHERE lower(normalized) = lower(?) OR lower(raw) = lower(?)", wanted, wanted);
    if (refs.length > 0) {
      const ref = refs.find((candidate) => candidate.target_path) ?? refs[0];
      const target = inCopy(ref.target_path);
      return {
        project: job.name, written, method: "refs", status: ref.status,
        ...(ref.target_path ? { relative: ref.target_path, path: target, exists: !!target && existsSync(target) } : {}),
        ...(ref.expected_path ? { expected: ref.expected_path } : {}),
        ...(ref.candidates ? { candidates: parseJson<string[]>(ref.candidates, []) } : {}),
        writtenIn: [...new Set(refs.map((candidate) => filePath(db, candidate.file_id)))],
      };
    }
    const lower = wanted.toLowerCase();
    for (const root of parseJson<Array<{ original?: string; in_copy?: string }>>(metaValue(db, "reference_roots"), [])) {
      const original = String(root.original ?? "").replace(/\//g, "\\").replace(/\\+$/, "").toLowerCase();
      if (!original || !(lower === original || lower.startsWith(`${original}\\`))) continue;
      const rest = wanted.slice(original.length).replace(/^\\+/, "");
      const relative = path.win32.join(root.in_copy ?? "", rest);
      const target = inCopy(relative);
      return {
        project: job.name, written, method: "reference_roots", relative, path: target, exists: !!target && existsSync(target),
        hint: "Worked out from where the job was copied from; check it exists before relying on it.",
      };
    }
    return {
      project: job.name, written, method: null, path: null, ...copyInfo(db),
      hint: "Not found in the references Project Analyzer read, and no known original location matches. Ask the user where it is rather than guess; " +
        "once found, record it as a note (e.g. 'TOPO xref of CAD\\Site.dwg is CAD\\Topo.dwg in the copy').",
    };
  });
}

// ---------------------------------------------------------------- once per session, with the first code result

// A short overview is attached to code results once per project per server session, so it reaches the assistant
// without being repeated on every call.
const announced = new Set<string>();

export interface ProjectBriefing {
  project: string;
  keyFacts: string[];
  notes: number;
  highChecks: string[];
  missingFiles: number;
  filesFolder: string;
  hint: string;
}

export async function projectNotesOnce(): Promise<ProjectBriefing | undefined> {
  try {
    const job = projectForDrawing(await activeDrawingPath());
    if (!job || announced.has(job.file)) return undefined;
    const briefing = withProjectDb(job.file, ({ db }) => ({
      project: job.name,
      keyFacts: facts(db, `${CURRENT} AND f.key = 1 AND ${NOT_SUPERSEDED} ORDER BY f.added, f.id LIMIT 15`).map((fact) => `[${fact.id}] ${fact.fact}`),
      notes: Number((db.prepare(`SELECT COUNT(*) AS n FROM facts f WHERE ${CURRENT} AND f.tool = ?`).get(TOOL) as { n: number }).n),
      highChecks: all<{ title: string }>(db, "SELECT title FROM intel_checks WHERE severity = 'high' ORDER BY id LIMIT 10").map((row) => row.title),
      missingFiles: Number(all<{ n: number }>(db, "SELECT COUNT(DISTINCT lower(normalized)) AS n FROM refs WHERE status = 'missing'")[0]?.n ?? 0),
      filesFolder: filesDirectoryOf(job),
      hint: "civil3d_project_notes action=overview for everything known about this job (facts, questions, rules, checks, people, practices).",
    }));
    announced.add(job.file);
    return briefing;
  } catch {
    return undefined;
  }
}

/** For tests. */
export function resetProjectNotesAnnouncements() {
  announced.clear();
}
