// Types shared by the tool catalog: domain names, capabilities and the flat catalog entry. A new domain adds its
// name to ToolDomain (CLAUDE.md, "Adding a tool"). Capabilities are not just labels: approvalPolicy.ts derives whether
// an action needs approval from them, and domainRuntime.ts derives the MCP annotations (readOnlyHint,
// destructiveHint), so declare them truthfully (CLAUDE.md rule 4). Used by domainRuntime.ts, toolManifest.ts,
// tool_catalog.ts, helpMetadata.ts and the orchestration layer. Types only, no runtime code.

/** Groups tools in the catalog, civil3d_docs and the orchestrator. Some names are reserved and have no domain file. */
export type ToolDomain =
  | "drawing"
  | "geometry"
  | "workflow"
  | "grading"
  | "point"
  | "surface"
  | "alignment"
  | "profile"
  | "corridor"
  | "section"
  | "pipe"
  | "parcel"
  | "quantity_takeoff"
  | "standards"
  | "qc"
  | "labeling"
  | "coordinate_system"
  | "data_shortcut"
  | "assembly"
  | "project"
  | "feature_line"
  | "style"
  | "job"
  | "hydrology"
  | "sight_distance"
  | "detention"
  | "slope_analysis"
  | "cost_estimation"
  | "survey"
  | "plan_production"
  | "docs"
  | "help"
  | "catchment"
  | "stm"
  | "plugin"
  | "scripting";

/**
 * What an action does. For approval (approvalPolicy.ts), "create", "edit", "delete", "manage", "import" and "export"
 * mark a mutation; "query", "analyze", "inspect", "generate" and "register" do not. "delete", "import" and "export"
 * always need approval; the other mutations only when the action isn't safeForRetry.
 */
export type ToolCapability =
  | "query"
  | "create"
  | "edit"
  | "delete"
  | "analyze"
  | "manage"
  | "generate"
  | "register"
  | "inspect"
  | "export"
  | "import";

/** One tool in the flat catalog (one per exposure, built by toolManifest.ts); `operations` are its action names. */
export interface ToolCatalogEntry {
  toolName: string;
  displayName: string;
  description: string;
  domain: ToolDomain;
  capabilities: ToolCapability[];
  operations?: string[];
  pluginMethods?: string[];
  requiresActiveDrawing: boolean;
  safeForRetry: boolean;
  status: "implemented" | "planned";
}
