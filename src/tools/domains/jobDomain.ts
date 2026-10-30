import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The job domain: `civil3d_job`, background jobs for the few plugin operations that can run longer than one request
 * should (start, status, cancel). No aliases. The plugin side is JobCommands.cs and JobRegistry.cs; domainRuntime.ts
 * turns this definition into a validated, approval-checked MCP tool.
 *
 * `start` returns at once with a job id and state "running"; the AI then polls `status` until the state is
 * completed, failed or cancelled. The job is bound to the drawing active when it started and fails, changing
 * nothing, if another drawing is active when its turn in the host queue comes. Corridor rebuilds make their own jobs
 * (civil3d_corridor) and are checked with the same `status` / `cancel`.
 */

/**
 * The operations a job can run, each a plugin method run in the background: publish_sheet_pdf (publishSheetPdf),
 * surface_dem_import (createSurfaceFromDem) and bulk_qc_report (qcReportGenerate).
 */
const JobOperationSchema = z.enum([
  "publish_sheet_pdf",
  "surface_dem_import",
  "bulk_qc_report",
]);

const JobStatusArgs = z.object({ action: z.literal("status"), jobId: z.string().min(1) });
const JobCancelArgs = z.object({ action: z.literal("cancel"), jobId: z.string().min(1) });
/**
 * `start`: `parameters` are the inner plugin method's own arguments, passed through unchecked here (they aren't
 * validated by that method's domain schema), so a wrong name only shows up when the job fails.
 */
const JobStartArgs = z.object({
  action: z.literal("start"),
  operation: JobOperationSchema,
  parameters: z.record(z.string(), z.unknown()),
});

/**
 * A job as JobRegistry.cs reports it: state, progress, the inner method's `result` once completed (or an error
 * object once failed), timing, the drawing it is bound to, and the registry's counts and limits.
 */
const JobResponseSchema = z.object({
  jobId: z.string(),
  state: z.enum(["running", "completed", "failed", "cancelled"]),
  operation: z.string(),
  progressPercent: z.number().nullable().optional(),
  currentPhase: z.string().nullable().optional(),
  estimatedRemainingSeconds: z.number().nullable().optional(),
  result: z.unknown().nullable().optional(),
  // ISO time strings: job data arrives from the plugin as JSON, so these are never Date objects (and zod 4 can't
  // describe a Date in the JSON Schema the MCP client receives).
  createdAt: z.string().optional(),
  completedAt: z.string().nullable().optional(),
  durationMs: z.number().nullable().optional(),
  requestId: z.string().nullable().optional(),
  drawingIdentity: z.string().nullable().optional(),
  failureCategory: z.string().nullable().optional(),
  warnings: z.array(z.string()).optional(),
  registry: z.object({
    total: z.number().int().nonnegative(),
    running: z.number().int().nonnegative(),
    capacity: z.number().int().positive(),
    terminalRetentionMinutes: z.number().int().positive(),
  }).optional(),
}).passthrough();

/**
 * The job domain definition, listed in toolManifest.ts. `start` needs approval (a non-retryable `manage` action);
 * `status` and `cancel` don't need an open drawing, so a job can be checked after the user switches drawings.
 */
export const JOB_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "job",
  actions: {
    start: {
      action: "start",
      inputSchema: JobStartArgs,
      responseSchema: JobResponseSchema,
      capabilities: ["manage", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["startJob"],
      execute: async (args) => await withApplicationConnection(async (appClient) =>
        await appClient.sendCommand("startJob", {
          operation: args.operation,
          parameters: args.parameters,
        })),
    },
    status: {
      action: "status",
      inputSchema: JobStatusArgs,
      responseSchema: JobResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["getJobStatus"],
      execute: async (args) => await withApplicationConnection(async (appClient) =>
        await appClient.sendCommand("getJobStatus", { jobId: args.jobId })),
    },
    cancel: {
      action: "cancel",
      inputSchema: JobCancelArgs,
      responseSchema: JobResponseSchema,
      capabilities: ["manage"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      pluginMethods: ["cancelJob"],
      execute: async (args) => await withApplicationConnection(async (appClient) =>
        await appClient.sendCommand("cancelJob", { jobId: args.jobId })),
    },
  },
  exposures: [{
    toolName: "civil3d_job",
    displayName: "Civil 3D Job",
    description: "Starts approved long-running publish, DEM import, or bulk-QC jobs and checks or cancels existing Civil 3D jobs. Corridor rebuild actions already return jobs directly.",
    inputShape: {
      action: z.enum(["start", "status", "cancel"]),
      jobId: z.string().optional(),
      operation: JobOperationSchema.optional(),
      parameters: z.record(z.string(), z.unknown()).optional(),
    },
    supportedActions: ["start", "status", "cancel"],
    resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
  }],
};
