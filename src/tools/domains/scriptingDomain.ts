import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { listSkills, readSkill, searchSkills } from "../skillsLibrary.js";

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
      execute: async (args) => await sendCommand("executeCode", { code: args.code, args: args.args ?? {}, description: args.description ?? null }),
    },
    query: {
      action: "query", inputSchema: QueryArgs, responseSchema: GenericResponseSchema,
      capabilities: ["query", "analyze"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["queryCode"],
      execute: async (args) => await sendCommand("queryCode", { code: args.code, args: args.args ?? {}, description: args.description ?? null }),
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
      execute: async (args) => listSkills(args.category as string | undefined),
    },
    skills_search: {
      action: "skills_search", inputSchema: SkillsSearchArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => searchSkills(args.query as string),
    },
    skills_read: {
      action: "skills_read", inputSchema: SkillsReadArgs, responseSchema: GenericResponseSchema,
      capabilities: ["inspect"], requiresActiveDrawing: false, safeForRetry: true,
      execute: async (args) => readSkill(args.skillName as string),
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
      displayName: "Civil 3D Code Skills",
      description:
        "Browses the skills library: documented, proven C# templates for Civil 3D/AutoCAD tasks, with the rules and pitfalls learned on real drawings. " +
        "Search it BEFORE writing new code for civil3d_execute/civil3d_query. " +
        "action=list (optional category), search (query), or read (skillName) to get the full template, then adapt it for civil3d_query or civil3d_execute.",
      inputShape: {
        action: z.enum(["list", "search", "read"]),
        category: z.string().optional(),
        query: z.string().optional(),
        skillName: z.string().optional(),
      },
      supportedActions: ["skills_list", "skills_search", "skills_read"],
      resolveAction: (rawArgs) => {
        const action = `skills_${String(rawArgs.action ?? "")}`;
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
        "save, run and delete are protected actions (approval flow).",
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
