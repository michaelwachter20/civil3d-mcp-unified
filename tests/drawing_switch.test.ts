import { describe, expect, it } from "vitest";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";

describe("civil3d_drawing open drawings and tab switching", () => {
  const risk = (action: string) => {
    const definition = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions[action];
    return hasApprovalRisk({
      toolName: "civil3d_drawing",
      action,
      capabilities: definition.capabilities,
      safeForRetry: definition.safeForRetry,
    });
  };

  it("lists open drawings and switches tabs without approval", () => {
    expect(risk("list_open")).toBe(false);
    expect(risk("switch")).toBe(false);
    expect(risk("command_line")).toBe(false);
    expect(DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.command_line.pluginMethods).toEqual(["getCommandLine"]);
    expect(risk("save")).toBe(true);
  });

  it("maps the actions to the plugin and requires a drawing name to switch", () => {
    expect(DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.list_open.pluginMethods).toEqual(["listOpenDrawings"]);
    expect(DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.switch.pluginMethods).toEqual(["activateDrawing"]);
    expect(DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.switch.inputSchema.safeParse({ action: "switch" }).success).toBe(false);
    expect(DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.switch.inputSchema.safeParse({ action: "switch", name: "Site-GoBy" }).success).toBe(true);
  });
});
