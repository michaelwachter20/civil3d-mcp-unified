import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { jobDataProblems } from "./generalKnowledge.js";
import { knowledgeError, sharedRoot, writeAtomically } from "./knowledgeBase.js";
import { TOOL } from "./projectDb.js";

/**
 * Good practices: engineering and company practice that doesn't depend on a program ("storm pipe minimum slope is
 * 0.5%", "label structures MH-n"). Shared by the whole suite (Project Analyzer, Civil 3D MCP, TBC MCP) in
 * <shared folder>\practices.json, following project-analyzer's docs/suite-knowledge.md:
 *
 *   - ids practice-N, never reused; removing sets "retired" (nothing is deleted)
 *   - one line each, duplicate check (case-insensitive) before adding
 *   - each tool changes only entries whose "source" is its own; fields it doesn't know are kept
 *   - written atomically (temp file + rename), UTF-8 without BOM, LF, 2-space indent; re-read right before writing;
 *     a file that doesn't parse is never rewritten
 */

export interface Practice {
  id: string;
  practice: string;
  topics?: string[];
  source?: string;
  added?: string;
  updated?: string;
  retired?: string;
  [field: string]: unknown;
}

export function practicesFile(): string {
  return path.join(sharedRoot(), "practices.json");
}

const oneLine = (value: string) => value.replace(/\s+/g, " ").trim();

function readPracticesFile(): { data: Record<string, unknown>; practices: Practice[] } {
  const file = practicesFile();
  if (!existsSync(file)) return { data: {}, practices: [] };
  let data: unknown;
  try {
    data = JSON.parse(readFileSync(file, "utf8").replace(/^﻿/, ""));
  } catch (error) {
    throw knowledgeError("CIVIL3D.CONFLICT", `${file} doesn't parse (${(error as Error).message}); it won't be rewritten. Fix it by hand first.`);
  }
  if (!data || typeof data !== "object" || Array.isArray(data)) {
    throw knowledgeError("CIVIL3D.CONFLICT", `${file} isn't a JSON object with a "practices" list; it won't be rewritten.`);
  }
  const record = data as Record<string, unknown>;
  const practices = Array.isArray(record.practices)
    ? (record.practices as unknown[]).filter((entry): entry is Practice => !!entry && typeof entry === "object" && typeof (entry as Practice).id === "string")
    : [];
  return { data: record, practices };
}

/** Re-reads the file, applies the change to the fresh list and writes it back (other tools may have written since). */
function change<T>(work: (practices: Practice[]) => T): T {
  const { data } = readPracticesFile();
  const entries = Array.isArray(data.practices) ? (data.practices as Practice[]) : [];
  const result = work(entries);
  writeAtomically(practicesFile(), `${JSON.stringify({ ...data, practices: entries }, null, 2)}\n`);
  return result;
}

const present = (practice: Practice) => ({
  id: practice.id,
  practice: practice.practice,
  ...(practice.topics?.length ? { topics: practice.topics } : {}),
  source: practice.source ?? "unknown",
  ...(practice.source === TOOL ? {} : { readOnly: true }),
});

export function listPractices(includeRetired = false) {
  const { practices } = readPracticesFile();
  return practices.filter((practice) => includeRetired || !practice.retired);
}

/** Current practices for the server instructions; never throws. */
export function practicesForInstructions(): Practice[] {
  try {
    return listPractices();
  } catch {
    return [];
  }
}

export function describePractices(includeRetired = false) {
  const practices = listPractices(includeRetired);
  return {
    path: practicesFile(),
    count: practices.length,
    practices: practices.map((practice) => ({ ...present(practice), ...(practice.retired ? { retired: practice.retired } : {}) })),
  };
}

export function searchPractices(query: string) {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  const matches = listPractices()
    .map((practice) => {
      const topics = (practice.topics ?? []).join(" ").toLowerCase();
      const body = String(practice.practice ?? "").toLowerCase();
      const score = terms.reduce((total, term) => total + (topics.includes(term) ? 3 : 0) + (body.includes(term) ? 1 : 0), 0);
      return { practice, score };
    })
    .filter((entry) => entry.score > 0)
    .sort((left, right) => right.score - left.score);
  return { query, count: matches.length, practices: matches.slice(0, 50).map((entry) => present(entry.practice)) };
}

/**
 * Practices are general know-how: refuse text that names a job, its folders or its files (contract: no job data in
 * general knowledge). Stricter than the check on Civil 3D's own know-how (generalKnowledge.ts), which it also applies:
 * a practice is one sentence, so any path or file name is refused. Returns the reason, or undefined when it's fine.
 */
export function jobDataIn(text: string): string | undefined {
  if (/[a-zA-Z]:\\|\\\\[^\\\s]+\\/.test(text)) return "it contains a folder path";
  if (/\b[\w-]+\.(dwg|dxf|dwt|pdf|docx?|xlsx?|csv|vce|msg|jxl|job)\b/i.test(text)) return "it names a file";
  const problem = jobDataProblems(text)[0];
  return problem ? `it contains ${problem}` : undefined;
}

function cleanText(practice: string): string {
  const text = oneLine(practice);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The practice is empty.");
  const reason = jobDataIn(text);
  if (reason) {
    throw knowledgeError("CIVIL3D.INVALID_INPUT", `Not saved: ${reason}. Practices must hold for every job; put facts about one job in civil3d_project_notes.`);
  }
  return text;
}

const cleanTopics = (topics: string[] | undefined) => [...new Set((topics ?? []).map(oneLine).filter(Boolean))];

export function addPractice(practice: string, topics?: string[]) {
  const text = cleanText(practice);
  return change((entries) => {
    const duplicate = entries.find((entry) => entry && !entry.retired && String(entry.practice ?? "").toLowerCase() === text.toLowerCase());
    if (duplicate) return { success: true, action: "unchanged", practice: present(duplicate), note: "That practice is already recorded." };
    const next = Math.max(0, ...entries.map((entry) => Number(/^practice-(\d+)$/.exec(String(entry?.id))?.[1] ?? 0))) + 1;
    const entry: Practice = { id: `practice-${next}`, practice: text, topics: cleanTopics(topics), source: TOOL, added: new Date().toISOString() };
    entries.push(entry);
    return { success: true, action: "added", practice: present(entry), path: practicesFile() };
  });
}

function ownEntry(entries: Practice[], id: string): Practice {
  const entry = entries.find((candidate) => candidate?.id === id);
  if (!entry) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No practice with id '${id}'. Use action=list to see them.`);
  if (entry.source !== TOOL) {
    throw knowledgeError("CIVIL3D.CONFLICT", `${id} belongs to ${entry.source ?? "another tool"}; only that tool changes it. Add a new practice instead.`);
  }
  if (entry.retired) throw knowledgeError("CIVIL3D.CONFLICT", `${id} was removed on ${entry.retired}. Add a new practice instead.`);
  return entry;
}

export function updatePractice(id: string, practice: string, topics?: string[]) {
  const text = cleanText(practice);
  return change((entries) => {
    const entry = ownEntry(entries, id);
    entry.practice = text;
    if (topics) entry.topics = cleanTopics(topics);
    entry.updated = new Date().toISOString();
    return { success: true, action: "updated", practice: present(entry) };
  });
}

export function removePractice(id: string) {
  return change((entries) => {
    const entry = ownEntry(entries, id);
    entry.retired = new Date().toISOString();
    return { success: true, action: "retired", id, note: "Hidden from now on; kept in practices.json and its id is never reused." };
  });
}
