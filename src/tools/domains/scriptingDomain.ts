import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import {
  appendVersionSection, listSkills, readSkill, retireSkill, searchSkills, writeSkill, type SkillParameter,
} from "../skillsLibrary.js";
import {
  addPreference, currentRelease, lessonsRelevantTo, listPreferences, preferencesFile, recordLesson, removeLesson,
  removePreference, searchLessons, updatePreference,
} from "../knowledgeBase.js";
import {
  addProjectNote, linkProjectFolder, listProjectNotes, projectNotesOnce, removeProjectNote, unlinkProjectFolder, updateProjectNote,
} from "../projectNotes.js";

/**
 * Code execution domain: Roslyn C# scripts with full AutoCAD + Civil 3D .NET
 * API access, raw AutoCAD commands, a skills (template) library, and a saved
 * script library for repetitive tasks.
 *
 * Execution engine and skills originate from barbosaihan/civil3d-mcp (MIT).
 */

const GenericResponseSchema = z.object({}).passthrough();
const ScriptArgsSchema = z.record(z.unknown());
const PointTokenSchema = z.object({ x: z.number(), y: z.number(), z: z.number().optional() });
const CommandTokenSchema = z.union([z.string(), z.number(), z.boolean(), PointTokenSchema]);
const CommandSchema = z.union([z.string().min(1), z.array(CommandTokenSchema).min(1)]);
const ModeSchema = z.enum(["write", "read"]);
const ScriptNameSchema = z.string().min(1).max(64);
const ScriptParameterSchema = z.object({
  name: z.string().min(1).max(64),
  type: z.enum(["string", "number", "integer", "boolean", "point", "string[]", "json"]).default("string"),
  required: z.boolean().optional(),
  default: z.unknown().optional(),
  description: z.string().max(300).optional(),
});

const ExecuteArgs = z.object({
  action: z.literal("execute"),
  code: z.string().min(1).max(200_000),
  args: ScriptArgsSchema.optional(),
  description: z.string().max(500).optional(),
});
const QueryArgs = z.object({
  action: z.literal("query"),
  code: z.string().min(1).max(200_000),
  args: ScriptArgsSchema.optional(),
  description: z.string().max(500).optional(),
});
const CompileArgs = z.object({ action: z.literal("compile"), code: z.string().min(1).max(200_000), mode: ModeSchema.optional() });
const StatusArgs = z.object({ action: z.literal("status") });
const CommandArgs = z.object({ action: z.literal("command"), command: CommandSchema });
const SkillsListArgs = z.object({ action: z.literal("skills_list"), category: z.string().optional() });
const SkillsSearchArgs = z.object({ action: z.literal("skills_search"), query: z.string().min(1) });
const SkillsReadArgs = z.object({ action: z.literal("skills_read"), skillName: z.string().min(1) });
const SkillParameterSchema = z.object({
  name: z.string().min(1).max(64),
  type: z.string().max(32).default("string"),
  required: z.boolean().default(false),
  description: z.string().max(300).optional(),
});
const SkillsWriteArgs = z.object({
  action: z.literal("skills_write"),
  skillName: z.string().min(1).max(64),
  category: z.string().max(64).optional(),
  description: z.string().min(1).max(500),
  content: z.string().min(1).max(200_000),
  requiresWrite: z.boolean().optional(),
  parameters: z.array(SkillParameterSchema).max(30).optional(),
});
const SkillsAppendArgs = z.object({ action: z.literal("skills_append_version_section"), skillName: z.string().min(1).max(64), content: z.string().min(1).max(100_000) });
const SkillsRetireArgs = z.object({ action: z.literal("skills_retire"), skillName: z.string().min(1).max(64) });
const PreferencesListArgs = z.object({ action: z.literal("preferences_list") });
const PreferencesAddArgs = z.object({ action: z.literal("preferences_add"), rule: z.string().min(1).max(1_000) });
const PreferencesUpdateArgs = z.object({ action: z.literal("preferences_update"), id: z.string().min(1), rule: z.string().min(1).max(1_000) });
const PreferencesRemoveArgs = z.object({ action: z.literal("preferences_remove"), id: z.string().min(1) });
const MemoryRecordArgs = z.object({ action: z.literal("memory_record"), lesson: z.string().min(1).max(2_000), topics: z.array(z.string().min(1).max(120)).min(1).max(12) });
const MemorySearchArgs = z.object({ action: z.literal("memory_search"), query: z.string().optional() });
const MemoryRemoveArgs = z.object({ action: z.literal("memory_remove"), id: z.string().min(1) });
const ProjectNameSchema = z.string().min(1).max(120);
const ProjectListArgs = z.object({ action: z.literal("project_list"), project: ProjectNameSchema.optional() });
const ProjectLinkArgs = z.object({ action: z.literal("project_link"), project: ProjectNameSchema, folder: z.string().min(1).max(260).optional() });
const ProjectUnlinkArgs = z.object({ action: z.literal("project_unlink"), project: ProjectNameSchema, folder: z.string().min(1).max(260) });
const ProjectAddArgs =z.object({ action: z.literal("project_add"), note: z.string().min(1).max(2_000), project: ProjectNameSchema.optional() });
const ProjectUpdateArgs = z.object({ action: z.literal("project_update"), id: z.string().min(1), note: z.string().min(1).max(2_000), project: ProjectNameSchema.optional() });
const ProjectRemoveArgs = z.object({ action: z.literal("project_remove"), id: z.string().min(1), project: ProjectNameSchema.optional() });
const ScriptSaveArgs = z.object({
  action: z.literal("script_save"),
  name: ScriptNameSchema,
  description: z.string().min(1).max(500),
  code: z.string().min(1).max(200_000),
  mode: ModeSchema.optional(),
  parameters: z.array(ScriptParameterSchema).max(30).optional(),
  overwrite: z.boolean().optional(),
});
const ScriptListArgs = z.object({ action: z.literal("script_list"), query: z.string().optional() });
const ScriptGetArgs = z.object({ action: z.literal("script_get"), name: ScriptNameSchema });
const ScriptRunArgs = z.object({ action: z.literal("script_run"), name: ScriptNameSchema, args: ScriptArgsSchema.optional() });
const ScriptDeleteArgs = z.object({ action: z.literal("script_delete"), name: ScriptNameSchema });

/**
 * Adds lessons whose topics appear in the code, so known pitfalls surface right where they matter, and
 * (once per session) the notes of the project the active drawing belongs to.
 */
async function withLessons(code: string, result: unknown) {
  if (!result || typeof result !== "object") return result;
  let enriched = result as Record<string, unknown>;
  try {
    const lessons = lessonsRelevantTo(code, await currentRelease());
    if (lessons.length) enriched = { ...enriched, relatedLessons: lessons };
  } catch {
    // Lessons are advice; never fail the run because of them.
  }
  const notes = await projectNotesOnce();
  if (notes) enriched = { ...enriched, projectNotes: notes };
  return enriched;
}

const sendCommand = (method: string, params: Record<string, unknown>) =>
  withApplicationConnection(async (appClient) => await appClient.sendCommand(method, params));

const CHECK_EXISTING_FIRST =
  "Before writing new code, check for an existing solution: civil3d_script action=list (ready-to-run saved scripts) " +
  "and civil3d_skills action=search (proven templates); adapt a match instead of starting from scratch.";

const SCRIPT_GLOBALS_HELP =
  "Code is a C# script body (statements, then `return <value>;` — anonymous objects, lists and dictionaries become JSON). " +
  "Globals: Document, CivilDoc (CivilDocument), Database, Transaction (use Transaction.GetObject), Editor, ModelSpace, " +
  "Args (values from `args`: Args.GetString/GetDouble/GetInt/GetBool/GetPoint/GetStringList/Get<T>/Has), Print(obj) for log lines, " +
  "Get<T>(id, mode), AddToModelSpace(entity), FromHandle(hexHandle), IsWriteMode, CancellationToken (check it in long loops). " +
  "Other drawings (e.g. a go-by to learn from): OpenDrawings lists the drawings open in this Civil 3D window (Name, Path, Active); " +
  "Reference(\"Site-GoBy\") or Reference(@\"C:\\path\\goby.dwg\") opens another drawing READ ONLY with .Database, .CivilDoc, .Get<T>(id), " +
  ".ModelSpace, .FromHandle(h) (always rolled back; a path that isn't open is read in the background). Changes always go to the active drawing. " +
  "FocusOn(min, max) / FocusOn(points) / FocusOn(ids) names the area a write is about: afterwards the view zooms to it (otherwise it pans to everything " +
  "the change touched, which for surfaces and point groups can be the whole drawing). Always call it in write scripts. " +
  "Delete point groups with RemovePointGroup(name), never PointGroup.Erase() (that leaves an empty slot in the display order). " +
  "Auto-imported: System, System.Linq, System.Collections.Generic, System.IO, System.Text, System.Text.Json(.Nodes), System.Globalization, " +
  "Autodesk.AutoCAD.{ApplicationServices,DatabaseServices,EditorInput,Geometry,Colors}, Autodesk.Civil, Autodesk.Civil.{ApplicationServices,DatabaseServices,DatabaseServices.Styles,Settings}. " +
  "Names that exist in both AutoCAD and Civil (Entity, Surface, Section, DBObject) are ambiguous — fully qualify them or use TinSurface etc. " +
  "`dynamic` is not supported. Setting CogoPoint.LabelLocation on an already-dragged label is applied relative to the old drag: call ResetLabelLocation() first. " +
  "Never move, explode or edit COGO points unless the user explicitly asks; label positions are separate. " +
  "Any exception rolls back the whole transaction. Prefer a dedicated civil3d_* tool when one exists; check civil3d_skills for working templates.";

export const SCRIPTING_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "scripting",
  actions: {
    execute: {
      action: "execute", inputSchema: ExecuteArgs, responseSchema: GenericResponseSchema,
      capabilities: ["create", "edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["executeCode"],
      execute: async (args) => withLessons(args.code as string, await sendCommand("executeCode", { code: args.code, args: args.args ?? {}, description: args.description ?? null })),
    },
    query: {
      action: "query", inputSchema: QueryArgs, responseSchema: GenericResponseSchema,
      capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["queryCode"],
      execute: async (args) => withLessons(args.code as string, await sendCommand("queryCode", { code: args.code, args: args.args ?? {}, description: args.description ?? null })),
    },
    compile: {
      action: "compile", inputSchema: CompileArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["compileCode"],
      execute: async (args) => await sendCommand("compileCode", { code: args.code, mode: args.mode ?? "write" }),
    },
    status: {
      action: "status", inputSchema: StatusArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getScriptingStatus"],
      execute: async () => await sendCommand("getScriptingStatus", {}),
    },
    command: {
      action: "command", inputSchema: CommandArgs, responseSchema: GenericResponseSchema,
      capabilities: ["edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["runAcadCommand"],
      execute: async (args) => await sendCommand("runAcadCommand", { command: args.command }),
    },
    skills_list: {
      action: "skills_list", inputSchema: SkillsListArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => listSkills(args.category as string | undefined, await currentRelease()),
    },
    skills_search: {
      action: "skills_search", inputSchema: SkillsSearchArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => searchSkills(args.query as string, await currentRelease()),
    },
    skills_read: {
      action: "skills_read", inputSchema: SkillsReadArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => {
        const release = await currentRelease();
        const skill = readSkill(args.skillName as string, release);
        const lessons = lessonsRelevantTo(`${skill.name} ${skill.content}`, release);
        return lessons.length ? { ...skill, relatedLessons: lessons } : skill;
      },
    },
    // Learning actions only change the user's knowledge-base files (never the drawing), so they don't use the
    // drawing approval flow; the tool descriptions tell the assistant to ask the user first.
    skills_write: {
      action: "skills_write", inputSchema: SkillsWriteArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => writeSkill({
        name: args.skillName as string,
        category: args.category as string | undefined,
        description: args.description as string,
        content: args.content as string,
        requiresWrite: args.requiresWrite as boolean | undefined,
        parameters: args.parameters as SkillParameter[] | undefined,
      }, await currentRelease()),
    },
    skills_append_version_section: {
      action: "skills_append_version_section", inputSchema: SkillsAppendArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: false,
      execute: async (args) => appendVersionSection(args.skillName as string, args.content as string, await currentRelease()),
    },
    skills_retire: {
      action: "skills_retire", inputSchema: SkillsRetireArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => retireSkill(args.skillName as string),
    },
    preferences_list: {
      action: "preferences_list", inputSchema: PreferencesListArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async () => ({ path: preferencesFile(), preferences: listPreferences() }),
    },
    preferences_add: {
      action: "preferences_add", inputSchema: PreferencesAddArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => addPreference(args.rule as string),
    },
    preferences_update: {
      action: "preferences_update", inputSchema: PreferencesUpdateArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => updatePreference(args.id as string, args.rule as string),
    },
    preferences_remove: {
      action: "preferences_remove", inputSchema: PreferencesRemoveArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => removePreference(args.id as string),
    },
    memory_record: {
      action: "memory_record", inputSchema: MemoryRecordArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => recordLesson(args.lesson as string, args.topics as string[], await currentRelease()),
    },
    memory_search: {
      action: "memory_search", inputSchema: MemorySearchArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => searchLessons(args.query as string | undefined, await currentRelease()),
    },
    memory_remove: {
      action: "memory_remove", inputSchema: MemoryRemoveArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => removeLesson(args.id as string, await currentRelease()),
    },
    project_list: {
      action: "project_list", inputSchema: ProjectListArgs, responseSchema: GenericResponseSchema,
      capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => listProjectNotes(args.project as string | undefined),
    },
    project_link: {
      action: "project_link", inputSchema: ProjectLinkArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => linkProjectFolder(args.project as string, args.folder as string | undefined),
    },
    project_unlink: {
      action: "project_unlink", inputSchema: ProjectUnlinkArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => unlinkProjectFolder(args.project as string, args.folder as string),
    },
    project_add: {
      action: "project_add", inputSchema: ProjectAddArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => addProjectNote(args.note as string, args.project as string | undefined),
    },
    project_update: {
      action: "project_update", inputSchema: ProjectUpdateArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => updateProjectNote(args.id as string, args.note as string, args.project as string | undefined),
    },
    project_remove: {
      action: "project_remove", inputSchema: ProjectRemoveArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => removeProjectNote(args.id as string, args.project as string | undefined),
    },
    script_save: {
      action: "script_save", inputSchema: ScriptSaveArgs, responseSchema: GenericResponseSchema,
      capabilities: ["register"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["scriptSave"],
      execute: async (args) => await sendCommand("scriptSave", {
        name: args.name, description: args.description, code: args.code, mode: args.mode ?? "write",
        parameters: args.parameters ?? [], overwrite: args.overwrite ?? false,
      }),
    },
    script_list: {
      action: "script_list", inputSchema: ScriptListArgs, responseSchema: GenericResponseSchema,
      capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["scriptList"],
      execute: async (args) => await sendCommand("scriptList", { query: args.query ?? null }),
    },
    script_get: {
      action: "script_get", inputSchema: ScriptGetArgs, responseSchema: GenericResponseSchema,
      capabilities: ["query"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["scriptGet"],
      execute: async (args) => await sendCommand("scriptGet", { name: args.name }),
    },
    script_run: {
      action: "script_run", inputSchema: ScriptRunArgs, responseSchema: GenericResponseSchema,
      capabilities: ["create", "edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["scriptRun"],
      execute: async (args) => await sendCommand("scriptRun", { name: args.name, args: args.args ?? {} }),
    },
    script_delete: {
      action: "script_delete", inputSchema: ScriptDeleteArgs, responseSchema: GenericResponseSchema,
      capabilities: ["delete"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["scriptDelete"],
      execute: async (args) => await sendCommand("scriptDelete", { name: args.name }),
    },
  },
  exposures: [
    {
      toolName: "civil3d_execute",
      displayName: "Civil 3D Execute C#",
      description:
        "Runs C# code inside Civil 3D with WRITE access to the full AutoCAD and Civil 3D .NET API (Roslyn). " +
        "Use it for anything the dedicated civil3d_* tools do not cover. " + CHECK_EXISTING_FIRST + " The transaction commits on success and all changes form one undo step. " +
        "Protected action: call civil3d_preview_action, then civil3d_request_approval, then retry with approvalToken. " +
        "Inspect first with civil3d_query, and validate tricky code with civil3d_script action=compile. " +
        "If the task will repeat, save it with civil3d_script action=save. " + SCRIPT_GLOBALS_HELP,
      inputShape: {
        code: z.string().min(1).describe("C# script body. End with `return new { ... };` to send results back."),
        args: ScriptArgsSchema.optional().describe("Named inputs readable in code via Args, e.g. {\"layer\":\"C-ROAD\",\"offset\":5}."),
        description: z.string().max(500).optional().describe("One-line summary for the audit log."),
      },
      supportedActions: ["execute"],
      resolveAction: (rawArgs) => ({ action: "execute", args: { ...rawArgs, action: "execute" } }),
    },
    {
      toolName: "civil3d_query",
      displayName: "Civil 3D Query C#",
      description:
        "Runs READ-ONLY C# code inside Civil 3D (transaction always rolled back; commands, saves, commits and file writes are blocked). " +
        "No approval needed — use it freely to inspect drawings, find objects, and compute values. " + CHECK_EXISTING_FIRST + " " + SCRIPT_GLOBALS_HELP,
      inputShape: {
        code: z.string().min(1).describe("C# script body. End with `return ...;`."),
        args: ScriptArgsSchema.optional().describe("Named inputs readable in code via Args."),
        description: z.string().max(500).optional().describe("One-line summary for the audit log."),
      },
      supportedActions: ["query"],
      resolveAction: (rawArgs) => ({ action: "query", args: { ...rawArgs, action: "query" } }),
    },
    {
      toolName: "acad_command",
      displayName: "AutoCAD Command",
      description:
        "Runs a native AutoCAD/Civil 3D command-line command with its prompt responses, e.g. " +
        "[\"_.-LAYER\",\"_M\",\"C-ROAD\",\"\"] or [\"_.ZOOM\",\"_E\"] or [\"_.LINE\",{\"x\":0,\"y\":0},{\"x\":10,\"y\":5},\"\"]. " +
        "Use \"\" for Enter, '_.' prefixes for language-independent names, and dash-prefixed names (-LAYER, -PURGE) to avoid dialogs. " +
        "Command-line output is not captured; verify with civil3d_query. Prefer civil3d_execute for anything the API can do. " +
        "Protected action (approval flow). Shell, NETLOAD, AutoLISP and similar commands are blocked.",
      inputShape: {
        command: CommandSchema.describe("Command name, or an array of command name followed by prompt responses (strings, numbers, booleans, or {x,y,z} points)."),
      },
      supportedActions: ["command"],
      resolveAction: (rawArgs) => ({ action: "command", args: { ...rawArgs, action: "command" } }),
    },
    {
      toolName: "civil3d_skills",
      displayName: "Civil 3D Skills",
      description:
        "Verified, step-by-step recipes for Civil 3D work: when to use each one, the user's rules, the pitfalls found on real drawings, " +
        "and the C# script. At the start of each chat read basics 'working_safely_in_civil3d'. Search BEFORE writing new code for " +
        "civil3d_execute/civil3d_query. Read: action=list (optional category), search (query), read (skillName). " +
        "Learn (the user's library, survives releases; add, never delete): write (skillName, description, content = markdown with " +
        "when-to-use, rules, pitfalls and a ```csharp block; optional category, requiresWrite, parameters) creates or improves a skill " +
        "verified on the running Civil 3D release (the previous version is archived; writing a built-in skill's name saves the user's version, " +
        "which then replaces it); append_version_section (skillName, content) adds what's different on this release to a skill verified on another " +
        "release (overwriting such a skill is refused); retire (skillName) archives a user skill. " +
        "Ask the user before learning; offer it when a multi-step task worked and the user is happy. Learning never changes the drawing.",
      inputShape: {
        action: z.enum(["list", "search", "read", "write", "append_version_section", "retire"]),
        category: z.string().optional(),
        query: z.string().optional(),
        skillName: z.string().optional(),
        description: z.string().optional(),
        content: z.string().optional(),
        requiresWrite: z.boolean().optional(),
        parameters: z.array(SkillParameterSchema).optional(),
      },
      supportedActions: ["skills_list", "skills_search", "skills_read", "skills_write", "skills_append_version_section", "skills_retire"],
      resolveAction: (rawArgs) => {
        const action = `skills_${String(rawArgs.action ?? "")}`;
        return { action, args: { ...rawArgs, action } };
      },
    },
    {
      toolName: "civil3d_preferences",
      displayName: "Civil 3D Preferences",
      description:
        "The user's standing rules (e.g. 'we plot at 1\" = 30\''; 'never move COGO points'; 'hide group is HIDDEN POINTS'). They are " +
        "included in the instructions every chat receives. action=list; add (rule) when the user states a standing rule or standard; " +
        "update (id, rule); remove (id) when the user retracts one. Ask before adding or changing.",
      inputShape: {
        action: z.enum(["list", "add", "update", "remove"]),
        rule: z.string().optional(),
        id: z.string().optional(),
      },
      supportedActions: ["preferences_list", "preferences_add", "preferences_update", "preferences_remove"],
      resolveAction: (rawArgs) => {
        const action = `preferences_${String(rawArgs.action ?? "")}`;
        return { action, args: { ...rawArgs, action } };
      },
    },
    {
      toolName: "civil3d_memory",
      displayName: "Civil 3D Lessons",
      description:
        "Lessons: short gotchas and facts learned while working (e.g. 'setting CogoPoint.LabelLocation on a dragged label is applied " +
        "relative to the old drag; call ResetLabelLocation first'). Each is stamped with the Civil 3D release and tagged with topics " +
        "(API names, tools, subjects); matching lessons are returned automatically with civil3d_execute/civil3d_query results and skill " +
        "reads. action=record (lesson, topics) right after you discover something non-obvious; search (query, optional); remove (id) only " +
        "for a wrong lesson from the running release (lessons from other releases and built-in lessons are kept). Lessons must be GENERAL " +
        "(useful on any project): no drawing names, point numbers, coordinates, dates, point group/layer names or site features. " +
        "Put project-specific facts in civil3d_project_notes instead.",
      inputShape: {
        action: z.enum(["record", "search", "remove"]),
        lesson: z.string().optional(),
        topics: z.array(z.string()).optional(),
        query: z.string().optional(),
        id: z.string().optional(),
      },
      supportedActions: ["memory_record", "memory_search", "memory_remove"],
      resolveAction: (rawArgs) => {
        const action = `memory_${String(rawArgs.action ?? "")}`;
        return { action, args: { ...rawArgs, action } };
      },
    },
    {
      toolName: "civil3d_project_notes",
      displayName: "Civil 3D Project Notes",
      description:
        "Notes about ONE project, kept only on this computer (never in general lessons or skills): what its point groups and layers are " +
        "for, which survey dates or point ranges belong to which feature, which drawing is the go-by, sheet conventions, decisions the user " +
        "made. A project is matched by folder; its notes are returned with the first civil3d_execute/civil3d_query result of a session. " +
        "action=list (current drawing's project, or project); link (project, optional folder; default the active drawing's folder) to create " +
        "a project or add another folder to it at any time (a project can span several folders); unlink (project, folder) to drop a folder " +
        "(notes are kept); add (note) / update (id, note) / remove (id), optional project. Ask before adding or changing.",
      inputShape: {
        action: z.enum(["list", "link", "unlink", "add", "update", "remove"]),
        project: z.string().optional(),
        folder: z.string().optional(),
        note: z.string().optional(),
        id: z.string().optional(),
      },
      supportedActions: ["project_list", "project_link", "project_unlink", "project_add", "project_update", "project_remove"],
      resolveAction: (rawArgs) => {
        const action = `project_${String(rawArgs.action ?? "")}`;
        return { action, args: { ...rawArgs, action } };
      },
    },
    {
      toolName: "civil3d_script",
      displayName: "Civil 3D Saved Scripts",
      description:
        "Manages reusable C# scripts for repetitive tasks, stored as editable .csx files on the user's machine. " +
        "action=save (name, description, code, mode write|read, typed parameters [{name,type,required,default,description}] read via Args), " +
        "list (optional query), get (name, returns code), run (name, args), delete (name), compile (code, mode — check code without running), " +
        "status (scripts folder, sandbox, timeout). Save a script once a civil3d_execute/civil3d_query snippet works and the task is likely to recur; " +
        "search with action=list before writing new code. Users can also run saved scripts in Civil 3D by typing MCPRUN. " +
        "Nothing is lost: save with overwrite=true archives the previous version (scripts/_archive) and bumps its version, and delete " +
        "retires the script to the archive. save, run and delete are protected actions (approval flow).",
      inputShape: {
        action: z.enum(["save", "list", "get", "run", "delete", "compile", "status"]),
        name: z.string().optional(),
        description: z.string().optional(),
        code: z.string().optional(),
        mode: ModeSchema.optional(),
        parameters: z.array(ScriptParameterSchema).optional(),
        overwrite: z.boolean().optional(),
        args: ScriptArgsSchema.optional(),
        query: z.string().optional(),
      },
      supportedActions: ["script_save", "script_list", "script_get", "script_run", "script_delete", "compile", "status"],
      resolveAction: (rawArgs) => {
        const raw = String(rawArgs.action ?? "");
        const action = raw === "compile" || raw === "status" ? raw : `script_${raw}`;
        return { action, args: { ...rawArgs, action } };
      },
    },
  ],
};
