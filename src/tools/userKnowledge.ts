import { existsSync, mkdirSync, readdirSync, readFileSync, realpathSync, statSync } from "node:fs";
import path from "node:path";
import type { DatabaseSync } from "node:sqlite";
import zlib from "node:zlib";

/**
 * A job's User Knowledge folder, <knowledge dir>\projects\<slug>\User Knowledge (contract: project-analyzer's
 * docs/suite-knowledge.md, "User knowledge (per job)"): documents the user adds for one job, such as proposal
 * guidelines, building-type requirements, a client's standards or code books. Civil 3D MCP only creates the folder
 * (empty) and reads it; it never writes, moves or deletes anything there.
 *
 *   listFolder, legacyFiles            what is in the folder, and files older versions put next to it
 *   analyzerDocuments, readByAnalyzer  which documents Project Analyzer already read into the project database
 *   extractText                        plain text, and .docx / .xlsx / .pptx from their XML (a small zip reader below)
 *
 * Uses Node built-ins only.
 */

export const USER_KNOWLEDGE = "User Knowledge";
/** How Project Analyzer writes a user-knowledge document's path in `files.path`. */
export const USER_KNOWLEDGE_PREFIX = "<user knowledge>\\";

/** <knowledge dir>\projects\<slug> for a job database <slug>.db. */
export const jobDirectoryOf = (databaseFile: string) => databaseFile.replace(/\.db$/i, "");
export const userKnowledgeDirectoryOf = (databaseFile: string) => path.join(jobDirectoryOf(databaseFile), USER_KNOWLEDGE);

/** Creates the (empty) User Knowledge folder when it doesn't exist; the only thing this tool ever writes there. */
export function ensureUserKnowledgeDirectory(databaseFile: string): string {
  const dir = userKnowledgeDirectoryOf(databaseFile);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  return dir;
}

// ---------------------------------------------------------------- listing

export interface FolderEntry {
  /** Relative to the folder, with backslashes like the database: "Guidelines\Proposal guide.pdf". */
  path: string;
  absolute: string;
  size: number;
  modified: string;
  mtimeMs: number;
}

// OS litter and Office lock files, the same ones Project Analyzer ignores.
const LITTER = new Set(["thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db"]);
const isLitter = (name: string) => LITTER.has(name.toLowerCase()) || name.startsWith("~$") || name.startsWith("._");

const toStoredPath = (relative: string) => relative.split(/[\\/]+/).join("\\");

/** Files under `dir` (subfolders included), sorted by path; `skipTop` names top-level folders to leave out. */
export function listFolder(dir: string, options: { limit?: number; skipTop?: string } = {}): { files: FolderEntry[]; truncated: boolean } {
  const limit = options.limit ?? 500;
  const files: FolderEntry[] = [];
  let truncated = false;
  const walk = (current: string, top: boolean) => {
    let entries;
    try { entries = readdirSync(current, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      if (files.length >= limit) { truncated = true; return; }
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!(top && options.skipTop && entry.name.toLowerCase() === options.skipTop.toLowerCase())) walk(full, false);
        continue;
      }
      if (!entry.isFile() || isLitter(entry.name)) continue;
      try {
        const stat = statSync(full);
        files.push({ path: toStoredPath(path.relative(dir, full)), absolute: full, size: stat.size, modified: stat.mtime.toISOString(), mtimeMs: stat.mtimeMs });
      } catch { /* removed while listing */ }
    }
  };
  if (existsSync(dir)) walk(dir, true);
  files.sort((a, b) => a.path.localeCompare(b.path));
  return { files, truncated };
}

/** Files put directly in <slug>\ by versions before User Knowledge existed (they aren't read any more). */
export const legacyFiles = (databaseFile: string) => listFolder(jobDirectoryOf(databaseFile), { limit: 200, skipTop: USER_KNOWLEDGE });

// ---------------------------------------------------------------- what Project Analyzer read

export interface AnalyzerDocument { id: string; path: string; size: number | null; mtime: string | null; status: string; error: string | null }

/** The database's user-knowledge rows by lower-case relative path (empty when there are none or no files table). */
export function analyzerDocuments(db: DatabaseSync): Map<string, AnalyzerDocument> {
  const found = new Map<string, AnalyzerDocument>();
  try {
    const rows = db.prepare("SELECT id, path, size, mtime, status, error FROM files WHERE substr(path, 1, ?) = ?")
      .all(USER_KNOWLEDGE_PREFIX.length, USER_KNOWLEDGE_PREFIX) as unknown as AnalyzerDocument[];
    for (const row of rows) found.set(row.path.slice(USER_KNOWLEDGE_PREFIX.length).toLowerCase(), row);
  } catch { /* not a project database Project Analyzer wrote to */ }
  return found;
}

/** Whole epoch seconds of a stored time ("2026-10-20T12:00:00+00:00", "...Z", fractions of any length). */
export function epochSeconds(value: string | null | undefined): number | undefined {
  if (!value) return undefined;
  const parsed = Date.parse(value.trim().replace(/(\.\d{3})\d+/, "$1"));
  return Number.isNaN(parsed) ? undefined : Math.floor(parsed / 1000);
}

/** True when the database has this document as it is now (same size, same modified time in whole seconds). */
export function readByAnalyzer(entry: FolderEntry, row: AnalyzerDocument | undefined): boolean {
  return !!row && row.size === entry.size && epochSeconds(row.mtime) === Math.floor(entry.mtimeMs / 1000);
}

// ---------------------------------------------------------------- reading directly

export const MAX_DIRECT_BYTES = 50 * 1024 * 1024;
const MAX_ENTRY_BYTES = 64 * 1024 * 1024;

const PLAIN_TEXT = new Set([
  ".txt", ".text", ".md", ".markdown", ".rst", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".yaml", ".yml",
  ".ini", ".cfg", ".conf", ".log",
]);
const OFFICE = new Set([".docx", ".xlsx", ".pptx"]);

const extensionOf = (name: string) => path.extname(name).toLowerCase();

/** Whether this tool can read the document itself (format and size). */
export const readableHere = (name: string, size: number) =>
  size <= MAX_DIRECT_BYTES && (PLAIN_TEXT.has(extensionOf(name)) || OFFICE.has(extensionOf(name)));

export interface TextPart { label: string | null; text: string }

/** The document's text in parts (sheets, slides), or undefined for a format this tool can't read. */
export function extractText(file: string): TextPart[] | undefined {
  const extension = extensionOf(file);
  if (PLAIN_TEXT.has(extension)) {
    const text = decodeText(readFileSync(file));
    return [{ label: null, text: extension === ".html" || extension === ".htm" ? stripHtml(text) : text }];
  }
  if (!OFFICE.has(extension)) return undefined;
  const zip = readZip(readFileSync(file));
  if (extension === ".docx") return [{ label: null, text: wordText(zip) }];
  if (extension === ".xlsx") return excelText(zip);
  return powerPointText(zip);
}

/** Parts joined the way the project database's text is shown: '## <label>' before each labelled part. */
export const joinParts = (parts: TextPart[]) => parts.map((part) => (part.label ? `## ${part.label}\n\n${part.text}` : part.text)).join("\n\n");

function decodeText(bytes: Buffer): string {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return bytes.subarray(2).toString("utf16le");
  if (bytes[0] === 0xfe && bytes[1] === 0xff) return Buffer.from(bytes.subarray(2)).swap16().toString("utf16le");
  return bytes.toString("utf8").replace(/^﻿/, "");
}

function stripHtml(html: string): string {
  return decodeXml(html.replace(/<(script|style)\b[\s\S]*?<\/\1>/gi, " ").replace(/<br\s*\/?>|<\/(p|div|li|tr|h\d)>/gi, "\n").replace(/<[^>]+>/g, " "))
    .replace(/[ \t]+/g, " ").replace(/ *\n */g, "\n").replace(/\n{3,}/g, "\n\n").trim();
}

function decodeXml(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (whole, entity: string) => {
    const lower = entity.toLowerCase();
    if (lower.startsWith("#x")) return String.fromCodePoint(parseInt(lower.slice(2), 16));
    if (lower.startsWith("#")) return String.fromCodePoint(parseInt(lower.slice(1), 10));
    return ({ amp: "&", lt: "<", gt: ">", quot: "\"", apos: "'" } as Record<string, string>)[lower] ?? whole;
  });
}

const tidy = (text: string) => text.replace(/[ \t]+\n/g, "\n").replace(/\n{3,}/g, "\n\n").trim();

// ---------------------------------------------------------------- zip (Office files)

type Zip = Map<string, () => Buffer>;

/**
 * The entries of a zip file (Office Open XML is one), each read on demand: end of central directory record,
 * central directory entries, local headers, stored or deflated data. Zip64 and encrypted entries are refused;
 * an entry may not unpack to more than 64 MB.
 */
export function readZip(buffer: Buffer): Zip {
  let end = -1;
  for (let at = buffer.length - 22; at >= Math.max(0, buffer.length - 22 - 0xffff); at -= 1) {
    if (buffer.readUInt32LE(at) === 0x06054b50) { end = at; break; }
  }
  if (end < 0) throw new Error("not a zip file (no end of central directory)");
  const count = buffer.readUInt16LE(end + 10);
  const directoryOffset = buffer.readUInt32LE(end + 16);
  if (count === 0xffff || directoryOffset === 0xffffffff) throw new Error("zip64 files aren't supported");
  const entries: Zip = new Map();
  let at = directoryOffset;
  for (let index = 0; index < count; index += 1) {
    if (at + 46 > buffer.length || buffer.readUInt32LE(at) !== 0x02014b50) throw new Error("damaged zip central directory");
    const flags = buffer.readUInt16LE(at + 8);
    const method = buffer.readUInt16LE(at + 10);
    const compressedSize = buffer.readUInt32LE(at + 20);
    const size = buffer.readUInt32LE(at + 24);
    const nameLength = buffer.readUInt16LE(at + 28);
    const next = at + 46 + nameLength + buffer.readUInt16LE(at + 30) + buffer.readUInt16LE(at + 32);
    const localHeader = buffer.readUInt32LE(at + 42);
    const name = buffer.toString(flags & 0x800 ? "utf8" : "latin1", at + 46, at + 46 + nameLength);
    entries.set(name, () => {
      if (flags & 0x1) throw new Error(`${name} is encrypted`);
      if (size > MAX_ENTRY_BYTES) throw new Error(`${name} is too large (${size} bytes)`);
      if (localHeader + 30 > buffer.length || buffer.readUInt32LE(localHeader) !== 0x04034b50) throw new Error(`damaged zip entry ${name}`);
      const start = localHeader + 30 + buffer.readUInt16LE(localHeader + 26) + buffer.readUInt16LE(localHeader + 28);
      const data = buffer.subarray(start, start + compressedSize);
      if (method === 0) return Buffer.from(data);
      if (method === 8) return zlib.inflateRawSync(data, { maxOutputLength: MAX_ENTRY_BYTES });
      throw new Error(`${name} uses an unsupported compression method (${method})`);
    });
    at = next;
  }
  return entries;
}

const entryText = (zip: Zip, name: string) => zip.get(name)?.().toString("utf8");

const attributes = (tag: string) =>
  Object.fromEntries([...tag.matchAll(/([\w:]+)\s*=\s*"([^"]*)"/g)].map((match) => [match[1], decodeXml(match[2])]));

/** Relationship id -> part name, from an OPC .rels part (targets resolved against the part's folder). */
function relationships(zip: Zip, part: string): Map<string, string> {
  const folder = path.posix.dirname(part);
  const rels = entryText(zip, path.posix.join(folder, "_rels", `${path.posix.basename(part)}.rels`)) ?? "";
  const found = new Map<string, string>();
  for (const match of rels.matchAll(/<Relationship\b[^>]*>/g)) {
    const { Id, Target } = attributes(match[0]);
    if (Id && Target) found.set(Id, Target.startsWith("/") ? Target.slice(1) : path.posix.normalize(path.posix.join(folder, Target)));
  }
  return found;
}

const numbered = (zip: Zip, pattern: RegExp) =>
  [...zip.keys()].filter((name) => pattern.test(name)).sort((a, b) => Number(a.match(/(\d+)\.xml$/)?.[1]) - Number(b.match(/(\d+)\.xml$/)?.[1]));

function wordText(zip: Zip): string {
  const xml = entryText(zip, "word/document.xml");
  if (xml === undefined) throw new Error("not a Word document (no word/document.xml)");
  // A table row becomes one line, its cells separated by tabs (\u0000 marks a cell's end until then).
  const pieces: string[] = [];
  for (const match of xml.matchAll(/<w:t(?:\s[^>]*)?>([\s\S]*?)<\/w:t>|<w:tab\/>|<w:(?:br|cr)\b[^>]*\/>|<\/w:p>|<\/w:tc>|<\/w:tr>/g)) {
    if (match[1] !== undefined) pieces.push(decodeXml(match[1]));
    else if (match[0] === "<w:tab/>") pieces.push("\t");
    else if (match[0] === "</w:tc>") pieces.push("\u0000");
    else pieces.push("\n");
  }
  return tidy(pieces.join("").replace(/\n?\u0000/g, "\t"));
}

const columnIndex = (reference: string | undefined) =>
  (reference?.match(/^[A-Z]+/i)?.[0].toUpperCase() ?? "").split("").reduce((sum, letter) => sum * 26 + letter.charCodeAt(0) - 64, 0) - 1;

const runText = (xml: string) => [...xml.replace(/<rPh\b[\s\S]*?<\/rPh>/g, "").matchAll(/<t(?:\s[^>]*)?>([\s\S]*?)<\/t>/g)].map((match) => decodeXml(match[1])).join("");

function excelText(zip: Zip): TextPart[] {
  const workbook = entryText(zip, "xl/workbook.xml");
  if (workbook === undefined) throw new Error("not an Excel workbook (no xl/workbook.xml)");
  const shared = [...(entryText(zip, "xl/sharedStrings.xml") ?? "").matchAll(/<si>([\s\S]*?)<\/si>/g)].map((match) => runText(match[1]));
  const rels = relationships(zip, "xl/workbook.xml");
  const sheets = [...workbook.matchAll(/<sheet\b[^>]*>/g)].map((match) => attributes(match[0]))
    .map((sheet) => ({ name: sheet.name ?? "Sheet", part: rels.get(sheet["r:id"] ?? "") }))
    .filter((sheet): sheet is { name: string; part: string } => !!sheet.part && zip.has(sheet.part));
  const list = sheets.length ? sheets : numbered(zip, /^xl\/worksheets\/sheet\d+\.xml$/).map((part, index) => ({ name: `Sheet${index + 1}`, part }));
  return list.map((sheet) => {
    const xml = entryText(zip, sheet.part) ?? "";
    const lines: string[] = [];
    for (const row of xml.matchAll(/<row\b[^>]*?(?:\/>|>([\s\S]*?)<\/row>)/g)) {
      const cells: string[] = [];
      for (const cell of (row[1] ?? "").matchAll(/<c\b([^>]*?)(?:\/>|>([\s\S]*?)<\/c>)/g)) {
        const { r, t } = attributes(cell[1]);
        const inner = cell[2] ?? "";
        const value = inner.match(/<v>([\s\S]*?)<\/v>/)?.[1];
        let text = "";
        if (t === "s" && value !== undefined) text = shared[Number(value)] ?? "";
        else if (t === "inlineStr") text = runText(inner);
        else if (t === "b" && value !== undefined) text = value === "1" ? "TRUE" : "FALSE";
        else if (value !== undefined) text = decodeXml(value);
        const column = r ? columnIndex(r) : cells.length;
        while (cells.length < column) cells.push("");
        cells[column >= 0 ? column : cells.length] = text.replace(/\s+/g, " ").trim();
      }
      if (cells.some((cell) => cell)) lines.push(cells.join("\t").replace(/\t+$/, ""));
    }
    return { label: sheet.name, text: lines.join("\n") };
  });
}

function powerPointText(zip: Zip): TextPart[] {
  const presentation = entryText(zip, "ppt/presentation.xml");
  if (presentation === undefined) throw new Error("not a PowerPoint presentation (no ppt/presentation.xml)");
  const rels = relationships(zip, "ppt/presentation.xml");
  const ordered = [...presentation.matchAll(/<p:sldId\b[^>]*>/g)].map((match) => rels.get(attributes(match[0])["r:id"] ?? ""))
    .filter((part): part is string => !!part && zip.has(part));
  const slides = ordered.length ? ordered : numbered(zip, /^ppt\/slides\/slide\d+\.xml$/);
  return slides.map((part, index) => {
    const xml = entryText(zip, part) ?? "";
    const pieces: string[] = [];
    for (const match of xml.matchAll(/<a:t>([\s\S]*?)<\/a:t>|<a:br\b[^>]*\/>|<\/a:p>/g)) {
      pieces.push(match[1] !== undefined ? decodeXml(match[1]) : "\n");
    }
    return { label: `Slide ${index + 1}`, text: tidy(pieces.join("")) };
  });
}

// ---------------------------------------------------------------- paths

/**
 * The absolute path of a document named relative to the folder (with or without the '<user knowledge>\' prefix) or
 * by an absolute path, or undefined when it lies outside the folder (also through a link or junction).
 */
export function insideFolder(folder: string, file: string): string | undefined {
  let wanted = file.trim();
  if (wanted.toLowerCase().startsWith(USER_KNOWLEDGE_PREFIX)) wanted = wanted.slice(USER_KNOWLEDGE_PREFIX.length);
  const absolute = path.isAbsolute(wanted) || path.win32.isAbsolute(wanted)
    ? path.resolve(wanted)
    : path.resolve(folder, wanted.split(/[\\/]+/).join(path.sep));
  const contained = (root: string, target: string) => {
    const relative = path.relative(root, target);
    return !!relative && relative !== ".." && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
  };
  if (!contained(folder, absolute)) return undefined;
  if (existsSync(absolute)) {
    try {
      if (!contained(realpathSync(folder), realpathSync(absolute))) return undefined;
    } catch { /* checked by the caller when reading */ }
  }
  return absolute;
}
