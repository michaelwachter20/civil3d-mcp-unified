/**
 * Reads the drawing context civil3d_orchestrate plans against, in one plugin round trip.
 * Used by src/tools/civil3d_orchestrate.ts (every orchestrate call, planning included, so it needs Civil 3D with the
 * AI connection on). Calls the plugin method `getProjectContext` (DrawingCommands.GetProjectContextAsync), a
 * read-only request that adds no undo step. The result feeds SelectionResolver and WorkflowPlanner.
 * Note: "project" here means the open drawing, not the suite's job database (that is civil3d_project_notes).
 */
import { withApplicationConnection } from "../utils/ConnectionManager.js";

/** Drawing info (object counts etc.), the Civil 3D object types, and the user's current selection. */
export interface ProjectContext {
  drawingInfo: unknown | null;
  objectTypes: string[];
  selectedObjects: unknown[];
}

/**
 * Asks the plugin for the active drawing's info, object types and up to `selectedObjectLimit` selected objects
 * (the plugin clamps it to 1-100). Missing or malformed parts become null or empty lists. Throws when the plugin
 * can't be reached.
 */
export async function getProjectContext(selectedObjectLimit = 25): Promise<ProjectContext> {
  const context = await withApplicationConnection(async (appClient) =>
    appClient.sendCommand("getProjectContext", { selectedObjectLimit }),
  ) as Partial<ProjectContext>;

  return {
    drawingInfo: context.drawingInfo ?? null,
    objectTypes: Array.isArray(context.objectTypes) ? context.objectTypes : [],
    selectedObjects: Array.isArray(context.selectedObjects) ? context.selectedObjects : [],
  };
}
