/**
 * Global registry that captures MCP tool handlers during registerTool() registration.
 * The HTTP bridge uses this registry to invoke any registered tool without going
 * through the MCP stdio protocol, so HTTP clients reach every tool, and
 * civil3d_orchestrate uses it to run the tool it picked.
 *
 * How it works:
 *   1. Domain (domainRuntime.ts), approval (approvalTool.ts) and help (helpTool.ts) registration capture the same
 *      handlers passed to registerTool(); every manifest alias is captured, even when compact mode doesn't list it
 *   2. Each handler is stored here by tool name
 *   3. httpBridge.ts and civil3d_orchestrate.ts look up handlers from this registry
 *   4. The handler is called with the same params the MCP protocol would provide (with an empty `extra`: no progress;
 *      cancellation and the request id come from the caller's requestContext, e.g. the HTTP request's)
 *   5. The MCP CallToolResult is unwrapped to extract the JSON payload
 *
 * Because the same handler runs, schema validation and approval apply exactly as for an MCP call.
 */
import { Civil3DRpcError } from "../utils/SocketClient.js";

/** MCP CallToolResult shape returned by every registerTool() handler */
interface McpCallToolResult {
  content: Array<{
    type: string;
    text?: string;
    data?: string;
    mimeType?: string;
    uri?: string;
  }>;
  structuredContent?: Record<string, unknown>;
  isError?: boolean;
  errorCode?: string;
  rpcCode?: number;
}

/** A registered tool's handler: (arguments, MCP request extra) -> CallToolResult. */
type ToolHandler = (
  args: Record<string, unknown>,
  extra?: unknown
) => Promise<McpCallToolResult>;

const _handlers = new Map<string, ToolHandler>();

/**
 * Store a tool handler during registration.
 * Called while domain and approval tools are registered.
 */
export function captureToolHandler(name: string, handler: ToolHandler): void {
  _handlers.set(name, handler);
}

/**
 * Retrieve a previously captured tool handler by name.
 */
export function getToolHandler(name: string): ToolHandler | undefined {
  return _handlers.get(name);
}

/**
 * Check whether a handler exists for the given tool name.
 */
export function hasToolHandler(name: string): boolean {
  return _handlers.has(name);
}

/**
 * Return all registered tool names (sorted).
 */
export function listRegisteredToolNames(): string[] {
  return [..._handlers.keys()].sort();
}

/**
 * Execute a registered tool and unwrap the MCP CallToolResult into a plain
 * JSON-serialisable object suitable for HTTP responses.
 *
 * Throws if the tool is not registered (a plain Error) or if the handler signals an error: a Civil3DRpcError when the
 * result carries an errorCode (manifest and help tools), else a plain Error with the result text (the approval
 * tools), which the HTTP bridge classifies by message.
 */
export async function executeRegisteredTool(
  toolName: string,
  parameters: Record<string, unknown>
): Promise<unknown> {
  const handler = _handlers.get(toolName);
  if (!handler) {
    throw new Error(
      `Tool '${toolName}' is not registered. Available: ${listRegisteredToolNames().length} tools.`
    );
  }

  const mcpResult = await handler(parameters, {});

  // If the tool signalled an error, throw so the caller can set the HTTP status
  if (mcpResult?.isError) {
    const errorText =
      mcpResult.content?.[0]?.text ?? `Tool '${toolName}' returned an error`;
    if (!mcpResult.errorCode) {
      // Preserve connection/unavailability errors for the HTTP bridge's
      // message-based classifier instead of relabeling them as internal 500s.
      throw new Error(errorText);
    }
    throw new Civil3DRpcError(
      errorText,
      mcpResult.errorCode,
      mcpResult.rpcCode ?? -32000,
    );
  }

  // Prefer the typed MCP result while preserving the plain HTTP payload shape.
  if (mcpResult?.structuredContent) {
    if (
      "result" in mcpResult.structuredContent &&
      "action" in mcpResult.structuredContent
    ) {
      return mcpResult.structuredContent.result;
    }
    return mcpResult.structuredContent;
  }

  // Backwards-compatible text envelope fallback.
  const text = mcpResult?.content?.[0]?.text;
  if (text) {
    try {
      return JSON.parse(text);
    } catch {
      // Not JSON – return as a message string
      return { message: text };
    }
  }

  return mcpResult;
}
