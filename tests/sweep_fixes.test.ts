import { describe, expect, it } from "vitest";
import { z } from "zod";
import { GEOMETRY_DOMAIN_DEFINITION } from "../src/tools/domains/geometryDomain.js";
import { findManifestAction, findManifestActionByCall } from "../src/tools/toolManifest.js";

/** What a sweep of every action in a live Civil 3D found, for the parts that can be checked without one. */
describe("the full sweep's fixes", () => {
  it("keeps the z of a 3D point through the geometry tool's input shape", () => {
    const tool = GEOMETRY_DOMAIN_DEFINITION.exposures[0];
    const input = { action: "create_3dpolyline", points: [{ x: 1, y: 2, z: 3 }, { x: 4, y: 5, z: 6 }] };
    const parsed = z.object(tool.inputShape).parse(input);
    expect(GEOMETRY_DOMAIN_DEFINITION.actions.create_3dpolyline.inputSchema.parse(parsed)).toMatchObject(input);
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

  it("doesn't match an action to another one", () => {
    expect(findManifestActionByCall("civil3d_script", "nope", { action: "run", name: "a" })).toBeUndefined();
  });
});
