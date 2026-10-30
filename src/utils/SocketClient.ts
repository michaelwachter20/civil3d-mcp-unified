import * as net from "net";
import { createLogger } from "./logger.js";
import { createRpcRequestId } from "./requestContext.js";

/**
 * The TCP client for the Civil 3D plugin: one JSON-RPC 2.0 request over one connection to the plugin's loopback
 * listener (RpcTcpServer.cs, port 8080 by default), then the plugin's single JSON answer. ConnectionManager.ts makes
 * one ApplicationClientConnection per call (`withApplicationConnection`), wires the caller's abort signal to
 * `cancel`, and disconnects afterwards; domain actions only ever see `sendCommand`.
 *
 * A JSON-RPC error answer becomes a Civil3DRpcError carrying the plugin's domain code (`error.data.code`, such as
 * CIVIL3D.OBJECT_NOT_FOUND) and the numeric code; domainRuntime.ts and the HTTP bridge read both. Connection
 * failures, timeouts and malformed answers are plain Errors.
 */

const log = createLogger("SocketClient");
/** How long one command may take before it is rejected (CIVIL3D_COMMAND_TIMEOUT, ms; default two minutes). */
const COMMAND_TIMEOUT_MS = parseInt(process.env.CIVIL3D_COMMAND_TIMEOUT ?? "120000", 10);
/** Largest answer accepted before the connection is dropped (CIVIL3D_MAX_RESPONSE_BYTES; default 8 MiB). */
const MAX_RESPONSE_BYTES = parseInt(process.env.CIVIL3D_MAX_RESPONSE_BYTES ?? "8388608", 10);

/** A request waiting for its answer: settled by the answer, the timeout, or the connection failing. */
interface PendingRequest {
  resolve: (response: string) => void;
  reject: (error: Error) => void;
  timeout: NodeJS.Timeout;
}

/**
 * An error the plugin answered with: `code` is the domain code (e.g. "CIVIL3D.INVALID_INPUT") and `rpcCode` the
 * JSON-RPC number (e.g. -32602). Also thrown by ConnectionManager.ts and toolHandlerRegistry.ts for cancellation and
 * unknown tools, so callers can treat every coded failure the same way.
 */
export class Civil3DRpcError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly rpcCode: number,
  ) {
    super(message);
    this.name = "Civil3DRpcError";
  }
}

/**
 * One connection to the plugin. The plugin reads one request per connection, so although the class keeps a map of
 * pending requests, it is used for a single `sendCommand` and then disconnected (ConnectionManager.ts).
 */
export class ApplicationClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  responseCallbacks: Map<string, PendingRequest> = new Map();
  buffer: string = "";

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
    this.socket = new net.Socket();
    this.setupSocketListeners();
  }

  private setupSocketListeners(): void {
    this.socket.on("connect", () => {
      this.isConnected = true;
      log.info("Connected", { host: this.host, port: this.port });
    });

    // NOTE: each chunk is decoded on its own (`data.toString()`), so a multi-byte UTF-8 character split across two
    // chunks comes out as replacement characters; a large answer with non-ASCII text (a layer named "Höhe", say) can
    // be garbled. Collecting Buffers, or socket.setEncoding("utf8"), would decode across chunk boundaries.
    this.socket.on("data", (data) => {
      this.buffer += data.toString();
      if (Buffer.byteLength(this.buffer, "utf8") > MAX_RESPONSE_BYTES) {
        const error = new Error(`Civil 3D response exceeds ${MAX_RESPONSE_BYTES} bytes.`);
        this.rejectPendingRequests(error);
        this.buffer = "";
        this.socket.destroy(error);
        return;
      }
      this.processBuffer();
    });

    this.socket.on("close", () => {
      this.isConnected = false;
      this.rejectPendingRequests(new Error("Civil 3D plugin connection closed before the command completed."));
      log.debug("Connection closed");
    });

    this.socket.on("error", (error) => {
      log.error("Connection error", { error: String(error) });
      this.isConnected = false;
      this.rejectPendingRequests(new Error(`Civil 3D plugin connection failed: ${error.message}`));
    });
  }

  private rejectPendingRequests(error: Error): void {
    for (const pending of this.responseCallbacks.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.responseCallbacks.clear();
  }

  /**
   * The plugin sends one JSON object and no delimiter, so the answer is complete once the buffer parses. Each chunk
   * re-parses the whole buffer, which is fine for the sizes MAX_RESPONSE_BYTES allows.
   */
  private processBuffer(): void {
    try {
      JSON.parse(this.buffer);
      this.handleResponse(this.buffer);
      this.buffer = "";
    } catch {
      // Incomplete JSON — wait for more data
    }
  }

  /**
   * Starts connecting and returns at once: true means the attempt began, not that it succeeded. A refused
   * connection arrives later as the socket's "error" event, which rejects the pending request.
   */
  public connect(): boolean {
    if (this.isConnected) {
      return true;
    }

    try {
      this.socket.connect(this.port, this.host);
      return true;
    } catch (error) {
      log.error("Failed to connect", { host: this.host, port: this.port, error: String(error) });
      return false;
    }
  }

  public disconnect(): void {
    this.rejectPendingRequests(new Error("Civil 3D plugin connection was closed by the client."));
    this.socket.end();
    this.isConnected = false;
  }

  /**
   * Abandons the request: rejects it with CIVIL3D.CANCELLED (or the given error) and destroys the socket. The plugin
   * notices the client went away and cancels the request on its side.
   */
  public cancel(error = new Civil3DRpcError(
    "Civil 3D request was cancelled by the caller.",
    "CIVIL3D.CANCELLED",
    -32010,
  )): void {
    this.rejectPendingRequests(error);
    this.socket.destroy();
    this.isConnected = false;
  }

  private generateRequestId(): string {
    return createRpcRequestId();
  }

  private handleResponse(responseData: string): void {
    try {
      const response = JSON.parse(responseData);
      const requestId = response.id || "default";

      const pending = this.responseCallbacks.get(requestId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.responseCallbacks.delete(requestId);
        pending.resolve(responseData);
      }
    } catch (error) {
      log.error("Error parsing response", { error: String(error) });
    }
  }

  /**
   * Sends plugin method `command` (a CommandDispatcher.cs name such as "listAlignments") with `params` and resolves
   * with the JSON-RPC `result`. Rejects with Civil3DRpcError for an error answer, and with a plain Error when the
   * connection fails, the answer is not a valid JSON-RPC 2.0 envelope for this request id, or COMMAND_TIMEOUT_MS
   * passes. A timeout only rejects; it doesn't close the socket (the caller's disconnect does).
   */
  public sendCommand(command: string, params: any = {}): Promise<any> {
    return new Promise((resolve, reject) => {
      if (!this.isConnected && !this.connect()) {
        reject(new Error(`Failed to connect to Civil 3D plugin at ${this.host}:${this.port}.`));
        return;
      }

      const requestId = this.generateRequestId();

      const commandObj = {
        jsonrpc: "2.0",
        method: command,
        params,
        id: requestId,
      };

      const timeout = setTimeout(() => {
        const pending = this.responseCallbacks.get(requestId);
        if (pending) {
          this.responseCallbacks.delete(requestId);
          log.warn("Command timed out", { method: command, requestId, timeoutMs: COMMAND_TIMEOUT_MS });
          pending.reject(new Error(`Command timed out after ${COMMAND_TIMEOUT_MS}ms: ${command}`));
        }
      }, COMMAND_TIMEOUT_MS);

      this.responseCallbacks.set(requestId, {
        timeout,
        reject,
        resolve: (responseData) => {
          try {
            const response = JSON.parse(responseData);
            if (response.jsonrpc !== "2.0" || response.id !== requestId) {
              reject(new Error("Invalid JSON-RPC 2.0 response envelope from Civil 3D plugin."));
              return;
            }

            const hasResult = Object.prototype.hasOwnProperty.call(response, "result");
            const hasError = Object.prototype.hasOwnProperty.call(response, "error");
            if (hasResult === hasError) {
              reject(new Error("Invalid JSON-RPC 2.0 response: expected exactly one of result or error."));
              return;
            }

            if (response.error) {
              if (typeof response.error.code !== "number"
                || typeof response.error.message !== "string"
                || typeof response.error.data?.code !== "string") {
                reject(new Error("Invalid JSON-RPC 2.0 error response from Civil 3D plugin."));
                return;
              }
              const domainCode = typeof response.error.data?.code === "string"
                ? response.error.data.code
                : "CIVIL3D.UNKNOWN_ERROR";
              const rpcCode = typeof response.error.code === "number"
                ? response.error.code
                : -32000;
              reject(new Civil3DRpcError(
                response.error.message || "Unknown error from Civil 3D plugin",
                domainCode,
                rpcCode,
              ));
            } else {
              resolve(response.result);
            }
          } catch (error) {
            if (error instanceof Error) {
              reject(new Error(`Failed to parse response: ${error.message}`));
            } else {
              reject(new Error(`Failed to parse response: ${String(error)}`));
            }
          }
        },
      });

      try {
        const commandString = JSON.stringify(commandObj);
        log.debug("Sending command", { method: command, requestId });
        this.socket.write(commandString);
      } catch (error) {
        clearTimeout(timeout);
        this.responseCallbacks.delete(requestId);
        reject(error);
      }
    });
  }
}
