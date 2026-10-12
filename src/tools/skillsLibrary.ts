import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Skills are documented C# templates (`*.skill.md`) that the AI adapts and
 * runs through civil3d_execute / civil3d_query. Format (YAML-ish frontmatter):
 *
 * ---
 * name: list_surfaces
 * category: surfaces
 * description: List all surfaces with basic statistics
 * requires_write: false
 * parameters:
 *   - name: surfaceName
 *     type: string
 *     required: true
 *     description: Surface to inspect
 * ---
 * ## Code Template
 * ```csharp
 * ...
 * ```
 *
 * Ported from barbosaihan/civil3d-mcp (MIT) with CRLF-safe parsing.
 */

export interface SkillParameter {
  name: string;
  type: string;
  required: boolean;
  description?: string;
}

export interface SkillMetadata {
  name: string;
  category: string;
  description: string;
  requiresWrite: boolean;
  parameters: SkillParameter[];
}

export interface Skill extends SkillMetadata {
  content: string;
  relativePath: string;
}

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));

/** Candidate skill roots: env override, build/skills (packaged), repo-root skills (dev). */
export function resolveSkillsDirectory(): string | undefined {
  const candidates = [
    process.env.CIVIL3D_SKILLS_DIR,
    path.resolve(moduleDirectory, "..", "skills"),
    path.resolve(moduleDirectory, "..", "..", "skills"),
  ].filter((candidate): candidate is string => Boolean(candidate && candidate.trim()));

  return candidates.find((candidate) => existsSync(candidate) && statSync(candidate).isDirectory());
}

function findSkillFiles(directory: string): string[] {
  const results: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      results.push(...findSkillFiles(fullPath));
    } else if (entry.isFile() && entry.name.endsWith(".skill.md")) {
      results.push(fullPath);
    }
  }
  return results.sort();
}

function unquote(value: string): string {
  const trimmed = value.trim();
  if ((trimmed.startsWith("\"") && trimmed.endsWith("\"")) || (trimmed.startsWith("'") && trimmed.endsWith("'"))) {
    return trimmed.slice(1, -1);
  }
  return trimmed;
}

export function parseSkill(raw: string, fallbackName: string, relativePath = ""): Skill | undefined {
  const text = raw.replace(/^﻿/, "").replace(/\r\n?/g, "\n");
  const match = text.match(/^---\n([\s\S]*?)\n---\n?([\s\S]*)$/);
  if (!match) {
    return undefined;
  }

  const [, frontmatter, body] = match;
  const fields: Record<string, string> = {};
  const parameters: SkillParameter[] = [];
  let current: Partial<SkillParameter> | undefined;
  let inParameters = false;

  const flush = () => {
    if (current?.name) {
      parameters.push({
        name: current.name,
        type: current.type ?? "string",
        required: current.required ?? false,
        ...(current.description ? { description: current.description } : {}),
      });
    }
    current = undefined;
  };

  for (const line of frontmatter.split("\n")) {
    if (!line.trim()) continue;
    const topLevel = /^([A-Za-z_][\w-]*):\s*(.*)$/.exec(line);
    if (topLevel && !line.startsWith(" ")) {
      flush();
      inParameters = topLevel[1] === "parameters";
      if (!inParameters) fields[topLevel[1]] = unquote(topLevel[2]);
      continue;
    }

    if (!inParameters) continue;
    const item = /^\s*-\s*(?:([\w-]+):\s*(.*))?$/.exec(line);
    if (item) {
      flush();
      current = {};
      if (item[1]) assignParameterField(current, item[1], item[2] ?? "");
      continue;
    }

    const field = /^\s+([\w-]+):\s*(.*)$/.exec(line);
    if (field && current) {
      assignParameterField(current, field[1], field[2]);
    }
  }
  flush();

  return {
    name: fields.name || fallbackName,
    category: fields.category || "general",
    description: fields.description || "",
    requiresWrite: (fields.requires_write ?? fields.requiresWrite ?? "false").toLowerCase() === "true",
    parameters,
    content: body.trim(),
    relativePath,
  };
}

function assignParameterField(target: Partial<SkillParameter>, key: string, rawValue: string) {
  const value = unquote(rawValue);
  if (key === "name") target.name = value;
  else if (key === "type") target.type = value;
  else if (key === "required") target.required = value.toLowerCase() === "true";
  else if (key === "description") target.description = value;
}

let cache: { directory: string; loadedAt: number; skills: Skill[] } | undefined;
const CACHE_TTL_MS = 30_000;

/** Loads every skill (cached briefly so edits on disk are picked up). */
export function loadSkills(): Skill[] {
  const directory = resolveSkillsDirectory();
  if (!directory) {
    return [];
  }

  if (cache && cache.directory === directory && Date.now() - cache.loadedAt < CACHE_TTL_MS) {
    return cache.skills;
  }

  const skills = findSkillFiles(directory)
    .map((file) => parseSkill(
      readFileSync(file, "utf8"),
      path.basename(file, ".skill.md"),
      path.relative(directory, file).split(path.sep).join("/"),
    ))
    .filter((skill): skill is Skill => skill !== undefined);

  cache = { directory, loadedAt: Date.now(), skills };
  return skills;
}

export function summarizeSkill(skill: Skill) {
  return {
    name: skill.name,
    category: skill.category,
    description: skill.description,
    requiresWrite: skill.requiresWrite,
    parameters: skill.parameters.map((parameter) => parameter.name),
  };
}

export function listSkills(category?: string) {
  const skills = loadSkills().filter((skill) =>
    !category || skill.category.toLowerCase() === category.toLowerCase());
  return {
    directory: resolveSkillsDirectory() ?? null,
    categories: [...new Set(loadSkills().map((skill) => skill.category))].sort(),
    count: skills.length,
    skills: skills.map(summarizeSkill),
  };
}

export function searchSkills(query: string) {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  const scored = loadSkills()
    .map((skill) => {
      const name = skill.name.toLowerCase().replace(/[_-]+/g, " ");
      const meta = `${skill.category} ${skill.description}`.toLowerCase();
      const content = skill.content.toLowerCase();
      const score = terms.reduce((total, term) =>
        total + (name.includes(term) ? 5 : 0) + (meta.includes(term) ? 3 : 0) + (content.includes(term) ? 1 : 0), 0);
      return { skill, score };
    })
    .filter((entry) => entry.score > 0)
    .sort((left, right) => right.score - left.score || left.skill.name.localeCompare(right.skill.name));

  return {
    query,
    count: scored.length,
    results: scored.slice(0, 20).map((entry) => ({ ...summarizeSkill(entry.skill), score: entry.score })),
  };
}

export function readSkill(skillName: string) {
  const skills = loadSkills();
  const normalized = skillName.trim().toLowerCase().replace(/\.skill\.md$/, "");
  const skill = skills.find((candidate) => candidate.name.toLowerCase() === normalized);
  if (!skill) {
    const error = new Error(
      `Skill '${skillName}' not found. Available: ${skills.map((candidate) => candidate.name).join(", ") || "(none)"}.`,
    ) as Error & { code: string };
    error.code = "CIVIL3D.OBJECT_NOT_FOUND";
    throw error;
  }

  return {
    name: skill.name,
    category: skill.category,
    description: skill.description,
    requiresWrite: skill.requiresWrite,
    parameters: skill.parameters,
    path: skill.relativePath,
    content: skill.content,
    howToUse: skill.requiresWrite
      ? "Adapt the template, then run it with civil3d_execute (approval-gated). Pass inputs via `args` and read them with Args.GetString/GetDouble/..."
      : "Adapt the template, then run it with civil3d_query (read-only). Pass inputs via `args` and read them with Args.GetString/GetDouble/...",
  };
}
