import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerTools } from "../src/tools/register.js";
import { getToolHandler } from "../src/tools/toolHandlerRegistry.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";
import { selectManifestExposures } from "../src/tools/toolManifest.js";
import { listSkills, parseSkill, readSkill, searchSkills } from "../src/tools/skillsLibrary.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { SERVER_INSTRUCTIONS, buildServerInstructions } from "../src/serverInstructions.js";
import { appendVersionSection, invalidateSkillCache, retireSkill, writeSkill } from "../src/tools/skillsLibrary.js";
import {
  addPreference, allLessons, lessonsRelevantTo, listPreferences, recordLesson, releaseFromAcadver, removeLesson,
  removePreference, setReleaseForTesting, updatePreference,
} from "../src/tools/knowledgeBase.js";
import { existsSync, mkdtempSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

describe("knowledge base: skills, preferences and lessons", () => {
  let root: string;
  const previous = process.env.CIVIL3D_KNOWLEDGE_DIR;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-kb-"));
    process.env.CIVIL3D_KNOWLEDGE_DIR = root;
    setReleaseForTesting("2027");
    invalidateSkillCache();
  });

  afterEach(() => {
    if (previous === undefined) delete process.env.CIVIL3D_KNOWLEDGE_DIR; else process.env.CIVIL3D_KNOWLEDGE_DIR = previous;
    setReleaseForTesting(undefined);
    invalidateSkillCache();
    rmSync(root, { recursive: true, force: true });
  });

  it("learns a skill stamped with the release, archives old versions, and retires instead of deleting", () => {
    const saved = writeSkill({
      name: "renumber_points", category: "points", description: "Renumber COGO points in a selected group",
      content: "## Rules\n- Never change elevations\n\n```csharp\nreturn 1;\n```",
      parameters: [{ name: "group", type: "string", required: true, description: "Point group" }],
    }, "2027");
    expect(saved).toMatchObject({ action: "created", civil3dVersions: ["2027"] });
    expect(searchSkills("renumber points", "2027").results[0]).toMatchObject({ name: "renumber_points", source: "personal" });
    expect(readSkill("renumber_points", "2027").content).toMatch(/^> Verified on Civil 3D 2027\./);

    const updated = writeSkill({ name: "renumber_points", category: "points", description: "v2", content: "better" }, "2027");
    expect(updated.action).toBe("updated");
    expect(existsSync(updated.previousVersionArchived as string)).toBe(true);

    const retired = retireSkill("renumber_points");
    expect(retired.archivedTo).toHaveLength(1);
    expect(() => readSkill("renumber_points")).toThrow(/not found/);
    expect(readdirSync(join(root, "skills", "_archive"))).toHaveLength(2);
  });

  it("refuses to overwrite a skill verified on another release and adds a version section instead", () => {
    writeSkill({ name: "label_tool", description: "Labels", content: "2027 steps" }, "2027");
    expect(() => writeSkill({ name: "label_tool", description: "Labels", content: "2028 steps" }, "2028")).toThrow(/append_version_section/);
    expect(readSkill("label_tool", "2028").version_note).toMatch(/Verified on Civil 3D 2027; you're running 2028/);

    const appended = appendVersionSection("label_tool", "Different on 2028", "2028");
    expect(appended.civil3dVersions).toEqual(["2027", "2028"]);
    const skill = readSkill("label_tool", "2028");
    expect(skill.version_note).toBeUndefined();
    expect(skill.content).toContain("2027 steps");
    expect(skill.content).toContain("## Civil 3D 2028\n\nDifferent on 2028");
  });

  it("lets the user's version replace a built-in skill; retiring it restores the built-in", () => {
    const builtIn = readSkill("list_surfaces");
    expect(builtIn.source).toBe("built-in");
    expect(writeSkill({ name: "list_surfaces", category: "surfaces", description: "Improved", content: "better" }, "2027").overridesBuiltIn).toBe(true);
    expect(readSkill("list_surfaces")).toMatchObject({ source: "personal", overridesBuiltIn: true });
    expect(retireSkill("list_surfaces").builtInVersionActiveAgain).toBe(true);
    expect(readSkill("list_surfaces").content).toBe(builtIn.content);
    expect(() => retireSkill("list_surfaces")).toThrow(/built-in skill and can't be retired/);
  });

  it("rejects unsafe skill names", () => {
    expect(() => writeSkill({ name: "../evil", description: "x", content: "x" }, "2027")).toThrow(/Invalid skill name/);
    expect(() => writeSkill({ name: "ok", category: "a/b", description: "x", content: "x" }, "2027")).toThrow(/Invalid category/);
  });

  it("keeps standing preferences and puts them into every chat's instructions", () => {
    const first = addPreference("Hide group is 'HIDDEN POINTS'");
    expect(first.action).toBe("added");
    expect(addPreference("hide group is 'hidden points'").action).toBe("unchanged");
    addPreference("We plot at 1\" = 30'");
    updatePreference(first.preference.id, "Hide group is 'Hidden Points'");
    expect(listPreferences().map((preference) => preference.rule)).toEqual(["Hide group is 'Hidden Points'", "We plot at 1\" = 30'"]);

    writeSkill({ name: "my_workflow", description: "Office label cleanup", content: "x" }, "2027");
    const instructions = buildServerInstructions();
    expect(instructions).toContain("Hide group is 'Hidden Points'");
    expect(instructions).toContain("my_workflow: Office label cleanup");
    expect(instructions).toContain("working_safely_in_civil3d");

    removePreference(first.preference.id);
    expect(listPreferences()).toHaveLength(1);
  });

  it("records lessons with topics and surfaces them for matching code", () => {
    expect(() => recordLesson("Something", [], "2027")).toThrow(/topic/);
    const recorded = recordLesson("FeatureLine.AssignElevationsFromSurface needs a surface id, not a name", ["AssignElevationsFromSurface"], "2027");
    expect(recorded.action).toBe("recorded");
    expect(allLessons().some((lesson) => lesson.source === "seed")).toBe(true);

    const related = lessonsRelevantTo("var fl = ...; fl.AssignElevationsFromSurface(id, true); p.LabelLocation = pt;", "2027");
    expect(related.map((lesson) => lesson.id)).toEqual(expect.arrayContaining([recorded.lesson.id, "seed-1"]));
    expect(lessonsRelevantTo("p.LabelLocation = pt;", "2028")[0].version_note).toMatch(/Learned on Civil 3D 2027/);
  });

  it("only removes lessons learned on the running release", () => {
    const lesson = recordLesson("Wrong lesson", ["SomeApi"], "2027").lesson;
    expect(() => removeLesson("seed-1", "2027")).toThrow(/Built-in lessons/);
    expect(() => removeLesson(lesson.id, "2028")).toThrow(/learned on Civil 3D 2027/);
    expect(removeLesson(lesson.id, "2027").action).toBe("removed");
  });

  it("stamps only the release, never the licensee name in ACADVER", () => {
    expect(releaseFromAcadver("26.0s (Example Company)")).toBe("2027");
    expect(releaseFromAcadver("25.1")).toBe("2026");
    expect(releaseFromAcadver("27.0x (Co)")).toBe("R27.0");
    expect(releaseFromAcadver(undefined)).toBeUndefined();
  });

  it("lets the assistant learn without approval, but removing knowledge needs approval; maps tool actions", () => {
    for (const action of [
      "skills_write", "skills_append_version_section", "skills_retire",
      "preferences_list", "preferences_add", "preferences_update",
      "memory_record", "memory_search",
    ]) {
      expect(riskFor(action), action).toBe(false);
    }
    expect(riskFor("preferences_remove")).toBe(true);
    expect(riskFor("memory_remove")).toBe(true);
    const tool = (name: string) => SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === name)!;
    expect(tool("civil3d_skills").resolveAction({ action: "append_version_section" }).action).toBe("skills_append_version_section");
    expect(tool("civil3d_preferences").resolveAction({ action: "add" }).action).toBe("preferences_add");
    expect(tool("civil3d_memory").resolveAction({ action: "record" }).action).toBe("memory_record");
  });
});

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
    expect(names).toEqual(["civil3d_execute", "civil3d_query", "acad_command", "civil3d_skills", "civil3d_preferences", "civil3d_memory", "civil3d_project_notes", "civil3d_script"]);
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
