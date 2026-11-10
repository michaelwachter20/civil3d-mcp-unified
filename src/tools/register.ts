// Registration order for everything the MCP server offers, called once by src/index.ts at startup: the manifest
// domains (toolManifest.ts, which also captures every handler for the HTTP bridge), the approval tools
// (approvalTool.ts), civil3d_help (helpTool.ts), and the MCP resources (mcpResources.ts, plus the help resources).
// Registering touches no drawing and needs no Civil 3D.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerApprovalTool } from "./approvalTool.js";
import { registerManifestTools } from "./toolManifest.js";
import { registerMcpResources } from "./mcpResources.js";
import { registerHelpResources, registerHelpTool } from "./helpTool.js";

/** Registers every tool and resource on `server`. */
export async function registerTools(server: McpServer) {
  // Manifest-driven domains: every entry of MIGRATED_DOMAIN_DEFINITIONS (alignment, surface, ..., workflow, plugin,
  // docs, scripting). Add new migrated domains to toolManifest.ts, not here.
  registerManifestTools(server);
  registerApprovalTool(server);
  registerHelpTool(server);
  registerMcpResources(server);
  registerHelpResources(server);
}
