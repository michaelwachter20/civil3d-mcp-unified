// Guards how one CALL is judged for approval (src/tools/domainRuntime.ts approvalTargetFor, approvalPolicy.ts): a
// declared preview (dryRun, applyChanges off, no outputPath) needs no token, the same action that writes does; an
// unsupported action is refused with CIVIL3D.API_ERROR before approval and never reaches the plugin; approval errors
// carry their own codes. Also the arguments a few fixed actions now send. ConnectionManager is mocked; no Civil 3D.

import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({ sendCommandMock: vi.fn() }));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>) =>
    await operation({ sendCommand: sendCommandMock }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { approvalTargetFor, captureDomainToolHandlers, type DomainToolDefinition } from "../src/tools/domainRuntime.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";
import { ASSEMBLY_DOMAIN_DEFINITION } from "../src/tools/domains/assemblyDomain.js";
import { COST_ESTIMATION_DOMAIN_DEFINITION } from "../src/tools/domains/costEstimationDomain.js";
import { HYDROLOGY_DOMAIN_DEFINITION } from "../src/tools/domains/hydrologyDomain.js";
import { PIPE_DOMAIN_DEFINITION } from "../src/tools/domains/pipeDomain.js";
import { PLAN_PRODUCTION_DOMAIN_DEFINITION } from "../src/tools/domains/planProductionDomain.js";
import { POINT_DOMAIN_DEFINITION } from "../src/tools/domains/pointDomain.js";
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";
import { PROJECT_DOMAIN_DEFINITION } from "../src/tools/domains/projectDomain.js";
import { STANDARDS_DOMAIN_DEFINITION } from "../src/tools/domains/standardsDomain.js";
import { SURFACE_DOMAIN_DEFINITION } from "../src/tools/domains/surfaceDomain.js";
import { WORKFLOW_DOMAIN_DEFINITION } from "../src/tools/domains/workflowDomain.js";

/** Whether this call of `action` (raw arguments, validated as the runtime does) would need an approvalToken. */
function needsApproval(definition: DomainToolDefinition, action: string, args: Record<string, unknown>): boolean {
  const actionDefinition = definition.actions[action];
  const parsed = actionDefinition.inputSchema.parse({ action, ...args }) as Record<string, unknown>;
  return isApprovalRequired(approvalTargetFor(`civil3d_${definition.domain}`, actionDefinition, parsed));
}

describe("approval follows what the call does", () => {
  it("a dry run or a recommendation needs no token; the same action that writes does", () => {
    expect(needsApproval(PIPE_DOMAIN_DEFINITION, "size_network", { networkName: "Storm 1" })).toBe(false);
    expect(needsApproval(PIPE_DOMAIN_DEFINITION, "size_network", { networkName: "Storm 1", applyChanges: true })).toBe(true);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "pipe_network_design", { networkName: "Storm 1" })).toBe(false);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "pipe_network_design", { networkName: "Storm 1", applyChanges: true })).toBe(true);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "qc_fix_and_verify", { dryRun: true })).toBe(false);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "qc_fix_and_verify", {})).toBe(true);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "data_shortcut_reference_sync", { projectFolder: "P", shortcutName: "EG", shortcutType: "surface", dryRun: true })).toBe(false);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "data_shortcut_reference_sync", { projectFolder: "P", shortcutName: "EG", shortcutType: "surface" })).toBe(true);
    expect(needsApproval(PROJECT_DOMAIN_DEFINITION, "data_shortcut_sync", { dryRun: true })).toBe(false);
    expect(needsApproval(PROJECT_DOMAIN_DEFINITION, "data_shortcut_sync", {})).toBe(true);
    expect(needsApproval(STANDARDS_DOMAIN_DEFINITION, "fix_drawing_standards", { dryRun: true })).toBe(false);
    expect(needsApproval(STANDARDS_DOMAIN_DEFINITION, "fix_drawing_standards", { dryRun: false })).toBe(true);
  });

  it("a dry run that still saves the drawing needs a token", () => {
    const references = [{ projectFolder: "P", shortcutName: "EG", shortcutType: "surface" }];
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "project_reference_setup", { references, dryRun: true })).toBe(false);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "project_reference_setup", { references, dryRun: true, saveAs: "C:/x/y.dwg" })).toBe(true);
  });

  it("an action that writes a file needs a token exactly when it is given one to write", () => {
    const payItems = [{ code: "1", description: "Pipe", unit: "LF", unitPrice: 10 }];
    expect(needsApproval(COST_ESTIMATION_DOMAIN_DEFINITION, "material_cost_estimate", { payItems })).toBe(false);
    expect(needsApproval(COST_ESTIMATION_DOMAIN_DEFINITION, "material_cost_estimate", { payItems, outputPath: "C:/out/cost.csv" })).toBe(true);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "corridor_qc_report", { corridorName: "Main" })).toBe(false);
    expect(needsApproval(WORKFLOW_DOMAIN_DEFINITION, "corridor_qc_report", { corridorName: "Main", outputPath: "C:/out/qc.txt" })).toBe(true);
  });

  it("assembly edit only lists without a subassembly (a read); naming one needs a token", () => {
    expect(needsApproval(ASSEMBLY_DOMAIN_DEFINITION, "edit", { assemblyName: "Typical" })).toBe(false);
    expect(needsApproval(ASSEMBLY_DOMAIN_DEFINITION, "edit", { assemblyName: "Typical", subassemblyName: "Lane" })).toBe(true);
    expect(needsApproval(ASSEMBLY_DOMAIN_DEFINITION, "edit", { assemblyName: "Typical", delete: true })).toBe(true);
  });

  it("the Storm and Sanitary Analysis dialog can change pipes, so it needs a token", () => {
    expect(needsApproval(HYDROLOGY_DOMAIN_DEFINITION, "open_storm_sanitary_analysis", {})).toBe(true);
  });
});

describe("through the runtime", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({});
    captureDomainToolHandlers(PLAN_PRODUCTION_DOMAIN_DEFINITION);
    captureDomainToolHandlers(PIPE_DOMAIN_DEFINITION);
  });

  it("refuses an unsupported action with CIVIL3D.API_ERROR, without approval and without calling the plugin", async () => {
    const result = await getToolHandler("civil3d_plan_production")!({
      action: "sheet_set_title_block", sheetSetName: "C:/Plans/Set.dst", sheetName: "C-101", titleBlockPath: "C:/tb.dwg",
    });
    expect(result.isError).toBe(true);
    expect(result.errorCode).toBe("CIVIL3D.API_ERROR");
    expect(result.content[0].text).toMatch(/title block/);
    expect(sendCommandMock).not.toHaveBeenCalled();
  });

  it("answers a missing token with CIVIL3D.APPROVAL_REQUIRED and a refused one with CIVIL3D.APPROVAL_INVALID", async () => {
    const handler = getToolHandler("civil3d_pipe")!;
    const missing = await handler({ action: "delete_pressure_network", name: "WM-1" });
    expect(missing.errorCode).toBe("CIVIL3D.APPROVAL_REQUIRED");
    expect(missing.content[0].text).toMatch(/civil3d_request_approval/);
    const refused = await handler({ action: "delete_pressure_network", name: "WM-1", approvalToken: "not-a-token" });
    expect(refused.errorCode).toBe("CIVIL3D.APPROVAL_INVALID");
    expect([missing.rpcCode, refused.rpcCode]).toEqual([-32060, -32061]);
    expect(sendCommandMock).not.toHaveBeenCalled();
  });

  it("answers bad arguments and an unknown action with CIVIL3D.INVALID_INPUT and a readable message", async () => {
    const handler = getToolHandler("civil3d_pipe")!;
    const unknown = await handler({ action: "no_such_action" });
    expect(unknown.errorCode).toBe("CIVIL3D.INVALID_INPUT");
    expect(unknown.content[0].text).toMatch(/Supported actions/);
    const bad = await handler({ action: "delete_pressure_network", name: 42 });
    expect(bad.errorCode).toBe("CIVIL3D.INVALID_INPUT");
    expect(bad.content[0].text).toMatch(/Invalid input/);
    expect(bad.content[0].text).not.toMatch(/"code":/);
    expect(sendCommandMock).not.toHaveBeenCalled();
  });
});

describe("what fixed actions send", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({});
  });

  it("a whole-drawing point transform sends allPoints, and a turn sends its base point", async () => {
    const transform = POINT_DOMAIN_DEFINITION.actions.transform;
    await transform.execute(transform.inputSchema.parse({ action: "transform", allPoints: true, rotateRadians: 0.5, baseX: 10, baseY: 20 }));
    expect(sendCommandMock).toHaveBeenCalledWith("transformCogoPoints", expect.objectContaining({ allPoints: true, baseX: 10, baseY: 20 }));
  });

  it("the hydraulic analysis leaves unit-dependent defaults to the plugin", async () => {
    const analysis = PIPE_DOMAIN_DEFINITION.actions.hydraulic_analysis;
    await analysis.execute(analysis.inputSchema.parse({ action: "hydraulic_analysis", networkName: "Storm 1" }));
    expect(sendCommandMock).toHaveBeenCalledWith("analyzePipeNetworkHydraulics",
      expect.objectContaining({ minCoverDepth: null, minVelocity: null, maxVelocity: null }));
  });

  it("Storm and Sanitary Analysis refuses a filePath instead of ignoring it", () => {
    const open = HYDROLOGY_DOMAIN_DEFINITION.actions.open_storm_sanitary_analysis;
    expect(() => open.inputSchema.parse({ action: "open_storm_sanitary_analysis", filePath: "C:/a.stm" })).toThrow(/takes no file/);
    expect(() => open.inputSchema.parse({ action: "open_storm_sanitary_analysis" })).not.toThrow();
  });

  it("a profile report over no samples says so instead of reporting an infinite elevation", async () => {
    sendCommandMock.mockImplementation(async (method: string) => method === "getProfile"
      ? {
        name: "FG", handle: "1", type: "Layout", style: "s", layer: "l", startStation: 0, endStation: 100,
        minElevation: 10, maxElevation: 12, entityCount: 0, entities: [], pviCount: 2, units: { horizontal: "Foot", vertical: "Foot" },
      }
      : { alignmentName: "A", profileName: "FG", startStation: 0, endStation: 100, interval: 25, samples: [], units: "Foot" });
    const report = PROFILE_DOMAIN_DEFINITION.actions.report;
    await expect(report.execute(report.inputSchema.parse({ action: "report", alignmentName: "A", profileName: "FG" })))
      .rejects.toMatchObject({ code: "CIVIL3D.INVALID_INPUT", message: expect.stringMatching(/no elevations/) });
  });

  it("a drainage workflow with no sampled elevations says so instead of reporting an infinite elevation", async () => {
    sendCommandMock.mockImplementation(async (method: string) => {
      if (method === "getSurface") {
        return {
          name: "EG", handle: "1", type: "TIN", style: "s", layer: "l", units: "Foot", isReference: false, dependentAlignments: [], dependentCorridors: [],
          statistics: { minimumElevation: 0, maximumElevation: 1, meanElevation: 0.5, area2d: 1, area3d: 1, numberOfPoints: 3, numberOfTriangles: 1 },
          boundingBox: { minX: 0, minY: 0, maxX: 1, maxY: 1 },
        };
      }
      const point = { x: 5, y: 5, elevation: 1 };
      if (method === "traceHydrologyFlowPath") {
        return {
          surfaceName: "EG", status: "complete", stepDistance: 1, stepCount: 0, totalDistance: 0, dropElevation: 0,
          startPoint: point, endPoint: point, points: [], units: { horizontal: "Foot", vertical: "Foot" },
        };
      }
      if (method === "getSurfaceElevationsAlong") return { surfaceName: "EG", units: "Foot", samples: [] };
      return {
        method: "rational", drainageArea: 1, runoffCoefficient: 0.5, rainfallIntensity: 2, areaUnits: "acres", intensityUnits: "in_per_hr",
        runoffRate: { cfs: 1, cubicMetersPerSecond: 0.03 },
        normalizedInputs: { drainageAreaAcres: 1, drainageAreaHectares: 0.4, rainfallIntensityInPerHr: 2, rainfallIntensityMmPerHr: 50.8 },
      };
    });
    const drainage = SURFACE_DOMAIN_DEFINITION.actions.drainage_workflow;
    await expect(drainage.execute(drainage.inputSchema.parse({
      action: "drainage_workflow", name: "EG", x: 5, y: 5, drainageArea: 1, runoffCoefficient: 0.5, rainfallIntensity: 2,
      areaUnits: "acres", intensityUnits: "in_per_hr",
    }))).rejects.toMatchObject({ code: "CIVIL3D.INVALID_INPUT", message: expect.stringMatching(/No elevations could be sampled/) });
  });
});
