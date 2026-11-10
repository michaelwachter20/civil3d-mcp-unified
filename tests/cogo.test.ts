// Guards the intended input shapes (zod) for point groups, point export/transform, COGO inverse/direction/traverse/
// curve solving and survey database/figure actions.
// The schemas are the REAL ones from src/tools/domains/ (pointDomain, geometryDomain, surveyDomain), reached through
// each alias tool the way a call is validated (tests/helpers/realSchemas.ts); survey database_create has no alias, so
// it goes through civil3d_survey. Pure schema tests: no Civil 3D, no plugin, no files.

import { describe, it, expect } from "vitest";
import { toolActionInput, toolInput } from "./helpers/realSchemas.js";

const PointGroupCreateSchema = toolInput("civil3d_point_group_create");
const PointGroupUpdateSchema = toolInput("civil3d_point_group_update");
const PointExportSchema = toolInput("civil3d_point_export");
const PointTransformSchema = toolInput("civil3d_point_transform");
const CogoInverseSchema = toolInput("civil3d_cogo_inverse");
const CogoDirectionDistanceSchema = toolInput("civil3d_cogo_direction_distance");
const CogoTraverseSchema = toolInput("civil3d_cogo_traverse");
const CogoCurveSolveSchema = toolInput("civil3d_cogo_curve_solve");
const SurveyDatabaseCreateSchema = toolActionInput("civil3d_survey", "database_create");
const SurveyFigureListSchema = toolInput("civil3d_survey_figure_list");
const SurveyFigureGetSchema = toolInput("civil3d_survey_figure_get");

// ─── Point group tests ────────────────────────────────────────────────────────

describe("civil3d_point_group_create input schema", () => {
  it("accepts minimal args (name only)", () => {
    expect(PointGroupCreateSchema.safeParse({ name: "Topo Points" }).success).toBe(true);
  });

  it("accepts all optional filter fields", () => {
    expect(PointGroupCreateSchema.safeParse({
      name: "Spot Shots",
      description: "Field topo shots",
      includeNumbers: "1-500",
      excludeNumbers: "101-110",
      includeDescriptions: "TOPO*",
    }).success).toBe(true);
  });

  it("rejects empty object", () => {
    expect(PointGroupCreateSchema.safeParse({}).success).toBe(false);
  });
});

describe("civil3d_point_group_update input schema", () => {
  it("accepts name only (all fields optional except name)", () => {
    expect(PointGroupUpdateSchema.safeParse({ name: "Topo Points" }).success).toBe(true);
  });

  it("accepts all updatable fields", () => {
    expect(PointGroupUpdateSchema.safeParse({
      name: "Topo Points",
      includeNumbers: "1-600",
      description: "Updated",
    }).success).toBe(true);
  });
});

describe("civil3d_point_export input schema", () => {
  it("accepts empty args (export all with defaults)", () => {
    expect(PointExportSchema.safeParse({}).success).toBe(true);
  });

  it("accepts all format options", () => {
    for (const format of ["pnezd", "penz", "xyzd", "xyz", "csv"]) {
      expect(PointExportSchema.safeParse({ format }).success).toBe(true);
    }
  });

  it("accepts groupName filter", () => {
    expect(PointExportSchema.safeParse({ groupName: "Topo Points", format: "xyzd" }).success).toBe(true);
  });

  it("accepts pointNumbers filter", () => {
    expect(PointExportSchema.safeParse({ pointNumbers: [1, 2, 3, 4, 5] }).success).toBe(true);
  });
});

describe("civil3d_point_transform input schema", () => {
  it("accepts empty args (no-op transform)", () => {
    expect(PointTransformSchema.safeParse({}).success).toBe(true);
  });

  it("accepts translation only", () => {
    expect(PointTransformSchema.safeParse({
      translateX: 100,
      translateY: 200,
    }).success).toBe(true);
  });

  it("accepts full transform", () => {
    expect(PointTransformSchema.safeParse({
      pointNumbers: [1, 2, 3],
      translateX: 5000,
      translateY: 10000,
      translateZ: 0,
      rotateRadians: 0.0174533,
      scaleFactor: 1.0,
      baseX: 1000,
      baseY: 2000,
    }).success).toBe(true);
  });

  it("needs a base point to scale or rotate about", () => {
    expect(PointTransformSchema.safeParse({ pointNumbers: [1], rotateRadians: 0.1 }).success).toBe(false);
    expect(PointTransformSchema.safeParse({ pointNumbers: [1], scaleFactor: 2 }).success).toBe(false);
    expect(PointTransformSchema.safeParse({ pointNumbers: [1], scaleFactor: 1, rotateRadians: 0 }).success).toBe(true);
  });

  it("refuses point numbers that aren't positive integers", () => {
    expect(PointTransformSchema.safeParse({ pointNumbers: [0] }).success).toBe(false);
    expect(PointTransformSchema.safeParse({ pointNumbers: [1.5] }).success).toBe(false);
  });
});

// ─── COGO inverse tests ───────────────────────────────────────────────────────

describe("civil3d_cogo_inverse input schema", () => {
  it("accepts two coordinate pairs", () => {
    expect(CogoInverseSchema.safeParse({
      x1: 1000, y1: 2000, x2: 1300, y2: 2400,
    }).success).toBe(true);
  });

  it("rejects missing coordinates", () => {
    expect(CogoInverseSchema.safeParse({ x1: 1000, y1: 2000, x2: 1300 }).success).toBe(false);
  });
});

// ─── COGO direction-distance tests ───────────────────────────────────────────

describe("civil3d_cogo_direction_distance input schema", () => {
  it("accepts minimal args", () => {
    expect(CogoDirectionDistanceSchema.safeParse({
      fromX: 1000,
      fromY: 2000,
      bearingDegrees: 45.0,
      distance: 100,
    }).success).toBe(true);
  });

  it("accepts optional fromZ and slope", () => {
    expect(CogoDirectionDistanceSchema.safeParse({
      fromX: 0,
      fromY: 0,
      fromZ: 100,
      bearingDegrees: 90,
      distance: 200,
      slope: 5.0,
    }).success).toBe(true);
  });

  it("rejects non-positive distance", () => {
    expect(CogoDirectionDistanceSchema.safeParse({
      fromX: 0, fromY: 0, bearingDegrees: 0, distance: 0,
    }).success).toBe(false);
  });
});

// ─── COGO traverse tests ──────────────────────────────────────────────────────

describe("civil3d_cogo_traverse input schema", () => {
  it("accepts single course", () => {
    expect(CogoTraverseSchema.safeParse({
      startX: 0,
      startY: 0,
      courses: [{ bearingDegrees: 45, distance: 100 }],
    }).success).toBe(true);
  });

  it("accepts closed traverse with multiple courses", () => {
    expect(CogoTraverseSchema.safeParse({
      startX: 1000,
      startY: 2000,
      startZ: 500,
      courses: [
        { bearingDegrees: 0, distance: 100, description: "N" },
        { bearingDegrees: 90, distance: 100, description: "E" },
        { bearingDegrees: 180, distance: 100, description: "S" },
        { bearingDegrees: 270, distance: 100, description: "W" },
      ],
      isClosed: true,
    }).success).toBe(true);
  });

  it("rejects empty courses array", () => {
    expect(CogoTraverseSchema.safeParse({
      startX: 0, startY: 0, courses: [],
    }).success).toBe(false);
  });
});

// ─── COGO curve solve tests ───────────────────────────────────────────────────

describe("civil3d_cogo_curve_solve input schema", () => {
  it("accepts radius + delta", () => {
    expect(CogoCurveSolveSchema.safeParse({ radius: 200, deltaDegrees: 30 }).success).toBe(true);
  });

  it("accepts radius + length", () => {
    expect(CogoCurveSolveSchema.safeParse({ radius: 500, length: 100 }).success).toBe(true);
  });

  it("accepts tangent + chord (any pair)", () => {
    expect(CogoCurveSolveSchema.safeParse({ tangent: 53.59, chord: 103.53 }).success).toBe(true);
  });

  it("rejects more or fewer than two elements", () => {
    expect(CogoCurveSolveSchema.safeParse({
      radius: 200,
      deltaDegrees: 30,
      length: 104.72,
      tangent: 53.59,
      chord: 103.53,
    }).success).toBe(false);
    expect(CogoCurveSolveSchema.safeParse({ radius: 200 }).success).toBe(false);
    expect(CogoCurveSolveSchema.safeParse({}).success).toBe(false);
  });
});

// ─── Survey database / figure tests ──────────────────────────────────────────

describe("civil3d_survey_database_create input schema", () => {
  it("accepts name only", () => {
    expect(SurveyDatabaseCreateSchema.safeParse({ name: "Survey DB 1" }).success).toBe(true);
  });

  it("accepts name and path", () => {
    expect(SurveyDatabaseCreateSchema.safeParse({
      name: "Survey DB 1",
      path: "C:/Projects/survey.sdb",
    }).success).toBe(true);
  });

  it("rejects empty object", () => {
    expect(SurveyDatabaseCreateSchema.safeParse({}).success).toBe(false);
  });
});

describe("civil3d_survey_figure_list input schema", () => {
  it("accepts empty args (list all databases)", () => {
    expect(SurveyFigureListSchema.safeParse({}).success).toBe(true);
  });

  it("accepts databaseName filter", () => {
    expect(SurveyFigureListSchema.safeParse({ databaseName: "Survey DB 1" }).success).toBe(true);
  });
});

describe("civil3d_survey_figure_get input schema", () => {
  it("accepts name only", () => {
    expect(SurveyFigureGetSchema.safeParse({ name: "TOE-SLOPE" }).success).toBe(true);
  });

  it("accepts name and databaseName", () => {
    expect(SurveyFigureGetSchema.safeParse({
      name: "TOE-SLOPE",
      databaseName: "Survey DB 1",
    }).success).toBe(true);
  });

  it("rejects empty object", () => {
    expect(SurveyFigureGetSchema.safeParse({}).success).toBe(false);
  });
});
