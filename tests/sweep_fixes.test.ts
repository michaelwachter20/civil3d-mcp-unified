// Regression tests for input-shape bugs found by a full sweep of every action in a live Civil 3D (geometry, survey,
// section, plan production domains and manifest action lookup). Imports the real definitions from src/; no Civil 3D.

import { describe, expect, it } from "vitest";
import { z } from "zod";
import { GEOMETRY_DOMAIN_DEFINITION } from "../src/tools/domains/geometryDomain.js";
import { PLAN_PRODUCTION_DOMAIN_DEFINITION } from "../src/tools/domains/planProductionDomain.js";
import { SECTION_DOMAIN_DEFINITION } from "../src/tools/domains/sectionDomain.js";
import { SURVEY_DOMAIN_DEFINITION } from "../src/tools/domains/surveyDomain.js";
import { findManifestAction, findManifestActionByCall } from "../src/tools/toolManifest.js";

/** What a sweep of every action in a live Civil 3D found, for the parts that can be checked without one. */
describe("the full sweep's fixes", () => {
  it("keeps the z of a 3D point through the geometry tool's input shape", () => {
    const tool = GEOMETRY_DOMAIN_DEFINITION.exposures[0];
    const input = { action: "create_3dpolyline", points: [{ x: 1, y: 2, z: 3 }, { x: 4, y: 5, z: 6 }] };
    const parsed = z.object(tool.inputShape).parse(input);
    expect(GEOMETRY_DOMAIN_DEFINITION.actions.create_3dpolyline.inputSchema.parse(parsed)).toMatchObject(input);
  });

  it("keeps a survey setup's shots through the tool's input shape", () => {
    const tool = SURVEY_DOMAIN_DEFINITION.exposures[0];
    const input = {
      action: "setup_add",
      databaseName: "Elm Street",
      networkName: "Control",
      stationPoint: 1,
      backsightPoint: 2,
      observations: [{ pointNumber: 3, angleDegrees: 90, distance: 100, description: "IP" }],
    };
    const parsed = z.object(tool.inputShape).parse(input);
    expect(SURVEY_DOMAIN_DEFINITION.actions.setup_add.inputSchema.parse(parsed)).toMatchObject(input);
  });

  it("lets a section's point limit through every tool that shows it", () => {
    const input = { action: "get_section_data", alignmentName: "Elm", sampleLineGroupName: "SL", station: 100, maxPoints: 5000 };
    for (const tool of SECTION_DOMAIN_DEFINITION.exposures.filter((exposure) => exposure.supportedActions.includes("get_section_data"))) {
      expect(z.object(tool.inputShape).partial().passthrough().parse(input)).toMatchObject({ maxPoints: 5000 });
      expect(Object.keys(tool.inputShape)).toContain("maxPoints");
    }
  });

  it("takes either a figure or a network to insert, not both", () => {
    const schema = SURVEY_DOMAIN_DEFINITION.actions.add_to_drawing.inputSchema;
    const base = { action: "add_to_drawing", databaseName: "Elm Street" };
    expect(schema.safeParse({ ...base, figureName: "EP1" }).success).toBe(true);
    expect(schema.safeParse(base).success).toBe(false);
    expect(schema.safeParse({ ...base, figureName: "EP1", networkName: "Control" }).success).toBe(false);
  });

  it("still takes 2D points for a 2D polyline", () => {
    const tool = GEOMETRY_DOMAIN_DEFINITION.exposures[0];
    const parsed = z.object(tool.inputShape).parse({ action: "create_polyline", points: [{ x: 1, y: 2 }, { x: 4, y: 5 }] });
    expect(GEOMETRY_DOMAIN_DEFINITION.actions.create_polyline.inputSchema.parse(parsed)).toBeTruthy();
  });

  it.each([
    ["civil3d_script", "run", "script_run", { action: "run", name: "a" }],
    ["civil3d_script", "delete", "script_delete", { action: "delete", name: "a" }],
    ["civil3d_preferences", "remove", "preferences_remove", { action: "remove", id: "pref-1" }],
    ["civil3d_memory", "remove", "memory_remove", { action: "remove", id: "lesson-1" }],
    ["civil3d_practices", "remove", "practices_remove", { action: "remove", id: "practice-1" }],
  ])("approves %s %s by the name the tool shows", (toolName, action, inside, parameters) => {
    expect(findManifestAction(toolName, action)).toBeUndefined();
    expect(findManifestActionByCall(toolName, action, parameters)?.actionDefinition.action).toBe(inside);
  });

  it("takes a paper size for a sheet set's PDF", () => {
    const input = { action: "sheet_set_export", sheetSetName: "a.dst", outputPath: "a.pdf", paperSize: "ANSI_D" };
    expect(PLAN_PRODUCTION_DOMAIN_DEFINITION.actions.sheet_set_export.inputSchema.parse(input)).toMatchObject(input);
  });

  it("lets sheet_add leave the layout out, since it defaults to the sheet's name", () => {
    const input = { action: "sheet_add", sheetSetName: "a.dst", sheetName: "Layout2" };
    expect(PLAN_PRODUCTION_DOMAIN_DEFINITION.actions.sheet_add.inputSchema.parse(input)).toMatchObject(input);
  });

  it("doesn't match an action to another one", () => {
    expect(findManifestActionByCall("civil3d_script", "nope", { action: "run", name: "a" })).toBeUndefined();
  });
});
