import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { approvalPolicy, isApprovalRequired } from "./approvalPolicy.js";
import { captureToolHandler } from "./toolHandlerRegistry.js";
import { findManifestAction } from "./toolManifest.js";

/**
 * The two approval tools, `civil3d_preview_action` and `civil3d_request_approval`, registered directly by
 * register.ts (not through the domain manifest). The rules themselves live in approvalPolicy.ts; domainRuntime.ts
 * enforces them on every manifest tool call.
 *
 * The flow for a risky action (delete, import, a non-retryable edit, ...): the AI may preview it to learn whether a
 * token is needed, requests a token with the exact tool, action and parameters it will send, then repeats the call
 * with `approvalToken`. The token is single use, lasts `ttlSeconds` (30-600, default 300) and is bound to a hash of
 * those parameters and to the active drawing's fingerprint, so a token for "delete alignment Elm St" can't delete
 * anything else, or anything after the drawing changed.
 */
type JsonObject = Record<string, unknown>;

/** Arguments of both tools; `parameters` must be the full argument object of the later call, `action` included. */
const approvalInputShape = {
  toolName: z.string().min(1),
  action: z.string().min(1),
  parameters: z.record(z.string(), z.unknown()),
  ttlSeconds: z.number().int().min(30).max(600).optional(),
};

const PreviewResponseSchema = z.object({
  status: z.enum(["approval_required", "ready"]),
  toolName: z.string(),
  action: z.string(),
  capabilities: z.array(z.string()),
  safeForRetry: z.boolean(),
  instruction: z.string(),
});

const ApprovalResponseSchema = z.object({
  status: z.literal("approved"),
  toolName: z.string(),
  action: z.string(),
  approvalToken: z.string(),
  expiresAt: z.string(),
  drawingFingerprint: z.string(),
  instruction: z.string(),
});

/**
 * The manifest action being approved, checked the way the real call will be: the tool's exposure must resolve the
 * parameters to the named action (for the canonical tool that means `parameters.action` is set) and the action's
 * input schema must accept them. So a token is never issued for a call that would fail validation anyway.
 */
function resolveApprovalTarget(args: { toolName: string; action: string; parameters: JsonObject }) {
  const target = findManifestAction(args.toolName, args.action);
  if (!target) {
    throw new Error(`Unknown tool action '${args.toolName}' / '${args.action}'.`);
  }

  const resolved = target.exposure.resolveAction(args.parameters);
  if (resolved.action !== args.action) {
    throw new Error(
      `Parameters do not resolve to action '${args.action}' for tool '${args.toolName}'. ` +
      "Include the tool action in parameters when calling a multi-action tool.",
    );
  }
  target.actionDefinition.inputSchema.parse(resolved.args);
  return target;
}

function successResult(structuredContent: JsonObject) {
  return {
    content: [{
      type: "text" as const,
      text: JSON.stringify(structuredContent, null, 2),
    }],
    structuredContent,
  };
}

function errorResult(toolName: string, error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  return {
    content: [{ type: "text" as const, text: `${toolName} failed: ${message}` }],
    isError: true,
  };
}

/**
 * Registers both tools with the MCP server and captures their handlers for the HTTP bridge. The preview never
 * changes anything; the request refuses (an error result) for an action that doesn't need approval, and asks the
 * plugin for the drawing fingerprint (getDrawingInfo), so it needs Civil 3D running; only an action that needs no
 * drawing may get a token with none open. Failures come back as `isError` results with the message, not thrown.
 */
export function registerApprovalTool(server: McpServer) {
  const previewHandler = async (rawArgs: JsonObject) => {
    try {
      const args = rawArgs as { toolName: string; action: string; parameters: JsonObject };
      const target = resolveApprovalTarget(args);
      const requiresApproval = isApprovalRequired({
        toolName: args.toolName,
        action: args.action,
        capabilities: target.actionDefinition.capabilities,
        safeForRetry: target.actionDefinition.safeForRetry,
        requiresActiveDrawing: target.actionDefinition.requiresActiveDrawing,
      });
      return successResult({
        status: requiresApproval ? "approval_required" : "ready",
        toolName: args.toolName,
        action: args.action,
        capabilities: target.actionDefinition.capabilities,
        safeForRetry: target.actionDefinition.safeForRetry,
        instruction: requiresApproval
          ? "Call civil3d_request_approval with the same toolName, action, and parameters before execution."
          : "This action may be executed directly.",
      });
    } catch (error) {
      return errorResult("civil3d_preview_action", error);
    }
  };

  const requestHandler = async (rawArgs: JsonObject) => {
    try {
      const args = rawArgs as {
        toolName: string;
        action: string;
        parameters: JsonObject;
        ttlSeconds?: number;
      };
      const target = resolveApprovalTarget(args);
      const receipt = await approvalPolicy.requestApproval(
        {
          toolName: args.toolName,
          action: args.action,
          capabilities: target.actionDefinition.capabilities,
          safeForRetry: target.actionDefinition.safeForRetry,
          requiresActiveDrawing: target.actionDefinition.requiresActiveDrawing,
        },
        args.parameters,
        (args.ttlSeconds ?? 300) * 1000,
      );

      return successResult({
        status: "approved",
        toolName: args.toolName,
        action: args.action,
        ...receipt,
        instruction: "Retry the approved tool call with approvalToken and the identical parameters.",
      });
    } catch (error) {
      return errorResult("civil3d_request_approval", error);
    }
  };

  captureToolHandler("civil3d_preview_action", previewHandler);
  captureToolHandler("civil3d_request_approval", requestHandler);

  server.registerTool(
    "civil3d_preview_action",
    {
      title: "Preview Civil 3D Action",
      description: "Validates and previews whether a Civil 3D action requires approval before it can execute. This tool never changes the drawing.",
      inputSchema: {
        toolName: approvalInputShape.toolName,
        action: approvalInputShape.action,
        parameters: approvalInputShape.parameters,
      },
      outputSchema: PreviewResponseSchema,
      annotations: {
        title: "Preview Civil 3D Action",
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    previewHandler,
  );

  server.registerTool(
    "civil3d_request_approval",
    {
      title: "Request Civil 3D Approval",
      description: "Issues a short-lived, single-use approval token for a specific destructive or ambiguous Civil 3D action in the active drawing.",
      inputSchema: approvalInputShape,
      outputSchema: ApprovalResponseSchema,
      annotations: {
        title: "Request Civil 3D Approval",
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    requestHandler,
  );
}
