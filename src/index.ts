// Entry point of the Civil 3D MCP server (build/index.js, started by the AI client over stdio).
// Builds the McpServer with the instructions from serverInstructions.ts, sweeps temp files a killed write left in the
// knowledge folders, registers every tool (tools/register.ts), starts the local HTTP bridge (httpBridge.ts,
// 127.0.0.1:3000) and connects the stdio transport. Nothing here touches a drawing: tools reach Civil 3D only through
// the plugin over JSON-RPC (utils/ConnectionManager.ts). stdout belongs to the MCP protocol, so all logging goes to
// stderr (utils/logger.ts). SIGINT/SIGTERM close the bridge (at most 2 s) and the server, then exit.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { startHttpBridge } from "./httpBridge.js";
import { createLogger } from "./utils/logger.js";
import { APP_VERSION } from "./version.js";
import { buildServerInstructions } from "./serverInstructions.js";
import { sweepKnowledgeTemporaryFiles } from "./tools/knowledgeBase.js";

const log = createLogger("MCP");

const server = new McpServer(
  {
    name: "civil3d-mcp",
    version: APP_VERSION,
  },
  { instructions: buildServerInstructions() },
);

/** Starts the server: cleanup, tool registration, HTTP bridge, stdio transport, then installs the shutdown handlers. */
async function main() {
  // Temp files a crashed or killed write left in the knowledge folders (older than an hour).
  sweepKnowledgeTemporaryFiles();
  await registerTools(server);
  const httpServer = startHttpBridge();

  const transport = new StdioServerTransport();
  await server.connect(transport);
  log.info("Civil 3D MCP Server started");

  let shuttingDown = false;
  const shutdown = async (signal: string) => {
    if (shuttingDown) {
      return;
    }
    shuttingDown = true;
    log.info("Shutting down", { signal });

    try {
      await new Promise<void>((resolve) => {
        httpServer.close(() => resolve());
        // Stop accepting new connections but don't let lingering sockets block exit.
        setTimeout(() => resolve(), 2000).unref();
      });
    } catch (error) {
      log.warn("HTTP bridge shutdown failed", { error: String(error) });
    }

    try {
      await server.close();
    } catch (error) {
      log.warn("MCP server close failed", { error: String(error) });
    }

    process.exit(0);
  };

  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));
}

main().catch((error) => {
  log.error("Error starting Civil 3D MCP Server", { error: String(error) });
  process.exit(1);
});
