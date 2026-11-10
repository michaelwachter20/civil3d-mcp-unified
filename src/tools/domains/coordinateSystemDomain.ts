// Coordinate system domain: the `civil3d_coordinate_system` tool (no aliases). Listed in MIGRATED_DOMAIN_DEFINITIONS
// (toolManifest.ts) and turned into an MCP tool by domainRuntime.ts.
// Both actions are read-only forwarders to CoordinateSystemCommands.cs in the plugin (getCoordinateSystemInfo,
// transformCoordinates): they never change the drawing, so approvalPolicy.ts never asks for a token.
// No logic runs in Node.
import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const InfoArgs = z.object({ action: z.literal("info") });
const TransformArgs = z.object({ action: z.literal("transform"), fromSystem: z.string().min(1), toSystem: z.string().min(1), x: z.number(), y: z.number(), z: z.number().optional() });
const InfoResponseSchema = z.object({ name: z.string().nullable(), zone: z.string().nullable(), datum: z.string().nullable(), projection: z.string().nullable(), linearUnits: z.string(), centralMeridian: z.number().nullable(), falseEasting: z.number().nullable(), falseNorthing: z.number().nullable(), scaleFactor: z.number().nullable() }).passthrough();
const TransformResponseSchema = z.object({ x: z.number(), y: z.number(), z: z.number().optional() }).passthrough();

export const COORDINATE_SYSTEM_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "coordinate_system",
  actions: {
    // info -> getCoordinateSystemInfo; transform -> transformCoordinates ('drawing', 'geographic' or a CS code).
    info: { action: "info", inputSchema: InfoArgs, responseSchema: InfoResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getCoordinateSystemInfo"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getCoordinateSystemInfo", {})) },
    transform: { action: "transform", inputSchema: TransformArgs, responseSchema: TransformResponseSchema, capabilities: ["query", "analyze"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["transformCoordinates"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("transformCoordinates", { fromSystem: args.fromSystem, toSystem: args.toSystem, x: args.x, y: args.y, z: args.z })) },
  },
  exposures: [
    { toolName: "civil3d_coordinate_system", displayName: "Civil 3D Coordinate System", description: "Gives the drawing's coordinate system and transforms points. fromSystem and toSystem are 'drawing', 'geographic' (WGS84: x is longitude, y is latitude, in degrees) or any Civil 3D coordinate system code such as FL83-EF.", inputShape: { action: z.enum(["info", "transform"]), fromSystem: z.string().optional(), toSystem: z.string().optional(), x: z.number().optional(), y: z.number().optional(), z: z.number().optional() }, supportedActions: ["info", "transform"], resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }) },
  ],
};
