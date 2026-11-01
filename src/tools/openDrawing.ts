import { statSync } from "node:fs";
import path from "node:path";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { knowledgeError } from "./knowledgeBase.js";
import { jobByName, jobForPath } from "./projectDb.js";
import { projectLatest, seriesKey } from "./projectNotes.js";

/**
 * civil3d_drawing action=open: open a drawing by name the way a person means it. In order:
 *   1. a drawing already open in this Civil 3D window that matches the name (file name, name without .dwg, or the
 *      same series, e.g. "C-101" for "C-101 rev2.dwg"): it becomes the active tab; nothing is read from disk;
 *   2. a full path given by the caller;
 *   3. the latest version in the job's database (civil3d_project_notes latest): the DWG modified last, or among the
 *      files whose folder holds the `near` words.
 * The plugin opens it read-only when asked or when someone else has it open. The result says which file was used and
 * why, the other versions, and, when an open drawing was used, whether a newer version is on the drive.
 */

interface OpenDrawing { name: string; path: string; active: boolean }

let openDrawingsForTesting: (() => Promise<OpenDrawing[]>) | undefined;
let pluginForTesting: ((method: string, params: Record<string, unknown>) => Promise<unknown>) | undefined;

/** For tests: the open drawings and the plugin calls (undefined = ask Civil 3D). */
export function setOpenDrawingHooksForTesting(hooks: { open?: () => Promise<OpenDrawing[]>; plugin?: (method: string, params: Record<string, unknown>) => Promise<unknown> } | undefined) {
  openDrawingsForTesting = hooks?.open;
  pluginForTesting = hooks?.plugin;
}

async function plugin(method: string, params: Record<string, unknown>): Promise<unknown> {
  if (pluginForTesting) return pluginForTesting(method, params);
  return withApplicationConnection(async (client) => await client.sendCommand(method, params));
}

async function openDrawings(): Promise<OpenDrawing[]> {
  if (openDrawingsForTesting) return openDrawingsForTesting();
  const result = await plugin("listOpenDrawings", {}) as { drawings?: OpenDrawing[] } | undefined;
  return result?.drawings ?? [];
}

const words = (text: string) => text.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim().split(" ").filter(Boolean);

function matchesOpen(drawing: OpenDrawing, wanted: string, near: string[]): boolean {
  const file = path.win32.basename(drawing.path || drawing.name);
  const stem = path.win32.parse(file).name.toLowerCase();
  const asked = path.win32.basename(wanted);
  const askedStem = path.win32.parse(asked).name.toLowerCase();
  const sameName = file.toLowerCase() === asked.toLowerCase() || stem === askedStem || seriesKey(file) === seriesKey(asked);
  if (!sameName) return false;
  const have = new Set(words(drawing.path || drawing.name));
  return near.every((word) => have.has(word));
}

export interface OpenArgs { name?: string; path?: string; near?: string; project?: string; readOnly?: boolean }

// A folder named for a date or revision: 'Packages\2026-02', '25-10\10-29 grading', 'Rev 3', '20260214 delivery'.
const DATED_FOLDER = /\d{4}-\d{2}|\d{1,2}-\d{1,2}(-\d{2,4})?|\b\d{6,8}\b|\brev(ision)?\b/i;

/**
 * True when the same name in several folders may be different drawings (each phase's own C-109) rather than dated
 * deliveries of one drawing, which is what "the latest" is for. The folders that differ decide.
 */
function differentAreas(paths: string[]): boolean {
  const folders = [...new Set(paths.map((file) => path.win32.dirname(file).toLowerCase()))];
  return folders.length > 1 && folders.some((folder) => !DATED_FOLDER.test(path.win32.basename(folder)));
}

/** True when the file on the drive was saved after the open drawing's, and is really there. */
function savedLater(file: string, open: string): boolean {
  try {
    return statSync(file).mtimeMs > statSync(open).mtimeMs;
  } catch {
    return false;   // one of them isn't on this PC: nothing to say about which is newer
  }
}

export async function openDrawingByName(args: OpenArgs) {
  const wanted = (args.path ?? args.name ?? "").trim().replace(/^"|"$/g, "");
  if (!wanted) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give name (e.g. C-101) or path (a full .dwg path).");
  const near = args.near ? words(args.near) : [];

  // 1. Already open: the user has usually opened what matters.
  const open = await openDrawings();
  const isFullPath = /^[a-z]:\\|^\\\\/i.test(wanted.replace(/\//g, "\\"));
  // A job named by the caller limits the open drawings too: another job's C-104 may be open with the same name.
  const onlyJob = args.project ? jobByName(args.project) : undefined;
  const openMatches = open.filter((drawing) => isFullPath
    ? (drawing.path || "").toLowerCase() === wanted.replace(/\//g, "\\").toLowerCase()
    : matchesOpen(drawing, wanted, near) && (!onlyJob || jobForPath(drawing.path)?.job.file === onlyJob.file));
  if (openMatches.length > 1) {
    return {
      opened: null, reason: "several open drawings match", choices: openMatches.map((drawing) => drawing.path || drawing.name),
      hint: "Ask the user which one, then switch to it (action=switch with its full path).",
    };
  }
  let newer: { path: string; modified: string | null } | undefined;
  const latestFor = async () => {
    try {
      return await projectLatest(wanted, args.project, args.near);
    } catch {
      return undefined;   // no job database for this drawing: fine when it's open already
    }
  };
  if (openMatches.length === 1) {
    const drawing = openMatches[0];
    const switched = await plugin("activateDrawing", { name: drawing.path || drawing.name });
    const latest = isFullPath ? undefined : await latestFor();
    const series = latest?.series.find((candidate) => candidate.kind === "cad");
    const newest = series?.latest;
    // Newer only if it really is: the same drawing elsewhere (a phase's own C-109) isn't a newer version of this one,
    // the database is a snapshot, and a drawing saved since it was made is what's newest.
    const spread = series ? differentAreas([series.latest.path, ...series.older.map((member) => member.path)]) : false;
    if (newest?.fullPath && newest.exists && !spread && drawing.path
      && newest.fullPath.toLowerCase() !== drawing.path.toLowerCase() && savedLater(newest.fullPath, drawing.path)) {
      newer = { path: newest.fullPath, modified: newest.modified };
    }
    return {
      opened: drawing.path || drawing.name, reason: "already open (open drawings come first)", switched,
      ...(newer ? { newerOnDrive: newer, hint: "Use the open drawing; tell the user once that a newer version is on the drive." } : {}),
    };
  }

  // 2. A full path.
  if (isFullPath) {
    const result = await plugin("openDrawing", { path: wanted, readOnly: args.readOnly ?? false });
    return { opened: wanted, reason: "the path given", result };
  }

  // 3. The latest version in the job's database.
  const latest = await projectLatest(wanted, args.project, args.near);
  const drawings = latest.series.filter((series) => series.kind === "cad" && /\.(dwg|dxf|dwt)$/i.test(series.latest.path));
  if (drawings.length === 0) {
    return { opened: null, reason: "no drawing by that name", project: latest.project, referenceOnly: latest.referenceOnly, otherFiles: latest.series.map((series) => series.latest.path), hint: latest.hint };
  }
  if (drawings.length > 1) {
    return {
      opened: null, reason: "several drawings match", project: latest.project,
      choices: drawings.map((series) => ({ latest: series.latest.path, modified: series.latest.modified, versions: series.versions })),
      hint: "Ask the user which one (or pass near with words about its folder).",
    };
  }
  const series = drawings[0];
  // A series holds .dwg, .dxf and .dwt alike: "open C-104" means the drawing, not a newer export of it.
  const dwg = /\.dwg$/i.test(series.latest.path) ? undefined : series.older.find((member) => /\.dwg$/i.test(member.path));
  const chosen = dwg ? { ...series, latest: dwg, older: series.older.filter((member) => member !== dwg) } : series;
  // The same name in several folders may be different drawings (each phase's C-109): ask before opening, not after.
  if (!args.near && differentAreas([chosen.latest.path, ...chosen.older.map((member) => member.path)])) {
    return {
      opened: null, reason: "this name is in several folders", project: latest.project, referenceOnly: latest.referenceOnly,
      choices: [chosen.latest, ...chosen.older].map((member) => ({ path: member.path, modified: member.modified })),
      hint: "If these are dated deliveries of one drawing, the first is the latest. If they are different areas or phases, ask the user which (or pass near with words about its folder).",
    };
  }
  if (!chosen.latest.fullPath || !chosen.latest.exists) {
    return {
      opened: null, reason: "the latest version isn't on this PC", project: latest.project, referenceOnly: latest.referenceOnly, latest: chosen.latest,
      hint: "The job database says where it was when the job was read; ask the user where the job folder is now.",
    };
  }
  const result = await plugin("openDrawing", { path: chosen.latest.fullPath, readOnly: args.readOnly ?? false });
  return {
    opened: chosen.latest.fullPath, reason: `latest version (modified ${chosen.latest.modified ?? "?"}) of ${chosen.versions}`,
    project: latest.project, referenceOnly: latest.referenceOnly, result,
    older: chosen.older.slice(0, 5).map((member) => ({ path: member.path, modified: member.modified })),
    ...(chosen.note ? { note: chosen.note } : {}),
    ...(chosen.folders ? { folders: chosen.folders, hint: "These are dated deliveries of one drawing: this is the one saved last." } : {}),
  };
}
