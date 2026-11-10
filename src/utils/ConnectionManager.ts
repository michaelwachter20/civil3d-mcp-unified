// The one way TypeScript code reaches the Civil 3D plugin: `withApplicationConnection(client => client.sendCommand(...))`.
// Used by every domain action's execute, the orchestrator (ProjectContextService.ts), approvalPolicy.ts (the drawing
// fingerprint), the knowledge tools (best-effort release/drawing lookups) and verify-live-plugin.mjs. Each
// sendCommand opens its own TCP connection (SocketClient.ts) to CIVIL3D_HOST:CIVIL3D_PORT (default localhost:8080),
// waits at most CIVIL3D_CONNECT_TIMEOUT ms (default 5000) to connect, sends one request and closes. The caller's abort
// signal (requestContext.ts) destroys the socket and turns the failure into CIVIL3D.CANCELLED. No approval or
// validation happens here: that is domainRuntime.ts's job before execute runs.
import { ApplicationClientConnection, Civil3DRpcError } from "./SocketClient.js";
import { createLogger } from "./logger.js";
import { currentAbortSignal } from "./requestContext.js";

const log = createLogger("ConnectionManager");

const CIVIL3D_HOST = process.env.CIVIL3D_HOST ?? "localhost";
const CIVIL3D_PORT = parseInt(process.env.CIVIL3D_PORT ?? "8080", 10);
const CONNECT_TIMEOUT_MS = parseInt(process.env.CIVIL3D_CONNECT_TIMEOUT ?? "5000", 10);

/** What a domain action sees: send a plugin method (a CommandDispatcher.cs name) with params, get its `result`. */
export interface ApplicationCommandClient {
  sendCommand(command: string, params?: unknown): Promise<any>;
}

/**
 * Runs an operation against the Civil 3D plugin. The native transport accepts
 * exactly one JSON-RPC request per TCP connection, so every sendCommand call
 * receives its own short-lived connection. Composite domain actions may safely
 * issue several sequential commands through the client passed to operation.
 */
export async function withApplicationConnection<T>(
  operation: (client: ApplicationCommandClient) => Promise<T>
): Promise<T> {
  const client: ApplicationCommandClient = {
    sendCommand: async (command, params = {}) => await sendSingleCommand(command, params),
  };

  return await operation(client);
}

/**
 * Connects, sends one command, waits for the answer, and always disconnects. Throws "Failed to connect ..." or
 * "... timed out" when the plugin isn't listening (AI connection off, Civil 3D closed), the plugin's Civil3DRpcError
 * for an error answer, and CIVIL3D.CANCELLED when the caller aborted at any point (that error wins over whatever the
 * aborted socket reported).
 */
async function sendSingleCommand(command: string, params: unknown): Promise<any> {
  const appClient = new ApplicationClientConnection(CIVIL3D_HOST, CIVIL3D_PORT);
  const signal = currentAbortSignal();
  const cancellationError = new Civil3DRpcError(
    `Civil 3D command '${command}' was cancelled by the caller.`,
    "CIVIL3D.CANCELLED",
    -32010,
  );
  const cancel = () => appClient.cancel(cancellationError);

  try {
    if (signal?.aborted) throw cancellationError;
    signal?.addEventListener("abort", cancel, { once: true });
    if (!appClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        let settled = false;

        const cleanup = () => {
          clearTimeout(timeout);
          appClient.socket.removeListener("connect", onConnect);
          appClient.socket.removeListener("error", onError);
        };

        const onConnect = () => {
          if (settled) {
            return;
          }
          settled = true;
          cleanup();
          resolve();
        };

        const onError = (error: Error) => {
          if (settled) {
            return;
          }
          settled = true;
          cleanup();
          log.error("Connection failed", {
            host: CIVIL3D_HOST,
            port: CIVIL3D_PORT,
            error: error.message,
          });
          reject(new Error(`Failed to connect to Civil 3D plugin at ${CIVIL3D_HOST}:${CIVIL3D_PORT}`));
        };

        appClient.socket.on("connect", onConnect);
        appClient.socket.on("error", onError);

        const timeout = setTimeout(() => {
          if (settled) {
            return;
          }
          settled = true;
          cleanup();
          appClient.socket.destroy();
          log.warn("Connection timed out", { host: CIVIL3D_HOST, port: CIVIL3D_PORT, timeoutMs: CONNECT_TIMEOUT_MS });
          reject(new Error(`Connection to Civil 3D plugin timed out after ${CONNECT_TIMEOUT_MS}ms`));
        }, CONNECT_TIMEOUT_MS);

        if (!appClient.connect()) {
          onError(new Error("Socket connect call failed."));
        }
      });
    }

    return await appClient.sendCommand(command, params);
  } catch (error) {
    if (signal?.aborted) throw cancellationError;
    throw error;
  } finally {
    signal?.removeEventListener("abort", cancel);
    appClient.disconnect();
  }
}
