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

import { POINT_DOMAIN_DEFINITION } from "../src/tools/domains/pointDomain.js";
import { SERVER_INSTRUCTIONS } from "../src/serverInstructions.js";

describe("points from a file through Civil 3D's own importer", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
    sendCommandMock.mockResolvedValue({ imported: 2 });
  });

  it("import_file hands the file, the Civil 3D format name and the point group to importPointFile", async () => {
    const action = POINT_DOMAIN_DEFINITION.actions.import_file;
    const args = action.inputSchema.parse({
      action: "import_file", path: "C:/Jobs/1234/Survey/topo.csv", fileFormat: "PNEZD (comma delimited)", pointGroup: "2026 10 19 ABC",
    });
    await action.execute(args);
    expect(sendCommandMock).toHaveBeenCalledWith("importPointFile", {
      path: "C:/Jobs/1234/Survey/topo.csv", format: "PNEZD (comma delimited)", pointGroup: "2026 10 19 ABC",
    });
  });

  it("list_formats asks the drawing for its point file formats", async () => {
    await POINT_DOMAIN_DEFINITION.actions.list_formats.execute({ action: "list_formats" });
    expect(sendCommandMock).toHaveBeenCalledWith("listPointFileFormats", {});
  });

  it("the instructions tell the AI to use the importer and that other point-making asks the user", () => {
    expect(SERVER_INSTRUCTIONS).toMatch(/import_file/);
    expect(SERVER_INSTRUCTIONS).toMatch(/asks the user/);
  });
});
