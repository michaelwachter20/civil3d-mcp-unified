import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The section domain: `civil3d_section` (sample lines, section data, section views) and its aliases
 * `civil3d_section_view_create`, `_view_list`, `_view_update_style`, `_view_group_create` and `_view_export`. The
 * plugin side is SectionCommands.cs; domainRuntime.ts turns the definition at the bottom into validated,
 * approval-checked MCP tools. Every action forwards to one plugin method; the only work in Node is splitting
 * `insertionPoint` into the plugin's `insertionX` / `insertionY`.
 *
 * Sample line groups are always looked up under an alignment, by name. Stations, offsets and widths are in drawing
 * units. Section view placement (rows, gaps) follows the drawing's draft placement settings, which the .NET API
 * doesn't let a call change.
 */

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericSectionResponseSchema = z.object({}).passthrough();

const SampleLineGroupSchema = z.object({
  name: z.string(),
  handle: z.string(),
  sampleLineCount: z.number(),
  stations: z.array(z.number()),
});

const SectionListSampleLinesResponseSchema = z.object({
  sampleLineGroups: z.array(SampleLineGroupSchema),
});

/**
 * The section at one sample line: per sampled surface, matching lists of offsets and elevations.
 * NOTE: the plugin's getSectionData always answers with an empty `surfaces` list (it finds the sample line but never
 * reads its sections), so this only confirms the sample line exists; view_export is the action that reads section
 * points.
 */
const SectionDataResponseSchema = z.object({
  station: z.number(),
  surfaces: z.array(z.object({
    surfaceName: z.string(),
    offsets: z.array(z.number()),
    elevations: z.array(z.number()),
  })),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
  }),
});

// ─── Per-action input schemas ─────────────────────────────────────────────────

const SectionListSampleLinesArgsSchema = z.object({
  action: z.literal("list_sample_lines"),
  alignmentName: z.string(),
});

/** `station` must match an existing sample line's station (to 0.0001); it is not interpolated. */
const SectionGetDataArgsSchema = z.object({
  action: z.literal("get_section_data"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  station: z.number(),
});

/**
 * A new sample line group `groupName` on the alignment, with one sample line per station: the given `stations`, or
 * every `interval` from the alignment's start station. Each line runs `leftWidth` left and `rightWidth` right of the
 * alignment and is named "SL-<station>". `surfaces` names the sources to sample; an empty list samples every source.
 * The plugin always creates a new group; it doesn't add lines to an existing one.
 */
const SectionCreateSampleLinesArgsSchema = z.object({
  action: z.literal("create_sample_lines"),
  alignmentName: z.string(),
  groupName: z.string(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number(),
  rightWidth: z.number(),
  surfaces: z.array(z.string()),
}).superRefine((value, ctx) => {
  if (!value.stations && value.interval === undefined) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Either stations or interval is required.",
      path: ["stations"],
    });
  }
});

/**
 * Section views for every sample line of the group, placed from `insertionPoint` [x, y] in model space. Style and
 * band set are names (the drawing's defaults when left out); the offsets and station range narrow what the views
 * show.
 */
const SectionViewCreateArgsSchema = z.object({
  action: z.literal("view_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.tuple([z.number(), z.number()]),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().nonnegative().optional(),
  rightOffset: z.number().nonnegative().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

const SectionViewListArgsSchema = z.object({
  action: z.literal("view_list"),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
});

/**
 * Needs `style` or `bandSetStyle` (the plugin refuses a call with neither). `applyToAll` is true unless set; false changes only
 * the group's first section view.
 */
const SectionViewUpdateStyleArgsSchema = z.object({
  action: z.literal("view_update_style"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  applyToAll: z.boolean().optional(),
});

const SectionViewGroupCreateArgsSchema = z.object({
  action: z.literal("view_group_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.tuple([z.number(), z.number()]),
  style: z.string().optional(),
  plotStyle: z.string().optional(),
});

/**
 * Writes the group's section points to a CSV file (Station, Source, Offset and, unless `includeElevations` is false,
 * Elevation), optionally only between two stations. Despite the name it exports data, not views. The path goes
 * through FileBoundary's export roots in the plugin and an existing file is kept unless `overwrite` is true.
 */
const SectionViewExportArgsSchema = z.object({
  action: z.literal("view_export"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

// ─── Canonical input shape ────────────────────────────────────────────────────

/**
 * Every argument any `civil3d_section` action takes, all optional except `action`: the one input schema MCP clients
 * see. The per-action `*ArgsSchema` objects above say what each action needs and validate the call.
 */
const canonicalSectionInputShape = {
  action: z.enum([
    "list_sample_lines",
    "get_section_data",
    "create_sample_lines",
    "view_create",
    "view_list",
    "view_update_style",
    "view_group_create",
    "view_export",
  ]),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
  station: z.number().optional(),
  groupName: z.string().optional(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number().optional(),
  rightWidth: z.number().optional(),
  surfaces: z.array(z.string()).optional(),
  insertionPoint: z.tuple([z.number(), z.number()]).optional(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().nonnegative().optional(),
  rightOffset: z.number().nonnegative().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
  applyToAll: z.boolean().optional(),
  plotStyle: z.string().optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
};

// ─── Domain definition ────────────────────────────────────────────────────────

/**
 * The section domain: `civil3d_section` (sample line groups, sample lines, section data, section views and view
 * groups, view styles, section data export) and its aliases. Every action is one call to SectionCommands.cs.
 * Sample-line sources are better changed with the script helpers SectionSources / SetSectionSources (README);
 * a direct GetSectionSources call on a group opened for read crashes Civil 3D. Listed in toolManifest.ts;
 * exposures[0] is the canonical tool.
 */
export const SECTION_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "section",
  actions: {
    list_sample_lines: {
      action: "list_sample_lines",
      inputSchema: SectionListSampleLinesArgsSchema,
      responseSchema: SectionListSampleLinesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSampleLineGroups"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSampleLineGroups", {
          alignmentName: args.alignmentName,
        }),
      ),
    },
    get_section_data: {
      action: "get_section_data",
      inputSchema: SectionGetDataArgsSchema,
      responseSchema: SectionDataResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          station: args.station,
        }),
      ),
    },
    create_sample_lines: {
      action: "create_sample_lines",
      inputSchema: SectionCreateSampleLinesArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSampleLines"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSampleLines", {
          alignmentName: args.alignmentName,
          groupName: args.groupName,
          stations: args.stations,
          interval: args.interval,
          leftWidth: args.leftWidth,
          rightWidth: args.rightWidth,
          surfaces: args.surfaces,
        }),
      ),
    },
    view_create: {
      action: "view_create",
      inputSchema: SectionViewCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViews", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            bandSetStyle: args.bandSetStyle ?? null,
            leftOffset: args.leftOffset ?? null,
            rightOffset: args.rightOffset ?? null,
            stationStart: args.stationStart ?? null,
            stationEnd: args.stationEnd ?? null,
          });
        },
      ),
    },
    view_list: {
      action: "view_list",
      inputSchema: SectionViewListArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSectionViews", {
          alignmentName: args.alignmentName ?? null,
          sampleLineGroupName: args.sampleLineGroupName ?? null,
        }),
      ),
    },
    view_update_style: {
      action: "view_update_style",
      inputSchema: SectionViewUpdateStyleArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updateSectionViewStyles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updateSectionViewStyles", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          style: args.style ?? null,
          bandSetStyle: args.bandSetStyle ?? null,
          applyToAll: args.applyToAll ?? true,
        }),
      ),
    },
    view_group_create: {
      action: "view_group_create",
      inputSchema: SectionViewGroupCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViewGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViewGroup", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            plotStyle: args.plotStyle ?? null,
          });
        },
      ),
    },
    view_export: {
      action: "view_export",
      inputSchema: SectionViewExportArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          includeElevations: args.includeElevations ?? true,
          stationStart: args.stationStart ?? null,
          stationEnd: args.stationEnd ?? null,
        }),
      ),
    },
  },
  // The canonical tool first; the view aliases are listed to MCP clients only with CIVIL3D_ENABLE_TOOL_ALIASES=true.
  exposures: [
    {
      toolName: "civil3d_section",
      displayName: "Civil 3D Section",
      description: "Reads Civil 3D section data, manages sample lines, and controls section view creation and export through a single domain tool.",
      inputShape: canonicalSectionInputShape,
      supportedActions: [
        "list_sample_lines",
        "get_section_data",
        "create_sample_lines",
        "view_create",
        "view_list",
        "view_update_style",
        "view_group_create",
        "view_export",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_section_view_create",
      displayName: "Civil 3D Section View Create",
      description: "Create Civil 3D section views for a sample line group at the specified insertion point. Optionally applies a style, band set, and constrains to a station range.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.tuple([z.number(), z.number()]),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        leftOffset: z.number().nonnegative().optional(),
        rightOffset: z.number().nonnegative().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_create"],
      resolveAction: (rawArgs) => ({
        action: "view_create",
        args: {
          action: "view_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          leftOffset: rawArgs.leftOffset,
          rightOffset: rawArgs.rightOffset,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_list",
      displayName: "Civil 3D Section View List",
      description: "List Civil 3D section views in the active drawing, optionally filtered by alignment and sample line group.",
      inputShape: {
        alignmentName: z.string().optional(),
        sampleLineGroupName: z.string().optional(),
      },
      supportedActions: ["view_list"],
      resolveAction: (rawArgs) => ({
        action: "view_list",
        args: {
          action: "view_list",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_update_style",
      displayName: "Civil 3D Section View Update Style",
      description: "Update the display style and/or band set style on existing Civil 3D section views for a sample line group.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        applyToAll: z.boolean().optional(),
      },
      supportedActions: ["view_update_style"],
      resolveAction: (rawArgs) => ({
        action: "view_update_style",
        args: {
          action: "view_update_style",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          applyToAll: rawArgs.applyToAll,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_group_create",
      displayName: "Civil 3D Section View Group Create",
      description: "Creates section views for all stations in a sample line group using the drawing's Civil 3D draft-placement settings.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.tuple([z.number(), z.number()]),
        style: z.string().optional(),
        plotStyle: z.string().optional(),
      },
      supportedActions: ["view_group_create"],
      resolveAction: (rawArgs) => ({
        action: "view_group_create",
        args: {
          action: "view_group_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          plotStyle: rawArgs.plotStyle,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_export",
      displayName: "Civil 3D Section View Export",
      description: "Export Civil 3D section data to a CSV or text file, including station, offset, and surface elevation data per section.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        outputPath: z.string(),
        overwrite: z.boolean().optional(),
        includeElevations: z.boolean().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_export"],
      resolveAction: (rawArgs) => ({
        action: "view_export",
        args: {
          action: "view_export",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          includeElevations: rawArgs.includeElevations,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
  ],
};
