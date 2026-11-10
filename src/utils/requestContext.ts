// Per-request context carried through async calls with AsyncLocalStorage: the request id that ties log lines, the MCP
// result's _meta and the plugin's log together, and the client's abort signal. domainRuntime.ts (and the HTTP bridge
// through it) starts a context per tool call with runWithRequestId; SocketClient.ts uses createRpcRequestId for each
// JSON-RPC id, and ConnectionManager.ts listens to currentAbortSignal to cancel the TCP request when the caller gives up.
import { AsyncLocalStorage } from "node:async_hooks";
import { randomUUID } from "node:crypto";

/** What one tool call carries through its async work. */
interface RequestExecutionContext {
  requestId: string;
  signal?: AbortSignal;
}

const requestContext = new AsyncLocalStorage<RequestExecutionContext>();

/** The id of the tool call this code runs for, or undefined outside one. */
export function currentRequestId(): string | undefined {
  return requestContext.getStore()?.requestId;
}

/** The caller's abort signal for the current tool call, if the client sent one. */
export function currentAbortSignal(): AbortSignal | undefined {
  return requestContext.getStore()?.signal;
}

/** Reuses the current request id (so nested tool calls share one id), else makes a new UUID. */
export function createRequestId(): string {
  return currentRequestId() ?? randomUUID();
}

/**
 * Id for one JSON-RPC request to the plugin: `<request id>.<uuid>` inside a tool call, so the plugin log can be matched
 * to the tool call, and unique per request because a composite action sends several.
 */
export function createRpcRequestId(): string {
  const traceId = currentRequestId();
  return traceId ? `${traceId}.${randomUUID()}` : randomUUID();
}

/**
 * Runs `action` with `requestId` and `signal` as the current context. The signal defaults to the outer one, so a nested
 * call keeps the original caller's cancellation.
 */
export function runWithRequestId<T>(
  requestId: string,
  action: () => T,
  signal: AbortSignal | undefined = currentAbortSignal(),
): T {
  return requestContext.run({ requestId, signal }, action);
}
