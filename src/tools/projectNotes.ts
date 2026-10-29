import { existsSync, statSync } from "node:fs";
import path from "node:path";
import type { DatabaseSync } from "node:sqlite";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { knowledgeError } from "./knowledgeBase.js";
import { listPractices } from "./practices.js";
import {
  baseName, databaseFileFor, directoryOf, fullText, immediate, invalidateJobIndex, isBusy, isCorrupt, isMissingTableOrColumn, jobByName, jobForPath,
  jobIndex, metaValue, nextId, normalizePath, nowIso, onDisk, pathKey, relativeTo, searchText, searchTokens, storedPath, TOOL, withoutProfile,
  withProjectDb, type Job,
} from "./projectDb.js";
import {
  analyzerDocuments, ensureUserKnowledgeDirectory, extractText, insideFolder, jobDirectoryOf, joinParts, legacyFiles, listFolder, longPath, MAX_DIRECT_BYTES,
  readableHere, readByAnalyzer, USER_KNOWLEDGE_PREFIX, USER_KNOWLEDGE_RANGE, userKnowledgeDirectoryOf, type AnalyzerDocument, type FolderEntry, type TextPart,
} from "./userKnowledge.js";

export { projectsDirectory } from "./projectDb.js";

/**
 * Project notes: what is known about ONE job, kept in the suite's project database
 * <shared folder>\projects\<slug>.db (see projectDb.ts and project-analyzer's docs/suite-knowledge.md). Project
 * Analyzer fills it from the job folder; Civil 3D MCP and TBC MCP read it and add notes. A note is a row of `facts`
 * with tool = 'civil3d'; this tool changes only its own facts and folders. A job is matched by folder: the active
 * drawing belongs to the job whose source_root or linked folder contains it (deepest wins).
 * The user's own documents for the job (guidelines, requirements, standards) go in
 * <shared folder>\projects\<slug>\User Knowledge\ (see userKnowledge.ts); this tool only creates and reads that folder.
 * Read actions open the database read-only; only link, unlink and the note actions write (projectDb.ts).
 */

/**
 * The suite's rule for the job database (contract: "Reference only"), on every read answer with the date it describes:
 * its stored text, coordinates and quantities are a snapshot of the job folder, for finding things and never for edits.
 */
export const REFERENCE_ONLY =
  "Reference only: the job database is a snapshot of the job's files as of {date}, and they may have changed since. Use it to find " +
  "and understand things (what a file says, where it is, who did what, when). Never use its coordinates, elevations, points, stations, " +
  "dimensions, quantities or text as data to edit or import into a drawing: take them from the current source file the user names, and " +
  "if they haven't named one, ask which file is current.";

/** REFERENCE_ONLY with the date of the last Project Analyzer run (meta last_run_finished), if there was one. */
export function referenceOnly(db: DatabaseSync): string {
  return REFERENCE_ONLY.replace("{date}", metaValue(db, "last_run_finished")?.slice(0, 10) || "the last time a tool wrote to it");
}

const oneLine = (value: string) => value.replace(/\s+/g, " ").trim();
const parseJson = <T>(text: string | null | undefined, fallback: T): T => {
  try { return text ? (JSON.parse(text) as T) : fallback; } catch { return fallback; }
};

/** The job's User Knowledge folder <shared folder>\projects\<slug>\User Knowledge (not created here). */
export function userKnowledgeDirectory(name: string): string {
  return userKnowledgeDirectoryOf(databaseFileFor(name));
}

const USE_USER_KNOWLEDGE =
  "When a user-knowledge document applies to the task (a guideline, a requirement), follow it and name the document; " +
  "when it conflicts with a fact, a practice or the drawings, tell the user rather than choose.";

const analyzerCommand = (job: Job) => `python analyze.py knowledge --project "${job.name}"`;

/** The User Knowledge folder (created when missing) and, for each document, whether the database has it as it is now. */
function userKnowledgeStatus(db: DatabaseSync, job: Job) {
  const folder = ensureUserKnowledgeDirectory(job.file);
  const { files, truncated, skipped } = listFolder(folder);
  const rows = analyzerDocuments(db);
  const entries = files.map((entry) => {
    const row = rows.get(entry.path.toLowerCase());
    return { entry, row, read: readByAnalyzer(entry, row) };
  });
  return { folder, entries, truncated, skipped, rows };
}

/** Every known job: name, database and folders; a job whose database can't be read now has a note saying why. */
export function listProjects() {
  return jobIndex().jobs.map((job) => ({ name: job.name, path: job.file, folders: job.roots.map((root) => root.path), ...(job.problem ? { note: job.problem } : {}) }));
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

async function describe(job: Job) {
  const userKnowledgeFolder = ensureUserKnowledgeDirectory(job.file);
  const { files } = listFolder(userKnowledgeFolder);
  return withProjectDb(job.file, ({ db }) => {
    const others = db.prepare(`SELECT tool, COUNT(*) AS n FROM facts f WHERE ${CURRENT} AND tool <> ? GROUP BY tool`).all(TOOL) as Array<{ tool: string; n: number }>;
    return {
      project: job.name, referenceOnly: referenceOnly(db),
      path: job.file,
      folders: job.roots.map((root) => ({ path: root.path, tool: root.tool })),
      notes: facts(db, `${CURRENT} AND f.tool = ? ORDER BY f.added, f.id`, TOOL),
      otherFacts: Object.fromEntries(others.map((row) => [row.tool, row.n])),
      userKnowledgeFolder,
      userKnowledge: files.length,
      hint: "notes are this tool's own facts. action=overview shows what the whole suite knows about the job; search, file, entity, timeline and locate read more; " +
        "files and read show the user's own documents for the job (user knowledge).",
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

/**
 * The job's User Knowledge folder and what is in it (action=files): each document with whether Project Analyzer has
 * read it as it is now and whether this tool can read it itself. Files older versions kept directly in <slug>\
 * are listed apart (`elsewhere`).
 */
export async function listProjectFiles(name?: string) {
  const { job } = await resolveProject(name);
  const { folder, entries, truncated, skipped } = await withProjectDb(job.file, ({ db }) => userKnowledgeStatus(db, job));
  const files = entries.map(({ entry, row, read }) => ({
    path: entry.path, size: entry.size, modified: entry.modified, readByAnalyzer: read,
    ...(row && !read ? { changedSinceAnalyzerRead: true } : {}),
    readableHere: readableHere(entry.path, entry.size),
  }));
  const legacy = legacyFiles(job.file).files;
  const unreadable = files.filter((file) => !file.readByAnalyzer && !file.readableHere).length;
  return {
    project: job.name, userKnowledgeFolder: folder, files, ...(truncated ? { truncated } : {}),
    ...(skipped.length ? {
      unreadable: skipped.slice(0, 30),
      unreadableHint: "These couldn't be read from the folder (no access, in use, or a name Windows can't open); they are left out. Tell the user.",
    } : {}),
    ...(legacy.length ? {
      elsewhere: legacy.map((file) => ({ path: file.path, size: file.size, modified: file.modified })),
      elsewhereHint: `These files are in ${jobDirectoryOf(job.file)}, where versions before 2.4.0 kept reference files, outside User Knowledge; ` +
        `they aren't read any more. Suggest the user moves them into ${folder} (this tool doesn't move files).`,
    } : {}),
    hint: files.length === 0
      ? "Empty. The user can put documents for this job here: proposal guidelines, requirements for the building type, the client's " +
        "standards, design manuals, code books (subfolders are fine)."
      : `Read a document with action=read (file = its path here). ${USE_USER_KNOWLEDGE}` +
        (unreadable ? ` Documents with readByAnalyzer and readableHere both false (PDFs, scans, drawings, older Office formats) are read by ` +
          `Project Analyzer: ${analyzerCommand(job)}; or the AI app can open them itself from the folder.` : ""),
  };
}

const notReadableHint = (job: Job, absolute: string, row: AnalyzerDocument | undefined, read: boolean) => {
  const openIt = `If your AI app can open files, it can open ${absolute} itself.`;
  if (read && row) {
    return `Project Analyzer read it (status ${row.status}${row.error ? `: ${row.error}` : ""}) but found no text in it. ${openIt}`;
  }
  return `${row ? "It changed since Project Analyzer read it. " : ""}Civil 3D MCP reads plain text, Word (.docx), Excel (.xlsx) and ` +
    `PowerPoint (.pptx) documents up to ${MAX_DIRECT_BYTES / 1024 / 1024} MB itself. Others (PDFs, scans, drawings, older Office formats) ` +
    `are read by Project Analyzer: ${analyzerCommand(job)}, then read it again here. ${openIt}`;
};

/**
 * One document of the job's User Knowledge folder (action=read): the text Project Analyzer stored when it has the
 * document as it is now, else the text read straight from the file (plain text, .docx, .xlsx, .pptx), else a note
 * saying who can read it. Paths outside the folder are refused. Long text comes back in pieces (offset, maxChars).
 */
export async function readUserKnowledge(file: string, name?: string, options: { maxChars?: number; offset?: number } = {}) {
  const { job } = await resolveProject(name);
  const folder = ensureUserKnowledgeDirectory(job.file);
  const maxChars = Math.max(1, Math.floor(options.maxChars ?? 20_000));
  const offset = Math.max(0, Math.floor(options.offset ?? 0));
  let absolute = insideFolder(folder, file);
  if (!absolute) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", `'${file}' isn't in the job's User Knowledge folder (${folder}); only documents there can be read with action=read.`);
  }
  if (!existsSync(longPath(absolute))) {
    // A bare file name: the one document with that name anywhere in the folder.
    const byName = /[\\/]/.test(file) ? [] : listFolder(folder).files.filter((entry) => baseName(entry.path).toLowerCase() === file.trim().toLowerCase());
    if (byName.length !== 1) {
      throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", byName.length > 1
        ? `Several documents are named '${file}': ${byName.map((entry) => entry.path).join("; ")}. Give the path.`
        : `No document '${file}' in ${folder}. action=files lists them.`);
    }
    absolute = byName[0].absolute;
  }
  const stat = statSync(longPath(absolute));
  if (!stat.isFile()) throw knowledgeError("CIVIL3D.INVALID_INPUT", `'${file}' is a folder; action=files lists the documents in it.`);
  const entry: FolderEntry = {
    path: path.relative(folder, absolute).split(/[\\/]+/).join("\\"), absolute, size: stat.size, modified: stat.mtime.toISOString(), mtimeMs: stat.mtimeMs,
  };
  const stored = await withProjectDb(job.file, ({ db }) => {
    const row = analyzerDocuments(db).get(entry.path.toLowerCase());
    const read = readByAnalyzer(entry, row);
    let text = "";
    if (read && row) {
      try { text = fullText(db, row.id); } catch (error) { passOnDamage(error); /* no text stored */ }
    }
    return { row, read, text };
  });
  const base = { project: job.name, file: entry.path, path: absolute, size: entry.size, modified: entry.modified, readByAnalyzer: stored.read };
  let source: "project-analyzer" | "direct";
  let text: string;
  if (stored.read && stored.text.trim()) {
    source = "project-analyzer";
    text = stored.text;
  } else if (readableHere(entry.path, entry.size)) {
    try {
      text = joinParts(extractText(absolute) ?? []);
      source = "direct";
    } catch (error) {
      return { ...base, readableHere: true, text: null, error: (error as Error).message, hint: notReadableHint(job, absolute, stored.row, stored.read) };
    }
  } else {
    return { ...base, readableHere: false, text: null, hint: notReadableHint(job, absolute, stored.row, stored.read) };
  }
  const end = offset + maxChars;
  const truncated = end < text.length;
  return {
    ...base, source, chars: text.length, offset, text: text.slice(offset, end), truncated,
    ...(truncated ? { nextOffset: end } : {}),
    hint: `${truncated ? `More follows: read again with offset=${end}. ` : ""}${USE_USER_KNOWLEDGE}`,
  };
}

/** Creates the project database if needed and links a folder to it (default: the active drawing's folder). */
export async function linkProjectFolder(name: string, folder?: string) {
  const target = folder ?? (await activeDrawingPath().then((drawing) => (drawing ? directoryOf(drawing) : undefined)));
  if (!target) throw knowledgeError("CIVIL3D.INVALID_INPUT", "No folder given and the active drawing's folder isn't known.");
  const existing = jobByName(name);
  const file = existing?.file ?? databaseFileFor(name);
  const normalized = normalizePath(target);
  const result = await withProjectDb(file, ({ db }) => immediate(db, () => {
    const roots = [metaValue(db, "source_root"), ...(db.prepare("SELECT path FROM folders").all() as Array<{ path: string }>).map((row) => row.path)]
      .filter((root): root is string => !!root);
    if (roots.some((root) => pathKey(root) === pathKey(normalized))) return "unchanged";
    db.prepare("INSERT INTO folders(path, tool, added) VALUES (?, ?, ?)").run(normalized, TOOL, nowIso());
    return existing ? "linked" : "created";
  }), { create: true, name: oneLine(name), write: true });
  invalidateJobIndex();
  const job = jobByName(name)!;
  const userKnowledgeFolder = ensureUserKnowledgeDirectory(job.file);
  return { success: true, action: result, project: job.name, folders: job.roots.map((root) => root.path), path: job.file, userKnowledgeFolder };
}

/** Removes a folder this tool linked (notes are kept; other tools' folders and source_root stay). */
export async function unlinkProjectFolder(name: string, folder: string) {
  const job = jobByName(name);
  if (!job) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project named '${name}'.`);
  const key = pathKey(folder);
  await withProjectDb(job.file, ({ db }) => immediate(db, () => {
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

/** Busy and damage always reach the caller (withProjectDb waits, or says how to restore); other errors are the caller's. */
function passOnDamage(error: unknown) {
  if (isBusy(error) || isCorrupt(error)) throw error;
}

/**
 * Rows of a query on tables a database may not have (an MCP-made database, an older analyzer): a missing table or
 * column gives no rows. Anything else (busy, a damaged file) is thrown, never turned into an empty answer.
 */
const all = <T>(db: DatabaseSync, sql: string, ...params: Array<string | number | null>): T[] => {
  try {
    return db.prepare(sql).all(...params) as unknown as T[];
  } catch (error) {
    if (isMissingTableOrColumn(error)) return [];
    throw error;
  }
};

/** The paths of these file ids, in one query per answer (500 ids at a time). */
function filePaths(db: DatabaseSync, ids: Iterable<string | null | undefined>): Map<string, string> {
  const wanted = [...new Set([...ids].filter((id): id is string => !!id))];
  const found = new Map<string, string>();
  for (let at = 0; at < wanted.length; at += 500) {
    const chunk = wanted.slice(at, at + 500);
    for (const row of all<{ id: string; path: string }>(db, `SELECT id, path FROM files WHERE id IN (${chunk.map(() => "?").join(", ")})`, ...chunk)) {
      found.set(row.id, row.path);
    }
  }
  return found;
}

/** A file id's path from filePaths' answer: the id itself when it has no row, undefined for none. */
const pathIn = (paths: Map<string, string>, fileId: string | null | undefined) => (fileId ? paths.get(fileId) ?? fileId : undefined);

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
    // Job files only: user-knowledge documents are counted under userKnowledge.
    const statuses = all<{ status: string; n: number }>(
      db, "SELECT status, COUNT(*) AS n FROM files WHERE NOT (path >= ? AND path < ?) GROUP BY status ORDER BY n DESC", ...USER_KNOWLEDGE_RANGE);
    const knowledge = userKnowledgeStatus(db, job);
    const legacy = legacyFiles(job.file).files.length;
    const lastRun = metaValue(db, "last_run_finished");
    return {
      project: job.name, referenceOnly: referenceOnly(db),
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
      userKnowledge: {
        folder: knowledge.folder,
        documents: knowledge.entries.length,
        names: knowledge.entries.slice(0, 30).map(({ entry }) => entry.path),
        readByAnalyzer: knowledge.entries.filter(({ read }) => read).length,
        ...(legacy ? { elsewhere: legacy } : {}),
        hint: "The user's own documents for this job (proposal guidelines, building-type requirements, standards, code books): " +
          `action=files lists them, read (file) reads one, search covers them. ${USE_USER_KNOWLEDGE}` +
          (legacy ? " Files kept next to the folder by older versions aren't read: action=files lists them (elsewhere)." : ""),
      },
      hint: "search (query), file (file id or path), entity (name), timeline (from, to), checks (what Project Analyzer found to look at), " +
        "changes (requirement and file changes, the last 30 days by default) and locate (path) read more. " +
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

interface SearchHit { file: string | null; path?: string; part: string | null; snippet: string; userKnowledge?: true; readDirectly?: true }

// Documents read straight from the folder by one search: each at most 20 MB, 200 MB in all.
const SEARCH_DIRECT_MAX_BYTES = 20 * 1024 * 1024;
const SEARCH_DIRECT_TOTAL_BYTES = 200 * 1024 * 1024;

function snippetOf(text: string, words: string[]): string {
  const lower = text.toLowerCase();
  const found = words.map((word) => lower.indexOf(word)).filter((at) => at >= 0);
  const start = Math.max((found.length ? Math.min(...found) : 0) - 100, 0);
  return `${start ? "…" : ""}${text.slice(start, start + 240).replace(/\s+/g, " ").trim()}`;
}

/**
 * Searches the User Knowledge documents the database doesn't have as they are now and that this tool can read
 * itself: a document matches when it has every word (case-insensitive); the snippet comes from the first part
 * (sheet, slide) that has them all, else from the first one that has the first word.
 */
function searchUnreadDocuments(entries: Array<{ entry: FolderEntry; read: boolean }>, words: string[], limit: number) {
  const hits: SearchHit[] = [];
  const notSearched: string[] = [];
  let budget = SEARCH_DIRECT_TOTAL_BYTES;
  for (const { entry, read } of entries) {
    if (read) continue;
    if (hits.length >= limit) break;
    if (!readableHere(entry.path, entry.size) || entry.size > SEARCH_DIRECT_MAX_BYTES || entry.size > budget) { notSearched.push(entry.path); continue; }
    budget -= entry.size;
    let parts: TextPart[];
    try { parts = extractText(entry.absolute) ?? []; } catch { notSearched.push(entry.path); continue; }
    const has = (text: string) => { const lower = text.toLowerCase(); return words.every((word) => lower.includes(word)); };
    if (!has(parts.map((part) => part.text).join("\n"))) continue;
    const part = parts.find((candidate) => has(candidate.text)) ?? parts.find((candidate) => candidate.text.toLowerCase().includes(words[0])) ?? parts[0];
    hits.push({ file: null, path: `${USER_KNOWLEDGE_PREFIX}${entry.path}`, part: part.label, snippet: snippetOf(part.text, words), userKnowledge: true, readDirectly: true });
  }
  return { hits, notSearched };
}

/**
 * Full-text search of the job's files (user-knowledge documents included, marked userKnowledge), plus the
 * user-knowledge documents the database doesn't have yet that this tool reads itself, plus a search of the facts.
 */
export async function searchProject(query: string, name?: string, limit = 20) {
  const { job } = await resolveProject(name);
  const tokens = searchTokens(query).map((token) => token.toLowerCase());
  if (tokens.length === 0) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give words or codes to search for (e.g. 'MH-3 culvert').");
  const max = Math.min(Math.max(limit, 1), 50);
  return withProjectDb(job.file, ({ db }) => {
    let hits: SearchHit[] = [];
    let note: string | undefined;
    try {
      const found = searchText(db, query, max);
      const paths = filePaths(db, found.map((hit) => hit.fileId));
      hits = found.map((hit) => {
        const at = pathIn(paths, hit.fileId);
        return { file: hit.fileId, path: at, part: hit.label, snippet: hit.snippet, ...(at?.startsWith(USER_KNOWLEDGE_PREFIX) ? { userKnowledge: true as const } : {}) };
      });
    } catch (error) {
      passOnDamage(error);
      note = `Text search failed: ${(error as Error).message}`;
    }
    const direct = searchUnreadDocuments(userKnowledgeStatus(db, job).entries, tokens, max);
    const like = tokens.map(() => "lower(f.fact || ' ' || f.topics) LIKE ?").join(" AND ");
    const matching = facts(db, `${CURRENT} AND ${like} ORDER BY f.key DESC, f.added DESC LIMIT 50`, ...tokens.map((token) => `%${token}%`));
    const anyKnowledge = direct.hits.length > 0 || hits.some((hit) => hit.userKnowledge);
    return {
      project: job.name, referenceOnly: referenceOnly(db), query, facts: matching, hits: [...hits, ...direct.hits],
      ...(direct.notSearched.length ? {
        userKnowledgeNotSearched: direct.notSearched.slice(0, 30),
        userKnowledgeNotSearchedHint: "Project Analyzer hasn't read these user-knowledge documents as they are now, and Civil 3D MCP couldn't " +
          `search them itself (format, size or damage): ${analyzerCommand(job)}.`,
      } : {}),
      ...(anyKnowledge ? { hint: `Hits marked userKnowledge are the user's own documents for this job: read one with action=read (file = its path without '<user knowledge>\\'). ${USE_USER_KNOWLEDGE}` } : {}),
      ...(note ? { note } : {}),
    };
  });
}

interface FileRow {
  id: string; path: string; name: string; kind: string; size: number | null; status: string; duplicate_of: string | null;
  summary: string | null; doc_date: string | null; author: string | null; error: string | null; chars: number | null; parent_id: string | null;
}

function findFile(db: DatabaseSync, file: string): FileRow {
  const columns = "id, path, name, kind, size, status, duplicate_of, summary, doc_date, author, error, chars, parent_id";
  // A path on this computer (in the job folder or one of its other folders) is looked up as the database stores it.
  const wanted = (path.win32.isAbsolute(file) ? storedPath(db, file) : undefined) ?? file.replace(/\//g, "\\");
  const byId = db.prepare(`SELECT ${columns} FROM files WHERE id = ?`).get(file) as unknown as FileRow | undefined;
  if (byId) return byId;
  // `= ? COLLATE NOCASE` so files_path_nocase answers it.
  const byPath = db.prepare(`SELECT ${columns} FROM files WHERE path = ? COLLATE NOCASE`).get(wanted) as unknown as FileRow | undefined;
  if (byPath) return byPath;
  const candidates = db.prepare(`SELECT ${columns} FROM files WHERE name = ? COLLATE NOCASE OR path LIKE ? ESCAPE '!' LIMIT 11`)
    .all(baseName(wanted), `%\\${escapeLike(wanted)}`) as unknown as FileRow[];
  if (candidates.length === 1) return candidates[0];
  if (candidates.length > 1) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", `Several files match '${file}': ${candidates.slice(0, 10).map((row) => row.path).join("; ")}. Give the path.`);
  }
  throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No file '${file}' in the project database. Use action=search to find it.`);
}

/** A value for LIKE ... ESCAPE '!': its own % and _ match only themselves. */
const escapeLike = (text: string) => text.replace(/!/g, "!!").replace(/%/g, "!%").replace(/_/g, "!_");

const TEXT_PREVIEW_CHARS = 4_000;

/** One file of the job: details, summary, references from and to it, facts citing it, the start of its text. */
export async function projectFileDetails(file: string, name?: string) {
  const { job } = await resolveProject(name);
  return withProjectDb(job.file, ({ db }) => {
    const row = findFile(db, file);
    const textOf = row.status === "duplicate" && row.duplicate_of ? row.duplicate_of : row.id;
    let text = "";
    try { text = fullText(db, textOf); } catch (error) { passOnDamage(error); /* no text stored */ }
    const summary = all<{ summary: string; points: string; people: string; tool: string; added: string }>(
      db, "SELECT summary, points, people, tool, added FROM summaries WHERE file_id = ? ORDER BY added DESC, id DESC LIMIT 1", row.id)[0];
    const refsTo = all<{ file_id: string; kind: string; raw: string; status: string }>(
      db, "SELECT file_id, kind, raw, status FROM refs WHERE target_id = ? OR target_path = ? COLLATE NOCASE", row.id, row.path);
    const paths = filePaths(db, refsTo.map((ref) => ref.file_id));
    const place = row.path.includes("::") ? undefined : onDisk(db, row.path);
    return {
      project: job.name, referenceOnly: referenceOnly(db),
      file: Object.fromEntries(Object.entries(row).filter(([, value]) => value !== null)),
      ...(place ? { onDisk: place } : {}),
      props: Object.fromEntries(all<{ key: string; value: string }>(db, "SELECT key, value FROM props WHERE file_id = ? ORDER BY key", row.id).map((prop) => [prop.key, prop.value])),
      ...(summary ? { summary: { ...summary, points: parseJson(summary.points, []), people: parseJson(summary.people, []) } } : {}),
      refsFrom: all<Record<string, unknown>>(db, "SELECT kind, raw, location, status, target_path, expected_path FROM refs WHERE file_id = ?", row.id),
      refsTo: refsTo.map((ref) => ({ ...ref, from: pathIn(paths, ref.file_id) })),
      // Facts whose sources name the file: the text holds the quoted id (no JSON parsing of every fact), then checked.
      facts: facts(db, `${CURRENT} AND instr(f.sources, ?) > 0 ORDER BY f.added`, JSON.stringify(row.id))
        .filter((fact) => (fact.sources ?? []).some((source) => (source as { file?: unknown } | null)?.file === row.id)),
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
    const columns = "id, type, name, aliases, files, mentions, first_date, last_date";
    const names = (row: EntityRow) => [row.name, ...parseJson<string[]>(row.aliases, [])].map((value) => String(value).toLowerCase());
    // Found in SQLite, not by reading every entity (a big job has tens of thousands): the id, then the name
    // (intel_entities_name), then an alias (rows whose aliases text holds it, checked here).
    const found = all<EntityRow>(db, `SELECT ${columns} FROM intel_entities WHERE id = ? LIMIT 1`, wanted)[0]
      ?? all<EntityRow>(db, `SELECT ${columns} FROM intel_entities WHERE name = ? COLLATE NOCASE LIMIT 1`, wanted)[0]
      ?? all<EntityRow>(db, `SELECT ${columns} FROM intel_entities WHERE instr(lower(aliases), ?) > 0 LIMIT 200`, wanted).find((row) => names(row).includes(wanted));
    if (!found) {
      // Names or aliases that hold the words, or are held in them; the aliases' JSON read only where it is valid.
      const similar = all<EntityRow>(
        db, `SELECT ${columns} FROM intel_entities WHERE instr(lower(name), ?) > 0 OR instr(?, lower(name)) > 0 OR instr(lower(aliases), ?) > 0 ` +
          "OR EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(aliases) THEN aliases ELSE '[]' END) WHERE instr(?, lower(value)) > 0) LIMIT 50",
        wanted, wanted, wanted, wanted)
        .filter((row) => names(row).some((value) => value.includes(wanted) || wanted.includes(value))).slice(0, 10).map((row) => row.name);
      const matchingFacts = facts(db, `${CURRENT} AND lower(f.topics || ' ' || f.fact) LIKE ? ORDER BY f.added LIMIT 50`, `%${wanted}%`);
      return {
        project: job.name, referenceOnly: referenceOnly(db), entity: null, query: entity, ...(similar.length ? { similar } : {}), facts: matchingFacts,
        hint: similar.length ? "Not found by that exact name; try one of the similar names." : "Not a known name; try action=search.",
      };
    }
    const allNames = names(found);
    const entityFiles = all<{ file_id: string; count: number }>(db, "SELECT file_id, count FROM intel_entity_files WHERE entity_id = ? ORDER BY count DESC", found.id);
    // Filtered in SQLite, not here: a big job has hundreds of thousands of events (reading them all took about a second).
    const named = allNames.map(() => "instr(lower(title), ?) > 0").join(" OR ");
    const events = all<{ date: string; kind: string; title: string; file_id: string | null }>(
      db, `SELECT date, kind, title, file_id FROM intel_events WHERE file_id IN (SELECT file_id FROM intel_entity_files WHERE entity_id = ?) OR ${named} ORDER BY date LIMIT 50`,
      found.id, ...allNames);
    // Stories about it (its id as subject) or naming it in the title, references or people; at most 10 of 30 steps
    // each, as TBC MCP: one RFI on a real job made a 143,000-character answer.
    const mentions = allNames.map(() => "instr(lower(title), ?) > 0 OR instr(lower(refs), ?) > 0 OR instr(lower(people), ?) > 0").join(" OR ");
    const stories = all<{ id: string; title: string; first_date: string; last_date: string }>(
      db, `SELECT id, title, first_date, last_date FROM intel_stories WHERE subject = ? OR ${mentions} LIMIT 10`,
      found.id, ...allNames.flatMap((value) => [value, value, value]))
      .map((story) => ({
        story,
        stepsTotal: all<{ n: number }>(db, "SELECT count(*) AS n FROM intel_story_steps WHERE story_id = ?", story.id)[0]?.n ?? 0,
        steps: all<{ date: string; kind: string; who: string; what: string; file_id: string }>(
          db, "SELECT date, kind, who, what, file_id FROM intel_story_steps WHERE story_id = ? ORDER BY position LIMIT 30", story.id),
      }));
    const shownFiles = entityFiles.slice(0, 50);
    const paths = filePaths(db, [...shownFiles.map((row) => row.file_id), ...events.map((event) => event.file_id), ...stories.flatMap((story) => story.steps.map((step) => step.file_id))]);
    const like = allNames.map(() => "lower(f.topics) LIKE ?").join(" OR ");
    return {
      project: job.name, referenceOnly: referenceOnly(db),
      entity: {
        id: found.id, type: found.type, name: found.name, aliases: parseJson<string[]>(found.aliases, []),
        files: found.files, mentions: found.mentions, first: found.first_date, last: found.last_date,
      },
      files: shownFiles.map((row) => ({ file: row.file_id, path: pathIn(paths, row.file_id), count: row.count })),
      events: events.map((event) => ({ date: event.date, kind: event.kind, title: event.title, file: pathIn(paths, event.file_id) })),
      stories: stories.map(({ story, stepsTotal, steps }) => ({
        id: story.id, title: story.title, first: story.first_date, last: story.last_date,
        steps: steps.map((step) => ({ date: step.date, kind: step.kind, who: step.who, what: step.what, file: pathIn(paths, step.file_id) })),
        ...(stepsTotal > 30 ? { stepsTotal, stepsHint: "The first 30 steps; action=timeline lists the rest by date." } : {}),
      })),
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
    // The same range as inRange, in SQLite so the intel_events_date index answers it: a date at or after `from`, and
    // one that starts with `to` or sorts before it ("￿" sorts after every character a date has).
    const range = "date IS NOT NULL AND date >= ? AND date <= ?";
    const bounds = [from ?? "", `${to ?? ""}￿`];
    const total = all<{ n: number }>(db, `SELECT count(*) AS n FROM intel_events WHERE ${range}`, ...bounds)[0]?.n ?? 0;
    const shown = Math.max(limit, 1);
    const rows = all<{ date: string; kind: string; title: string; confidence: string; file_id: string | null }>(
      db, `SELECT date, kind, title, confidence, file_id FROM intel_events WHERE ${range} ORDER BY date LIMIT ?`, ...bounds, shown);
    const paths = filePaths(db, rows.map((event) => event.file_id));
    const events = rows.map((event) => ({
      date: event.date, kind: event.kind, title: event.title, ...(event.confidence && event.confidence !== "high" ? { confidence: event.confidence } : {}), file: pathIn(paths, event.file_id),
    }));
    const dated = facts(db, `${CURRENT} AND f.date IS NOT NULL`).filter((fact) => inRange(fact.date ?? null))
      .map((fact) => ({ date: fact.date!, kind: "fact", title: fact.fact, fact: fact.id, tool: fact.tool }));
    const timeline = [...events, ...dated].sort((left, right) => left.date.localeCompare(right.date));
    const count = total + dated.length;
    return {
      project: job.name, referenceOnly: referenceOnly(db), from: from ?? null, to: to ?? null, count,
      timeline: timeline.slice(0, shown), ...(count > limit ? { truncated: true } : {}),
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
    const inCopy = (relative: string | null | undefined) => (relative ? onDisk(db, relative) : undefined);
    type RefRow = { file_id: string; kind: string; status: string; target_path: string | null; expected_path: string | null; candidates: string | null; location: string | null };
    const columns = "file_id, kind, status, target_path, expected_path, candidates, location";
    // By refs.normalized (refs_normalized_nocase answers it), else as written (raw has no index: only when needed).
    const byNormalized = all<RefRow>(db, `SELECT ${columns} FROM refs WHERE normalized = ? COLLATE NOCASE`, wanted);
    const refs = byNormalized.length ? byNormalized : all<RefRow>(db, `SELECT ${columns} FROM refs WHERE raw = ? COLLATE NOCASE`, wanted);
    if (refs.length > 0) {
      const ref = refs.find((candidate) => candidate.target_path) ?? refs[0];
      const target = inCopy(ref.target_path);
      return {
        project: job.name, referenceOnly: referenceOnly(db), written, method: "refs", status: ref.status,
        ...(ref.target_path ? { relative: ref.target_path, path: target, exists: !!target && existsSync(target) } : {}),
        ...(ref.expected_path ? { expected: ref.expected_path } : {}),
        ...(ref.candidates ? { candidates: parseJson<string[]>(ref.candidates, []) } : {}),
        writtenIn: (() => {
          const paths = filePaths(db, refs.map((candidate) => candidate.file_id));
          return [...new Set(refs.map((candidate) => pathIn(paths, candidate.file_id)))];
        })(),
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
        project: job.name, referenceOnly: referenceOnly(db), written, method: "reference_roots", relative, path: target, exists: !!target && existsSync(target),
        hint: "Worked out from where the job was copied from; check it exists before relying on it.",
      };
    }
    return {
      project: job.name, referenceOnly: referenceOnly(db), written, method: null, path: null, ...copyInfo(db),
      hint: "Not found in the references Project Analyzer read, and no known original location matches. Ask the user where it is rather than guess; " +
        "once found, record it as a note (e.g. 'TOPO xref of CAD\\Site.dwg is CAD\\Topo.dwg in the copy').",
    };
  });
}

// ---------------------------------------------------------------- checks and changes

const SEVERITY_ORDER = "CASE severity WHEN 'high' THEN 0 WHEN 'medium' THEN 1 WHEN 'info' THEN 2 ELSE 3 END";
const CHECK_FILES_SHOWN = 10;

export interface ChecksOptions { severity?: string; kind?: string; limit?: number }

/**
 * What Project Analyzer found worth a look (intel_checks: missing files, unanswered RFIs, amounts that don't add up...),
 * high first, then medium, then info, each by kind and title; optionally one severity or kind. Each with the paths of
 * its files (at most 10), plus how many there are of each severity and kind.
 */
export async function projectChecks(name?: string, options: ChecksOptions = {}) {
  const { job } = await resolveProject(name);
  const limit = Math.min(Math.max(Math.floor(options.limit ?? 50), 1), 500);
  return withProjectDb(job.file, ({ db }) => {
    const where: string[] = [];
    const params: string[] = [];
    if (options.severity?.trim()) { where.push("severity = ? COLLATE NOCASE"); params.push(options.severity.trim()); }
    if (options.kind?.trim()) { where.push("kind = ? COLLATE NOCASE"); params.push(options.kind.trim()); }
    const filter = where.length ? `WHERE ${where.join(" AND ")}` : "";
    const counts = (column: "severity" | "kind") => Object.fromEntries(
      all<{ value: string | null; n: number }>(db, `SELECT ${column} AS value, COUNT(*) AS n FROM intel_checks GROUP BY ${column} ORDER BY ${column === "severity" ? SEVERITY_ORDER : "kind"}`)
        .map((row) => [row.value ?? "(none)", row.n]));
    const bySeverity = counts("severity");
    const byKind = counts("kind");
    const count = all<{ n: number }>(db, `SELECT COUNT(*) AS n FROM intel_checks ${filter}`, ...params)[0]?.n ?? 0;
    const rows = all<{ id: string; severity: string | null; kind: string | null; title: string | null; detail: string | null; files: string | null }>(
      db, `SELECT id, severity, kind, title, detail, files FROM intel_checks ${filter} ORDER BY ${SEVERITY_ORDER}, kind, title LIMIT ?`, ...params, limit);
    const fileIds = rows.map((row) => parseJson<unknown[]>(row.files, []).filter((id): id is string => typeof id === "string"));
    const paths = filePaths(db, fileIds.flatMap((ids) => ids.slice(0, CHECK_FILES_SHOWN)));
    const total = Object.values(bySeverity).reduce((sum, n) => sum + n, 0);
    return {
      project: job.name, referenceOnly: referenceOnly(db),
      ...(options.severity ? { severity: options.severity } : {}), ...(options.kind ? { kind: options.kind } : {}),
      count, bySeverity, byKind,
      checks: rows.map((row, at) => ({
        id: row.id, severity: row.severity, kind: row.kind, title: row.title, ...(row.detail ? { detail: row.detail } : {}),
        files: fileIds[at].slice(0, CHECK_FILES_SHOWN).map((id) => pathIn(paths, id)),
        ...(fileIds[at].length > CHECK_FILES_SHOWN ? { moreFiles: fileIds[at].length - CHECK_FILES_SHOWN } : {}),
      })),
      ...(count > rows.length ? { truncated: true } : {}),
      hint: total === 0
        ? "No checks: Project Analyzer makes them when it reads the job folder (analyze.py intel, also at the end of every run)."
        : "Checks are what Project Analyzer found worth a look, not decisions: tell the user about the ones that matter for the task. " +
          "action=file (a path) shows a file; severity and kind filter the list.",
    };
  });
}

/** YYYY-MM-DD of the day `days` days before today (local). */
const daysAgo = (days: number) => {
  const at = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
  return `${at.getFullYear()}-${String(at.getMonth() + 1).padStart(2, "0")}-${String(at.getDate()).padStart(2, "0")}`;
};

export interface ChangesOptions { since?: string; file?: string; limit?: number }

/**
 * What changed: requirement changes ("going forward", "no longer", "revised spec": intel_events of kind change, dated
 * by the email or document that says them) and changes to files the analyzer noticed on an update (file_changes),
 * newest first. Since a date (default: 30 days back, unless a file is given) and/or for one file.
 */
export async function projectChanges(name?: string, options: ChangesOptions = {}) {
  const { job } = await resolveProject(name);
  if (options.since && !/^\d{4}(-\d{2}(-\d{2})?)?$/.test(options.since)) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", `Dates are YYYY, YYYY-MM or YYYY-MM-DD, not '${options.since}'.`);
  }
  const since = options.since ?? (options.file ? undefined : daysAgo(30));
  const limit = Math.min(Math.max(Math.floor(options.limit ?? 50), 1), 500);
  return withProjectDb(job.file, ({ db }) => {
    const target = options.file ? findFile(db, options.file) : undefined;
    const eventWhere = ["kind = 'change'", ...(since ? ["date >= ?"] : []), ...(target ? ["file_id = ?"] : [])].join(" AND ");
    const changeWhere = [...(since ? ["seen >= ?"] : []), ...(target ? ["file_id = ?"] : [])].join(" AND ") || "1";
    const params = [...(since ? [since] : []), ...(target ? [target.id] : [])];
    const eventCount = all<{ n: number }>(db, `SELECT COUNT(*) AS n FROM intel_events WHERE ${eventWhere}`, ...params)[0]?.n ?? 0;
    const events = all<{ date: string; title: string; confidence: string | null; file_id: string | null; location: string | null }>(
      db, `SELECT date, title, confidence, file_id, location FROM intel_events WHERE ${eventWhere} ORDER BY date DESC LIMIT ?`, ...params, limit);
    const fileCount = all<{ n: number }>(db, `SELECT COUNT(*) AS n FROM file_changes WHERE ${changeWhere}`, ...params)[0]?.n ?? 0;
    const fileChanges = all<{ file_id: string; path: string; change: string; seen: string; mtime: string | null; old_path: string | null; summary: string | null }>(
      db, `SELECT file_id, path, change, seen, mtime, old_path, summary FROM file_changes WHERE ${changeWhere} ORDER BY seen DESC, id DESC LIMIT ?`, ...params, limit);
    const paths = filePaths(db, events.map((event) => event.file_id));
    return {
      project: job.name, referenceOnly: referenceOnly(db), since: since ?? null, ...(target ? { file: target.path } : {}),
      count: { changes: eventCount, fileChanges: fileCount },
      changes: events.map((event) => ({
        date: event.date, title: event.title, file: pathIn(paths, event.file_id),
        ...(event.location ? { where: event.location } : {}), ...(event.confidence && event.confidence !== "high" ? { confidence: event.confidence } : {}),
      })),
      fileChanges: fileChanges.map((row) => ({
        seen: row.seen, change: row.change, path: row.path, ...(row.old_path ? { oldPath: row.old_path } : {}),
        ...(row.mtime ? { modified: row.mtime } : {}), ...(row.summary ? { summary: row.summary } : {}),
      })),
      ...(eventCount > events.length || fileCount > fileChanges.length ? { truncated: true } : {}),
      hint: eventCount + fileCount === 0
        ? `No changes ${since ? `since ${since}` : "recorded"}${target ? ` for ${target.path}` : ""}. Project Analyzer finds them when it reads or updates the job folder; ` +
          "pass since (YYYY[-MM[-DD]]) to look further back."
        : "changes are requirement changes found by their wording (the newest one about a thing is the one to follow, unless a fact says otherwise); " +
          "fileChanges are what an update of the job folder found (changed in place, removed, moved). Tell the user about the ones that matter for the task.",
    };
  });
}

// ---------------------------------------------------------------- the latest version of a file

// Contract: intel_series (project-analyzer's analyzer/series.py). A series is the files on the drive of one kind whose
// names are the same once dates, "rev N", "current", "copy" and the like are taken out; the latest is the one modified
// last. The key below is drawing_history.series_key without the title block, so lookups match the analyzer's.
const SERIES_REMOVE = /\b(?:\d{8}|\d{6}|\d{2}[-_ ]\d{2}[-_ ]\d{2,4}|current|copy|backup|bak|old|new|test|void|draft|final|rev(?:ision)?\s*\d*|v\d+|to)\b|\(\d+\)/gi;

/** 'C-101 rev2 (2).dwg' -> 'c 101'; the analyzer's series key of a file name. */
export function seriesKey(fileName: string): string {
  const stem = path.win32.parse(fileName.replace(/\//g, "\\")).name;
  const key = stem.replace(SERIES_REMOVE, " ").replace(/[\s_.-]+/g, " ").trim().toLowerCase();
  return key || stem.toLowerCase();
}

const wordsOf = (text: string) => text.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim().split(" ").filter(Boolean);

interface SeriesRow {
  series_id: string; key: string; kind: string; position: number; file_id: string; path: string; modified: string | null;
  title_date: string | null; revision: string | null; latest: number; note: string | null;
}

/**
 * The series a name or path points to (an exact path, a file name at the end of a path, the series key of the name, a
 * key starting with it), drawings first. `near`: words about the folder ("phase 2 area b"); only members whose path
 * holds all of them as whole words are kept, and the latest is the newest of those.
 */
export async function projectLatest(name: string, project?: string, near?: string) {
  const { job } = await resolveProject(project);
  const wanted = name.trim().replace(/^"|"$/g, "").replace(/\//g, "\\");
  if (!wanted) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give a file name or path, e.g. C-101 or Drawings\\C-101.dwg.");
  return withProjectDb(job.file, ({ db }) => {
    const hasTable = all<{ name: string }>(db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'intel_series'").length > 0;
    if (!hasTable) {
      return { project: job.name, referenceOnly: referenceOnly(db), name, series: [], hint: "This job's database has no version list yet: it is built by Project Analyzer (analyze.py intel, also at the end of every run). Ask the user, or look in the job folder." };
    }
    const key = seriesKey(wanted.split("\\").pop() ?? wanted);
    // Case-insensitive as `= ? COLLATE NOCASE` / LIKE, so intel_series_key_nocase answers the key lookups.
    const tries: Array<[string, string]> = [
      ["SELECT DISTINCT series_id FROM intel_series WHERE path = ? COLLATE NOCASE", wanted],
      ["SELECT DISTINCT series_id FROM intel_series WHERE path LIKE ? ESCAPE '!'", `%\\${escapeLike(wanted)}`],
      ["SELECT DISTINCT series_id FROM intel_series WHERE key = ? COLLATE NOCASE", key],
      ["SELECT DISTINCT series_id FROM intel_series WHERE key LIKE ? ESCAPE '!'", `${escapeLike(key)}%`],
    ];
    let found: SeriesRow[][] = [];
    for (const [sql, value] of tries) {
      const ids = all<{ series_id: string }>(db, sql, value).map((row) => row.series_id).slice(0, 20);
      if (ids.length) {
        // Every member of every series found, in one query.
        const members = all<SeriesRow>(db, `SELECT * FROM intel_series WHERE series_id IN (${ids.map(() => "?").join(", ")}) ORDER BY series_id, position`, ...ids);
        const bySeries = new Map(ids.map((id) => [id, [] as SeriesRow[]]));
        for (const member of members) bySeries.get(member.series_id)?.push(member);
        found = [...bySeries.values()].filter((group) => group.length > 0);
        break;
      }
    }
    const hints = near ? wordsOf(near) : [];
    if (hints.length) {
      found = found.map((members) => members.filter((member) => {
        const have = new Set(wordsOf(member.path));
        return hints.every((hint) => have.has(hint));
      })).filter((members) => members.length > 0);
    }
    found.sort((left, right) => Number(left[0].kind !== "cad") - Number(right[0].kind !== "cad") || left[0].key.localeCompare(right[0].key));
    const describeMember = (member: SeriesRow) => {
      const full = onDisk(db, member.path);
      return {
        path: member.path, fullPath: full, exists: !!full && existsSync(full), modified: member.modified?.slice(0, 10) ?? null,
        ...(member.title_date ? { titleBlockDate: member.title_date } : {}), ...(member.revision ? { revision: member.revision } : {}),
      };
    };
    const series = found.map((members) => {
      const latest = members[members.length - 1];
      const folders = new Set(members.map((member) => path.win32.dirname(member.path).toLowerCase()));
      return {
        key: latest.key, kind: latest.kind, versions: members.length, latest: describeMember(latest),
        ...(latest.note && !hints.length ? { note: latest.note } : {}),
        older: members.slice(0, -1).reverse().slice(0, 10).map(describeMember),
        ...(folders.size > 1 ? { folders: folders.size } : {}),
      };
    });
    return {
      project: job.name, referenceOnly: referenceOnly(db), name, ...(near ? { near } : {}), series,
      hint: series.length === 0
        ? "No file by that name in the job's database. Ask the user, or check list_open in civil3d_drawing."
        : "Drawings already open come first (civil3d_drawing list_open). The latest is the one modified last. When one name is in several " +
          "folders (folders > 1) that look like different areas (phases, areas, sites) rather than dated deliveries of one drawing, " +
          "ask which, or pass near with the user's words about the folder.",
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
  userKnowledge: { documents: number; names?: string[] };
  referenceOnly: string;
  hint: string;
}

export async function projectNotesOnce(): Promise<ProjectBriefing | undefined> {
  try {
    const job = projectForDrawing(await activeDrawingPath());
    if (!job || announced.has(job.file)) return undefined;
    const briefing = await withProjectDb(job.file, ({ db }): ProjectBriefing => {
      const knowledge = listFolder(ensureUserKnowledgeDirectory(job.file)).files;
      return {
        project: job.name, referenceOnly: referenceOnly(db),
        keyFacts: facts(db, `${CURRENT} AND f.key = 1 AND ${NOT_SUPERSEDED} ORDER BY f.added, f.id LIMIT 15`).map((fact) => `[${fact.id}] ${fact.fact}`),
        notes: Number((db.prepare(`SELECT COUNT(*) AS n FROM facts f WHERE ${CURRENT} AND f.tool = ?`).get(TOOL) as { n: number }).n),
        highChecks: all<{ title: string }>(db, "SELECT title FROM intel_checks WHERE severity = 'high' ORDER BY id LIMIT 10").map((row) => row.title),
        missingFiles: Number(all<{ n: number }>(db, "SELECT COUNT(DISTINCT normalized COLLATE NOCASE) AS n FROM refs WHERE status = 'missing'")[0]?.n ?? 0),
        userKnowledge: { documents: knowledge.length, ...(knowledge.length ? { names: knowledge.slice(0, 10).map((entry) => entry.path) } : {}) },
        hint: "civil3d_project_notes action=overview for everything known about this job (facts, questions, rules, checks, people, practices). " +
          (knowledge.length
            ? `userKnowledge lists the user's own documents for this job (guidelines, requirements, standards): read them with action=read. ${USE_USER_KNOWLEDGE}`
            : "The user can add their own documents for this job (guidelines, requirements, standards) to its User Knowledge folder (action=files)."),
      };
    });
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
