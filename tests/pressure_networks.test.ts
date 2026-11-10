// Guards the input shapes (zod) of the pressure-network tool actions (networks, pipes, fittings, cover, connect).
// The input schemas are the REAL ones from src/tools/domains/pipeDomain.ts, reached through each alias tool as a call
// is validated (tests/helpers/realSchemas.ts). The pressure-network answers are checked by a passthrough schema in src
// (any object), so there is no response shape to test here; set_cover, validate and connect are unsupported, and their
// input schemas are still checked because a call is validated before it is refused.
// Pure schema tests: no Civil 3D, no plugin.

import { describe, it, expect } from "vitest";
import { toolInput } from "./helpers/realSchemas.js";

const PressureNetworkCreateInputSchema = toolInput("civil3d_pressure_network_create");
const PressurePipeAddInputSchema = toolInput("civil3d_pressure_pipe_add");
const PressurePipeResizeInputSchema = toolInput("civil3d_pressure_pipe_resize");
const PressureNetworkSetCoverInputSchema = toolInput("civil3d_pressure_network_set_cover");
const PressureNetworkConnectInputSchema = toolInput("civil3d_pressure_network_connect");
const PressureFittingAddInputSchema = toolInput("civil3d_pressure_fitting_add");
const PressureAppurtenanceAddInputSchema = toolInput("civil3d_pressure_appurtenance_add");

// ---------------------------------------------------------------------------
// Tool input schemas
// ---------------------------------------------------------------------------

describe("PressureNetworkCreateInputSchema", () => {
  it("requires name and partsList", () => {
    expect(PressureNetworkCreateInputSchema.safeParse({ name: "WM", partsList: "Std" }).success).toBe(true);
    expect(PressureNetworkCreateInputSchema.safeParse({ name: "WM" }).success).toBe(false);
    expect(PressureNetworkCreateInputSchema.safeParse({ partsList: "Std" }).success).toBe(false);
  });

  it("accepts optional layer and reference fields", () => {
    const result = PressureNetworkCreateInputSchema.safeParse({
      name: "WM",
      partsList: "Std",
      layer: "WATER",
      referenceAlignment: "AL-1",
      referenceSurface: "EG",
    });
    expect(result.success).toBe(true);
    expect(result.data!.layer).toBe("WATER");
  });
});

describe("PressurePipeAddInputSchema", () => {
  it("requires networkName, partName, startPoint, endPoint", () => {
    const valid = PressurePipeAddInputSchema.safeParse({
      networkName: "WM",
      partName: "8in PVC",
      startPoint: { x: 0, y: 0, z: 100 },
      endPoint: { x: 100, y: 0, z: 100 },
    });
    expect(valid.success).toBe(true);
  });

  it("rejects missing endPoint", () => {
    const result = PressurePipeAddInputSchema.safeParse({
      networkName: "WM",
      partName: "8in PVC",
      startPoint: { x: 0, y: 0, z: 100 },
    });
    expect(result.success).toBe(false);
  });
});

describe("PressureNetworkSetCoverInputSchema", () => {
  it("accepts minCoverDepth with optional max", () => {
    expect(PressureNetworkSetCoverInputSchema.safeParse({ networkName: "WM", minCoverDepth: 3.0 }).success).toBe(true);
    expect(PressureNetworkSetCoverInputSchema.safeParse({ networkName: "WM", minCoverDepth: 3.0, maxCoverDepth: 10.0 }).success).toBe(true);
  });

  it("rejects missing minCoverDepth", () => {
    expect(PressureNetworkSetCoverInputSchema.safeParse({ networkName: "WM" }).success).toBe(false);
  });
});

describe("PressureNetworkConnectInputSchema", () => {
  it("requires both targetNetwork and sourceNetwork", () => {
    expect(PressureNetworkConnectInputSchema.safeParse({ targetNetwork: "WM-1", sourceNetwork: "WM-2" }).success).toBe(true);
    expect(PressureNetworkConnectInputSchema.safeParse({ targetNetwork: "WM-1" }).success).toBe(false);
  });
});

describe("PressurePipeResizeInputSchema", () => {
  it("requires networkName, pipeName, newPartName", () => {
    expect(PressurePipeResizeInputSchema.safeParse({ networkName: "WM", pipeName: "PP-1", newPartName: "12in DI" }).success).toBe(true);
    expect(PressurePipeResizeInputSchema.safeParse({ networkName: "WM", pipeName: "PP-1" }).success).toBe(false);
  });
});

describe("PressureFittingAddInputSchema", () => {
  it("requires networkName, partName, position", () => {
    expect(PressureFittingAddInputSchema.safeParse({ networkName: "WM", partName: "Elbow 90", position: { x: 100, y: 0, z: 98 } }).success).toBe(true);
    expect(PressureFittingAddInputSchema.safeParse({ networkName: "WM", partName: "Elbow 90" }).success).toBe(false);
  });
});

describe("PressureAppurtenanceAddInputSchema", () => {
  it("accepts optional onPipeName", () => {
    const result = PressureAppurtenanceAddInputSchema.safeParse({
      networkName: "WM",
      partName: "Gate Valve",
      position: { x: 50, y: 0, z: 99 },
      onPipeName: "PP-1",
    });
    expect(result.success).toBe(true);
    expect(result.data!.onPipeName).toBe("PP-1");
  });

  it("works without onPipeName", () => {
    expect(PressureAppurtenanceAddInputSchema.safeParse({ networkName: "WM", partName: "Hydrant", position: { x: 0, y: 0, z: 100 } }).success).toBe(true);
  });
});

