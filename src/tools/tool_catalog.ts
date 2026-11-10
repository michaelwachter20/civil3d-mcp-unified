// The flat tool catalog: every manifest exposure (GENERATED_TOOL_CATALOG_ENTRIES from toolManifest.ts) plus
// civil3d_help, which is registered outside the manifest. Read by the civil3d://catalog/tools resource
// (mcpResources.ts), civil3d_docs (docsDomain.ts) and the orchestrator's routes (orchestration/ToolCatalog.ts).
// Data only; nothing here calls Civil 3D.
import { GENERATED_TOOL_CATALOG_ENTRIES } from "./toolManifest.js";
import { HELP_TOOL_CATALOG_ENTRY } from "./helpMetadata.js";
import type { ToolCatalogEntry } from "./toolMetadata.js";

/** Every tool, canonical and alias, whether or not it is listed to MCP clients. */
export const TOOL_CATALOG: ToolCatalogEntry[] = [
  ...GENERATED_TOOL_CATALOG_ENTRIES,
  HELP_TOOL_CATALOG_ENTRY,
];

/** The whole catalog (civil3d_docs filters it). */
export function listToolCatalog() {
  return TOOL_CATALOG;
}

/** The distinct domain names in the catalog, sorted. */
export function listDomains() {
  return [...new Set(TOOL_CATALOG.map((entry) => entry.domain))].sort();
}
