import os from "node:os";
import { knowledgeError } from "./knowledgeBase.js";
import { jobIdentities } from "./projectDb.js";

/**
 * No job data in general know-how (project-analyzer's docs/suite-knowledge.md: no job data in any tool's repository).
 * Lessons, skills, scripts and preferences hold on every job: the built-in ones ship with the repository and learned
 * ones may be shared or promoted into it, so nothing saved may name a job, its folders or files, a project drive or
 * the user's profile. Facts about one job belong in the project database (civil3d_project_notes). The rules are TBC
 * MCP's (knowledge.job_data_problems), so both tools refuse the same things; tests/knowledge_audit.test.ts applies
 * them to everything shipped in skills\.
 */

export const ALLOWED_PATH_PREFIXES = ["c:\\program files", "c:\\program files (x86)", "c:\\programdata", "c:\\windows"];

/** Folder names too generic to identify a job, even when a job's folder ends in one. */
export const GENERIC_FOLDER_NAMES = new Set([
  "survey", "surveys", "project", "projects", "jobs", "job", "drawings", "drawing", "documents", "docs", "data", "field",
  "fielddata", "office", "design", "files", "export", "exports", "import", "imports", "current", "archive", "working",
  "work", "civil", "civil3d", "trimble", "plans", "sheets", "images", "photos", "control", "base", "final", "draft",
  "incoming", "outgoing", "submittals", "correspondence", "email", "reports", "models", "points",
]);

const DRIVE_PATH = /(?<![A-Za-z0-9])[A-Za-z]:\\/g;
const UNC_PATH = /(?:^|[\s"'(=[,`])\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9$._-]/;
// A drawing or survey file named for real ("C-104.dwg", "Elm St.vce"); "<drawing>.dwg", "*.dwg" and ".dwg" are
// general. The extension is all lower or all upper case, so C# members such as record.Job aren't taken for files.
const JOB_FILE = /[^\s\\/:*?"'<>|`]*[A-Za-z0-9_\-)]\.(?:dwg|DWG|vce|VCE|jxl|JXL|job|JOB)(?![A-Za-z0-9_])/g;

const escapeRegExp = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const lastFolder = (folder: string) => folder.replace(/[\\/]+$/, "").split(/[\\/]/).pop() ?? "";

/**
 * The names that identify a job in the shared projects folder: each job's name, its slug, and the last folder name
 * of each of its folders (4+ characters, generic folder names left out). Read-only; empty when there is no folder.
 */
export function jobNames(directory?: string): string[] {
  const names = new Set<string>();
  for (const job of jobIdentities(directory)) {
    for (const candidate of [job.name, job.slug, ...job.folders.map(lastFolder)]) {
      const name = candidate.trim();
      if (name.length >= 4 && !GENERIC_FOLDER_NAMES.has(name.toLowerCase())) names.add(name);
    }
  }
  return [...names];
}

/**
 * Why this text would put job data into general know-how; an empty list means it is general. Each reason reads after
 * "it contains". `names`: the job names to look for (default: the jobs in the shared projects folder).
 */
export function jobDataProblems(text: string, names?: string[]): string[] {
  if (!text) return [];
  const problems: string[] = [];
  const flat = text.replace(/\//g, "\\");
  const low = flat.replace(/\\{2,}/g, "\\").toLowerCase();   // escaped backslashes in code or JSON count as one
  const home = os.homedir().replace(/\//g, "\\").replace(/\\+$/, "").toLowerCase();
  if (home.length > 3 && low.includes(`${home}\\`)) {
    problems.push("a path inside your user profile");
  } else {
    for (const match of low.matchAll(DRIVE_PATH)) {
      const rest = low.slice(match.index);
      if (!ALLOWED_PATH_PREFIXES.some((prefix) => rest.startsWith(prefix) && !/[a-z0-9]/.test(rest.charAt(prefix.length)))) {
        problems.push(`an absolute path (${rest.slice(0, 40).split("\n")[0]}...)`);
        break;
      }
    }
  }
  if (UNC_PATH.test(flat) || UNC_PATH.test(flat.replace(/\\\\/g, "\\"))) problems.push("a network path (\\\\server\\share)");
  for (const name of [...(names ?? jobNames())].sort((left, right) => right.length - left.length)) {
    if (new RegExp(`(?<![A-Za-z0-9])${escapeRegExp(name)}(?![A-Za-z0-9])`, "i").test(text)) {
      problems.push(`the name of the job '${name}'`);
      break;
    }
  }
  for (const match of text.matchAll(JOB_FILE)) {
    if (/^\.[A-Za-z]/.test(text.slice(match.index + match[0].length, match.index + match[0].length + 2))) continue;
    problems.push(`a drawing or job file name (${match[0].replace(/^[([{,;]+|[)\]},;]+$/g, "")})`);
    break;
  }
  return problems;
}

/**
 * Refuses to save know-how that names a job, its folders or files, a project drive or the user's profile. Texts
 * that aren't strings (parameters, topics) are checked as JSON.
 */
export function checkGeneral(what: string, ...texts: unknown[]) {
  const problems = jobDataProblems(texts
    .filter((text) => text !== undefined && text !== null && text !== "")
    .map((text) => (typeof text === "string" ? text : JSON.stringify(text)))
    .join("\n"));
  if (problems.length) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT",
      `Not saved: this ${what} would put job data into Civil 3D's general know-how, which holds on every job and may be ` +
      `shared with other users: it contains ${problems.join("; ")}. Lessons, skills, scripts and preferences must be ` +
      `GENERAL (useful on any job): write 'the project folder', '<drawing>.dwg' or a script parameter instead of real ` +
      `names and paths. Record facts about this job with civil3d_project_notes action=add instead (ask the user first).`);
  }
}
