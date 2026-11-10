// Version numbers reported by the server: the app version from package.json (the single source of versions, see
// CLAUDE.md rule 7 and scripts/sync-version.mjs) and the installed MCP SDK version. Used by index.ts (server info),
// the health tool and the HTTP bridge's /health. Reads the files once at import time.
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/** The `version` field of a package.json, or "unknown". */
function readVersion(path: string): string {
  const value = JSON.parse(readFileSync(path, "utf8")) as { version?: string };
  return value.version ?? "unknown";
}

const require = createRequire(import.meta.url);
const packageJsonPath = new URL("../package.json", import.meta.url);
// The SDK's package.json is not exported, so it is found three folders above the resolved server entry
// (dist/<format>/server/mcp.js -> package root).
const sdkEntry = require.resolve("@modelcontextprotocol/sdk/server/mcp.js");

export const APP_VERSION = readVersion(fileURLToPath(packageJsonPath));
export const MCP_SDK_VERSION = readVersion(resolve(dirname(sdkEntry), "../../../package.json"));

/** Versions of this server, the MCP SDK and Node.js, for health answers. */
export function dependencyVersions() {
  return {
    application: APP_VERSION,
    mcpSdk: MCP_SDK_VERSION,
    node: process.version,
  };
}
