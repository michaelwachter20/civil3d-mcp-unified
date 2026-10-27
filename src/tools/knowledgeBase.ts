import { existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { withApplicationConnection } from "../utils/ConnectionManager.js";

/**
 * The assistant's knowledge base: everything it learns while working, kept as plain files so the next chat, or a
 * colleague's assistant pointed at the same folder, starts out experienced. It lives in the suite's shared folder
 * (project-analyzer's docs/suite-knowledge.md, "Where"; sharedRoot below), next to what Project Analyzer and TBC MCP
 * keep there:
 *
 *   practices.json              good practices shared by the suite (practices.ts)
 *   projects\<slug>.db          one database per job (projectDb.ts)
 *   civil3d\                    Civil 3D's own know-how (CIVIL3D_MCP_KNOWLEDGE_DIR overrides it):
 *     preferences.json          standing rules from the user, included in every AI's instructions
 *     memory\lessons.jsonl      gotchas and facts, each stamped with the Civil 3D release; shown when relevant
 *     skills\**\*.skill.md      verified step-by-step recipes (see skillsLibrary.ts)
 *     scripts\*.csx             reusable scripts (managed by the Civil 3D plugin)
 *
 * Rule: add, never delete. Knowledge from another Civil 3D release is kept; new releases add to it.
 * Built-in seed lessons ship with the server (skills\lessons.seed.jsonl) and can't be removed.
 */

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));

/** The shared folder's name in Documents. */
export const SHARED_FOLDER_NAME = "Project Knowledge";
/** Earlier default names, used as they are when they exist (nothing renames them; the user may). */
export const EARLIER_SHARED_FOLDER_NAMES = ["Civil3DMcp-KnowledgeBase", "Civil3DMcp"] as const;
/** Civil 3D MCP's own subfolder of the shared folder. */
export const KNOW_HOW_FOLDER_NAME = "civil3d";
/** What Civil 3D MCP kept directly in the shared folder before 2.4.0, and now keeps in civil3d\. */
export const KNOW_HOW_ITEMS = ["preferences.json", "memory", "skills", "scripts"] as const;
type KnowHowItem = (typeof KNOW_HOW_ITEMS)[number];

const isDirectory = (candidate: string) => {
  try {
    return statSync(candidate).isDirectory();
  } catch {
    return false;
  }
};

/**
 * The suite's shared folder: PROJECT_KNOWLEDGE_DIR, else CIVIL3D_KNOWLEDGE_DIR (its earlier name), else
 * Documents\Project Knowledge if it exists, else an earlier default that exists (Documents\Civil3DMcp-KnowledgeBase,
 * then Documents\Civil3DMcp) as it is, else Documents\Project Knowledge. Nothing is created, renamed or moved here:
 * a tool of an older version on the same PC would lose a moved folder. `documents` is for tests.
 */
export function sharedRoot(documents = path.join(os.homedir(), "Documents")): string {
  for (const variable of ["PROJECT_KNOWLEDGE_DIR", "CIVIL3D_KNOWLEDGE_DIR"]) {
    const configured = process.env[variable]?.trim();
    if (configured) return configured;
  }
  for (const name of [SHARED_FOLDER_NAME, ...EARLIER_SHARED_FOLDER_NAMES]) {
    if (isDirectory(path.join(documents, name))) return path.join(documents, name);
  }
  return path.join(documents, SHARED_FOLDER_NAME);
}

/** Civil 3D MCP's own know-how folder: CIVIL3D_MCP_KNOWLEDGE_DIR, else <shared>\civil3d. */
export function knowHowRoot(): string {
  return process.env.CIVIL3D_MCP_KNOWLEDGE_DIR?.trim() || path.join(sharedRoot(), KNOW_HOW_FOLDER_NAME);
}

/** Items whose own variable points elsewhere are neither moved nor looked for at the top of the shared folder. */
const OWN_VARIABLES: Partial<Record<KnowHowItem, string>> = { skills: "CIVIL3D_USER_SKILLS_DIR", scripts: "CIVIL3D_SCRIPTS_DIR" };
const managedHere = (item: KnowHowItem) =>
  !process.env.CIVIL3D_MCP_KNOWLEDGE_DIR?.trim() && !process.env[OWN_VARIABLES[item] ?? ""]?.trim();

const movesChecked = new Set<string>();

/**
 * Moves the know-how Civil 3D MCP kept at the top of the shared folder before 2.4.0 into <shared>\civil3d, once:
 * each item is renamed when civil3d\<item> doesn't exist yet. An item that can't be moved (in use) stays where it is
 * and is used from there, and the move is tried again on the next start; when both exist, civil3d\ is used and the
 * top-level one is left alone. Nothing is deleted. Nothing happens when CIVIL3D_MCP_KNOWLEDGE_DIR is set.
 */
export function moveKnowHowIntoOwnFolder(shared = sharedRoot()) {
  const result = { moved: [] as string[], notMoved: [] as string[] };
  if (process.env.CIVIL3D_MCP_KNOWLEDGE_DIR?.trim()) return result;
  const own = path.join(shared, KNOW_HOW_FOLDER_NAME);
  for (const item of KNOW_HOW_ITEMS) {
    const top = path.join(shared, item);
    if (!managedHere(item) || !existsSync(top) || existsSync(path.join(own, item))) continue;
    try {
      mkdirSync(own, { recursive: true });
      renameSync(top, path.join(own, item));
      result.moved.push(item);
    } catch {
      result.notMoved.push(item);   // in use or not allowed: used from the top this time, tried again next start
    }
  }
  return result;
}

/** Moves the know-how into civil3d\ the first time this process uses a shared folder. */
function checkMoveOnce(shared: string) {
  if (movesChecked.has(shared)) return;
  movesChecked.add(shared);
  moveKnowHowIntoOwnFolder(shared);
}

/** Where one know-how item is: civil3d\<item>, or the top-level one while it couldn't be moved yet. */
export function knowHowPath(item: KnowHowItem): string {
  const shared = sharedRoot();
  checkMoveOnce(shared);
  const own = path.join(knowHowRoot(), item);
  if (managedHere(item) && !existsSync(own) && existsSync(path.join(shared, item))) return path.join(shared, item);
  return own;
}

/**
 * Where the knowledge is, for the instructions and civil3d_health: the shared folder, Civil 3D's own folder, and
 * know-how still at the top of the shared folder (not moved yet, or left there because civil3d\ has its own).
 */
export function knowledgeStatus() {
  const shared = sharedRoot();
  checkMoveOnce(shared);
  const own = knowHowRoot();
  const atTop = KNOW_HOW_ITEMS.filter((item) => managedHere(item) && existsSync(path.join(shared, item)));
  const leftAtTop = atTop.filter((item) => existsSync(path.join(own, item)));
  const notMovedYet = atTop.filter((item) => !leftAtTop.includes(item));
  const notes = [
    ...(notMovedYet.length ? [`Not moved into ${own} yet (in use?), used from the shared folder until the next start: ${notMovedYet.join(", ")}.`] : []),
    ...(leftAtTop.length
      ? [`${leftAtTop.join(", ")} also at the top of ${shared}, from an earlier version; not used, since ${own} has its own. Nothing was deleted: the user can compare and tidy them.`]
      : []),
  ];
  return { sharedFolder: shared, civil3dFolder: own, ...(notes.length ? { notes } : {}) };
}

export function preferencesFile(): string {
  return knowHowPath("preferences.json");
}

export function lessonsFile(): string {
  return path.join(knowHowPath("memory"), "lessons.jsonl");
}

function seedLessonsFile(): string | undefined {
  return [
    path.resolve(moduleDirectory, "..", "skills", "lessons.seed.jsonl"),
    path.resolve(moduleDirectory, "..", "..", "skills", "lessons.seed.jsonl"),
  ].find((candidate) => existsSync(candidate));
}

export function writeAtomically(file: string, text: string) {
  mkdirSync(path.dirname(file), { recursive: true });
  const temporary = path.join(path.dirname(file), `.${path.basename(file)}.${process.pid}.${Date.now()}.tmp`);
  writeFileSync(temporary, text, "utf8");
  renameSync(temporary, file);
}

export function knowledgeError(code: "CIVIL3D.INVALID_INPUT" | "CIVIL3D.OBJECT_NOT_FOUND" | "CIVIL3D.CONFLICT", message: string) {
  const error = new Error(message) as Error & { code: string };
  error.code = code;
  return error;
}

// ---------------------------------------------------------------- Civil 3D release

// AutoCAD release numbers (ACADVER) to the Civil 3D product year.
const RELEASE_YEARS: Record<string, string> = {
  "23.0": "2019", "23.1": "2020", "24.0": "2021", "24.1": "2022", "24.2": "2023", "24.3": "2024",
  "25.0": "2025", "25.1": "2026", "26.0": "2027",
};

/** "26.0s (Company Name)" -> "2027". Only the release is kept: ACADVER can include the licensee's company. */
export function releaseFromAcadver(acadver?: string | null): string | undefined {
  const match = /^(\d+\.\d+)/.exec(String(acadver ?? "").trim());
  if (!match) return undefined;
  return RELEASE_YEARS[match[1]] ?? `R${match[1]}`;
}

let releaseCache: { value: string; detected: boolean; at: number } | undefined;

/** The running Civil 3D release (e.g. "2027"), asked from the plugin once; falls back to CIVIL3D_RELEASE or 2027. */
export async function currentRelease(): Promise<string> {
  if (releaseCache && (releaseCache.detected || Date.now() - releaseCache.at < 60_000)) return releaseCache.value;
  try {
    const health = await withApplicationConnection(async (client) =>
      await client.sendCommand("getCivil3DHealth", {})) as { civil3dVersion?: string } | undefined;
    const detected = releaseFromAcadver(health?.civil3dVersion);
    if (detected) {
      releaseCache = { value: detected, detected: true, at: Date.now() };
      return detected;
    }
  } catch {
    // Civil 3D closed or the AI connection is off: use the fallback without caching it for long.
  }
  const fallback = process.env.CIVIL3D_RELEASE?.trim() || "2027";
  releaseCache = { value: fallback, detected: false, at: Date.now() };
  return fallback;
}

/** For tests. */
export function setReleaseForTesting(release: string | undefined) {
  releaseCache = release ? { value: release, detected: true, at: Date.now() } : undefined;
}

// ---------------------------------------------------------------- preferences

export interface Preference {
  id: string;
  rule: string;
  added: string;
  updated?: string;
}

export function listPreferences(): Preference[] {
  const file = preferencesFile();
  if (!existsSync(file)) return [];
  const parsed = JSON.parse(readFileSync(file, "utf8").replace(/^﻿/, "")) as { preferences?: Preference[] };
  return Array.isArray(parsed.preferences) ? parsed.preferences : [];
}

function savePreferences(preferences: Preference[]) {
  writeAtomically(preferencesFile(), `${JSON.stringify({ preferences }, null, 2)}\n`);
}

const oneLine = (value: string) => value.replace(/[\r\n]+/g, " ").trim();

export function addPreference(rule: string) {
  const text = oneLine(rule);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The preference is empty.");
  const preferences = listPreferences();
  const duplicate = preferences.find((preference) => preference.rule.toLowerCase() === text.toLowerCase());
  if (duplicate) return { success: true, action: "unchanged", preference: duplicate, note: "That preference already exists." };
  const next = Math.max(0, ...preferences.map((preference) => Number(preference.id.replace(/\D/g, "")) || 0)) + 1;
  const preference = { id: `pref-${next}`, rule: text, added: new Date().toISOString() };
  savePreferences([...preferences, preference]);
  return { success: true, action: "added", preference, path: preferencesFile() };
}

export function updatePreference(id: string, rule: string) {
  const text = oneLine(rule);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The preference is empty.");
  const preferences = listPreferences();
  const preference = preferences.find((candidate) => candidate.id === id);
  if (!preference) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No preference with id '${id}'. Use action=list to see them.`);
  preference.rule = text;
  preference.updated = new Date().toISOString();
  savePreferences(preferences);
  return { success: true, action: "updated", preference };
}

export function removePreference(id: string) {
  const preferences = listPreferences();
  const preference = preferences.find((candidate) => candidate.id === id);
  if (!preference) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No preference with id '${id}'. Use action=list to see them.`);
  savePreferences(preferences.filter((candidate) => candidate.id !== id));
  return { success: true, action: "removed", preference };
}

// ---------------------------------------------------------------- lessons

export interface Lesson {
  id: string;
  lesson: string;
  /** API names, tools or subjects this lesson is about (e.g. "CogoPoint.LabelLocation", "labels"). */
  topics: string[];
  civil3d_release: string;
  added: string;
  source: "seed" | "learned";
}

function readJsonl(file: string | undefined, source: Lesson["source"]): Lesson[] {
  if (!file || !existsSync(file)) return [];
  return readFileSync(file, "utf8").replace(/^﻿/, "").split(/\r?\n/)
    .filter((line) => line.trim())
    .map((line) => ({ ...(JSON.parse(line) as Omit<Lesson, "source">), source }));
}

export function allLessons(): Lesson[] {
  return [...readJsonl(seedLessonsFile(), "seed"), ...readJsonl(lessonsFile(), "learned")];
}

function versionNote(release: string, current: string) {
  return release === current ? undefined : `Learned on Civil 3D ${release}; you're running ${current}. Check it still applies.`;
}

function present(lesson: Lesson, current: string) {
  const note = versionNote(lesson.civil3d_release, current);
  return { ...lesson, ...(note ? { version_note: note } : {}) };
}

export function recordLesson(lesson: string, topics: string[], release: string) {
  const text = oneLine(lesson);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The lesson is empty.");
  const cleanTopics = [...new Set(topics.map((topic) => oneLine(topic)).filter(Boolean))];
  if (cleanTopics.length === 0) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give at least one topic (an API name, tool or subject) so the lesson can surface when relevant.");
  }
  const existing = allLessons();
  const duplicate = existing.find((candidate) => candidate.lesson.toLowerCase() === text.toLowerCase());
  if (duplicate) return { success: true, action: "unchanged", lesson: duplicate, note: "That lesson is already recorded." };
  const learned = existing.filter((candidate) => candidate.source === "learned");
  const next = Math.max(0, ...learned.map((candidate) => Number(candidate.id.replace(/\D/g, "")) || 0)) + 1;
  const entry = { id: `lesson-${next}`, lesson: text, topics: cleanTopics, civil3d_release: release, added: new Date().toISOString() };
  const file = lessonsFile();
  const previous = existsSync(file) ? readFileSync(file, "utf8") : "";
  writeAtomically(file, `${previous}${previous && !previous.endsWith("\n") ? "\n" : ""}${JSON.stringify(entry)}\n`);
  return { success: true, action: "recorded", lesson: { ...entry, source: "learned" as const }, path: file };
}

export function searchLessons(query: string | undefined, current: string) {
  const terms = String(query ?? "").toLowerCase().split(/\s+/).filter(Boolean);
  const lessons = allLessons()
    .map((lesson) => {
      const topics = lesson.topics.join(" ").toLowerCase();
      const body = lesson.lesson.toLowerCase();
      const score = terms.length === 0 ? 1 : terms.reduce((total, term) => total + (topics.includes(term) ? 3 : 0) + (body.includes(term) ? 1 : 0), 0);
      return { lesson, score };
    })
    .filter((entry) => entry.score > 0)
    .sort((left, right) => right.score - left.score);
  return { query: query ?? null, count: lessons.length, lessons: lessons.slice(0, 50).map((entry) => present(entry.lesson, current)) };
}

/** Lessons whose topics appear in the given text (e.g. C# code about to run). */
export function lessonsRelevantTo(text: string, current: string, limit = 6) {
  const haystack = text.toLowerCase();
  return allLessons()
    .filter((lesson) => lesson.topics.some((topic) => topic.length >= 3 && haystack.includes(topic.toLowerCase())))
    .slice(0, limit)
    .map((lesson) => present(lesson, current));
}

/** Only lessons learned on the running release can be removed; seed lessons and other releases' lessons are kept. */
export function removeLesson(id: string, current: string) {
  const lesson = allLessons().find((candidate) => candidate.id === id);
  if (!lesson) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No lesson with id '${id}'.`);
  if (lesson.source === "seed") throw knowledgeError("CIVIL3D.CONFLICT", "Built-in lessons can't be removed. Record a new lesson that corrects it.");
  if (lesson.civil3d_release !== current) {
    throw knowledgeError("CIVIL3D.CONFLICT", `This lesson was learned on Civil 3D ${lesson.civil3d_release} and is kept for that release. Record a new lesson for ${current} instead.`);
  }
  const remaining = readJsonl(lessonsFile(), "learned").filter((candidate) => candidate.id !== id)
    .map(({ source: _source, ...rest }) => JSON.stringify(rest)).join("\n");
  writeAtomically(lessonsFile(), remaining ? `${remaining}\n` : "");
  return { success: true, action: "removed", id };
}
