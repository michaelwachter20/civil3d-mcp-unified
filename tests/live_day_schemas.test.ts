// Regression tests for input schemas that were wrong when first tried on a live Civil 3D: the tool's outer schema and
// each action's own schema must agree (surface and section domains). Pure schema tests; the real tool schemas are
// imported from src/.

import { describe, expect, it } from "vitest";
import { z } from "zod";
import { SURFACE_DOMAIN_DEFINITION } from "../src/tools/domains/surfaceDomain.js";
import { SECTION_DOMAIN_DEFINITION } from "../src/tools/domains/sectionDomain.js";

/** The tool's outer input shape and each action's own schema have to agree, or an action can never be called. */
function outerShape(definition: { exposures: Array<{ inputShape: z.ZodRawShape }> }) {
  return z.object(definition.exposures[0].inputShape);
}

describe("schemas found wrong on the live test day", () => {
  it("sample_elevations gets past the tool's outer schema with each of its methods", () => {
    for (const method of ["grid", "points", "transect"]) {
      expect(() => outerShape(SURFACE_DOMAIN_DEFINITION).parse({ action: "sample_elevations", name: "EG", method })).not.toThrow();
    }
  });

  it("volume methods still pass", () => {
    expect(() => outerShape(SURFACE_DOMAIN_DEFINITION).parse({ action: "compute_volume", method: "tin_volume" })).not.toThrow();
  });

  it("sample lines don't need a surface list: leaving it out samples every source", () => {
    const parsed = SECTION_DOMAIN_DEFINITION.actions.create_sample_lines.inputSchema.parse({
      action: "create_sample_lines", alignmentName: "Elm St", groupName: "SLG", interval: 50, leftWidth: 50, rightWidth: 50,
    }) as { surfaces: string[] };
    expect(parsed.surfaces).toEqual([]);
  });

  it("a section view can start left of the centerline", () => {
    expect(() => SECTION_DOMAIN_DEFINITION.actions.view_create.inputSchema.parse({
      action: "view_create", alignmentName: "Elm St", sampleLineGroupName: "SLG", insertionPoint: [0, 0], leftOffset: -20,
    })).not.toThrow();
  });
});
