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

import { PIPE_DOMAIN_DEFINITION, partDiameter } from "../src/tools/domains/pipeDomain.js";

const CATALOG = { partsLists: [{ name: "Storm", parts: ["12 inch Concrete Pipe", "15 inch Concrete Pipe", "18 inch Concrete Pipe"] }] };

/** A network of one 12-inch pipe at 1% in a drawing in `units`, as getPipeNetwork answers. */
function network(units: string) {
  return {
    name: "Storm 1",
    partsList: "Storm",
    units,
    pipes: [{ name: "P-1", diameter: 1, slope: 1, slopePercent: 1, partSizeName: "12 inch Concrete Pipe", length: 100 }],
  };
}

async function sizeNetwork(units: string, designFlow: number, applyChanges: boolean) {
  sendCommandMock.mockImplementation(async (method: string) => {
    if (method === "getPipeNetwork") return network(units);
    if (method === "listPipePartsCatalog") return CATALOG;
    return { resized: true };
  });
  const action = PIPE_DOMAIN_DEFINITION.actions.size_network;
  const args = action.inputSchema.parse({ action: "size_network", networkName: "Storm 1", defaultDesignFlow: designFlow, applyChanges });
  return await action.execute(args) as { appliedCount: number; recommendations: Array<Record<string, unknown>> };
}

describe("pipe sizing", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
  });

  it("reads a catalog size in the drawing's units", () => {
    expect(partDiameter("15 inch Concrete Pipe", "feet")).toBeCloseTo(1.25);
    expect(partDiameter('18" RCP', "feet")).toBeCloseTo(1.5);
    expect(partDiameter("375 mm PVC", "meters")).toBeCloseTo(0.375);
    expect(partDiameter("375 mm PVC", "feet")).toBeCloseTo(1.2303, 3);
    expect(partDiameter("15 Concrete Pipe", "feet")).toBeCloseTo(1.25); // no unit: inches in feet
    expect(partDiameter("Concrete Pipe", "feet")).toBeNull();
    expect(partDiameter("15 inch Concrete Pipe", "other")).toBeNull();
  });

  it("leaves a pipe alone when its part is already the right one", async () => {
    // 3 cfs at 1% fits a 12-inch pipe (about 3.56 cfs full).
    const result = await sizeNetwork("feet", 3, true);
    expect(result.appliedCount).toBe(0);
    expect(result.recommendations[0]).toMatchObject({ selectedPart: "12 inch Concrete Pipe", changeNeeded: false, applied: false });
    expect(sendCommandMock).not.toHaveBeenCalledWith("resizePipeInNetwork", expect.anything());
  });

  it("swaps to the bigger part only, sending no separate diameter", async () => {
    // 6 cfs at 1% needs a 15-inch pipe (about 6.46 cfs full).
    const result = await sizeNetwork("feet", 6, true);
    expect(result.appliedCount).toBe(1);
    expect(result.recommendations[0]).toMatchObject({ selectedPart: "15 inch Concrete Pipe", selectedDiameter: 1.25, units: "feet" });
    expect(sendCommandMock).toHaveBeenCalledWith("resizePipeInNetwork", { networkName: "Storm 1", pipeName: "P-1", newPartName: "15 inch Concrete Pipe" });
  });

  it("only recommends without applyChanges", async () => {
    const result = await sizeNetwork("feet", 6, false);
    expect(result.appliedCount).toBe(0);
    expect(result.recommendations[0]).toMatchObject({ changeNeeded: true, applied: false });
    expect(sendCommandMock).not.toHaveBeenCalledWith("resizePipeInNetwork", expect.anything());
  });

  it("changes nothing in a drawing whose units aren't feet or meters", async () => {
    const result = await sizeNetwork("other", 6, true);
    expect(result.appliedCount).toBe(0);
    expect(result.recommendations[0]).toMatchObject({ status: "skipped" });
  });
});
