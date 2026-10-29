import { allLessons, knowledgeStatus, listPreferences } from "./tools/knowledgeBase.js";
import { practicesForInstructions } from "./tools/practices.js";
import { loadSkills } from "./tools/skillsLibrary.js";

/**
 * Instructions sent to MCP clients when they connect (InitializeResult.instructions).
 * Clients typically add these to the model's context, so they apply to every session.
 */
export const SERVER_INSTRUCTIONS = [
  "Civil 3D MCP drives a live Autodesk Civil 3D drawing.",
  "At the start of each chat, read the skill 'working_safely_in_civil3d' (civil3d_skills action=read).",
  "Before writing new C# for civil3d_execute or civil3d_query, check for an existing solution first: " +
    "civil3d_script action=list for ready-to-run saved scripts, and civil3d_skills action=search for proven, documented templates " +
    "(they include rules and Civil 3D pitfalls learned on real drawings). Adapt a matching one instead of starting from scratch.",
  "Prefer a dedicated civil3d_* tool when one fits; use code execution for everything else.",
  "Never move, explode or edit COGO points or other survey data unless the user explicitly asks; point labels can be moved independently.",
  "Run a read-only check or a dryRun before changing a drawing, and make each change a single undo step.",
  "Knowledge comes in three kinds; keep each where it belongs and always ask the user before saving or changing any of it. " +
    "(1) How to drive Civil 3D: a standing rule from the user becomes a preference (civil3d_preferences action=add); a non-obvious " +
    "gotcha or fact becomes a lesson (civil3d_memory action=record, with topics); a multi-step task that worked and the user is happy " +
    "with becomes a skill (civil3d_skills action=write) and, if it will be re-run, a saved script (civil3d_script action=save, runnable " +
    "with MCPRUN). Improve skills that turned out wrong or incomplete; add, never delete. " +
    "(2) This job: facts about the current project go in civil3d_project_notes, the suite's project database on this computer, which " +
    "Project Analyzer fills from the job folder and TBC MCP shares; the job is matched to the drawing by folder. Start on a drawing with " +
    "civil3d_project_notes action=overview, and use search, file, entity, timeline and locate before asking the user what the files already say. " +
    "Job folders are usually copies: translate paths written in files (xrefs, images) with action=locate. " +
    "Each job also has a User Knowledge folder where the user puts their own documents for it (proposal guidelines, building-type " +
    "requirements, standards, code books): overview lists them, files and read open them, search covers them. When one applies to " +
    "the task, follow it and name the document; when it conflicts with a fact, a practice or the drawings, tell the user rather than choose. " +
    "(3) Engineering practice true on every job and in every program (minimum slopes, structure naming, the survey datum) goes in " +
    "civil3d_practices, shared with the other suite tools.",
  "REFERENCE ONLY: the job database (civil3d_project_notes) is a snapshot of the job's files, for finding and understanding things: " +
    "what a file says, where it is, who did what and when. Never use its coordinates, elevations, points, stations, dimensions, " +
    "quantities or text as data to edit a drawing or import into one. Take them from the current source file the user names (a drawing, " +
    "survey file or document in the job folder); if they haven't named one, ask which file is current before changing anything. " +
    "Never import from the suite's shared knowledge folder.",
  "Keep lessons, skills, scripts and practices GENERAL so they help on any project: no job names, drawing or file names, folders, point " +
    "numbers, coordinates, dates, point group or layer names, or site features. Those belong in civil3d_project_notes.",
].join("\n");

const MAX_PREFERENCES_CHARS = 8_000;
const MAX_PRACTICES_CHARS = 6_000;

const capped = (block: string, limit: number, more: string) => (block.length > limit ? `${block.slice(0, limit)}\n[...more: ${more}]` : block);

/**
 * Base instructions plus the user's preferences, the suite's good practices and the user's skills, read once at startup.
 * (Clients receive instructions when they connect; the tools always show the latest.)
 */
export function buildServerInstructions(): string {
  const parts = [SERVER_INSTRUCTIONS];
  try {
    const preferences = listPreferences();
    if (preferences.length > 0) {
      const block = preferences.map((preference) => `- [${preference.id}] ${preference.rule}`).join("\n");
      parts.push(`\nThe user's standing preferences (follow them):\n${capped(block, MAX_PREFERENCES_CHARS, "civil3d_preferences action=list")}`);
    }
  } catch {
    // Instructions must never block startup; the tools still expose everything.
  }

  const practices = practicesForInstructions();
  if (practices.length > 0) {
    const block = practices.map((practice) => `- [${practice.id}] ${practice.practice}`).join("\n");
    parts.push(`\nGood practices shared by the suite (apply them on every job):\n${capped(block, MAX_PRACTICES_CHARS, "civil3d_practices action=list")}`);
  }

  try {
    const personal = loadSkills().filter((skill) => skill.source === "personal");
    if (personal.length > 0) {
      parts.push(`\nThe user's own skills (check these first): ${personal
        .map((skill) => `${skill.name}${skill.overridesBuiltIn ? " (improved built-in)" : ""}: ${skill.description}`)
        .join("; ")}`);
    }

    const lessons = allLessons().length;
    const status = knowledgeStatus();
    if (lessons > 0) {
      parts.push(`\nCivil 3D know-how: ${status.civil3dFolder} (${lessons} lessons; relevant ones are returned with code results). ` +
        `Shared with the suite (practices, job databases): ${status.sharedFolder}.`);
    }
    if (status.notes) parts.push(`\nKnowledge folders: ${status.notes.join(" ")}`);
  } catch {
    // Instructions must never block startup; the tools still expose everything.
  }
  return parts.join("\n");
}
