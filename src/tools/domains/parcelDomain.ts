import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The parcel domain file: `civil3d_parcel` (sites and parcels) and its aliases `civil3d_parcel_create`,
 * `civil3d_parcel_edit`, `civil3d_parcel_lot_line_adjust` and `civil3d_parcel_report`. The plugin side is
 * ParcelEditingCommands.cs, which reaches sites and parcels partly by reflection because those members vary between
 * Civil 3D releases; domainRuntime.ts turns these definitions into validated, approval-checked MCP tools.
 *
 * Every action forwards to one plugin method. Areas are worked out by the plugin as if the drawing were in feet
 * (`targetAreaSqFt`, the report's `units`), so answers for a drawing in meters need care.
 */

const GenericParcelResponseSchema = z.object({}).passthrough();

const ParcelSiteSchema = z.object({
  name: z.string().optional(),
  handle: z.string().optional(),
  parcelCount: z.number().optional(),
}).passthrough();

const ParcelSitesResponseSchema = z.object({
  sites: z.array(ParcelSiteSchema),
});

const ParcelSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  number: z.number(),
  area: z.number(),
  perimeter: z.number(),
  style: z.string(),
});

const ParcelListResponseSchema = z.object({
  siteName: z.string(),
  parcels: z.array(ParcelSummarySchema),
  units: z.object({
    area: z.string(),
    length: z.string(),
  }),
});

const ParcelDetailResponseSchema = z.object({
  siteName: z.string().optional(),
  name: z.string().optional(),
  handle: z.string().optional(),
  number: z.number().optional(),
  area: z.number().optional(),
  perimeter: z.number().optional(),
  style: z.string().optional(),
}).passthrough();

/**
 * Every argument any `civil3d_parcel` action takes, all optional except `action`: the one input schema MCP clients
 * see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call.
 */
const canonicalParcelInputShape = {
  action: z.enum([
    "list_sites",
    "list",
    "get",
    "create",
    "edit",
    "lot_line_adjust",
    "report",
    "export_report",
  ]),
  siteName: z.string().optional(),
  parcelName: z.string().optional(),
  name: z.string().optional(),
  sourceHandle: z.string().optional(),
  points: z.array(z.tuple([z.number(), z.number()])).optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
  newName: z.string().optional(),
  description: z.string().optional(),
  targetAreaSqFt: z.number().optional(),
  lotLineHandle: z.string().optional(),
  tolerance: z.number().optional(),
  parcelNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeCoordinates: z.boolean().optional(),
  units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
};

const ParcelListSitesArgsSchema = z.object({
  action: z.literal("list_sites"),
});

const ParcelListArgsSchema = z.object({
  action: z.literal("list"),
  siteName: z.string(),
});

const ParcelGetArgsSchema = z.object({
  action: z.literal("get"),
  siteName: z.string(),
  parcelName: z.string(),
});

/**
 * `create`: a parcel in site `siteName` from either `sourceHandle` (the hex handle of a closed entity, such as a
 * polyline, used as the boundary) or `points` (at least three [x, y] vertices in drawing units, closed by the
 * plugin). The plugin refuses a call with neither.
 */
const ParcelCreateArgsSchema = z.object({
  action: z.literal("create"),
  siteName: z.string(),
  name: z.string().optional(),
  sourceHandle: z.string().optional(),
  points: z.array(z.tuple([z.number(), z.number()])).min(3).optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
});

const ParcelEditArgsSchema = z.object({
  action: z.literal("edit"),
  siteName: z.string(),
  parcelName: z.string(),
  newName: z.string().optional(),
  style: z.string().optional(),
  areaLabelStyle: z.string().optional(),
  description: z.string().optional(),
});

/**
 * `lot_line_adjust`: slide a lot line until the parcel reaches `targetAreaSqFt`, within `tolerance` (default 1, in
 * the drawing's square units). `lotLineHandle` picks which lot line moves. The answer's `converged` says whether the
 * area ended within tolerance; when it is false, check the parcel in Civil 3D.
 */
const ParcelLotLineAdjustArgsSchema = z.object({
  action: z.literal("lot_line_adjust"),
  siteName: z.string(),
  parcelName: z.string(),
  targetAreaSqFt: z.number().positive(),
  lotLineHandle: z.string().optional(),
  tolerance: z.number().positive().optional(),
});

/**
 * `report`: area, perimeter and (with `includeCoordinates`) vertices of the named parcels, or of every parcel in
 * the site. With `outputPath` the rows are also written to a .csv inside the export roots (FileBoundary.cs; an
 * existing file only with `overwrite`). `units` converts the area from square feet: sqft, acres, sqm or ha.
 */
const ParcelExportReportArgsSchema = z.object({
  action: z.literal("export_report"),
  siteName: z.string(),
  parcelNames: z.array(z.string()).optional(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  includeCoordinates: z.boolean().optional(),
  units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
});

const ParcelReportArgsSchema = z.object({
  action: z.literal("report"),
  siteName: z.string(),
  parcelNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeCoordinates: z.boolean().optional(),
  units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
});

/**
 * The parcel domain: `civil3d_parcel` (sites and parcels: list, get, create, edit, lot-line adjustment, reports)
 * and its aliases. Every action is one call to ParcelEditingCommands.cs. Listed in toolManifest.ts; exposures[0]
 * is the canonical tool.
 */
export const PARCEL_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "parcel",
  actions: {
    list_sites: {
      action: "list_sites",
      inputSchema: ParcelListSitesArgsSchema,
      responseSchema: ParcelSitesResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listParcelSites"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listParcelSites", {}),
      ),
    },
    list: {
      action: "list",
      inputSchema: ParcelListArgsSchema,
      responseSchema: ParcelListResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listParcels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listParcels", { siteName: args.siteName }),
      ),
    },
    get: {
      action: "get",
      inputSchema: ParcelGetArgsSchema,
      responseSchema: ParcelDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getParcel", {
          siteName: args.siteName,
          parcelName: args.parcelName,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: ParcelCreateArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createParcel", {
          siteName: args.siteName,
          name: args.name ?? null,
          sourceHandle: args.sourceHandle ?? null,
          points: args.points ?? null,
          style: args.style ?? null,
          areaLabelStyle: args.areaLabelStyle ?? null,
        }),
      ),
    },
    edit: {
      action: "edit",
      inputSchema: ParcelEditArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editParcel"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("editParcel", {
          siteName: args.siteName,
          parcelName: args.parcelName,
          newName: args.newName ?? null,
          style: args.style ?? null,
          areaLabelStyle: args.areaLabelStyle ?? null,
          description: args.description ?? null,
        }),
      ),
    },
    // NOTE: AdjustParcelLotLineAsync calls `AdjustLotLine` / `SlideAngle` on the Parcel by reflection, which aren't
    // documented Parcel members, and compares the target in square feet with Parcel.Area in drawing units without
    // converting (TargetAreaToDrawingUnits returns its input). When neither method exists nothing moves, and in a
    // drawing in meters `converged` compares square feet with square meters.
    lot_line_adjust: {
      action: "lot_line_adjust",
      inputSchema: ParcelLotLineAdjustArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["edit", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["adjustParcelLotLine"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("adjustParcelLotLine", {
          siteName: args.siteName,
          parcelName: args.parcelName,
          targetAreaSqFt: args.targetAreaSqFt,
          lotLineHandle: args.lotLineHandle ?? null,
          tolerance: args.tolerance ?? 1.0,
        }),
      ),
    },
    // Read only; writing the CSV is export_report (approval first). NOTE: its area conversion (ConvertArea) assumes the
    // drawing is in feet.
    report: {
      action: "report",
      inputSchema: ParcelReportArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["reportParcels"],
      // Read only: the report comes back in the answer. Writing it to a file is export_report, which needs approval.
      execute: async (args) => {
        if (args.outputPath) {
          throw new Error("report only reads; to write the report to a file use action export_report (it needs the user's approval).");
        }
        return await withApplicationConnection(
          async (appClient) => await appClient.sendCommand("reportParcels", {
            siteName: args.siteName,
            parcelNames: args.parcelNames ?? null,
            outputPath: null,
            overwrite: false,
            includeCoordinates: args.includeCoordinates ?? false,
            units: args.units ?? "sqft",
          }),
        );
      },
    },
    // The parcel report written to a CSV file (inside the export roots): a file is written, so approval first.
    export_report: {
      action: "export_report",
      inputSchema: ParcelExportReportArgsSchema,
      responseSchema: GenericParcelResponseSchema,
      capabilities: ["export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["reportParcels"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("reportParcels", {
          siteName: args.siteName,
          parcelNames: args.parcelNames ?? null,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          includeCoordinates: args.includeCoordinates ?? false,
          units: args.units ?? "sqft",
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_parcel",
      displayName: "Civil 3D Parcel",
      description: "Reads, creates, edits, adjusts, and reports Civil 3D parcel and site data through a single domain tool.",
      inputShape: canonicalParcelInputShape,
      supportedActions: ["list_sites", "list", "get", "create", "edit", "lot_line_adjust", "report", "export_report"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_parcel_create",
      displayName: "Civil 3D Parcel Create",
      description: "Creates a new Civil 3D parcel from a closed source object or a vertex list.",
      inputShape: {
        siteName: z.string(),
        name: z.string().optional(),
        sourceHandle: z.string().optional(),
        points: z.array(z.tuple([z.number(), z.number()])).min(3).optional(),
        style: z.string().optional(),
        areaLabelStyle: z.string().optional(),
      },
      supportedActions: ["create"],
      resolveAction: (rawArgs) => ({
        action: "create",
        args: {
          action: "create",
          siteName: rawArgs.siteName,
          name: rawArgs.name,
          sourceHandle: rawArgs.sourceHandle,
          points: rawArgs.points,
          style: rawArgs.style,
          areaLabelStyle: rawArgs.areaLabelStyle,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_edit",
      displayName: "Civil 3D Parcel Edit",
      description: "Edits Civil 3D parcel metadata and styles without changing geometry.",
      inputShape: {
        siteName: z.string(),
        parcelName: z.string(),
        newName: z.string().optional(),
        style: z.string().optional(),
        areaLabelStyle: z.string().optional(),
        description: z.string().optional(),
      },
      supportedActions: ["edit"],
      resolveAction: (rawArgs) => ({
        action: "edit",
        args: {
          action: "edit",
          siteName: rawArgs.siteName,
          parcelName: rawArgs.parcelName,
          newName: rawArgs.newName,
          style: rawArgs.style,
          areaLabelStyle: rawArgs.areaLabelStyle,
          description: rawArgs.description,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_lot_line_adjust",
      displayName: "Civil 3D Parcel Lot Line Adjust",
      description: "Adjusts a parcel lot line until a target area is reached within tolerance.",
      inputShape: {
        siteName: z.string(),
        parcelName: z.string(),
        targetAreaSqFt: z.number().positive(),
        lotLineHandle: z.string().optional(),
        tolerance: z.number().positive().optional(),
      },
      supportedActions: ["lot_line_adjust"],
      resolveAction: (rawArgs) => ({
        action: "lot_line_adjust",
        args: {
          action: "lot_line_adjust",
          siteName: rawArgs.siteName,
          parcelName: rawArgs.parcelName,
          targetAreaSqFt: rawArgs.targetAreaSqFt,
          lotLineHandle: rawArgs.lotLineHandle,
          tolerance: rawArgs.tolerance,
        },
      }),
    },
    {
      toolName: "civil3d_parcel_report",
      displayName: "Civil 3D Parcel Report",
      description: "Generates a parcel area and dimension report for one or more parcels in a site.",
      inputShape: {
        siteName: z.string(),
        parcelNames: z.array(z.string()).optional(),
        outputPath: z.string().optional(),
        overwrite: z.boolean().optional(),
        includeCoordinates: z.boolean().optional(),
        units: z.enum(["sqft", "acres", "sqm", "ha"]).optional(),
      },
      supportedActions: ["report", "export_report"],
      resolveAction: (rawArgs) => ({
        action: rawArgs.outputPath ? "export_report" : "report",
        args: {
          action: rawArgs.outputPath ? "export_report" : "report",
          siteName: rawArgs.siteName,
          parcelNames: rawArgs.parcelNames,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          includeCoordinates: rawArgs.includeCoordinates,
          units: rawArgs.units,
        },
      }),
    },
  ],
};
