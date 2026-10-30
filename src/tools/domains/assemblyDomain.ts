import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The assembly domain: `civil3d_assembly` (corridor assemblies and their subassemblies) and its aliases
 * `civil3d_assembly_create`, `civil3d_subassembly_create` and `civil3d_assembly_edit`. The plugin side is
 * AssemblyCreationCommands.cs, which uses only the documented Civil 3D API; domainRuntime.ts turns the definition into
 * validated, approval-checked MCP tools. Every action forwards to one plugin method; Node only splits `insertionPoint`
 * into the plugin's `insertX` / `insertY`.
 *
 * Assemblies are found by name, subassemblies by name within their assembly. corridorDomain.ts uses an assembly by
 * name when it adds a corridor region.
 */

const GenericAssemblyResponseSchema = z.object({}).passthrough();

/** A subassembly parameter value: a number (in the units the subassembly's own help gives), text or a flag. */
const AssemblyParameterValueSchema = z.union([z.number(), z.string(), z.boolean()]);

const AssemblySummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  subassemblyCount: z.number(),
  usedByCorridors: z.array(z.string()),
});

const AssemblyListResponseSchema = z.object({
  assemblies: z.array(AssemblySummarySchema),
});

const SubassemblySchema = z.object({
  name: z.string(),
  side: z.enum(["left", "right", "none"]),
  className: z.string(),
  parameters: z.record(z.string(), AssemblyParameterValueSchema),
});

const AssemblyDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  style: z.string(),
  subassemblies: z.array(SubassemblySchema),
  usedByCorridors: z.array(z.string()),
});

/**
 * Every argument any `civil3d_assembly` action takes, all optional except `action`: the one input schema MCP clients
 * see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call. Note the two
 * spellings of side: answers use lowercase ("left"), the create argument is capitalised ("Left").
 */
const canonicalAssemblyInputShape = {
  action: z.enum(["list", "get", "create", "create_subassembly", "edit"]),
  name: z.string().optional(),
  insertionPoint: z.object({
    x: z.number(),
    y: z.number(),
  }).optional(),
  description: z.string().optional(),
  assemblyType: z.enum(["UndividedCrownedRoad", "UndividedPlanarRoad", "DividedCrownedRoad", "DividedPlanarRoad", "Other", "Railway"]).optional(),
  assemblyName: z.string().optional(),
  subassemblyType: z.string().optional(),
  side: z.enum(["Left", "Right", "Both"]).optional(),
  parameters: z.record(z.string(), AssemblyParameterValueSchema).optional(),
  subassemblyName: z.string().optional(),
  delete: z.boolean().optional(),
};

const AssemblyListArgsSchema = z.object({
  action: z.literal("list"),
});

const AssemblyGetArgsSchema = z.object({
  action: z.literal("get"),
  name: z.string(),
});

/** An empty assembly (no subassemblies yet) of the given Civil 3D assembly type, at a model-space point. */
const AssemblyCreateArgsSchema = z.object({
  action: z.literal("create"),
  name: z.string(),
  insertionPoint: z.object({
    x: z.number(),
    y: z.number(),
  }),
  assemblyType: z.enum(["UndividedCrownedRoad", "UndividedPlanarRoad", "DividedCrownedRoad", "DividedPlanarRoad", "Other", "Railway"]),
  description: z.string().optional(),
});

/**
 * Imports a stock subassembly (`subassemblyType`, as Civil 3D's stock catalog names it) and attaches it to the
 * assembly; "Both" makes two, one per side. Each gets a unique name "<type>-<side>-<id>" and the given parameters.
 * The plugin refuses (and commits nothing) when the subassembly has no writable Side parameter.
 */
const AssemblyCreateSubassemblyArgsSchema = z.object({
  action: z.literal("create_subassembly"),
  assemblyName: z.string(),
  subassemblyType: z.string(),
  side: z.enum(["Left", "Right", "Both"]),
  parameters: z.record(z.string(), AssemblyParameterValueSchema).optional(),
});

/**
 * Three things in one action: without `subassemblyName` it lists the assembly's subassemblies; with it and
 * `delete: true` it erases that subassembly; otherwise it writes `parameters` to it, refusing the whole edit (nothing
 * committed) if any parameter isn't writable.
 */
const AssemblyEditArgsSchema = z.object({
  action: z.literal("edit"),
  assemblyName: z.string(),
  subassemblyName: z.string().optional(),
  parameters: z.record(z.string(), AssemblyParameterValueSchema).optional(),
  delete: z.boolean().optional(),
});

/**
 * The assembly domain: `civil3d_assembly` (list and inspect assemblies, create assemblies and subassemblies, edit)
 * and its aliases. Every action is one call to AssemblyCreationCommands.cs. Listed in toolManifest.ts;
 * exposures[0] is the canonical tool.
 */
export const ASSEMBLY_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "assembly",
  actions: {
    list: {
      action: "list",
      inputSchema: AssemblyListArgsSchema,
      responseSchema: AssemblyListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listAssemblies"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listAssemblies", {}),
      ),
    },
    get: {
      action: "get",
      inputSchema: AssemblyGetArgsSchema,
      responseSchema: AssemblyDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getAssembly"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getAssembly", {
          name: args.name,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: AssemblyCreateArgsSchema,
      responseSchema: GenericAssemblyResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createAssembly"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const insertionPoint = args.insertionPoint as { x: number; y: number };
        return await appClient.sendCommand("createAssembly", {
          name: args.name,
          insertX: insertionPoint.x,
          insertY: insertionPoint.y,
          assemblyType: args.assemblyType,
          description: args.description ?? "",
        });
      }),
    },
    create_subassembly: {
      action: "create_subassembly",
      inputSchema: AssemblyCreateSubassemblyArgsSchema,
      responseSchema: GenericAssemblyResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSubassembly"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSubassembly", {
          assemblyName: args.assemblyName,
          subassemblyType: args.subassemblyType,
          side: args.side,
          parameters: args.parameters ?? {},
        }),
      ),
    },
    // Declared with `delete` because it can erase a subassembly, so every call needs approval, even one that only
    // lists. The plugin runs the listing inside a write too (an undo step that changes nothing); `get` is the
    // read-only way to inspect.
    edit: {
      action: "edit",
      inputSchema: AssemblyEditArgsSchema,
      responseSchema: GenericAssemblyResponseSchema,
      capabilities: ["inspect", "edit", "delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editAssembly"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("editAssembly", {
          assemblyName: args.assemblyName,
          subassemblyName: args.subassemblyName ?? null,
          parameters: args.parameters ?? null,
          delete: args.delete ?? false,
        }),
      ),
    },
  },
  // The canonical tool first; the rest are aliases, listed to MCP clients only with CIVIL3D_ENABLE_TOOL_ALIASES=true.
  exposures: [
    {
      toolName: "civil3d_assembly",
      displayName: "Civil 3D Assembly",
      description: "Lists, inspects, creates, and edits Civil 3D assemblies and subassemblies through a single domain tool.",
      inputShape: canonicalAssemblyInputShape,
      supportedActions: ["list", "get", "create", "create_subassembly", "edit"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_assembly_create",
      displayName: "Civil 3D Assembly Create",
      description: "Creates a new Civil 3D assembly at a specified model-space location.",
      inputShape: {
        name: z.string(),
        insertionPoint: z.object({
          x: z.number(),
          y: z.number(),
        }),
        assemblyType: z.enum(["UndividedCrownedRoad", "UndividedPlanarRoad", "DividedCrownedRoad", "DividedPlanarRoad", "Other", "Railway"]),
        description: z.string().optional(),
      },
      supportedActions: ["create"],
      resolveAction: (rawArgs) => ({
        action: "create",
        args: {
          action: "create",
          name: rawArgs.name,
          insertionPoint: rawArgs.insertionPoint,
          assemblyType: rawArgs.assemblyType,
          description: rawArgs.description,
        },
      }),
    },
    {
      toolName: "civil3d_subassembly_create",
      displayName: "Civil 3D Subassembly Create",
      description: "Adds a subassembly from the Civil 3D catalog to an existing assembly.",
      inputShape: {
        assemblyName: z.string(),
        subassemblyType: z.string(),
        side: z.enum(["Left", "Right", "Both"]),
        parameters: z.record(z.string(), AssemblyParameterValueSchema).optional(),
      },
      supportedActions: ["create_subassembly"],
      resolveAction: (rawArgs) => ({
        action: "create_subassembly",
        args: {
          action: "create_subassembly",
          assemblyName: rawArgs.assemblyName,
          subassemblyType: rawArgs.subassemblyType,
          side: rawArgs.side,
          parameters: rawArgs.parameters,
        },
      }),
    },
    {
      toolName: "civil3d_assembly_edit",
      displayName: "Civil 3D Assembly Edit",
      description: "Inspects or modifies an existing Civil 3D assembly, including subassembly parameter edits and deletion.",
      inputShape: {
        assemblyName: z.string(),
        subassemblyName: z.string().optional(),
        parameters: z.record(z.string(), AssemblyParameterValueSchema).optional(),
        delete: z.boolean().optional(),
      },
      supportedActions: ["edit"],
      resolveAction: (rawArgs) => ({
        action: "edit",
        args: {
          action: "edit",
          assemblyName: rawArgs.assemblyName,
          subassemblyName: rawArgs.subassemblyName,
          parameters: rawArgs.parameters,
          delete: rawArgs.delete,
        },
      }),
    },
  ],
};
