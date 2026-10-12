import { describe, expect, it } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerTools } from "../src/tools/register.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";
import { selectManifestExposures } from "../src/tools/toolManifest.js";
import { listSkills, parseSkill, readSkill, searchSkills } from "../src/tools/skillsLibrary.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { SERVER_INSTRUCTIONS } from "../src/serverInstructions.js";

describe("server instructions", () => {
  it("tell every connecting client to check saved scripts and skills before writing new code", async () => {
    expect(SERVER_INSTRUCTIONS).toMatch(/civil3d_skills action=search/);
    expect(SERVER_INSTRUCTIONS).toMatch(/civil3d_script action=list/);

    const server = new McpServer({ name: "instructions", version: "test" }, { instructions: SERVER_INSTRUCTIONS });
    const client = new Client({ name: "instructions-client", version: "test" });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    try {
      await server.connect(serverTransport);
      await client.connect(clientTransport);
      expect(client.getInstructions()).toBe(SERVER_INSTRUCTIONS);
    } finally {
      await client.close();
      await server.close();
    }
  });

  it("repeats the check in the code-execution tool descriptions", () => {
    for (const name of ["civil3d_execute", "civil3d_query", "civil3d_skills"]) {
      const exposure = SCRIPTING_DOMAIN_DEFINITION.exposures.find((e) => e.toolName === name)!;
      expect(exposure.description).toMatch(/civil3d_skills|BEFORE writing new code/);
    }
  });

  it("finds the proven label-declutter skill by task description", () => {
    expect(searchSkills("point labels overlapping").results[0]?.name).toBe("declutter_point_labels");
  });
});

function riskFor(action: string) {
  const definition = SCRIPTING_DOMAIN_DEFINITION.actions[action];
  return hasApprovalRisk({
    toolName: "test",
    action,
    capabilities: definition.capabilities,
    safeForRetry: definition.safeForRetry,
    requiresActiveDrawing: definition.requiresActiveDrawing,
  });
}

describe("scripting domain", () => {
  it("exposes all scripting tools in compact mode", () => {
    const names = selectManifestExposures(SCRIPTING_DOMAIN_DEFINITION, false).map((exposure) => exposure.toolName);
    expect(names).toEqual(["civil3d_execute", "civil3d_query", "acad_command", "civil3d_skills", "civil3d_script"]);
  });

  it("gates drawing and file mutations behind approval but not read-only work", () => {
    expect(riskFor("execute")).toBe(true);
    expect(riskFor("command")).toBe(true);
    expect(riskFor("script_save")).toBe(true);
    expect(riskFor("script_run")).toBe(true);
    expect(riskFor("script_delete")).toBe(true);

    expect(riskFor("query")).toBe(false);
    expect(riskFor("compile")).toBe(false);
    expect(riskFor("status")).toBe(false);
    expect(riskFor("script_list")).toBe(false);
    expect(riskFor("script_get")).toBe(false);
    expect(riskFor("skills_read")).toBe(false);
  });

  it("maps civil3d_script and civil3d_skills sub-actions onto domain actions", () => {
    const script = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_script")!;
    expect(script.resolveAction({ action: "run", name: "x" }).action).toBe("script_run");
    expect(script.resolveAction({ action: "compile", code: "return 1;" }).action).toBe("compile");
    expect(script.resolveAction({ action: "status" }).action).toBe("status");

    const skills = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_skills")!;
    expect(skills.resolveAction({ action: "read", skillName: "list_surfaces" }).action).toBe("skills_read");
  });

  it("blocks an unapproved civil3d_execute before it reaches the plugin", async () => {
    const server = new McpServer({ name: "scripting-approval", version: "test" });
    await registerTools(server);

    const handler = getToolHandler("civil3d_execute");
    expect(handler).toBeDefined();
    const result = await handler!({ code: "return 1;" });

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("Approval required");
  });

  it("rejects malformed saved-script parameters before contacting the plugin", async () => {
    const server = new McpServer({ name: "scripting-validation", version: "test" });
    await registerTools(server);

    const handler = getToolHandler("civil3d_script");
    const result = await handler!({
      action: "save",
      name: "bad",
      description: "bad types",
      code: "return 1;",
      parameters: [{ name: "x", type: "matrix" }],
    });

    expect(result.isError).toBe(true);
  });
});

describe("skills library", () => {
  it("parses CRLF frontmatter with nested parameters", () => {
    const raw = [
      "---",
      "name: sample_skill",
      "category: surfaces",
      "description: \"Sample: does things\"",
      "requires_write: true",
      "parameters:",
      "  - name: surfaceName",
      "    type: string",
      "    required: true",
      "    description: Surface to use",
      "  - name: interval",
      "    type: number",
      "---",
      "## Code",
      "```csharp",
      "return 1;",
      "```",
    ].join("\r\n");

    const skill = parseSkill(raw, "fallback");
    expect(skill).toBeDefined();
    expect(skill!.name).toBe("sample_skill");
    expect(skill!.description).toBe("Sample: does things");
    expect(skill!.requiresWrite).toBe(true);
    expect(skill!.parameters).toEqual([
      { name: "surfaceName", type: "string", required: true, description: "Surface to use" },
      { name: "interval", type: "number", required: false },
    ]);
    expect(skill!.content).toContain("return 1;");
  });

  it("loads the bundled skills and finds them by search", () => {
    const listing = listSkills();
    expect(listing.count).toBeGreaterThanOrEqual(10);
    expect(listing.categories).toContain("surfaces");

    const found = searchSkills("surface volume");
    expect(found.results[0]?.name).toBe("surface_volume");

    const skill = readSkill("list_surfaces");
    expect(skill.content).toContain("csharp");
  });

  it("reports available skills when a name is unknown", () => {
    expect(() => readSkill("does_not_exist")).toThrow(/Available:/);
  });
});
