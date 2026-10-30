import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The point domain: `civil3d_point` for COGO points and point groups, and its aliases (`create_cogo_point`,
 * `civil3d_point_group_create` / `_update` / `_delete`, `civil3d_point_export`, `civil3d_point_transform`). The plugin
 * side is PointCommands.cs (list, get, create, import, import_file, list_formats, delete) and PointGroupCommands.cs
 * (groups, export, transform); domainRuntime.ts turns these definitions into validated, approval-checked MCP tools.
 *
 * Every action forwards to one plugin method; nothing is worked out in Node. Points are usually survey data, so the
 * ways of making them differ on purpose: `import_file` goes through Civil 3D's own importer, while `create` and
 * `import` (typed-in text) make the plugin ask the user in Civil 3D first (PointCreationGate.cs).
 */

/** One point to create: easting (x), northing (y), elevation (z) in drawing units, and an optional raw description. */
const PointCreateSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number(),
  description: z.string().nullable().optional(),
});

const PointMutationResponseSchema = z.object({}).passthrough();

/** A COGO point as listCogoPoints / getCogoPoint return it: x is the easting, y the northing, in drawing units. */
const PointSchema = z.object({
  number: z.number(),
  name: z.string().nullable(),
  x: z.number(),
  y: z.number(),
  z: z.number(),
  rawDescription: z.string(),
  fullDescription: z.string(),
});

const PointListResponseSchema = z.object({
  totalCount: z.number(),
  returnedCount: z.number(),
  points: z.array(PointSchema),
  units: z.string(),
});

const PointGroupSchema = z.object({
  name: z.string(),
  description: z.string().nullable(),
  pointCount: z.number(),
  includePattern: z.string().nullable(),
  excludePattern: z.string().nullable(),
});

const PointGroupsResponseSchema = z.object({
  groups: z.array(PointGroupSchema),
});

/**
 * Which points `transform` moves: the listed numbers, else the members of the point group. With neither, the plugin
 * moves every point in the drawing.
 */
const PointTransformSelectionShape = {
  pointNumbers: z.array(z.number().int().positive()).optional(),
  groupName: z.string().optional(),
};

/**
 * Every argument any `civil3d_point` action takes, all optional except `action`: the one input schema MCP clients
 * see. The per-action `*ArgsSchema` objects below say what each action needs and validate the call.
 */
const canonicalPointInputShape = {
  action: z.enum([
    "list",
    "get",
    "create",
    "list_groups",
    "import",
    "import_file",
    "list_formats",
    "delete",
    "group_create",
    "group_update",
    "group_delete",
    "export",
    "transform",
  ]),
  groupName: z.string().optional(),
  limit: z.number().optional(),
  offset: z.number().optional(),
  pointNumber: z.number().optional(),
  points: z.array(PointCreateSchema).optional(),
  startNumber: z.number().optional(),
  format: z.enum(["pnezd", "penz", "xyzd", "xyz", "csv"]).optional(),
  data: z.string().optional(),
  path: z.string().optional().describe("import_file: the point file, inside the import roots"),
  fileFormat: z.string().optional().describe("import_file: a Civil 3D point file format name, e.g. \"PNEZD (comma delimited)\" (list_formats)"),
  pointGroup: z.string().optional().describe("import_file: put the imported points in this point group (created when missing)"),
  targetSurface: z.string().optional(),
  keepPointNumbers: z.boolean().optional().describe("import: keep each point's number from the file when it is free in the drawing (default true)"),
  pointNumbers: z.array(z.number().int().positive()).optional(),
  name: z.string().optional(),
  description: z.string().optional(),
  includeNumbers: z.string().optional(),
  excludeNumbers: z.string().optional(),
  includeDescriptions: z.string().optional(),
  delimiter: z.string().optional(),
  translateX: z.number().optional(),
  translateY: z.number().optional(),
  translateZ: z.number().optional(),
  rotateRadians: z.number().optional(),
  scaleFactor: z.number().optional(),
  easting: z.number().optional(),
  northing: z.number().optional(),
  elevation: z.number().optional(),
  rawDescription: z.string().optional(),
};

const PointListArgsSchema = z.object({
  action: z.literal("list"),
  groupName: z.string().optional(),
  limit: z.number().optional(),
  offset: z.number().optional(),
});

const PointGetArgsSchema = z.object({
  action: z.literal("get"),
  pointNumber: z.number(),
});

const PointCreateArgsSchema = z.object({
  action: z.literal("create"),
  points: z.array(PointCreateSchema).min(1),
  startNumber: z.number().optional(),
});

const PointListGroupsArgsSchema = z.object({
  action: z.literal("list_groups"),
});

/**
 * `import`: points typed in as text (`data`, one point per line in `format`; pnezd is number, northing, easting,
 * elevation, description). Not for files: a file goes through `import_file`. The plugin asks the user first.
 */
const PointImportArgsSchema = z.object({
  action: z.literal("import"),
  format: z.enum(["pnezd", "penz", "xyzd", "xyz"]),
  data: z.string(),
  targetSurface: z.string().optional(),
  keepPointNumbers: z.boolean().optional(),
});

const PointImportFileArgsSchema = z.object({
  action: z.literal("import_file"),
  path: z.string(),
  fileFormat: z.string(),
  pointGroup: z.string().optional(),
});

const PointListFormatsArgsSchema = z.object({
  action: z.literal("list_formats"),
});

const PointDeleteArgsSchema = z.object({
  action: z.literal("delete"),
  pointNumbers: z.array(z.number()).min(1),
});

/**
 * `group_create`: a point group with Civil 3D's standard query. `includeNumbers` / `excludeNumbers` are range
 * strings ("1-100,205") and `includeDescriptions` a raw-description pattern ("MH*"); with none of them the group is
 * created empty.
 */
const PointGroupCreateArgsSchema = z.object({
  action: z.literal("group_create"),
  name: z.string(),
  description: z.string().optional(),
  includeNumbers: z.string().optional(),
  excludeNumbers: z.string().optional(),
  includeDescriptions: z.string().optional(),
});

const PointGroupUpdateArgsSchema = z.object({
  action: z.literal("group_update"),
  name: z.string(),
  description: z.string().optional(),
  includeNumbers: z.string().optional(),
  excludeNumbers: z.string().optional(),
  includeDescriptions: z.string().optional(),
});

const PointGroupDeleteArgsSchema = z.object({
  action: z.literal("group_delete"),
  name: z.string(),
});

/**
 * `export`: the points as text in the answer (`data`), not a file. A group and a number list together export only
 * the points in both; with neither, every point. Columns come back in the order `import` reads them.
 */
const PointExportArgsSchema = z.object({
  action: z.literal("export"),
  format: z.enum(["pnezd", "penz", "xyzd", "xyz", "csv"]).optional(),
  groupName: z.string().optional(),
  pointNumbers: z.array(z.number().int().positive()).optional(),
  delimiter: z.string().optional(),
});

/**
 * `transform`: moves points in place. The plugin scales, then rotates (radians, counter-clockwise), both about the
 * drawing origin (0,0), not about the points, then translates; elevations are only translated.
 */
const PointTransformArgsSchema = z.object({
  action: z.literal("transform"),
  ...PointTransformSelectionShape,
  translateX: z.number().optional(),
  translateY: z.number().optional(),
  translateZ: z.number().optional(),
  rotateRadians: z.number().optional(),
  scaleFactor: z.number().optional(),
});

/**
 * The point domain: `civil3d_point` (COGO points: list, get, create, import, delete, export, transform) and point
 * groups, with their aliases. Points are survey data: the server instructions tell the AI never to move or edit
 * them unless the user asks. Every action is one call to PointCommands.cs or PointGroupCommands.cs; import, export
 * and delete are approval-gated. Import takes the point data as text (`data`, with its `format`), not a file path.
 * Listed in toolManifest.ts; exposures[0] is the canonical tool.
 */
export const POINT_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "point",
  actions: {
    list: {
      action: "list",
      inputSchema: PointListArgsSchema,
      responseSchema: PointListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listCogoPoints", {
          groupName: args.groupName,
          limit: args.limit,
          offset: args.offset,
        }),
      ),
    },
    get: {
      action: "get",
      inputSchema: PointGetArgsSchema,
      responseSchema: PointSchema.passthrough(),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCogoPoint"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCogoPoint", {
          pointNumber: args.pointNumber,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: PointCreateArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createCogoPoints", {
          points: args.points,
          startNumber: args.startNumber,
        }),
      ),
    },
    list_groups: {
      action: "list_groups",
      inputSchema: PointListGroupsArgsSchema,
      responseSchema: PointGroupsResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPointGroups"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listPointGroups", {}),
      ),
    },
    import: {
      action: "import",
      inputSchema: PointImportArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["create", "import"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["importCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("importCogoPoints", {
          format: args.format,
          data: args.data,
          targetSurface: args.targetSurface,
          keepPointNumbers: args.keepPointNumbers,
        }),
      ),
    },
    // Civil 3D's own importer (CogoPoints.ImportPoints), the way points from a file come in: no question in Civil 3D.
    // create, import (typed-in data) and scripts that add points ask the user there first (PointCreationGate.cs).
    import_file: {
      action: "import_file",
      inputSchema: PointImportFileArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["create", "import"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["importPointFile"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("importPointFile", {
          path: args.path,
          format: args.fileFormat,
          pointGroup: args.pointGroup,
        }),
      ),
    },
    list_formats: {
      action: "list_formats",
      inputSchema: PointListFormatsArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPointFileFormats"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listPointFileFormats", {}),
      ),
    },
    delete: {
      action: "delete",
      inputSchema: PointDeleteArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteCogoPoints", {
          pointNumbers: args.pointNumbers,
        }),
      ),
    },
    group_create: {
      action: "group_create",
      inputSchema: PointGroupCreateArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createPointGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createPointGroup", {
          name: args.name,
          description: args.description ?? "",
          includeNumbers: args.includeNumbers ?? null,
          excludeNumbers: args.excludeNumbers ?? null,
          includeDescriptions: args.includeDescriptions ?? null,
        }),
      ),
    },
    group_update: {
      action: "group_update",
      inputSchema: PointGroupUpdateArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updatePointGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updatePointGroup", {
          name: args.name,
          description: args.description ?? null,
          includeNumbers: args.includeNumbers ?? null,
          excludeNumbers: args.excludeNumbers ?? null,
          includeDescriptions: args.includeDescriptions ?? null,
        }),
      ),
    },
    group_delete: {
      action: "group_delete",
      inputSchema: PointGroupDeleteArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["delete", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deletePointGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deletePointGroup", {
          name: args.name,
        }),
      ),
    },
    export: {
      action: "export",
      inputSchema: PointExportArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["query", "export"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["exportCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportCogoPoints", {
          format: args.format ?? "pnezd",
          groupName: args.groupName ?? null,
          pointNumbers: args.pointNumbers ?? null,
          delimiter: args.delimiter ?? ",",
        }),
      ),
    },
    // NOTE: with neither pointNumbers nor groupName, TransformCogoPointsAsync moves every point in the drawing; nothing
    // here or in the plugin asks for an explicit "all", although points are usually survey data.
    transform: {
      action: "transform",
      inputSchema: PointTransformArgsSchema,
      responseSchema: PointMutationResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["transformCogoPoints"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("transformCogoPoints", {
          pointNumbers: args.pointNumbers ?? null,
          groupName: args.groupName ?? null,
          translateX: args.translateX ?? 0,
          translateY: args.translateY ?? 0,
          translateZ: args.translateZ ?? 0,
          rotateRadians: args.rotateRadians ?? 0,
          scaleFactor: args.scaleFactor ?? 1.0,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_point",
      displayName: "Civil 3D Point",
      description: "Reads, creates, imports, transforms, exports, and deletes Civil 3D COGO points and point groups through a single domain tool. Points from a file: always import_file (Civil 3D's own importer; list_formats gives the format names). create, and import with typed-in data, ask the user in Civil 3D first (a window they answer, a no after 60 s) and are only for points the user asked for, never survey data.",
      inputShape: canonicalPointInputShape,
      supportedActions: ["list", "get", "create", "list_groups", "import", "import_file", "list_formats", "delete", "group_create", "group_update", "group_delete", "export", "transform"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "create_cogo_point",
      displayName: "Create COGO Point",
      description: "Creates a single new COGO point in the Civil 3D drawing.",
      inputShape: {
        easting: z.number(),
        northing: z.number(),
        elevation: z.number().optional(),
        rawDescription: z.string().optional(),
      },
      supportedActions: ["create"],
      resolveAction: (rawArgs) => ({
        action: "create",
        args: {
          action: "create",
          points: [{
            x: rawArgs.easting,
            y: rawArgs.northing,
            z: rawArgs.elevation ?? 0,
            description: rawArgs.rawDescription,
          }],
        },
      }),
    },
    {
      toolName: "civil3d_point_group_create",
      displayName: "Civil 3D Point Group Create",
      description: "Creates a new Civil 3D point group with optional filter criteria.",
      inputShape: {
        name: z.string(),
        description: z.string().optional(),
        includeNumbers: z.string().optional(),
        excludeNumbers: z.string().optional(),
        includeDescriptions: z.string().optional(),
      },
      supportedActions: ["group_create"],
      resolveAction: (rawArgs) => ({
        action: "group_create",
        args: {
          action: "group_create",
          name: rawArgs.name,
          description: rawArgs.description,
          includeNumbers: rawArgs.includeNumbers,
          excludeNumbers: rawArgs.excludeNumbers,
          includeDescriptions: rawArgs.includeDescriptions,
        },
      }),
    },
    {
      toolName: "civil3d_point_group_update",
      displayName: "Civil 3D Point Group Update",
      description: "Updates filter criteria and description of an existing Civil 3D point group.",
      inputShape: {
        name: z.string(),
        description: z.string().optional(),
        includeNumbers: z.string().optional(),
        excludeNumbers: z.string().optional(),
        includeDescriptions: z.string().optional(),
      },
      supportedActions: ["group_update"],
      resolveAction: (rawArgs) => ({
        action: "group_update",
        args: {
          action: "group_update",
          name: rawArgs.name,
          description: rawArgs.description,
          includeNumbers: rawArgs.includeNumbers,
          excludeNumbers: rawArgs.excludeNumbers,
          includeDescriptions: rawArgs.includeDescriptions,
        },
      }),
    },
    {
      toolName: "civil3d_point_group_delete",
      displayName: "Civil 3D Point Group Delete",
      description: "Deletes a Civil 3D point group without deleting the underlying COGO points.",
      inputShape: {
        name: z.string(),
      },
      supportedActions: ["group_delete"],
      resolveAction: (rawArgs) => ({
        action: "group_delete",
        args: {
          action: "group_delete",
          name: rawArgs.name,
        },
      }),
    },
    {
      toolName: "civil3d_point_export",
      displayName: "Civil 3D Point Export",
      description: "Exports Civil 3D COGO points to text or CSV using optional group or point-number filters.",
      inputShape: {
        format: z.enum(["pnezd", "penz", "xyzd", "xyz", "csv"]).optional(),
        groupName: z.string().optional(),
        pointNumbers: z.array(z.number().int().positive()).optional(),
        delimiter: z.string().optional(),
      },
      supportedActions: ["export"],
      resolveAction: (rawArgs) => ({
        action: "export",
        args: {
          action: "export",
          format: rawArgs.format,
          groupName: rawArgs.groupName,
          pointNumbers: rawArgs.pointNumbers,
          delimiter: rawArgs.delimiter,
        },
      }),
    },
    {
      toolName: "civil3d_point_transform",
      displayName: "Civil 3D Point Transform",
      description: "Transforms Civil 3D COGO points by translation, rotation, and/or scaling.",
      inputShape: {
        ...PointTransformSelectionShape,
        translateX: z.number().optional(),
        translateY: z.number().optional(),
        translateZ: z.number().optional(),
        rotateRadians: z.number().optional(),
        scaleFactor: z.number().optional(),
      },
      supportedActions: ["transform"],
      resolveAction: (rawArgs) => ({
        action: "transform",
        args: {
          action: "transform",
          pointNumbers: rawArgs.pointNumbers,
          groupName: rawArgs.groupName,
          translateX: rawArgs.translateX,
          translateY: rawArgs.translateY,
          translateZ: rawArgs.translateZ,
          rotateRadians: rawArgs.rotateRadians,
          scaleFactor: rawArgs.scaleFactor,
        },
      }),
    },
  ],
};
