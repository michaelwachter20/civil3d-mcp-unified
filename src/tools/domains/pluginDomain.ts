import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import { knowledgeStatus } from "../knowledgeBase.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const HealthResponseSchema = z.object({
  connected: z.boolean(),
  civil3dVersion: z.string().optional(),
  pluginVersion: z.string().optional(),
  drawingLoaded: z.boolean(),
  operationInProgress: z.boolean(),
  currentOperation: z.string().nullable(),
  queueDepth: z.number(),
  queueCapacity: z.number(),
  currentOperationStartedAtUnixMs: z.number().nullable(),
  currentRequestId: z.string().nullable(),
  currentOperationDurationMs: z.number().nullable(),
  memoryUsageMb: z.number(),
  logFilePath: z.string(),
  fileLoggingHealthy: z.boolean(),
  fileLoggingError: z.string().nullable(),
  jobs: z.object({
    total: z.number(),
    running: z.number(),
    completed: z.number(),
    failed: z.number(),
    cancelled: z.number(),
    capacity: z.number(),
    terminalRetentionMinutes: z.number(),
  }),
  /** Added by the server: where the knowledge is, and notes about know-how still at the top of the shared folder. */
  knowledge: z.object({
    sharedFolder: z.string(),
    civil3dFolder: z.string(),
    notes: z.array(z.string()).optional(),
  }).optional(),
});

export const PLUGIN_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plugin",
  actions: {
    health: {
      action: "health",
      inputSchema: z.object({ action: z.literal("health") }),
      responseSchema: HealthResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["getCivil3DHealth"],
      execute: async () => {
        const health = await withApplicationConnection(
          async (appClient) => await appClient.sendCommand("getCivil3DHealth", {}),
        );
        let knowledge: ReturnType<typeof knowledgeStatus> | undefined;
        try {
          knowledge = knowledgeStatus();
        } catch {
          // The health check never fails on the knowledge folders.
        }
        return health && typeof health === "object" && knowledge ? { ...health, knowledge } : health;
      },
    },
  },
  exposures: [
    {
      toolName: "civil3d_health",
      displayName: "Civil 3D Health",
      description: "Reports the status of the Civil 3D connection and plugin, and where the knowledge folders are.",
      inputShape: {},
      supportedActions: ["health"],
      resolveAction: () => ({ action: "health", args: { action: "health" } }),
    },
  ],
};
