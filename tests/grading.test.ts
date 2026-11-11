// Guards the input shapes (zod) of the grading-group, grading and feature-line actions, using the REAL schemas from
// src/tools/domains/gradingDomain.ts (tests/helpers/realSchemas.ts). The group and grading actions are unsupported and
// in no tool, so their action schemas are checked directly. Pure schema tests: no Civil 3D, no plugin.

import { describe, it, expect } from "vitest";
import { GRADING_DOMAIN_DEFINITION } from "../src/tools/domains/gradingDomain.js";
import { actionInput, toolActionInput, toolInput, toolShape } from "./helpers/realSchemas.js";

const GradingGroupGetSchema = actionInput(GRADING_DOMAIN_DEFINITION, "group_get");
const GradingGroupCreateSchema = actionInput(GRADING_DOMAIN_DEFINITION, "group_create");
const GradingGroupVolumeSchema = actionInput(GRADING_DOMAIN_DEFINITION, "group_volume");
const GradingGroupSurfaceCreateSchema = actionInput(GRADING_DOMAIN_DEFINITION, "group_surface_create");
const GradingListSchema = actionInput(GRADING_DOMAIN_DEFINITION, "list");
const GradingCreateSchema = actionInput(GRADING_DOMAIN_DEFINITION, "create");
const GradingDeleteSchema = actionInput(GRADING_DOMAIN_DEFINITION, "delete");
const FeatureLineGetSchema = toolActionInput("civil3d_feature_line", "get");
const FeatureLineExportSchema = toolActionInput("civil3d_feature_line", "export_as_polyline");
const FeatureLineCreateSchema = toolInput("civil3d_feature_line_create");

// ─── civil3d_grading_group_create ─────────────────────────────────────────

describe("civil3d_grading_group_create input schema", () => {
  it("accepts minimal valid args", () => {
    expect(GradingGroupCreateSchema.safeParse({ name: "GG-1" }).success).toBe(true);
  });

  it("accepts all optional fields", () => {
    expect(GradingGroupCreateSchema.safeParse({
      name: "GG-1",
      description: "Site grading group",
      useProjection: true,
    }).success).toBe(true);
  });

  it("rejects missing name", () => {
    expect(GradingGroupCreateSchema.safeParse({ description: "test" }).success).toBe(false);
  });
});

// ─── civil3d_grading_group_get ────────────────────────────────────────────

describe("civil3d_grading_group_get input schema", () => {
  it("accepts valid name", () => {
    expect(GradingGroupGetSchema.safeParse({ name: "GG-1" }).success).toBe(true);
  });

  it("rejects empty object", () => {
    expect(GradingGroupGetSchema.safeParse({}).success).toBe(false);
  });
});

// ─── civil3d_grading_group_volume ─────────────────────────────────────────

describe("civil3d_grading_group_volume input schema", () => {
  it("accepts valid name", () => {
    expect(GradingGroupVolumeSchema.safeParse({ name: "GG-1" }).success).toBe(true);
  });
});

// ─── civil3d_grading_group_surface_create ─────────────────────────────────

describe("civil3d_grading_group_surface_create input schema", () => {
  it("accepts minimal args", () => {
    expect(GradingGroupSurfaceCreateSchema.safeParse({ name: "GG-1" }).success).toBe(true);
  });

  it("accepts optional surfaceName", () => {
    expect(GradingGroupSurfaceCreateSchema.safeParse({
      name: "GG-1",
      surfaceName: "Graded Surface",
    }).success).toBe(true);
  });
});

// ─── civil3d_grading_list ─────────────────────────────────────────────────

describe("civil3d_grading_list input schema", () => {
  it("accepts groupName", () => {
    expect(GradingListSchema.safeParse({ groupName: "GG-1" }).success).toBe(true);
  });

  it("rejects empty object", () => {
    expect(GradingListSchema.safeParse({}).success).toBe(false);
  });
});

// ─── civil3d_grading_create ───────────────────────────────────────────────

describe("civil3d_grading_create input schema", () => {
  it("accepts minimal args", () => {
    expect(GradingCreateSchema.safeParse({
      groupName: "GG-1",
      featureLineName: "FL-PAD",
    }).success).toBe(true);
  });

  it("accepts all optional fields", () => {
    expect(GradingCreateSchema.safeParse({
      groupName: "GG-1",
      featureLineName: "FL-PAD",
      criteriaName: "Grade to Surface",
      side: "left",
    }).success).toBe(true);
  });

  it("rejects invalid side", () => {
    expect(GradingCreateSchema.safeParse({
      groupName: "GG-1",
      featureLineName: "FL-PAD",
      side: "uphill",
    }).success).toBe(false);
  });
});

// ─── civil3d_grading_delete ───────────────────────────────────────────────

describe("civil3d_grading_delete input schema", () => {
  it("requires both groupName and handle", () => {
    expect(GradingDeleteSchema.safeParse({ groupName: "GG-1", handle: "1A3F" }).success).toBe(true);
    expect(GradingDeleteSchema.safeParse({ groupName: "GG-1" }).success).toBe(false);
    expect(GradingDeleteSchema.safeParse({ handle: "1A3F" }).success).toBe(false);
  });
});

// ─── civil3d_feature_line_create ──────────────────────────────────────────

describe("civil3d_feature_line_create input schema", () => {
  it("accepts two valid 3D points", () => {
    expect(FeatureLineCreateSchema.safeParse({
      points: [
        { x: 100, y: 200, z: 10 },
        { x: 200, y: 300, z: 12 },
      ],
    }).success).toBe(true);
  });

  it("rejects fewer than 2 points", () => {
    expect(FeatureLineCreateSchema.safeParse({
      points: [{ x: 100, y: 200, z: 10 }],
    }).success).toBe(false);
  });

  // The plugin refuses a point without z ("elevation 0 will not be assumed"); a default here sent 0 for the caller.
  it("refuses a point without z instead of assuming elevation 0", () => {
    expect(FeatureLineCreateSchema.safeParse({
      points: [
        { x: 100, y: 200 },
        { x: 300, y: 400 },
      ],
    }).success).toBe(false);
    expect(FeatureLineCreateSchema.safeParse({
      points: [
        { x: 100, y: 200, z: 10 },
        { x: 300, y: 400 },
      ],
    }).success).toBe(false);
    for (const tool of ["civil3d_grading", "civil3d_feature_line_create"]) {
      expect(toolShape(tool).safeParse({ action: "feature_line_create", points: [{ x: 1, y: 2 }, { x: 3, y: 4 }] }).success).toBe(false);
      expect(toolShape(tool).safeParse({ action: "feature_line_create", points: [{ x: 1, y: 2, z: 0 }, { x: 3, y: 4, z: 0 }] }).success).toBe(true);
    }
  });

  it("accepts optional name and layer", () => {
    expect(FeatureLineCreateSchema.safeParse({
      points: [{ x: 0, y: 0, z: 0 }, { x: 10, y: 10, z: 5 }],
      name: "PAD-EDGE",
      layer: "GRADING",
    }).success).toBe(true);
  });
});

// ─── civil3d_feature_line get/export aliases ──────────────────────────────

describe("civil3d_feature_line get/export input schemas", () => {
  it("requires a name to get a feature line", () => {
    expect(FeatureLineGetSchema.safeParse({ name: "PAD-EDGE" }).success).toBe(true);
    expect(FeatureLineGetSchema.safeParse({}).success).toBe(false);
  });

  it("accepts export args with optional targetLayer", () => {
    expect(FeatureLineExportSchema.safeParse({ name: "PAD-EDGE" }).success).toBe(true);
    expect(FeatureLineExportSchema.safeParse({
      name: "PAD-EDGE",
      targetLayer: "C-TOPO-FL",
    }).success).toBe(true);
  });
});
