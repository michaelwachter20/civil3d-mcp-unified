import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { checkGeneral } from "./generalKnowledge.js";
import { knowHowPath, knowledgeError, writeAtomically } from "./knowledgeBase.js";

/**
 * Skills are verified, step-by-step recipes (`*.skill.md`): when to use them, the user's rules, the pitfalls
 * found on real drawings, and the C# script the assistant adapts and runs through civil3d_execute /
 * civil3d_query. Format (YAML-ish frontmatter):
 *
 * ---
 * name: list_surfaces
 * category: surfaces
 * description: List all surfaces with basic statistics
 * requires_write: false
 * civil3d_versions: 2027
 * parameters:
 *   - name: surfaceName
 *     type: string
 *     required: true
 *     description: Surface to inspect
 * ---
 *
 * Two libraries are merged:
 * - built-in skills, shipped with the server (read-only), and
 * - the user's skills in the know-how folder (default Documents\Project Knowledge\civil3d\skills), which the assistant
 *   creates and improves during normal use. A user skill with a built-in skill's name replaces it, so
 *   learned improvements survive new releases.
 *
 * Rule: add, never delete. Updating a skill archives the previous version in skills\_archive; retiring a skill
 * archives it. A skill verified on another Civil 3D release can't be overwritten: add a section for the
 * new release instead (append_version_section). Reads show a version_note when the running release differs.
 *
 * Skill format and parser ported from barbosaihan/civil3d-mcp (MIT) with CRLF-safe parsing.
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

export type SkillSource = "built-in" | "personal";

export interface Skill extends SkillMetadata {
  content: string;
  relativePath: string;
  /** Civil 3D releases the skill was verified on (empty = not stated). */
  versions: string[];
  source?: SkillSource;
  /** User skill that replaces a built-in skill of the same name. */
  overridesBuiltIn?: boolean;
  /** Absolute path, for user skills (used when archiving). */
  filePath?: string;
}

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
const NAME_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;
const MAX_SKILL_BYTES = 200_000;
const ARCHIVE_FOLDER = "_archive";

/** Built-in skill roots: env override, build/skills (packaged), repo-root skills (dev). */
export function resolveSkillsDirectory(): string | undefined {
  const candidates = [
    process.env.CIVIL3D_SKILLS_DIR,
    path.resolve(moduleDirectory, "..", "skills"),
    path.resolve(moduleDirectory, "..", "..", "skills"),
  ].filter((candidate): candidate is string => Boolean(candidate && candidate.trim()));

  return candidates.find((candidate) => existsSync(candidate) && statSync(candidate).isDirectory());
}

/** The user's skills folder: CIVIL3D_USER_SKILLS_DIR, else the know-how folder's skills\. It's created on first write. */
export function personalSkillsDirectory(): string {
  return process.env.CIVIL3D_USER_SKILLS_DIR?.trim() || knowHowPath("skills");
}

function findSkillFiles(directory: string): string[] {
  const results: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== ARCHIVE_FOLDER) results.push(...findSkillFiles(fullPath));
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

const parseVersions = (value: string | undefined) =>
  String(value ?? "").replace(/[[\]]/g, "").split(",").map((version) => version.trim()).filter(Boolean);

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
    versions: parseVersions(fields.civil3d_versions),
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

function loadFrom(directory: string | undefined, source: SkillSource): Skill[] {
  if (!directory || !existsSync(directory)) return [];
  return findSkillFiles(directory).flatMap((file): Skill[] => {
    const skill = parseSkill(
      readFileSync(file, "utf8"),
      path.basename(file, ".skill.md"),
      path.relative(directory, file).split(path.sep).join("/"),
    );
    return skill ? [{ ...skill, source, filePath: file }] : [];
  });
}

let cache: { key: string; loadedAt: number; skills: Skill[] } | undefined;
const CACHE_TTL_MS = 30_000;

/** Clears the in-memory skill cache (after a write, or when a test changes folders). */
export function invalidateSkillCache() {
  cache = undefined;
}

/** Loads built-in and user skills; a user skill replaces a built-in one with the same name. */
export function loadSkills(): Skill[] {
  const builtInDirectory = resolveSkillsDirectory();
  const personalDirectory = personalSkillsDirectory();
  const key = `${builtInDirectory ?? ""}|${personalDirectory}`;
  if (cache && cache.key === key && Date.now() - cache.loadedAt < CACHE_TTL_MS) {
    return cache.skills;
  }

  const byName = new Map<string, Skill>();
  for (const skill of loadFrom(builtInDirectory, "built-in")) byName.set(skill.name.toLowerCase(), skill);
  for (const skill of loadFrom(personalDirectory, "personal")) {
    const replaced = byName.get(skill.name.toLowerCase());
    byName.set(skill.name.toLowerCase(), { ...skill, overridesBuiltIn: replaced?.source === "built-in" });
  }

  const skills = [...byName.values()].sort((left, right) => left.name.localeCompare(right.name));
  cache = { key, loadedAt: Date.now(), skills };
  return skills;
}

function versionNote(skill: Skill, current?: string) {
  if (!current || skill.versions.length === 0 || skill.versions.includes(current)) return undefined;
  return `Verified on Civil 3D ${skill.versions.join(", ")}; you're running ${current}. Check it still works, then add a section for ${current} with append_version_section.`;
}

export function summarizeSkill(skill: Skill, current?: string) {
  const note = versionNote(skill, current);
  return {
    name: skill.name,
    category: skill.category,
    description: skill.description,
    requiresWrite: skill.requiresWrite,
    parameters: skill.parameters.map((parameter) => parameter.name),
    source: skill.source ?? "built-in",
    ...(skill.versions.length ? { civil3dVersions: skill.versions } : {}),
    ...(skill.overridesBuiltIn ? { overridesBuiltIn: true } : {}),
    ...(note ? { version_note: note } : {}),
  };
}

export function listSkills(category?: string, current?: string) {
  const all = loadSkills();
  const skills = all.filter((skill) => !category || skill.category.toLowerCase() === category.toLowerCase());
  return {
    directory: resolveSkillsDirectory() ?? null,
    personalDirectory: personalSkillsDirectory(),
    categories: [...new Set(all.map((skill) => skill.category))].sort(),
    count: skills.length,
    skills: skills.map((skill) => summarizeSkill(skill, current)),
  };
}

export function searchSkills(query: string, current?: string) {
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
    results: scored.slice(0, 20).map((entry) => ({ ...summarizeSkill(entry.skill, current), score: entry.score })),
  };
}

function findSkill(skillName: string): Skill | undefined {
  const normalized = skillName.trim().toLowerCase().replace(/\.skill\.md$/, "");
  return loadSkills().find((candidate) => candidate.name.toLowerCase() === normalized);
}

export function readSkill(skillName: string, current?: string) {
  const skill = findSkill(skillName);
  if (!skill) {
    throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND",
      `Skill '${skillName}' not found. Available: ${loadSkills().map((candidate) => candidate.name).join(", ") || "(none)"}.`);
  }
  const note = versionNote(skill, current);
  return {
    name: skill.name,
    category: skill.category,
    description: skill.description,
    requiresWrite: skill.requiresWrite,
    parameters: skill.parameters,
    path: skill.relativePath,
    source: skill.source ?? "built-in",
    ...(skill.versions.length ? { civil3dVersions: skill.versions } : {}),
    ...(skill.overridesBuiltIn ? { overridesBuiltIn: true } : {}),
    ...(note ? { version_note: note } : {}),
    content: skill.content,
    howToUse: skill.requiresWrite
      ? "Adapt the template, then run it with civil3d_execute (approval-gated). Pass inputs via `args` and read them with Args.GetString/GetDouble/..."
      : "Adapt the template, then run it with civil3d_query (read-only). Pass inputs via `args` and read them with Args.GetString/GetDouble/...",
  };
}

const oneLine = (value: string) => value.replace(/[\r\n]+/g, " ").trim();

/** Keeps the previous version of a user skill in skills\_archive (add, never delete). */
function archive(file: string, reason: string) {
  const archiveDir = path.join(personalSkillsDirectory(), ARCHIVE_FOLDER);
  mkdirSync(archiveDir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const target = path.join(archiveDir, `${path.basename(file, ".skill.md")}.${stamp}.${reason}.skill.md`);
  renameSync(file, target);
  return target;
}

function render(meta: SkillMetadata & { versions: string[] }, body: string) {
  const frontmatter = [
    "---",
    `name: ${meta.name}`,
    `category: ${meta.category}`,
    `description: ${oneLine(meta.description)}`,
    `requires_write: ${meta.requiresWrite ? "true" : "false"}`,
    `civil3d_versions: ${meta.versions.join(", ")}`,
    meta.parameters.length === 0 ? "parameters: []" : "parameters:",
    ...meta.parameters.flatMap((parameter) => [
      `  - name: ${oneLine(parameter.name)}`,
      `    type: ${oneLine(parameter.type || "string")}`,
      `    required: ${parameter.required ? "true" : "false"}`,
      ...(parameter.description ? [`    description: ${oneLine(parameter.description)}`] : []),
    ]),
    `updated: ${new Date().toISOString()}`,
    "---",
    "",
  ].join("\n");
  const text = `${frontmatter}${body.replace(/\r\n?/g, "\n").trim()}\n`;
  if (Buffer.byteLength(text, "utf8") > MAX_SKILL_BYTES) throw knowledgeError("CIVIL3D.INVALID_INPUT", `Skill is larger than ${MAX_SKILL_BYTES} bytes.`);
  return text;
}

const banner = (versions: string[]) => `> Verified on Civil 3D ${versions.join(", ")}.`;
const stripBanner = (body: string) => body.replace(/^> Verified on Civil 3D [^\n]*\n+/, "");

export interface SkillWriteInput {
  name: string;
  category?: string;
  description: string;
  requiresWrite?: boolean;
  parameters?: SkillParameter[];
  /** Markdown body: when to use it, rules, pitfalls, and the C# script in a ```csharp block. */
  content: string;
}

/**
 * Creates or improves a user skill, verified on `release`. The previous version is archived. A skill verified
 * only on other releases is refused (use appendVersionSection). Writing a built-in skill's name saves the
 * user's version, which then replaces the built-in one.
 */
export function writeSkill(input: SkillWriteInput, release: string) {
  const name = input.name.trim().toLowerCase();
  const category = (input.category ?? "general").trim().toLowerCase();
  if (!NAME_PATTERN.test(name)) throw knowledgeError("CIVIL3D.INVALID_INPUT", `Invalid skill name '${input.name}'. Use 1-64 lowercase letters, digits, '-' or '_'.`);
  if (!NAME_PATTERN.test(category)) throw knowledgeError("CIVIL3D.INVALID_INPUT", `Invalid category '${input.category}'. Use 1-64 lowercase letters, digits, '-' or '_'.`);
  if (!input.description.trim()) throw knowledgeError("CIVIL3D.INVALID_INPUT", "A description is required; it is how the skill is found later.");
  if (!input.content.trim()) throw knowledgeError("CIVIL3D.INVALID_INPUT", "Skill content is empty.");
  checkGeneral("skill", name, category, input.description, input.content, input.parameters);

  const existing = findSkill(name);
  if (existing && existing.versions.length > 0 && !existing.versions.includes(release)) {
    throw knowledgeError("CIVIL3D.CONFLICT",
      `'${name}' was verified on Civil 3D ${existing.versions.join(", ")}, not ${release}. Keep that version and add what's different ` +
      `for ${release} with action=append_version_section.`);
  }

  const versions = [...new Set([...(existing?.versions ?? []), release])];
  const text = render({
    name, category, description: input.description, requiresWrite: Boolean(input.requiresWrite),
    parameters: input.parameters ?? [], versions,
  }, `${banner(versions)}\n\n${stripBanner(input.content.trim())}`);

  const archived = existing?.source === "personal" && existing.filePath ? archive(existing.filePath, "replaced") : undefined;
  const target = path.join(personalSkillsDirectory(), category, `${name}.skill.md`);
  writeAtomically(target, text);
  invalidateSkillCache();
  const replacesBuiltIn = existing?.source === "built-in" || Boolean(existing?.overridesBuiltIn);
  return {
    success: true,
    name,
    category,
    civil3dVersions: versions,
    path: target,
    action: existing?.source === "personal" ? "updated" : "created",
    ...(archived ? { previousVersionArchived: archived } : {}),
    ...(replacesBuiltIn ? {
      overridesBuiltIn: true,
      note: `Your version now replaces the built-in '${name}' skill. Retire it to go back to the built-in one.`,
    } : {}),
  };
}

/** Adds a "## Civil 3D <release>" section to a skill verified on other releases, keeping everything else. */
export function appendVersionSection(skillName: string, sectionContent: string, release: string) {
  const skill = findSkill(skillName);
  if (!skill) throw knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `Skill '${skillName}' not found.`);
  if (!sectionContent.trim()) throw knowledgeError("CIVIL3D.INVALID_INPUT", "The section is empty.");
  checkGeneral("skill section", sectionContent);
  if (skill.versions.includes(release)) {
    throw knowledgeError("CIVIL3D.CONFLICT", `'${skill.name}' is already verified on Civil 3D ${release}; improve it with action=write instead.`);
  }

  const versions = [...skill.versions, release];
  const body = `${banner(versions)}\n\n${stripBanner(skill.content)}\n\n## Civil 3D ${release}\n\n${sectionContent.replace(/\r\n?/g, "\n").trim()}`;
  const text = render({ ...skill, versions }, body);
  const archived = skill.source === "personal" && skill.filePath ? archive(skill.filePath, "before-" + release) : undefined;
  const target = path.join(personalSkillsDirectory(), skill.category, `${skill.name}.skill.md`);
  writeAtomically(target, text);
  invalidateSkillCache();
  return { success: true, name: skill.name, civil3dVersions: versions, path: target, ...(archived ? { previousVersionArchived: archived } : {}) };
}

/** Retires a user skill: moves it to skills\_archive. Built-in skills can't be retired, only replaced. */
export function retireSkill(skillName: string) {
  const name = skillName.trim().toLowerCase().replace(/\.skill\.md$/, "");
  const root = personalSkillsDirectory();
  const files = existsSync(root) ? findSkillFiles(root).filter((file) => path.basename(file, ".skill.md").toLowerCase() === name) : [];
  const builtIn = loadFrom(resolveSkillsDirectory(), "built-in").some((skill) => skill.name.toLowerCase() === name);
  if (files.length === 0) {
    throw builtIn
      ? knowledgeError("CIVIL3D.INVALID_INPUT", `'${name}' is a built-in skill and can't be retired. To change it, write your own version with the same name.`)
      : knowledgeError("CIVIL3D.OBJECT_NOT_FOUND", `No user skill named '${name}'.`);
  }
  const archived = files.map((file) => archive(file, "retired"));
  invalidateSkillCache();
  return { success: true, retired: name, archivedTo: archived, builtInVersionActiveAgain: builtIn };
}
