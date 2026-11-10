// The catalog entry for civil3d_help, which helpTool.ts registers directly rather than through the manifest, so
// tool_catalog.ts adds it by hand. Keep `operations` in step with helpTool.ts's actions. The description is catalog
// text (civil3d_docs, the orchestrator); change it on purpose only.
import type { ToolCatalogEntry } from "./toolMetadata.js";

/** civil3d_help: read-only search of the installed Autodesk Offline Help and the tutorial video catalog. */
export const HELP_TOOL_CATALOG_ENTRY: ToolCatalogEntry = {
  toolName: "civil3d_help",
  displayName: "Civil 3D Offline Help",
  description: "Search installed Autodesk Civil 3D offline help and retrieve cited topics with renderable images.",
  domain: "help",
  capabilities: ["query"],
  operations: ["search", "search_videos", "get_topic", "status", "reindex"],
  requiresActiveDrawing: false,
  safeForRetry: true,
  status: "implemented",
};
