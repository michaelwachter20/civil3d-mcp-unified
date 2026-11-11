/**
 * The grading domain: `civil3d_grading` (grading groups, gradings, criteria, feature lines), the `civil3d_feature_line`
 * alias (its own `list` / `get` / `export_as_polyline` operations, mapped onto the `feature_line_*` actions) and one
 * alias per offered action (`civil3d_grading_group_list`, `civil3d_feature_line_create`, ...). The plugin side is
 * GradingCommands.cs; domainRuntime.ts turns the definition into validated, approval-checked MCP tools. Every action
 * forwards to one plugin method; nothing is worked out in Node.
 *
 * Civil 3D's API doesn't reach grading groups or gradings, so `group_list` only counts gradings, and the other group
 * and grading actions (group_get, group_create, group_delete, group_volume, group_surface_create, list, get, create,
 * delete) are in no exposure and declared `unsupported` (refused with CIVIL3D.API_ERROR, no approval, no plugin call,
 * if the orchestrator ever reaches one). Criteria and feature lines work. Coordinates are in drawing units.
 *
 * Approval (approvalPolicy.ts): group_list, criteria_list and the feature line reads need none. feature_line_create
 * and feature_line_export_as_polyline write the drawing and need an approvalToken.
 */
import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/** Why every grading group / grading action but group_list is unsupported (GradingCommands.cs's OutOfReach). */
const GRADING_OUT_OF_REACH =
  "Civil 3D's API doesn't reach grading groups or gradings, in .NET or COM. Use the Grading Creation Tools. A group's " +
  "surface is an ordinary surface, so the surface tools can read it and its volumes.";

/** A point the plugin answers with, in drawing units; a missing elevation reads as 0. For answers only. */
const Point3DSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional().default(0),
});

/**
 * A point of a feature line to create. The elevation is required: the plugin refuses a point without z rather than
 * assume elevation 0 (GradingCommands.cs), and a default here would hide that by sending 0 for the caller.
 */
const FeatureLinePointSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().describe("Elevation in drawing units. Required: elevation 0 is never assumed."),
});

const FeatureLineSummarySchema = z.object({
  name: z.string().optional(),
  handle: z.string().optional(),
  layer: z.string().optional(),
  style: z.string().optional(),
}).passthrough();

const FeatureLineListResponseSchema = z.object({
  featureLines: z.array(FeatureLineSummarySchema),
});

/** One feature line with its vertices; lengths and elevations are in the drawing units that `units` names. */
const FeatureLineDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  layer: z.string(),
  style: z.string(),
  length: z.number(),
  vertexCount: z.number(),
  vertices: z.array(Point3DSchema),
  minElevation: z.number(),
  maxElevation: z.number(),
  units: z.string(),
});

const GenericGradingResponseSchema = z.object({}).passthrough();

/**
 * Every argument any `civil3d_grading` action takes, all optional except `action`: the one input schema MCP clients
 * see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call.
 */
const canonicalGradingInputShape = {
  // The group and grading actions Civil 3D's API can't reach are left out on purpose (they are `unsupported`).
  action: z.enum([
    "group_list",
    "criteria_list",
    "feature_line_list",
    "feature_line_get",
    "feature_line_export_as_polyline",
    "feature_line_create",
  ]),
  name: z.string().optional(),
  targetLayer: z.string().optional(),
  points: z.array(FeatureLinePointSchema).optional(),
  layer: z.string().optional(),
};

const GradingGroupListArgsSchema = z.object({
  action: z.literal("group_list"),
});

const GradingGroupGetArgsSchema = z.object({
  action: z.literal("group_get"),
  name: z.string(),
});

const GradingGroupCreateArgsSchema = z.object({
  action: z.literal("group_create"),
  name: z.string(),
  description: z.string().optional(),
  useProjection: z.boolean().optional(),
});

const GradingGroupDeleteArgsSchema = z.object({
  action: z.literal("group_delete"),
  name: z.string(),
});

const GradingGroupVolumeArgsSchema = z.object({
  action: z.literal("group_volume"),
  name: z.string(),
});

/** `surfaceName` optional. The plugin currently refuses this action (see the file header). */
const GradingGroupSurfaceCreateArgsSchema = z.object({
  action: z.literal("group_surface_create"),
  name: z.string(),
  surfaceName: z.string().optional(),
});

const GradingListArgsSchema = z.object({
  action: z.literal("list"),
  groupName: z.string(),
});

const GradingGetArgsSchema = z.object({
  action: z.literal("get"),
  groupName: z.string(),
  handle: z.string(),
});

/**
 * A grading from a feature line (by name) into a group, with a criteria name from criteria_list (Civil 3D's default
 * when left out) on the given side (right when left out). The plugin currently refuses it (see the file header).
 */
const GradingCreateArgsSchema = z.object({
  action: z.literal("create"),
  groupName: z.string(),
  featureLineName: z.string(),
  criteriaName: z.string().optional(),
  side: z.enum(["left", "right", "both"]).optional(),
});

const GradingDeleteArgsSchema = z.object({
  action: z.literal("delete"),
  groupName: z.string(),
  handle: z.string(),
});

const GradingCriteriaListArgsSchema = z.object({
  action: z.literal("criteria_list"),
});

const FeatureLineListArgsSchema = z.object({
  action: z.literal("feature_line_list"),
});

const FeatureLineGetArgsSchema = z.object({
  action: z.literal("feature_line_get"),
  name: z.string(),
});

const FeatureLineExportArgsSchema = z.object({
  action: z.literal("feature_line_export_as_polyline"),
  name: z.string(),
  targetLayer: z.string().optional(),
});

/** A new feature line through at least two points, in order, on layer "0" unless `layer` says otherwise. */
const FeatureLineCreateArgsSchema = z.object({
  action: z.literal("feature_line_create"),
  points: z.array(FeatureLinePointSchema).min(2),
  name: z.string().optional(),
  layer: z.string().optional(),
});

/**
 * The grading domain: `civil3d_grading` (grading groups, gradings, grading criteria, feature lines, and a surface
 * or volume from a grading group) and its aliases. Every action is one call to GradingCommands.cs; there, every
 * group and grading method except listGradingGroups throws CIVIL3D.API_ERROR at once, so those actions are declared
 * `unsupported` and never sent; the criteria and feature line methods do real work. Listed in toolManifest.ts;
 * exposures[0] is the canonical tool.
 */
export const GRADING_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "grading",
  actions: {
    group_list: {
      action: "group_list",
      inputSchema: GradingGroupListArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listGradingGroups"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listGradingGroups", {}),
      ),
    },
    group_get: {
      action: "group_get",
      inputSchema: GradingGroupGetArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getGradingGroup"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getGradingGroup", { name: args.name }),
      ),
    },
    group_create: {
      action: "group_create",
      inputSchema: GradingGroupCreateArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createGradingGroup"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createGradingGroup", {
          name: args.name,
          description: args.description ?? "",
          useProjection: args.useProjection ?? false,
        }),
      ),
    },
    group_delete: {
      action: "group_delete",
      inputSchema: GradingGroupDeleteArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["delete", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteGradingGroup"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteGradingGroup", { name: args.name }),
      ),
    },
    // Meant to give the grading group's cut and fill; unsupported like every group and grading action except group_list.
    group_volume: {
      action: "group_volume",
      inputSchema: GradingGroupVolumeArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getGradingGroupVolume"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getGradingGroupVolume", { name: args.name }),
      ),
    },
    group_surface_create: {
      action: "group_surface_create",
      inputSchema: GradingGroupSurfaceCreateArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["create", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurfaceFromGradingGroup"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurfaceFromGradingGroup", {
          name: args.name,
          surfaceName: args.surfaceName ?? null,
        }),
      ),
    },
    list: {
      action: "list",
      inputSchema: GradingListArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listGradings"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listGradings", { groupName: args.groupName }),
      ),
    },
    get: {
      action: "get",
      inputSchema: GradingGetArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getGrading"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getGrading", {
          groupName: args.groupName,
          handle: args.handle,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: GradingCreateArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createGrading"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createGrading", {
          groupName: args.groupName,
          featureLineName: args.featureLineName,
          criteriaName: args.criteriaName ?? null,
          side: args.side ?? "right",
        }),
      ),
    },
    delete: {
      action: "delete",
      inputSchema: GradingDeleteArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["delete", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteGrading"],
      unsupported: GRADING_OUT_OF_REACH,
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteGrading", {
          groupName: args.groupName,
          handle: args.handle,
        }),
      ),
    },
    criteria_list: {
      action: "criteria_list",
      inputSchema: GradingCriteriaListArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listGradingCriteria"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listGradingCriteria", {}),
      ),
    },
    feature_line_list: {
      action: "feature_line_list",
      inputSchema: FeatureLineListArgsSchema,
      responseSchema: FeatureLineListResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listFeatureLines"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listFeatureLines", {}),
      ),
    },
    feature_line_get: {
      action: "feature_line_get",
      inputSchema: FeatureLineGetArgsSchema,
      responseSchema: FeatureLineDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getFeatureLine"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getFeatureLine", { name: args.name }),
      ),
    },
    // "Export" means into the drawing: the plugin adds a 3D polyline through the feature line's vertices to model
    // space (on `targetLayer`, else the feature line's layer) and leaves the feature line as it is. No file is written,
    // so it is declared a `create` (the `export` capability means a file); non-retryable, so approval-gated.
    feature_line_export_as_polyline: {
      action: "feature_line_export_as_polyline",
      inputSchema: FeatureLineExportArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["query", "create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportFeatureLineAsPolyline"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportFeatureLineAsPolyline", {
          name: args.name,
          targetLayer: args.targetLayer,
        }),
      ),
    },
    feature_line_create: {
      action: "feature_line_create",
      inputSchema: FeatureLineCreateArgsSchema,
      responseSchema: GenericGradingResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createFeatureLine"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createFeatureLine", {
          points: args.points,
          name: args.name ?? null,
          layer: args.layer ?? "0",
        }),
      ),
    },
  },
  // The canonical tool first; the rest are aliases, listed to MCP clients only with CIVIL3D_ENABLE_TOOL_ALIASES=true.
  exposures: [
    {
      toolName: "civil3d_grading",
      displayName: "Civil 3D Grading",
      description: "Works with Civil 3D feature lines and grading criteria. Civil 3D's API doesn't reach grading groups or gradings, so group_list only counts gradings; create and edit them with the Grading Creation Tools.",
      inputShape: canonicalGradingInputShape,
      supportedActions: [
        "group_list",
        "criteria_list",
        "feature_line_list",
        "feature_line_get",
        "feature_line_export_as_polyline",
        "feature_line_create",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_feature_line",
      displayName: "Civil 3D Feature Line",
      description: "Reads Civil 3D feature lines and supports exporting them as 3D polylines.",
      inputShape: {
        action: z.enum(["list", "get", "export_as_polyline"]),
        name: z.string().optional(),
        targetLayer: z.string().optional(),
      },
      supportedActions: ["feature_line_list", "feature_line_get", "feature_line_export_as_polyline"],
      operations: ["list", "get", "export_as_polyline"],
      // Anything other than list or get becomes the export; the `action` enum above is what keeps other values out.
      resolveAction: (rawArgs) => ({
        action:
          rawArgs.action === "list"
            ? "feature_line_list"
            : rawArgs.action === "get"
              ? "feature_line_get"
              : "feature_line_export_as_polyline",
        args: rawArgs.action === "list"
          ? { action: "feature_line_list" }
          : rawArgs.action === "get"
            ? { action: "feature_line_get", name: rawArgs.name }
            : {
              action: "feature_line_export_as_polyline",
              name: rawArgs.name,
              targetLayer: rawArgs.targetLayer,
            },
      }),
    },
    {
      toolName: "civil3d_grading_group_list",
      displayName: "Civil 3D Grading Group List",
      description: "Lists all Civil 3D grading groups in the drawing.",
      inputShape: {},
      supportedActions: ["group_list"],
      resolveAction: () => ({ action: "group_list", args: { action: "group_list" } }),
    },
    // The group and grading actions Civil 3D's API can't reach (group_get ... delete) have no alias: they are kept
    // only as `unsupported` actions, so nothing offers them.
    {
      toolName: "civil3d_grading_criteria_list",
      displayName: "Civil 3D Grading Criteria List",
      description: "Lists the available Civil 3D grading criteria sets and criteria names.",
      inputShape: {},
      supportedActions: ["criteria_list"],
      resolveAction: () => ({ action: "criteria_list", args: { action: "criteria_list" } }),
    },
    {
      toolName: "civil3d_feature_line_create",
      displayName: "Civil 3D Feature Line Create",
      description: "Creates a new Civil 3D feature line from an ordered list of 3D points.",
      inputShape: {
        points: z.array(FeatureLinePointSchema).min(2),
        name: z.string().optional(),
        layer: z.string().optional(),
      },
      supportedActions: ["feature_line_create"],
      resolveAction: (rawArgs) => ({
        action: "feature_line_create",
        args: {
          action: "feature_line_create",
          points: rawArgs.points,
          name: rawArgs.name,
          layer: rawArgs.layer,
        },
      }),
    },
  ],
};
