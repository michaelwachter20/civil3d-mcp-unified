import { z, type ZodRawShape, type ZodTypeAny } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { approvalPolicy, getActiveDrawingFingerprint, hasApprovalRisk } from "./approvalPolicy.js";
import { captureToolHandler } from "./toolHandlerRegistry.js";
import { maybeStoreReportResource } from "./reportResourceStore.js";
import { idempotencyStore } from "./idempotencyStore.js";
import { createLogger } from "../utils/logger.js";
import { createRequestId, runWithRequestId } from "../utils/requestContext.js";
import type { ToolCapability, ToolCatalogEntry, ToolDomain } from "./toolMetadata.js";

/**
 * The runtime every manifest tool goes through: it turns a DomainToolDefinition (one file per domain in domains/)
 * into MCP tools and gives each call the same path:
 *
 *   resolveAction (exposure) -> inputSchema.parse -> approvalPolicy.enforce -> action.execute
 *     -> responseSchema.parse -> MCP result (text + structuredContent + resource link for big reports)
 *
 * plus a request id for the logs, MCP progress notifications, optional idempotency keys, and one error shape
 * (buildToolErrorResult) for everything a handler throws. toolManifest.ts calls captureDomainToolHandlers (for
 * the HTTP bridge and civil3d_orchestrate) and registerSelectedDomainTools (for the MCP client) for each domain.
 */

type JsonObject = Record<string, unknown>;

/** The parts of the MCP SDK's handler `extra` this file uses: the progress token, abort signal and notifier. */
interface ToolProgressExtra {
  _meta?: { progressToken?: string | number };
  signal?: AbortSignal;
  sendNotification?: (notification: {
    method: "notifications/progress";
    params: {
      progressToken: string | number;
      progress: number;
      total?: number;
      message?: string;
    };
  }) => Promise<void>;
}

/** Capabilities that change something; a tool whose actions have none of them is annotated readOnlyHint. */
const MUTATING_CAPABILITIES = new Set<ToolCapability>([
  "create",
  "edit",
  "delete",
  "manage",
  "generate",
  "register",
  "export",
  "import",
]);
const log = createLogger("DomainRuntime");

/**
 * One action of a domain. `capabilities` and `safeForRetry` decide approval (approvalPolicy.ts) and the MCP hints;
 * `requiresActiveDrawing` decides whether the drawing fingerprint is needed; `pluginMethods` names the C# methods
 * `execute` calls (checked against CommandDispatcher.cs by a test, and listed in docs/tools.generated.md).
 */
export interface DomainActionDefinition<TArgs = JsonObject> {
  action: string;
  inputSchema: z.ZodType<TArgs>;
  execute: (args: TArgs) => Promise<unknown>;
  responseSchema?: ZodTypeAny;
  capabilities: ToolCapability[];
  requiresActiveDrawing: boolean;
  safeForRetry: boolean;
  pluginMethods?: string[];
}

/**
 * One MCP tool that reaches some of a domain's actions. `inputShape` is the schema the client sees;
 * `resolveAction` maps the raw arguments to an action and that action's arguments (an alias fixes the action and
 * may rename fields). The optional catalog fields override what buildDomainToolCatalogEntries would derive.
 */
export interface DomainToolExposure {
  toolName: string;
  displayName: string;
  description: string;
  inputShape: ZodRawShape;
  supportedActions: string[];
  resolveAction: (rawArgs: JsonObject) => { action: string; args: JsonObject };
  capabilities?: ToolCapability[];
  operations?: string[];
  pluginMethods?: string[];
  requiresActiveDrawing?: boolean;
  safeForRetry?: boolean;
  status?: "implemented" | "planned";
}

/** A domain: its actions and its exposures; exposures[0] is the canonical tool shown in compact mode. */
export interface DomainToolDefinition {
  domain: ToolDomain;
  actions: Record<string, DomainActionDefinition>;
  exposures: DomainToolExposure[];
}

function uniqueStrings(values: Iterable<string | undefined>): string[] | undefined {
  const unique = [...new Set([...values].filter((value): value is string => Boolean(value)))];
  return unique.length > 0 ? unique : undefined;
}

function uniqueCapabilities(values: Iterable<ToolCapability | undefined>): ToolCapability[] {
  return [...new Set([...values].filter((value): value is ToolCapability => Boolean(value)))];
}

/**
 * The MCP error result for anything a handler threw: `isError`, the message prefixed with the tool and action, and
 * `errorCode` / `rpcCode` taken from the error when it carries them (Civil3DRpcError, knowledgeError), otherwise
 * inferred from the message. The HTTP bridge reads errorCode to choose the status.
 */
function buildToolErrorResult(toolName: string, actionName: string | undefined, error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  const typedErrorCode = error && typeof error === "object" && "code" in error
    && typeof error.code === "string"
    ? error.code
    : undefined;
  const errorCode = typedErrorCode ?? inferDomainErrorCode(message);
  const typedRpcCode = error && typeof error === "object" && "rpcCode" in error
    && typeof error.rpcCode === "number"
    ? error.rpcCode
    : undefined;
  const rpcCode = typedRpcCode ?? inferRpcErrorCode(errorCode);
  const scopedName = actionName ? `${toolName} action '${actionName}'` : toolName;

  console.error(`Error in ${scopedName}:`, error);

  return {
    content: [
      {
        type: "text" as const,
        text: `${scopedName} failed: ${message}`,
      },
    ],
    isError: true,
    errorCode,
    rpcCode,
  };
}

/** A CIVIL3D.* code guessed from an untyped error's message (timeouts, connection failures, not found, ...). */
function inferDomainErrorCode(message: string): string {
  if (/timed out|timeout/i.test(message)) return "CIVIL3D.TIMEOUT";
  if (/failed to connect|connection (?:failed|closed)|plugin not running/i.test(message)) return "CIVIL3D.UNAVAILABLE";
  if (/approval required|already exists|conflict/i.test(message)) return "CIVIL3D.CONFLICT";
  if (/not found|does not exist/i.test(message)) return "CIVIL3D.OBJECT_NOT_FOUND";
  if (/not registered|unknown tool/i.test(message)) return "CIVIL3D.METHOD_NOT_FOUND";
  if (/missing required|required fields?|invalid (?:input|parameter)|validation/i.test(message)) return "CIVIL3D.INVALID_INPUT";
  return "CIVIL3D.INTERNAL_ERROR";
}

/** The JSON-RPC number for a CIVIL3D.* code, matching JsonRpcProtocol.NumericErrorCode on the plugin side. */
function inferRpcErrorCode(domainCode: string): number {
  if (domainCode === "CIVIL3D.METHOD_NOT_FOUND") return -32601;
  if (domainCode === "CIVIL3D.INVALID_INPUT") return -32602;
  if (domainCode === "CIVIL3D.UNAVAILABLE") return -32001;
  if (domainCode === "CIVIL3D.OBJECT_NOT_FOUND") return -32004;
  if (domainCode === "CIVIL3D.TIMEOUT") return -32008;
  if (domainCode === "CIVIL3D.CONFLICT") return -32009;
  if (domainCode === "CIVIL3D.CANCELLED") return -32010;
  return -32000;
}

/**
 * Runs one call: resolve and validate the action, refuse an idempotencyKey on a non-retryable action, enforce
 * approval with the raw arguments (the token is bound to exactly what the client sent), execute, validate the
 * response, and keep a large or report-like answer as an MCP resource. With an idempotencyKey the result is shared
 * between identical calls on the same drawing (idempotencyStore.ts); approvalToken and the key itself are left out
 * of the signature.
 */
async function executeExposure(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
  rawArgs: JsonObject,
  progressExtra?: ToolProgressExtra,
) {
  const resolved = exposure.resolveAction(rawArgs);
  const actionName = resolved.action;

  if (!exposure.supportedActions.includes(actionName)) {
    throw new Error(
      `Unsupported action '${actionName}' for tool '${exposure.toolName}'. ` +
      `Supported actions: ${exposure.supportedActions.join(", ")}.`,
    );
  }

  const actionDefinition = definition.actions[actionName];
  if (!actionDefinition) {
    throw new Error(`Action '${actionName}' is not defined for domain '${definition.domain}'.`);
  }

  const parsedArgs = actionDefinition.inputSchema.parse(resolved.args);
  const idempotencyKey = typeof rawArgs.idempotencyKey === "string"
    ? rawArgs.idempotencyKey
    : undefined;
  if (idempotencyKey && !actionDefinition.safeForRetry) {
    const error = new Error(`Action '${actionName}' does not support idempotent retries.`) as Error & { code: string };
    error.code = "CIVIL3D.INVALID_INPUT";
    throw error;
  }

  const executeOnce = async () => {
    await approvalPolicy.enforce(
      {
        toolName: exposure.toolName,
        action: actionName,
        capabilities: actionDefinition.capabilities,
        safeForRetry: actionDefinition.safeForRetry,
        requiresActiveDrawing: actionDefinition.requiresActiveDrawing,
      },
      rawArgs,
    );
    await emitProgress(
      progressExtra,
      50,
      100,
      `${exposure.displayName}: validated and executing '${actionName}'.`,
    );
    const response = await actionDefinition.execute(parsedArgs);
    const validatedResponse = actionDefinition.responseSchema
      ? actionDefinition.responseSchema.parse(response)
      : response;
    const serializedResponse = JSON.stringify(validatedResponse, null, 2);
    const reportResource = maybeStoreReportResource(actionName, serializedResponse);

    return {
      content: [
        { type: "text" as const, text: serializedResponse },
        ...(reportResource ? [{
          type: "resource_link" as const,
          uri: reportResource.uri,
          name: reportResource.name,
          description: "Structured Civil 3D result retained for MCP resource retrieval.",
          mimeType: "application/json",
          size: reportResource.size,
        }] : []),
      ],
      structuredContent: { action: actionName, result: validatedResponse },
    };
  };

  if (!idempotencyKey) return executeOnce();
  const signature = { ...rawArgs };
  delete signature.approvalToken;
  delete signature.idempotencyKey;
  const drawingScope = actionDefinition.requiresActiveDrawing
    ? await getActiveDrawingFingerprint()
    : "drawing-independent";
  return idempotencyStore.execute(
    `${exposure.toolName}:${actionName}:${drawingScope}`,
    idempotencyKey,
    signature,
    executeOnce,
  );
}

/** Sends an MCP progress notification when the client asked for progress; a failed send is logged, never fatal. */
async function emitProgress(
  extra: ToolProgressExtra | undefined,
  progress: number,
  total: number,
  message: string,
): Promise<void> {
  const progressToken = extra?._meta?.progressToken;
  if (progressToken === undefined || typeof extra?.sendNotification !== "function") {
    return;
  }

  try {
    await extra.sendNotification({
      method: "notifications/progress",
      params: { progressToken, progress, total, message },
    });
  } catch (error) {
    console.warn(`Failed to send MCP progress notification for '${message}':`, error);
  }
}

/** The MCP output schema: { action, result } with the union of the supported actions' response schemas. */
export function buildExposureOutputSchema(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
): ZodTypeAny {
  const actionNames = exposure.supportedActions;
  const actionSchema = actionNames.length > 0
    ? z.enum(actionNames as [string, ...string[]])
    : z.string();
  const responseSchemas = actionNames.map((actionName) =>
    definition.actions[actionName]?.responseSchema ?? z.unknown()
  );
  const resultSchema = responseSchemas.length === 0
    ? z.unknown()
    : responseSchemas.length === 1
      ? responseSchemas[0]
      : z.union(responseSchemas as unknown as [ZodTypeAny, ZodTypeAny, ...ZodTypeAny[]]);

  return z.object({ action: actionSchema, result: resultSchema });
}

/**
 * MCP tool annotations derived from the actions: readOnlyHint when none mutates, destructiveHint when any needs
 * approval, idempotentHint when all are safe to retry. openWorldHint is false: the tools act on the local drawing.
 */
export function buildExposureAnnotations(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
) {
  const actions = exposure.supportedActions
    .map((actionName) => definition.actions[actionName])
    .filter((action): action is DomainActionDefinition => Boolean(action));
  const isReadOnly = actions.every((action) =>
    action.capabilities.every((capability) => !MUTATING_CAPABILITIES.has(capability))
  );
  const isDestructive = actions.some((action) => hasApprovalRisk({
    toolName: exposure.toolName,
    action: action.action,
    capabilities: action.capabilities,
    safeForRetry: action.safeForRetry,
  }));

  return {
    title: exposure.displayName,
    readOnlyHint: isReadOnly,
    destructiveHint: isDestructive,
    idempotentHint: actions.length > 0 && actions.every((action) => action.safeForRetry),
    openWorldHint: false,
  };
}

/** Registers every exposure of a domain with the MCP server (tests and tools that don't need compact mode). */
export function registerDomainTools(server: McpServer, definition: DomainToolDefinition) {
  registerSelectedDomainTools(server, definition, definition.exposures);
}

/**
 * The handler for one exposure: gives the call a request id (carried with the abort signal through
 * requestContext.ts, so the plugin call can be cancelled and logs line up), sends progress, logs start and end, and
 * turns any error into buildToolErrorResult. It never throws; the request id is returned in `_meta`.
 */
function createExposureHandler(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
) {
  return async (rawArgs: JsonObject, extra?: unknown) => {
    const progressExtra = extra && typeof extra === "object"
      ? extra as ToolProgressExtra
      : undefined;
    const requestId = createRequestId();
    const startedAt = performance.now();
    return runWithRequestId(requestId, async () => {
      try {
        log.info("Tool request started", { requestId, tool: exposure.toolName });
        await emitProgress(progressExtra, 0, 100, `${exposure.displayName}: request received.`);
        const result = await executeExposure(definition, exposure, rawArgs, progressExtra);
        await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: completed.`);
        log.info("Tool request completed", {
          requestId,
          tool: exposure.toolName,
          durationMs: Math.round(performance.now() - startedAt),
        });
        return { ...result, _meta: { requestId } };
      } catch (error) {
      const actionName =
        typeof rawArgs.action === "string"
          ? String(rawArgs.action)
          : exposure.supportedActions.length === 1
            ? exposure.supportedActions[0]
            : undefined;

        await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: failed.`);
        log.error("Tool request failed", {
          requestId,
          tool: exposure.toolName,
          durationMs: Math.round(performance.now() - startedAt),
          error: error instanceof Error ? error.message : String(error),
        });
        return { ...buildToolErrorResult(exposure.toolName, actionName, error), _meta: { requestId } };
      }
    }, progressExtra?.signal);
  };
}

/**
 * Stores a handler for every exposure, aliases included, in toolHandlerRegistry.ts, so the HTTP bridge and
 * civil3d_orchestrate can call any tool even when compact mode doesn't list it to MCP clients.
 */
export function captureDomainToolHandlers(definition: DomainToolDefinition) {
  for (const exposure of definition.exposures) {
    captureToolHandler(exposure.toolName, createExposureHandler(definition, exposure));
  }
}

/**
 * Registers the given exposures with the MCP server, adding the arguments every tool accepts (`approvalToken`,
 * `idempotencyKey`), the output schema and the annotations.
 */
export function registerSelectedDomainTools(
  server: McpServer,
  definition: DomainToolDefinition,
  exposures: DomainToolExposure[],
) {
  for (const exposure of exposures) {
    server.registerTool(
      exposure.toolName,
      {
        title: exposure.displayName,
        description: exposure.description,
        inputSchema: {
          ...exposure.inputShape,
          approvalToken: z.string().min(1).optional(),
          idempotencyKey: z.string().min(1).max(128).optional(),
        },
        outputSchema: buildExposureOutputSchema(definition, exposure),
        annotations: buildExposureAnnotations(definition, exposure),
      },
      createExposureHandler(definition, exposure),
    );
  }
}

/**
 * Catalog entries (tool_catalog.ts, docs/tools.generated.md, civil3d://catalog/tools) for a domain's exposures:
 * capabilities and plugin methods merged from the supported actions, `operations` when there is more than one,
 * requiresActiveDrawing if any action needs it, safeForRetry only if every action is.
 */
export function buildDomainToolCatalogEntries(definition: DomainToolDefinition): ToolCatalogEntry[] {
  return definition.exposures.map((exposure) => {
    const supportedActionDefinitions = exposure.supportedActions
      .map((actionName) => definition.actions[actionName])
      .filter((action): action is DomainActionDefinition => Boolean(action));

    return {
      toolName: exposure.toolName,
      displayName: exposure.displayName,
      description: exposure.description,
      domain: definition.domain,
      capabilities: exposure.capabilities ?? uniqueCapabilities(
        supportedActionDefinitions.flatMap((action) => action.capabilities),
      ),
      operations: exposure.operations ?? (exposure.supportedActions.length > 1
        ? exposure.supportedActions
        : undefined),
      pluginMethods: exposure.pluginMethods ?? uniqueStrings(
        supportedActionDefinitions.flatMap((action) => action.pluginMethods ?? []),
      ),
      requiresActiveDrawing: exposure.requiresActiveDrawing
        ?? supportedActionDefinitions.some((action) => action.requiresActiveDrawing),
      safeForRetry: exposure.safeForRetry
        ?? supportedActionDefinitions.every((action) => action.safeForRetry),
      status: exposure.status ?? "implemented",
    };
  });
}
