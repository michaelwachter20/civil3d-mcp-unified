import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { addPractice, describePractices, listPractices, practicesFile, removePractice, searchPractices, updatePractice } from "../src/tools/practices.js";
import { invalidateJobIndex } from "../src/tools/projectDb.js";
import { buildServerInstructions, SERVER_INSTRUCTIONS } from "../src/serverInstructions.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";

const FIXTURES = join(import.meta.dirname, "fixtures");

describe("good practices shared by the suite (practices.json)", () => {
  let root: string;
  const previous = process.env.PROJECT_KNOWLEDGE_DIR;
  const raw = () => readFileSync(practicesFile(), "utf8");

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-practices-"));
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    copyFileSync(join(FIXTURES, "fixture-practices.json"), join(root, "practices.json"));
    invalidateJobIndex();
  });

  afterEach(() => {
    invalidateJobIndex();
    if (previous === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  });

  it("lists every tool's practices, marking the other tools' entries read-only", () => {
    expect(describePractices().practices).toEqual([
      { id: "practice-1", practice: "Storm pipe minimum slope is 0.5%.", topics: ["storm", "slope"], source: "civil3d" },
      { id: "practice-2", practice: "Label structures MH-n, CB-n, FES-n.", topics: ["labels"], source: "tbc", readOnly: true },
    ]);
    expect(searchPractices("slope storm").practices.map((practice) => practice.id)).toEqual(["practice-1"]);
    expect(searchPractices("labels").practices.map((practice) => practice.id)).toEqual(["practice-2"]);
  });

  it("adds with the next id, one line, a duplicate check, and writes UTF-8 without BOM, LF, 2-space indent", () => {
    const added = addPractice("Minimum cover over storm pipe\nis 2 ft.", ["storm", "cover"]);
    expect(added).toMatchObject({ action: "added", practice: { id: "practice-3", practice: "Minimum cover over storm pipe is 2 ft.", source: "civil3d" } });
    expect(addPractice("minimum cover over storm pipe is 2 ft.").action).toBe("unchanged");
    expect(addPractice("Label structures MH-n, CB-n, FES-n.").action).toBe("unchanged");
    const text = raw();
    expect(text.charCodeAt(0)).not.toBe(0xfeff);
    expect(text).not.toContain("\r");
    expect(text).toMatch(/^\{\n {2}"practices": \[\n {4}\{/);
    expect(text.endsWith("}\n")).toBe(true);
    expect(JSON.parse(text).practices[2]).toMatchObject({ id: "practice-3", topics: ["storm", "cover"], added: expect.stringMatching(/Z$/) });
  });

  it("changes and retires only its own entries; retired ids are never reused", () => {
    expect(() => updatePractice("practice-2", "Label structures S-n.")).toThrow(/belongs to tbc/);
    expect(() => removePractice("practice-2")).toThrow(/belongs to tbc/);
    expect(() => updatePractice("practice-9", "x")).toThrow(/No practice/);
    expect(updatePractice("practice-1", "Storm pipe minimum slope is 0.4%.", ["storm"]).practice).toMatchObject({ practice: "Storm pipe minimum slope is 0.4%.", topics: ["storm"] });
    expect(removePractice("practice-1").action).toBe("retired");
    expect(listPractices().map((practice) => practice.id)).toEqual(["practice-2"]);
    expect(listPractices(true).find((practice) => practice.id === "practice-1")?.retired).toBeTruthy();
    expect(() => updatePractice("practice-1", "again")).toThrow(/was removed/);
    expect(addPractice("Use 0.5% minimum slope.").practice.id).toBe("practice-3");
  });

  it("keeps fields and entries it doesn't know, and re-reads the file right before writing", () => {
    const data = JSON.parse(raw());
    data.owner = "suite";
    data.practices[1].reviewed_by = "R. Lee";
    data.practices.push({ id: "practice-7", practice: "Written by another tool meanwhile.", source: "project-analyzer" });
    writeFileSync(practicesFile(), `${JSON.stringify(data, null, 2)}\n`);
    addPractice("Benchmarks are on NAVD88.");
    const after = JSON.parse(raw());
    expect(after.owner).toBe("suite");
    expect(after.practices[1].reviewed_by).toBe("R. Lee");
    expect(after.practices.map((practice: { id: string }) => practice.id)).toEqual(["practice-1", "practice-2", "practice-7", "practice-8"]);
  });

  it("never rewrites a file that doesn't parse", () => {
    writeFileSync(practicesFile(), "{ not json");
    expect(() => addPractice("Anything.")).toThrow(/doesn't parse/);
    expect(raw()).toBe("{ not json");
    expect(buildServerInstructions()).not.toMatch(/Good practices/);
  });

  it("starts the file when there is none", () => {
    rmSync(practicesFile());
    expect(describePractices().practices).toEqual([]);
    expect(addPractice("Survey in NAD83.").practice.id).toBe("practice-1");
    expect(JSON.parse(raw())).toEqual({ practices: [expect.objectContaining({ id: "practice-1", source: "civil3d" })] });
  });

  it("refuses practices that name a job, its folders or its files", () => {
    mkdirSync(join(root, "projects"));
    copyFileSync(join(FIXTURES, "fixture.db"), join(root, "projects", "elm-street-drainage.db"));
    expect(() => addPractice("On Elm Street Drainage use 1% slope.")).toThrow(/the name of the job 'Elm Street Drainage'/);
    expect(() => addPractice("Templates are in P:\\Standards\\C3D.")).toThrow(/folder path/);
    expect(() => addPractice("Copy layers from C-104.dwg.")).toThrow(/names a file/);
    expect(addPractice("Storm pipe maximum velocity is 10 ft/s.").action).toBe("added");
  });

  it("shows current practices to the AI next to the preferences", () => {
    writeFileSync(join(root, "preferences.json"), JSON.stringify({ preferences: [{ id: "pref-1", rule: "Plot at 1\" = 30'.", added: "2026-10-20T12:00:00.000Z" }] }));
    removePractice("practice-1");
    const instructions = buildServerInstructions();
    expect(instructions).toMatch(/standing preferences[\s\S]*\[pref-1\][\s\S]*Good practices shared by the suite[^\n]*\n- \[practice-2\] Label structures MH-n/);
    expect(instructions).not.toContain("practice-1");
    expect(SERVER_INSTRUCTIONS).toMatch(/three kinds/);
    expect(SERVER_INSTRUCTIONS).toMatch(/civil3d_practices/);
    expect(SERVER_INSTRUCTIONS).toMatch(/civil3d_project_notes action=overview/);
  });

  it("is a tool of its own; only removing asks for approval", () => {
    const risk = (action: string) => {
      const definition = SCRIPTING_DOMAIN_DEFINITION.actions[action];
      return hasApprovalRisk({ toolName: "civil3d_practices", action, capabilities: definition.capabilities, safeForRetry: definition.safeForRetry });
    };
    expect(["practices_list", "practices_search", "practices_add", "practices_update"].map(risk)).toEqual([false, false, false, false]);
    expect(risk("practices_remove")).toBe(true);
    const tool = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_practices")!;
    expect(tool.resolveAction({ action: "add" }).action).toBe("practices_add");
    expect(tool.description).toMatch(/Ask the user/);
  });
});
