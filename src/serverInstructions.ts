/**
 * Instructions sent to MCP clients when they connect (InitializeResult.instructions).
 * Clients typically add these to the model's context, so they apply to every session.
 */
export const SERVER_INSTRUCTIONS = [
  "Civil 3D MCP drives a live Autodesk Civil 3D drawing.",
  "Before writing new C# for civil3d_execute or civil3d_query, check for an existing solution first: " +
    "civil3d_script action=list for ready-to-run saved scripts, and civil3d_skills action=search for proven, documented templates " +
    "(they include rules and Civil 3D pitfalls learned on real drawings). Adapt a matching one instead of starting from scratch.",
  "Prefer a dedicated civil3d_* tool when one fits; use code execution for everything else.",
  "Never move, explode or edit COGO points or other survey data unless the user explicitly asks; point labels can be moved independently.",
  "Run a read-only check or a dryRun before changing a drawing, and make each change a single undo step.",
  "Once a script is proven on a real drawing, save it with civil3d_script action=save so it can be re-run (users can type MCPRUN in Civil 3D).",
].join("\n");
