import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The survey domain file: `civil3d_survey` (survey databases, networks, points, setups, observations and figures) and
 * its aliases `civil3d_survey_database_list`, `_figure_list`, `_figure_get` and `_observation_list`. The plugin side is
 * SurveyCommands.cs, on Civil 3D's survey COM interface; domainRuntime.ts turns these definitions into validated,
 * approval-checked MCP tools.
 *
 * Every action forwards to one plugin method. Two aren't exposed: network_adjust and landxml_import exist for
 * workflowDomain.ts's survey_import_adjust_figures, but the interface has no call for either, so the plugin checks the
 * arguments and always answers CIVIL3D.API_ERROR, changing nothing, and the user does those steps in the Survey tab.
 */

const GenericSurveyResponseSchema = z.object({}).passthrough();

/**
 * Every argument the exposed `civil3d_survey` actions take, all optional except `action`: the one input schema MCP
 * clients see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call.
 */
const canonicalSurveyInputShape = {
  action: z.enum([
    "database_list",
    "database_create",
    "network_list",
    "network_create",
    "point_list",
    "point_add",
    "setup_add",
    "figure_list",
    "figure_get",
    "figure_create",
    "observation_list",
    "add_to_drawing",
  ]),
  name: z.string().optional(),
  databaseName: z.string().optional(),
  networkName: z.string().optional(),
  figureName: z.string().optional(),
  observationType: z.enum(["all", "angles", "distances", "directions", "gps"]).optional(),
  description: z.string().optional(),
  pointNumber: z.number().int().positive().optional(),
  pointName: z.string().optional(),
  easting: z.number().optional(),
  northing: z.number().optional(),
  elevation: z.number().optional(),
  control: z.boolean().optional().describe("true for a control point (the default), false for a non-control point"),
  stationPoint: z.number().int().positive().optional(),
  backsightPoint: z.number().int().positive().optional(),
  observations: z.array(z.object({}).passthrough()).optional()
    .describe("Shots from the setup: pointNumber, angleDegrees, distance, and optionally angleType, distanceType, verticalDegrees, targetHeight, description"),
  pointNumbers: z.array(z.number().int().positive()).optional(),
  figureStyle: z.string().optional(),
  closed: z.boolean().optional(),
  layer: z.string().optional(),
  limit: z.number().int().positive().optional(),
  distanceUnit: z.enum(["feet", "meters", "survey_feet"]).optional()
    .describe("A new database's units; Civil 3D's default otherwise. Every length and coordinate is in the database's units"),
};

const SurveyDatabaseListArgsSchema = z.object({
  action: z.literal("database_list"),
});

/** `database_create`: makes the database in Civil 3D's survey working folder, which `database_list` reports. */
const SurveyDatabaseCreateArgsSchema = z.object({
  action: z.literal("database_create"),
  name: z.string().min(1),
  distanceUnit: z.enum(["feet", "meters", "survey_feet"]).optional(),
});

const SurveyNetworkListArgsSchema = z.object({
  action: z.literal("network_list"),
  databaseName: z.string(),
});

const SurveyNetworkCreateArgsSchema = z.object({
  action: z.literal("network_create"),
  databaseName: z.string(),
  networkName: z.string().min(1),
  description: z.string().optional(),
});

const SurveyPointListArgsSchema = z.object({
  action: z.literal("point_list"),
  databaseName: z.string(),
  limit: z.number().int().positive().optional(),
});

const SurveyPointAddArgsSchema = z.object({
  action: z.literal("point_add"),
  databaseName: z.string(),
  networkName: z.string(),
  pointNumber: z.number().int().positive(),
  easting: z.number(),
  northing: z.number(),
  elevation: z.number().optional(),
  pointName: z.string().optional(),
  description: z.string().optional(),
  control: z.boolean().optional(),
});

/** One shot from a setup, as a field book holds it. Angles are decimal degrees. */
const SurveyShotSchema = z.object({
  pointNumber: z.number().int().positive(),
  angleDegrees: z.number(),
  distance: z.number().positive(),
  angleType: z.enum(["angle", "deflection", "face1", "face2", "azimuth", "bearing_ne", "bearing_se", "bearing_sw", "bearing_nw"]).optional(),
  distanceType: z.enum(["slope", "horizontal"]).optional(),
  verticalDegrees: z.number().optional(),
  targetHeight: z.number().optional(),
  description: z.string().optional(),
});

const SurveySetupAddArgsSchema = z.object({
  action: z.literal("setup_add"),
  databaseName: z.string(),
  networkName: z.string(),
  stationPoint: z.number().int().positive(),
  backsightPoint: z.number().int().positive(),
  observations: z.array(SurveyShotSchema).optional(),
});

const SurveyAddToDrawingArgsSchema = z.object({
  action: z.literal("add_to_drawing"),
  databaseName: z.string(),
  figureName: z.string().optional(),
  networkName: z.string().optional(),
}).refine((args) => (args.figureName === undefined) !== (args.networkName === undefined), {
  message: "Give either figureName or networkName.",
});

const SurveyFigureListArgsSchema = z.object({
  action: z.literal("figure_list"),
  databaseName: z.string().optional(),
});

const SurveyFigureGetArgsSchema = z.object({
  action: z.literal("figure_get"),
  name: z.string(),
  databaseName: z.string().optional(),
});

const SurveyObservationListArgsSchema = z.object({
  action: z.literal("observation_list"),
  databaseName: z.string(),
  networkName: z.string().optional(),
  observationType: z.enum(["all", "angles", "distances", "directions", "gps"]).optional(),
});

/**
 * `network_adjust` and `landxml_import`: reached only through the survey workflow. The plugin validates the required
 * fields (and the LandXML path against the import roots) and then refuses with CIVIL3D.API_ERROR; method, confidence
 * level and the rest are never used.
 */
const SurveyNetworkAdjustArgsSchema = z.object({
  action: z.literal("network_adjust"),
  databaseName: z.string(),
  networkName: z.string(),
  method: z.enum(["least_squares", "compass", "transit", "crandall"]).optional(),
  confidenceLevel: z.number().min(50).max(99.9).optional(),
  applyAdjustment: z.boolean().optional(),
});

/** `figure_create`: joins points already in the database, in the order given. */
const SurveyFigureCreateArgsSchema = z.object({
  action: z.literal("figure_create"),
  databaseName: z.string(),
  figureName: z.string(),
  pointNumbers: z.array(z.number().int().positive()).min(2),
  figureStyle: z.string().optional(),
  closed: z.boolean().optional(),
  layer: z.string().optional(),
});

const SurveyLandXmlImportArgsSchema = z.object({
  action: z.literal("landxml_import"),
  filePath: z.string(),
  databaseName: z.string(),
  importPoints: z.boolean().optional(),
  importAlignments: z.boolean().optional(),
  importSurfaces: z.boolean().optional(),
  coordinateSystemOverride: z.string().optional(),
  duplicatePolicy: z.enum(["skip", "overwrite", "rename"]).optional(),
});

/**
 * The survey domain: `civil3d_survey` (survey databases, networks, points, setups, observations, figures) and its
 * aliases; network adjustment and LandXML import aren't advertised, because the interface can't do them. Every action
 * is one call to SurveyCommands.cs. workflowDomain.ts reuses these actions for survey_import_adjust_figures. Listed in
 * toolManifest.ts; exposures[0] is the canonical tool.
 */
export const SURVEY_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "survey",
  actions: {
    database_list: {
      action: "database_list",
      inputSchema: SurveyDatabaseListArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurveyDatabases"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurveyDatabases", {}),
      ),
    },
    database_create: {
      action: "database_create",
      inputSchema: SurveyDatabaseCreateArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurveyDatabase"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurveyDatabase", {
          name: args.name,
          distanceUnit: args.distanceUnit ?? null,
        }),
      ),
    },
    network_list: {
      action: "network_list",
      inputSchema: SurveyNetworkListArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurveyNetworks"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurveyNetworks", {
          databaseName: args.databaseName,
        }),
      ),
    },
    network_create: {
      action: "network_create",
      inputSchema: SurveyNetworkCreateArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurveyNetwork"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurveyNetwork", {
          databaseName: args.databaseName,
          networkName: args.networkName,
          description: args.description ?? null,
        }),
      ),
    },
    point_list: {
      action: "point_list",
      inputSchema: SurveyPointListArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurveyPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurveyPoints", {
          databaseName: args.databaseName,
          limit: args.limit ?? null,
        }),
      ),
    },
    point_add: {
      action: "point_add",
      inputSchema: SurveyPointAddArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurveyPoint"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurveyPoint", {
          databaseName: args.databaseName,
          networkName: args.networkName,
          pointNumber: args.pointNumber,
          easting: args.easting,
          northing: args.northing,
          elevation: args.elevation ?? null,
          pointName: args.pointName ?? null,
          description: args.description ?? null,
          control: args.control ?? true,
        }),
      ),
    },
    setup_add: {
      action: "setup_add",
      inputSchema: SurveySetupAddArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurveySetup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurveySetup", {
          databaseName: args.databaseName,
          networkName: args.networkName,
          stationPoint: args.stationPoint,
          backsightPoint: args.backsightPoint,
          observations: args.observations ?? [],
        }),
      ),
    },
    add_to_drawing: {
      action: "add_to_drawing",
      inputSchema: SurveyAddToDrawingArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurveyToDrawing"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSurveyToDrawing", {
          databaseName: args.databaseName,
          figureName: args.figureName ?? null,
          networkName: args.networkName ?? null,
        }),
      ),
    },
    figure_list: {
      action: "figure_list",
      inputSchema: SurveyFigureListArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurveyFigures"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurveyFigures", {
          databaseName: args.databaseName ?? null,
        }),
      ),
    },
    figure_get: {
      action: "figure_get",
      inputSchema: SurveyFigureGetArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurveyFigure"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSurveyFigure", {
          name: args.name,
          databaseName: args.databaseName ?? null,
        }),
      ),
    },
    observation_list: {
      action: "observation_list",
      inputSchema: SurveyObservationListArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurveyObservations"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSurveyObservations", {
          databaseName: args.databaseName,
          networkName: args.networkName ?? null,
          observationType: args.observationType ?? "all",
        }),
      ),
    },
    network_adjust: {
      action: "network_adjust",
      inputSchema: SurveyNetworkAdjustArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["analyze", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["adjustSurveyNetwork"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("adjustSurveyNetwork", {
          databaseName: args.databaseName,
          networkName: args.networkName,
          method: args.method ?? "least_squares",
          confidenceLevel: args.confidenceLevel ?? 95,
          applyAdjustment: args.applyAdjustment ?? false,
        }),
      ),
    },
    figure_create: {
      action: "figure_create",
      inputSchema: SurveyFigureCreateArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurveyFigure"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSurveyFigure", {
          databaseName: args.databaseName,
          figureName: args.figureName,
          pointNumbers: args.pointNumbers,
          figureStyle: args.figureStyle ?? null,
          closed: args.closed ?? false,
          layer: args.layer ?? null,
        }),
      ),
    },
    landxml_import: {
      action: "landxml_import",
      inputSchema: SurveyLandXmlImportArgsSchema,
      responseSchema: GenericSurveyResponseSchema,
      capabilities: ["create", "import", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["importSurveyLandXml"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("importSurveyLandXml", {
          filePath: args.filePath,
          databaseName: args.databaseName,
          importPoints: args.importPoints ?? true,
          importAlignments: args.importAlignments ?? false,
          importSurfaces: args.importSurfaces ?? false,
          coordinateSystemOverride: args.coordinateSystemOverride ?? null,
          duplicatePolicy: args.duplicatePolicy ?? "skip",
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_survey",
      displayName: "Civil 3D Survey",
      description: "Reads and builds Civil 3D survey databases: databases, networks, points, setups and observations, figures, and inserting a figure or network into the drawing. Only databases in the survey working folder are seen (database_list reports it). A database is on disk, not in the drawing, so only add_to_drawing can be undone. Network adjustment and LandXML import remain in the Survey tab.",
      inputShape: canonicalSurveyInputShape,
      supportedActions: [
        "database_list",
        "database_create",
        "network_list",
        "network_create",
        "point_list",
        "point_add",
        "setup_add",
        "figure_list",
        "figure_get",
        "figure_create",
        "observation_list",
        "add_to_drawing",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_survey_database_list",
      displayName: "Civil 3D Survey Database List",
      description: "Lists the Civil 3D survey databases in the survey working folder.",
      inputShape: {},
      supportedActions: ["database_list"],
      resolveAction: () => ({ action: "database_list", args: { action: "database_list" } }),
    },
    {
      toolName: "civil3d_survey_figure_list",
      displayName: "Civil 3D Survey Figure List",
      description: "Lists survey figures in one or more Civil 3D survey databases.",
      inputShape: { databaseName: z.string().optional() },
      supportedActions: ["figure_list"],
      resolveAction: (rawArgs) => ({
        action: "figure_list",
        args: { action: "figure_list", databaseName: rawArgs.databaseName },
      }),
    },
    {
      toolName: "civil3d_survey_figure_get",
      displayName: "Civil 3D Survey Figure Get",
      description: "Gets detailed vertex data for a specific Civil 3D survey figure.",
      inputShape: { name: z.string(), databaseName: z.string().optional() },
      supportedActions: ["figure_get"],
      resolveAction: (rawArgs) => ({
        action: "figure_get",
        args: { action: "figure_get", name: rawArgs.name, databaseName: rawArgs.databaseName },
      }),
    },
    {
      toolName: "civil3d_survey_observation_list",
      displayName: "Civil 3D Survey Observation List",
      description: "Lists raw field-book observations stored in a Civil 3D survey database.",
      inputShape: {
        databaseName: z.string(),
        networkName: z.string().optional(),
        observationType: z.enum(["all", "angles", "distances", "directions", "gps"]).optional(),
      },
      supportedActions: ["observation_list"],
      resolveAction: (rawArgs) => ({
        action: "observation_list",
        args: {
          action: "observation_list",
          databaseName: rawArgs.databaseName,
          networkName: rawArgs.networkName,
          observationType: rawArgs.observationType,
        },
      }),
    },
  ],
};
