import { allLessons, knowledgeRoot, listPreferences } from "./tools/knowledgeBase.js";
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
  "Keep a knowledge base that grows (add, never delete), always asking the user first: a standing rule from the user becomes a " +
    "preference (civil3d_preferences action=add); a non-obvious gotcha or fact becomes a lesson (civil3d_memory action=record, with topics); " +
    "a multi-step task that worked and the user is happy with becomes a skill (civil3d_skills action=write) and, if it will be re-run, a " +
    "saved script (civil3d_script action=save, runnable with MCPRUN). Improve skills that turned out wrong or incomplete.",
].join("\n");

const MAX_PREFERENCES_CHARS = 8_000;

/**
 * Base instructions plus the user's preferences and skills, read once at startup.
 * (Clients receive instructions when they connect; the tools always show the latest.)
 */
export function buildServerInstructions(): string {
  const parts = [SERVER_INSTRUCTIONS];
  try {
    const preferences = listPreferences();
    if (preferences.length > 0) {
      let block = preferences.map((preference) => `- [${preference.id}] ${preference.rule}`).join("\n");
      if (block.length > MAX_PREFERENCES_CHARS) block = `${block.slice(0, MAX_PREFERENCES_CHARS)}\n[...more: civil3d_preferences action=list]`;
      parts.push(`\nThe user's standing preferences (follow them):\n${block}`);
    }

    const personal = loadSkills().filter((skill) => skill.source === "personal");
    if (personal.length > 0) {
      parts.push(`\nThe user's own skills (check these first): ${personal
        .map((skill) => `${skill.name}${skill.overridesBuiltIn ? " (improved built-in)" : ""}: ${skill.description}`)
        .join("; ")}`);
    }

    const lessons = allLessons().length;
    if (lessons > 0) {
      parts.push(`\nKnowledge base: ${knowledgeRoot()} (${lessons} lessons; relevant ones are returned with code results).`);
    }
  } catch {
    // Instructions must never block startup; the tools still expose everything.
  }
  return parts.join("\n");
}
