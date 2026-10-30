import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({
  sendCommandMock: vi.fn(),
}));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({
    sendCommand: sendCommandMock,
  }),
}));

import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { PARCEL_DOMAIN_DEFINITION } from "../src/tools/domains/parcelDomain.js";
import { POINT_DOMAIN_DEFINITION } from "../src/tools/domains/pointDomain.js";
import { PROFILE_DOMAIN_DEFINITION } from "../src/tools/domains/profileDomain.js";
import { QC_DOMAIN_DEFINITION } from "../src/tools/domains/qcDomain.js";

/** The approval target of one action, as the runtime builds it. */
function target(toolName: string, definition: { actions: Record<string, { action: string; capabilities: never[]; safeForRetry: boolean }> }, action: string) {
  const a = definition.actions[action];
  return { toolName, action: a.action, capabilities: a.capabilities, safeForRetry: a.safeForRetry };
}

describe("actions that write files or move many objects", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({});
  });

  it("transform moves every point only when allPoints: true says so", async () => {
    const transform = POINT_DOMAIN_DEFINITION.actions.transform;
    await expect(transform.execute(transform.inputSchema.parse({ action: "transform", translateX: 10 })))
      .rejects.toThrow(/allPoints/);
    expect(sendCommandMock).not.toHaveBeenCalled();
    await transform.execute(transform.inputSchema.parse({ action: "transform", translateX: 10, allPoints: true }));
    await transform.execute(transform.inputSchema.parse({ action: "transform", translateX: 10, groupName: "Control" }));
    expect(sendCommandMock).toHaveBeenCalledTimes(2);
  });

  it("the parcel report only reads; writing it to a file is export_report, which needs approval", async () => {
    const report = PARCEL_DOMAIN_DEFINITION.actions.report;
    await expect(report.execute(report.inputSchema.parse({ action: "report", siteName: "Elm", outputPath: "C:/out/p.csv" })))
      .rejects.toThrow(/export_report/);
    expect(isApprovalRequired(target("civil3d_parcel", PARCEL_DOMAIN_DEFINITION as never, "report"))).toBe(false);
    expect(isApprovalRequired(target("civil3d_parcel", PARCEL_DOMAIN_DEFINITION as never, "export_report"))).toBe(true);
  });

  it("the QC report, which writes a file, needs approval", () => {
    expect(isApprovalRequired(target("civil3d_qc", QC_DOMAIN_DEFINITION as never, "generate_report"))).toBe(true);
  });

  it("profile sampling refuses an interval of zero or less (it would never end)", () => {
    const sample = PROFILE_DOMAIN_DEFINITION.actions.sample_elevations;
    expect(() => sample.inputSchema.parse({ action: "sample_elevations", alignmentName: "A", profileName: "P", interval: 0 })).toThrow();
    expect(() => sample.inputSchema.parse({ action: "sample_elevations", alignmentName: "A", profileName: "P", interval: 25 })).not.toThrow();
  });
});
