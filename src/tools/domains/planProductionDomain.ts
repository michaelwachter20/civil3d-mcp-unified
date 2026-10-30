import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The plan production domain: `civil3d_plan_production` and one alias per action (`civil3d_sheet_set_list`,
 * `civil3d_sheet_add`, `civil3d_sheet_publish_pdf`, ...). The plugin side is PlanProductionCommands.cs, which reaches
 * Civil 3D's sheet objects by reflection because the managed API doesn't document them; domainRuntime.ts turns these
 * definitions into validated, approval-checked MCP tools. Every action forwards to one plugin method; nothing runs in
 * Node.
 *
 * Several plugin methods refuse by design and change nothing: `createSheetSet`, `createPlanProfileSheet`,
 * `publishSheetPdf` and `exportSheetSet` check their arguments (and the output path, through FileBoundary) and then
 * throw CIVIL3D.API_ERROR, because there is no documented managed API (or no way yet to verify the PDF was written).
 * The working actions are the reads, `sheet_add`, `sheet_set_title_block`, `plan_profile_sheet_update_alignment` and
 * the viewport actions. (The jobs domain's `publish_sheet_pdf` runs the same `publishSheetPdf`, so it refuses too.)
 */

/** One sheet in a sheet set, as the list and info answers give it. */
const SheetSummarySchema = z.object({
  name: z.string(),
  number: z.string(),
  handle: z.string(),
  layoutName: z.string().nullable(),
});

const SheetSetSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  description: z.string().nullable(),
  sheetCount: z.number(),
});

const SheetDetailSchema = z.object({
  name: z.string(),
  number: z.string(),
  handle: z.string(),
  layoutName: z.string().nullable(),
  viewportScale: z.number().nullable(),
  alignmentName: z.string().nullable(),
  profileName: z.string().nullable(),
  titleBlock: z.string().nullable(),
});

const GenericPlanProductionResponseSchema = z.object({}).passthrough();

/**
 * Every argument any `civil3d_plan_production` action takes, all optional except `action`: the one input schema MCP
 * clients see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call.
 * `plan_profile_sheet_create` is missing from the enum on purpose (see its action below).
 */
const canonicalPlanProductionInputShape = {
  action: z.enum([
    "sheet_set_list",
    "sheet_set_get_info",
    "sheet_set_create",
    "sheet_add",
    "sheet_get_properties",
    "sheet_set_title_block",
    "plan_profile_sheet_update_alignment",
    "sheet_view_create",
    "sheet_view_set_scale",
    "sheet_publish_pdf",
    "sheet_set_export",
  ]),
  name: z.string().optional(),
  description: z.string().optional(),
  sheetSetName: z.string().optional(),
  sheetName: z.string().optional(),
  sheetNumber: z.string().optional(),
  layoutName: z.string().optional(),
  titleBlockPath: z.string().optional(),
  alignmentName: z.string().optional(),
  profileName: z.string().optional(),
  sheetTemplatePath: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  viewScale: z.number().optional(),
  viewName: z.string().optional(),
  centerX: z.number().optional(),
  centerY: z.number().optional(),
  width: z.number().optional(),
  height: z.number().optional(),
  scale: z.number().optional(),
  viewportHandle: z.string().optional(),
  layoutNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
  paperSize: z.string().optional(),
};

const SheetSetListArgsSchema = z.object({
  action: z.literal("sheet_set_list"),
});

const SheetSetGetInfoArgsSchema = z.object({
  action: z.literal("sheet_set_get_info"),
  name: z.string(),
});

const SheetSetCreateArgsSchema = z.object({
  action: z.literal("sheet_set_create"),
  name: z.string(),
  description: z.string().optional(),
});

const SheetAddArgsSchema = z.object({
  action: z.literal("sheet_add"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  sheetNumber: z.string().optional(),
  layoutName: z.string().optional(),
});

const SheetGetPropertiesArgsSchema = z.object({
  action: z.literal("sheet_get_properties"),
  sheetSetName: z.string(),
  sheetName: z.string(),
});

/** `titleBlockPath` is a .dwg or .dwt file; the plugin resolves it through FileBoundary's reference roots. */
const SheetSetTitleBlockArgsSchema = z.object({
  action: z.literal("sheet_set_title_block"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  titleBlockPath: z.string(),
});

const PlanProfileSheetCreateArgsSchema = z.object({
  action: z.literal("plan_profile_sheet_create"),
  sheetSetName: z.string(),
  alignmentName: z.string(),
  profileName: z.string().optional(),
  sheetTemplatePath: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  viewScale: z.number().optional(),
});

const PlanProfileSheetUpdateAlignmentArgsSchema = z.object({
  action: z.literal("plan_profile_sheet_update_alignment"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  alignmentName: z.string(),
  profileName: z.string().optional(),
});

/**
 * A new paper-space viewport on a layout. Center, width and height are in paper units (the plugin defaults to a
 * viewport 8 by 6 centered at 0,0); `scale` is the drawing units per paper unit (40 for 1" = 40'), stored as a custom
 * scale of 1/scale. `viewName` applies a named model view to the viewport when one exists.
 */
const SheetViewCreateArgsSchema = z.object({
  action: z.literal("sheet_view_create"),
  layoutName: z.string(),
  viewName: z.string().optional(),
  centerX: z.number().optional(),
  centerY: z.number().optional(),
  width: z.number().optional(),
  height: z.number().optional(),
  scale: z.number().optional(),
});

/**
 * `scale` as in sheet_view_create (40 for 1" = 40'); without `viewportHandle` the plugin takes the first viewport in
 * the layout.
 * NOTE: the plugin also falls back to the first viewport when `viewportHandle` matches none, instead of refusing, and
 * the first viewport in a layout is usually AutoCAD's overall paper-space viewport, not a model view.
 */
const SheetViewSetScaleArgsSchema = z.object({
  action: z.literal("sheet_view_set_scale"),
  layoutName: z.string(),
  viewportHandle: z.string().optional(),
  scale: z.number().positive(),
});

const SheetPublishPdfArgsSchema = z.object({
  action: z.literal("sheet_publish_pdf"),
  layoutNames: z.array(z.string()).min(1),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
  paperSize: z.string().optional(),
});

const SheetSetExportArgsSchema = z.object({
  action: z.literal("sheet_set_export"),
  sheetSetName: z.string(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
});

/**
 * The plan production domain: `civil3d_plan_production` (sheet sets, sheets, title blocks, plan/profile sheets,
 * sheet views and scales, PDF publish and sheet set export) and its aliases. Every action is one call to
 * PlanProductionCommands.cs; publish and export write files, so they are approval-gated and their paths are
 * checked by FileBoundary in the plugin. Listed in toolManifest.ts; exposures[0] is the canonical tool.
 */
export const PLAN_PRODUCTION_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plan_production",
  actions: {
    sheet_set_list: {
      action: "sheet_set_list",
      inputSchema: SheetSetListArgsSchema,
      responseSchema: z.object({ sheetSets: z.array(SheetSetSummarySchema) }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSheetSets"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSheetSets", {}),
      ),
    },
    sheet_set_get_info: {
      action: "sheet_set_get_info",
      inputSchema: SheetSetGetInfoArgsSchema,
      responseSchema: z.object({
        name: z.string(),
        handle: z.string(),
        description: z.string().nullable(),
        sheets: z.array(SheetSummarySchema),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSheetSetInfo"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSheetSetInfo", { name: args.name }),
      ),
    },
    sheet_set_create: {
      action: "sheet_set_create",
      inputSchema: SheetSetCreateArgsSchema,
      responseSchema: z.object({ name: z.string(), handle: z.string(), created: z.boolean() }),
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSheetSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSheetSet", {
          name: args.name,
          description: args.description,
        }),
      ),
    },
    sheet_add: {
      action: "sheet_add",
      inputSchema: SheetAddArgsSchema,
      responseSchema: z.object({ name: z.string(), number: z.string(), handle: z.string(), added: z.boolean() }),
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSheet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSheet", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          sheetNumber: args.sheetNumber,
          layoutName: args.layoutName,
        }),
      ),
    },
    sheet_get_properties: {
      action: "sheet_get_properties",
      inputSchema: SheetGetPropertiesArgsSchema,
      responseSchema: SheetDetailSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSheetProperties"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSheetProperties", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
        }),
      ),
    },
    sheet_set_title_block: {
      action: "sheet_set_title_block",
      inputSchema: SheetSetTitleBlockArgsSchema,
      responseSchema: z.object({ sheetName: z.string(), titleBlock: z.string(), updated: z.boolean() }),
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setSheetTitleBlock"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setSheetTitleBlock", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          titleBlockPath: args.titleBlockPath,
        }),
      ),
    },
    // Not in the canonical tool's supportedActions and has no alias, so only the orchestrator reaches it; the plugin
    // always refuses (CIVIL3D.API_ERROR, nothing created) because there is no documented plan/profile sheet factory.
    plan_profile_sheet_create: {
      action: "plan_profile_sheet_create",
      inputSchema: PlanProfileSheetCreateArgsSchema,
      responseSchema: z.object({
        sheetName: z.string(),
        handle: z.string(),
        alignmentName: z.string(),
        profileName: z.string().nullable(),
        created: z.boolean(),
      }),
      capabilities: ["create", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createPlanProfileSheet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createPlanProfileSheet", {
          sheetSetName: args.sheetSetName,
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          sheetTemplatePath: args.sheetTemplatePath,
          startStation: args.startStation,
          endStation: args.endStation,
          viewScale: args.viewScale,
        }),
      ),
    },
    // NOTE: the plugin sets the sheet's alignment (and profile) id by trying property names through reflection and
    // ignores whether any of them took, so it can answer `updated: true` when nothing changed.
    plan_profile_sheet_update_alignment: {
      action: "plan_profile_sheet_update_alignment",
      inputSchema: PlanProfileSheetUpdateAlignmentArgsSchema,
      responseSchema: z.object({ sheetName: z.string(), alignmentName: z.string(), updated: z.boolean() }),
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updatePlanProfileSheetAlignment"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updatePlanProfileSheetAlignment", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          alignmentName: args.alignmentName,
          profileName: args.profileName,
        }),
      ),
    },
    sheet_view_create: {
      action: "sheet_view_create",
      inputSchema: SheetViewCreateArgsSchema,
      responseSchema: z.object({ handle: z.string(), layoutName: z.string(), scale: z.number().nullable(), created: z.boolean() }),
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSheetView"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSheetView", {
          layoutName: args.layoutName,
          viewName: args.viewName,
          centerX: args.centerX,
          centerY: args.centerY,
          width: args.width,
          height: args.height,
          scale: args.scale,
        }),
      ),
    },
    sheet_view_set_scale: {
      action: "sheet_view_set_scale",
      inputSchema: SheetViewSetScaleArgsSchema,
      responseSchema: z.object({ handle: z.string(), scale: z.number(), updated: z.boolean() }),
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setSheetViewScale"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setSheetViewScale", {
          layoutName: args.layoutName,
          viewportHandle: args.viewportHandle,
          scale: args.scale,
        }),
      ),
    },
    // sheet_publish_pdf and sheet_set_export: the plugin checks the output path (FileBoundary: export roots, .pdf,
    // no overwrite without `overwrite: true`) and then refuses with CIVIL3D.API_ERROR; no file is written.
    sheet_publish_pdf: {
      action: "sheet_publish_pdf",
      inputSchema: SheetPublishPdfArgsSchema,
      responseSchema: z.object({ outputPath: z.string(), sheetsPublished: z.number(), published: z.boolean() }),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["publishSheetPdf"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("publishSheetPdf", {
          layoutNames: args.layoutNames,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          plotStyleTable: args.plotStyleTable,
          paperSize: args.paperSize,
        }),
      ),
    },
    sheet_set_export: {
      action: "sheet_set_export",
      inputSchema: SheetSetExportArgsSchema,
      responseSchema: z.object({
        sheetSetName: z.string(),
        outputPath: z.string(),
        sheetsExported: z.number(),
        exported: z.boolean(),
      }),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportSheetSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportSheetSet", {
          sheetSetName: args.sheetSetName,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          plotStyleTable: args.plotStyleTable,
        }),
      ),
    },
  },
  // The canonical tool first; the rest are one-action aliases, listed to MCP clients only with
  // CIVIL3D_ENABLE_TOOL_ALIASES=true.
  exposures: [
    {
      toolName: "civil3d_plan_production",
      displayName: "Civil 3D Plan Production",
      description: "Lists, creates, updates, and publishes Civil 3D sheet sets, sheets, plan/profile sheets, and sheet views through a single domain tool.",
      inputShape: canonicalPlanProductionInputShape,
      supportedActions: [
        "sheet_set_list",
        "sheet_set_get_info",
        "sheet_set_create",
        "sheet_add",
        "sheet_get_properties",
        "sheet_set_title_block",
        "plan_profile_sheet_update_alignment",
        "sheet_view_create",
        "sheet_view_set_scale",
        "sheet_publish_pdf",
        "sheet_set_export",
      ],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_sheet_set_list",
      displayName: "Civil 3D Sheet Set List",
      description: "Lists all Plan Production sheet sets in the active drawing.",
      inputShape: {},
      supportedActions: ["sheet_set_list"],
      resolveAction: () => ({ action: "sheet_set_list", args: { action: "sheet_set_list" } }),
    },
    {
      toolName: "civil3d_sheet_set_get_info",
      displayName: "Civil 3D Sheet Set Get Info",
      description: "Gets detailed information about a Plan Production sheet set including all sheets.",
      inputShape: { name: z.string() },
      supportedActions: ["sheet_set_get_info"],
      resolveAction: (rawArgs) => ({ action: "sheet_set_get_info", args: { action: "sheet_set_get_info", name: rawArgs.name } }),
    },
    {
      toolName: "civil3d_sheet_set_create",
      displayName: "Civil 3D Sheet Set Create",
      description: "Creates a new Plan Production sheet set in the active drawing.",
      inputShape: { name: z.string(), description: z.string().optional() },
      supportedActions: ["sheet_set_create"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_create",
        args: { action: "sheet_set_create", name: rawArgs.name, description: rawArgs.description },
      }),
    },
    {
      toolName: "civil3d_sheet_add",
      displayName: "Civil 3D Sheet Add",
      description: "Adds a new sheet to an existing Plan Production sheet set.",
      inputShape: {
        sheetSetName: z.string(),
        sheetName: z.string(),
        sheetNumber: z.string().optional(),
        layoutName: z.string().optional(),
      },
      supportedActions: ["sheet_add"],
      resolveAction: (rawArgs) => ({
        action: "sheet_add",
        args: {
          action: "sheet_add",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          sheetNumber: rawArgs.sheetNumber,
          layoutName: rawArgs.layoutName,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_get_properties",
      displayName: "Civil 3D Sheet Get Properties",
      description: "Gets full properties of a specific sheet within a Plan Production sheet set.",
      inputShape: { sheetSetName: z.string(), sheetName: z.string() },
      supportedActions: ["sheet_get_properties"],
      resolveAction: (rawArgs) => ({
        action: "sheet_get_properties",
        args: { action: "sheet_get_properties", sheetSetName: rawArgs.sheetSetName, sheetName: rawArgs.sheetName },
      }),
    },
    {
      toolName: "civil3d_sheet_set_title_block",
      displayName: "Civil 3D Sheet Set Title Block",
      description: "Sets or updates the title block template on a sheet within a Plan Production sheet set.",
      inputShape: { sheetSetName: z.string(), sheetName: z.string(), titleBlockPath: z.string() },
      supportedActions: ["sheet_set_title_block"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_title_block",
        args: {
          action: "sheet_set_title_block",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          titleBlockPath: rawArgs.titleBlockPath,
        },
      }),
    },
    {
      toolName: "civil3d_plan_profile_sheet_update_alignment",
      displayName: "Civil 3D Plan Profile Sheet Update Alignment",
      description: "Updates the alignment and optionally the profile on an existing Plan/Profile sheet.",
      inputShape: {
        sheetSetName: z.string(),
        sheetName: z.string(),
        alignmentName: z.string(),
        profileName: z.string().optional(),
      },
      supportedActions: ["plan_profile_sheet_update_alignment"],
      resolveAction: (rawArgs) => ({
        action: "plan_profile_sheet_update_alignment",
        args: {
          action: "plan_profile_sheet_update_alignment",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_view_create",
      displayName: "Civil 3D Sheet View Create",
      description: "Creates a viewport or view on a sheet layout.",
      inputShape: {
        layoutName: z.string(),
        viewName: z.string().optional(),
        centerX: z.number().optional(),
        centerY: z.number().optional(),
        width: z.number().optional(),
        height: z.number().optional(),
        scale: z.number().optional(),
      },
      supportedActions: ["sheet_view_create"],
      resolveAction: (rawArgs) => ({
        action: "sheet_view_create",
        args: {
          action: "sheet_view_create",
          layoutName: rawArgs.layoutName,
          viewName: rawArgs.viewName,
          centerX: rawArgs.centerX,
          centerY: rawArgs.centerY,
          width: rawArgs.width,
          height: rawArgs.height,
          scale: rawArgs.scale,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_view_set_scale",
      displayName: "Civil 3D Sheet View Set Scale",
      description: "Updates the scale of a viewport on a sheet layout.",
      inputShape: {
        layoutName: z.string(),
        viewportHandle: z.string().optional(),
        scale: z.number().positive(),
      },
      supportedActions: ["sheet_view_set_scale"],
      resolveAction: (rawArgs) => ({
        action: "sheet_view_set_scale",
        args: {
          action: "sheet_view_set_scale",
          layoutName: rawArgs.layoutName,
          viewportHandle: rawArgs.viewportHandle,
          scale: rawArgs.scale,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_publish_pdf",
      displayName: "Civil 3D Sheet Publish PDF",
      description: "Publishes one or more sheet layouts to a PDF file.",
      inputShape: {
        layoutNames: z.array(z.string()).min(1),
        outputPath: z.string(),
        overwrite: z.boolean().optional(),
        plotStyleTable: z.string().optional(),
        paperSize: z.string().optional(),
      },
      supportedActions: ["sheet_publish_pdf"],
      resolveAction: (rawArgs) => ({
        action: "sheet_publish_pdf",
        args: {
          action: "sheet_publish_pdf",
          layoutNames: rawArgs.layoutNames,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          plotStyleTable: rawArgs.plotStyleTable,
          paperSize: rawArgs.paperSize,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_set_export",
      displayName: "Civil 3D Sheet Set Export",
      description: "Exports all sheets in a Plan Production sheet set to a single multi-page PDF.",
      inputShape: { sheetSetName: z.string(), outputPath: z.string(), overwrite: z.boolean().optional(), plotStyleTable: z.string().optional() },
      supportedActions: ["sheet_set_export"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_export",
        args: {
          action: "sheet_set_export",
          sheetSetName: rawArgs.sheetSetName,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          plotStyleTable: rawArgs.plotStyleTable,
        },
      }),
    },
  ],
};
