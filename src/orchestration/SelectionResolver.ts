/**
 * Fills route parameters the user didn't name from what is selected in the drawing, for civil3d_orchestrate.
 * Used only by src/tools/civil3d_orchestrate.ts, after ProjectContextService has read the selection from the plugin.
 * Pure function over that snapshot: it calls nothing and changes nothing. It only fills a field that is empty, and
 * only when exactly one selected object matches the type, so an ambiguous selection never picks a name.
 */
import { RouteParams } from "./IntentRouter.js";
import { ProjectContext } from "./ProjectContextService.js";

/** The fields of one selected object (as reported by the plugin's getProjectContext) that this file reads. */
interface SelectedObjectInfo {
  objectType?: string;
  name?: string;
}

/** The parameters after filling, and the names of the fields that came from the selection (shown in the answer). */
export interface SelectionResolution {
  resolvedParams: RouteParams;
  inferredFromSelection: string[];
}

/** The selection list from the context, or an empty list when the plugin sent none. */
function asSelectedObjects(projectContext: ProjectContext): SelectedObjectInfo[] {
  return Array.isArray(projectContext.selectedObjects)
    ? (projectContext.selectedObjects as SelectedObjectInfo[])
    : [];
}

/**
 * The name of the one selected object whose type contains `objectTypeMatch` (case-insensitive substring, so
 * "surface" matches "TinSurface"). Undefined when none or more than one match, or the name is blank.
 */
function findSelectedNameByType(projectContext: ProjectContext, objectTypeMatch: string): string | undefined {
  const matches = asSelectedObjects(projectContext).filter((item) => {
    const objectType = item.objectType?.toLowerCase() ?? "";
    return objectType.includes(objectTypeMatch);
  });

  if (matches.length !== 1) {
    return undefined;
  }

  const selectedName = matches[0]?.name?.trim();
  return selectedName && selectedName.length > 0 ? selectedName : undefined;
}

/** The first type in `objectTypeMatches` (in order) that has exactly one selected object, and that object's name. */
function findFirstSelectedName(projectContext: ProjectContext, objectTypeMatches: string[]): string | undefined {
  for (const objectTypeMatch of objectTypeMatches) {
    const selectedName = findSelectedNameByType(projectContext, objectTypeMatch);
    if (selectedName) {
      return selectedName;
    }
  }

  return undefined;
}

/**
 * Copies `params` and fills `alignmentName`, `surfaceName` and the generic `name` (surface, then alignment, then
 * corridor) from a single matching selected object when they are empty. Values the user gave are never replaced.
 */
export function resolveParamsFromSelection(params: RouteParams, projectContext: ProjectContext): SelectionResolution {
  const resolvedParams: RouteParams = { ...params };
  const inferredFromSelection: string[] = [];

  if (!resolvedParams.alignmentName) {
    const selectedAlignmentName = findSelectedNameByType(projectContext, "alignment");
    if (selectedAlignmentName) {
      resolvedParams.alignmentName = selectedAlignmentName;
      inferredFromSelection.push("alignmentName");
    }
  }

  if (!resolvedParams.surfaceName) {
    const selectedSurfaceName = findSelectedNameByType(projectContext, "surface");
    if (selectedSurfaceName) {
      resolvedParams.surfaceName = selectedSurfaceName;
      inferredFromSelection.push("surfaceName");
    }
  }

  if (!resolvedParams.name) {
    const selectedObjectName = findFirstSelectedName(projectContext, ["surface", "alignment", "corridor"]);
    if (selectedObjectName) {
      resolvedParams.name = selectedObjectName;
      inferredFromSelection.push("name");
    }
  }

  return {
    resolvedParams,
    inferredFromSelection,
  };
}
