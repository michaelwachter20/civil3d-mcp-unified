import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { knowledgeError, knowledgeRoot, writeAtomically } from "./knowledgeBase.js";

/**
 * Project notes: what the assistant learned about ONE job (its point groups and their purpose, which survey
 * dates belong to which feature, which drawing is the go-by, sheet conventions, ...). They stay on this
 * computer, in <knowledge dir>\projects\<project>.json, and are never part of the general lessons or skills.
 * A project is matched by folder: notes come back automatically when the active drawing is inside one of
 * the project's folders.
 */

export interface ProjectNote {
  id: string;
  note: string;
  added: string;
  updated?: string;
}

export interface Project {
  name: string;
  folders: string[];
  notes: ProjectNote[];
}

export function projectsDirectory(): string {
  return path.join(knowledgeRoot(), "projects");
}

const slug = (name: string) => name.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 64);
const oneLine = (value: string) => value.replace(/[\r\n]+/g, " ").trim();
const normalizeFolder = (folder: string) => path.resolve(folder).replace(/[\\/]+$/, "");

function projectFile(name: string): string {
  const key = slug(name);
  if (!key) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Give the project a name with letters or digits.");
  return path.join(projectsDirectory(), `${key}.json`);
}

export function listProjects(): Project[] {
  const dir = projectsDirectory();
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((file) => file.endsWith(".json"))
    .map((file) => JSON.parse(readFileSync(path.join(dir, file), "utf8").replace(/^﻿/, "")) as Project);
}

function saveProject(project: Project) {
  writeAtomically(projectFile(project.name), `${JSON.stringify(project, null, 2)}\n`);
}

function findProject(name: string): Project | undefined {
  const file = projectFile(name);
  return existsSync(file) ? (JSON.parse(readFileSync(file, "utf8").replace(/^﻿/, "")) as Project) : undefined;
}

/** The project whose folder contains the drawing (deepest folder wins). */
export function projectForDrawing(drawingPath: string | undefined): Project | undefined {
  if (!drawingPath) return undefined;
  const directory = normalizeFolder(path.dirname(drawingPath)).toLowerCase();
  let best: { project: Project; depth: number } | undefined;
  for (const project of listProjects()) {
    for (const folder of project.folders) {
      const root = normalizeFolder(folder).toLowerCase();
      if (directory === root || directory.startsWith(`${root}${path.sep}`) || directory.startsWith(`${root}/`)) {
        if (!best || root.length > best.depth) best = { project, depth: root.length };
      }
    }
  }
  return best?.project;
}

/** Path of the active drawing, asked from the plugin (undefined when Civil 3D can't be reached). */
export async function activeDrawingPath(): Promise<string | undefined> {
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

async function resolveProject(name: string | undefined) {
  if (name) {
    const project = findProject(name);
    if (!project) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project named '${name}'. Use action=link to create it.`);
    return { project, drawing: undefined as string | undefined };
  }
  const drawing = await activeDrawingPath();
  const project = projectForDrawing(drawing);
  if (!project) {
    throw knowledgeError(
      "CIVIL3D.OBJECT_NOT_FOUND",
      `The active drawing${drawing ? ` (${path.basename(drawing)})` : ""} isn't in a known project folder. ` +
      "Ask the user for a project name, then use action=link (name, optional folder; default: the active drawing's folder).",
    );
  }
  return { project, drawing };
}

export async function listProjectNotes(name?: string) {
  if (!name) {
    const drawing = await activeDrawingPath();
    const project = projectForDrawing(drawing);
    if (!project) {
      return {
        project: null,
        activeDrawing: drawing ? path.basename(drawing) : null,
        knownProjects: listProjects().map((candidate) => ({ name: candidate.name, folders: candidate.folders, notes: candidate.notes.length })),
        hint: "No project linked to this drawing's folder yet. Ask the user for a project name and use action=link.",
      };
    }
    return { project: project.name, folders: project.folders, notes: project.notes, path: projectFile(project.name) };
  }
  const { project } = await resolveProject(name);
  return { project: project.name, folders: project.folders, notes: project.notes, path: projectFile(project.name) };
}

/** Creates the project if needed and links a folder to it (default: the active drawing's folder). */
export async function linkProjectFolder(name: string, folder?: string) {
  const target = folder ?? (await activeDrawingPath().then((drawing) => (drawing ? path.dirname(drawing) : undefined)));
  if (!target) throw knowledgeError("CIVIL3D.INVALID_INPUT", "No folder given and the active drawing's folder isn't known.");
  const project = findProject(name) ?? { name: oneLine(name), folders: [], notes: [] };
  const normalized = normalizeFolder(target);
  const created = project.folders.length === 0 && project.notes.length === 0;
  if (!project.folders.some((existing) => normalizeFolder(existing).toLowerCase() === normalized.toLowerCase())) project.folders.push(normalized);
  saveProject(project);
  return { success: true, action: created ? "created" : "linked", project: project.name, folders: project.folders, path: projectFile(project.name) };
}

/** Removes a folder from a project (its notes are kept). */
export function unlinkProjectFolder(name: string, folder: string) {
  const project = findProject(name);
  if (!project) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No project named '${name}'.`);
  const normalized = normalizeFolder(folder).toLowerCase();
  const remaining = project.folders.filter((existing) => normalizeFolder(existing).toLowerCase() !== normalized);
  if (remaining.length === project.folders.length) {
    throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `'${folder}' isn't linked to '${project.name}'. Linked: ${project.folders.join("; ") || "(none)"}.`);
  }
  project.folders = remaining;
  saveProject(project);
  return { success: true, action: "unlinked", project: project.name, folders: project.folders };
}

export async function addProjectNote(note: string, name?: string) {
  const text = oneLine(note);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The note is empty.");
  const { project } = await resolveProject(name);
  const duplicate = project.notes.find((candidate) => candidate.note.toLowerCase() === text.toLowerCase());
  if (duplicate) return { success: true, action: "unchanged", project: project.name, note: duplicate };
  const next = Math.max(0, ...project.notes.map((candidate) => Number(candidate.id.replace(/\D/g, "")) || 0)) + 1;
  const entry = { id: `note-${next}`, note: text, added: new Date().toISOString() };
  project.notes.push(entry);
  saveProject(project);
  return { success: true, action: "added", project: project.name, note: entry, path: projectFile(project.name) };
}

export async function updateProjectNote(id: string, note: string, name?: string) {
  const text = oneLine(note);
  if (!text) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The note is empty.");
  const { project } = await resolveProject(name);
  const entry = project.notes.find((candidate) => candidate.id === id);
  if (!entry) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No note '${id}' in project '${project.name}'.`);
  entry.note = text;
  entry.updated = new Date().toISOString();
  saveProject(project);
  return { success: true, action: "updated", project: project.name, note: entry };
}

export async function removeProjectNote(id: string, name?: string) {
  const { project } = await resolveProject(name);
  const entry = project.notes.find((candidate) => candidate.id === id);
  if (!entry) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No note '${id}' in project '${project.name}'.`);
  project.notes = project.notes.filter((candidate) => candidate.id !== id);
  saveProject(project);
  return { success: true, action: "removed", project: project.name, note: entry };
}

// Notes are attached to code results once per project per server session, so they reach the assistant
// without being repeated on every call.
const announced = new Set<string>();

export async function projectNotesOnce(): Promise<{ project: string; notes: ProjectNote[] } | undefined> {
  try {
    const project = projectForDrawing(await activeDrawingPath());
    if (!project || project.notes.length === 0 || announced.has(project.name)) return undefined;
    announced.add(project.name);
    return { project: project.name, notes: project.notes };
  } catch {
    return undefined;
  }
}

/** For tests. */
export function resetProjectNotesAnnouncements() {
  announced.clear();
}
