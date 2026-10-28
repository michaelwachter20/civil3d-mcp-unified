import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { projectLatest, seriesKey, setActiveDrawingResolverForTesting } from "../src/tools/projectNotes.js";
import { invalidateJobIndex } from "../src/tools/projectDb.js";
import { openDrawingByName, setOpenDrawingHooksForTesting } from "../src/tools/openDrawing.js";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";

const { DatabaseSync: Database } = createRequire(import.meta.url)("node:sqlite") as typeof import("node:sqlite");
const FIXTURES = join(import.meta.dirname, "fixtures");
const ELM = "Elm Street Drainage";

// Made-up versions in a made-up job folder: C-101 in two packages (rev1 newer), C-200 under two headings, and C-300
// whose "rev2" is older than the plain name.
const FILES: Array<[string, string, string]> = [
  ["Packages\\2026-01\\C-101 GRADING.dwg", "c 101 grading", "2026-01-10T10:00:00+00:00"],
  ["Packages\\2026-02\\C-101 GRADING rev1.dwg", "c 101 grading", "2026-02-12T10:00:00+00:00"],
  ["Phase 2\\C-200.dwg", "c 200", "2026-03-01T10:00:00+00:00"],
  ["Phase 7\\C-200.dwg", "c 200", "2026-03-05T10:00:00+00:00"],
  ["C-300 rev2.dwg", "c 300", "2026-01-01T10:00:00+00:00"],
  ["C-300.dwg", "c 300", "2026-02-01T10:00:00+00:00"],
];

describe("the latest version of a file, and civil3d_drawing open", () => {
  let root: string;
  let job: string;
  const previous = process.env.PROJECT_KNOWLEDGE_DIR;
  let calls: Array<[string, Record<string, unknown>]>;
  let open: Array<{ name: string; path: string; active: boolean }>;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-latest-"));
    job = join(root, "Job");
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    mkdirSync(join(root, "projects"), { recursive: true });
    const file = join(root, "projects", "elm-street-drainage.db");
    copyFileSync(join(FIXTURES, "fixture.db"), file);
    const db = new Database(file);
    try {
      db.exec("CREATE TABLE IF NOT EXISTS intel_series (series_id TEXT, key TEXT, kind TEXT, position INTEGER, file_id TEXT, path TEXT, " +
        "modified TEXT, title_date TEXT, revision TEXT, latest INTEGER, note TEXT); DELETE FROM intel_series;");
      db.prepare("UPDATE meta SET value = ? WHERE key = 'source_root'").run(job);
      const groups = new Map<string, Array<[string, string]>>();
      for (const [path, key, modified] of FILES) groups.set(key, [...(groups.get(key) ?? []), [path, modified]]);
      let n = 0;
      for (const [key, members] of groups) {
        n += 1;
        members.forEach(([path, modified], index) => {
          const latest = index === members.length - 1;
          const note = latest && key === "c 300" ? "the highest revision in the names is C-300 rev2.dwg, but C-300.dwg was modified later" : null;
          db.prepare("INSERT INTO intel_series VALUES (?, ?, 'cad', ?, ?, ?, ?, NULL, NULL, ?, ?)")
            .run(`s-${n}`, key, index + 1, `f${n}${index}`, path, modified, latest ? 1 : 0, note);
          mkdirSync(join(job, path, ".."), { recursive: true });
          writeFileSync(join(job, path), "AC1032");
        });
      }
    } finally {
      db.close();
    }
    calls = [];
    open = [];
    setActiveDrawingResolverForTesting(async () => undefined);
    setOpenDrawingHooksForTesting({
      open: async () => open,
      plugin: async (method, params) => { calls.push([method, params]); return { ok: true }; },
    });
    invalidateJobIndex();
  });

  afterEach(() => {
    setOpenDrawingHooksForTesting(undefined);
    setActiveDrawingResolverForTesting(undefined);
    invalidateJobIndex();
    if (previous === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  });

  it("keys names the way the analyzer does", () => {
    expect(seriesKey("C-101 GRADING rev1.dwg")).toBe("c 101 grading");
    expect(seriesKey("all points 20250301 current (2).dwg")).toBe("all points");
  });

  it("finds the latest version, its older ones and where it is on this PC", async () => {
    const found = await projectLatest("C-101 grading", ELM);
    expect(found.series).toHaveLength(1);
    expect(found.series[0].latest).toMatchObject({ path: "Packages\\2026-02\\C-101 GRADING rev1.dwg", modified: "2026-02-12", exists: true });
    expect(found.series[0].latest.fullPath).toBe(join(job, "Packages\\2026-02\\C-101 GRADING rev1.dwg"));
    expect(found.series[0].older.map((member) => member.path)).toEqual(["Packages\\2026-01\\C-101 GRADING.dwg"]);
    expect((await projectLatest("Packages\\2026-01\\C-101 GRADING.dwg", ELM)).series[0].latest.path).toContain("rev1");
    expect((await projectLatest("C-300", ELM)).series[0].note).toContain("C-300 rev2.dwg");
  });

  it("words about the folder pick one of the same name in different folders", async () => {
    expect((await projectLatest("C-200", ELM)).series[0]).toMatchObject({ folders: 2, latest: { path: "Phase 7\\C-200.dwg" } });
    const near = await projectLatest("C-200", ELM, "phase 2");
    expect(near.series[0].latest.path).toBe("Phase 2\\C-200.dwg");
    expect(near.series[0].versions).toBe(1);
  });

  it("an open drawing comes first: it becomes the active tab, and a newer one on the drive is mentioned", async () => {
    open = [{ name: "C-101 GRADING.dwg", path: join(job, "Packages\\2026-01\\C-101 GRADING.dwg"), active: false }];
    const result = await openDrawingByName({ name: "C-101 GRADING", project: ELM });
    expect(result.reason).toContain("already open");
    expect(calls).toEqual([["activateDrawing", { name: join(job, "Packages\\2026-01\\C-101 GRADING.dwg") }]]);
    expect(result).toMatchObject({ newerOnDrive: { path: join(job, "Packages\\2026-02\\C-101 GRADING rev1.dwg") } });
  });

  it("otherwise opens the latest version from the job's database", async () => {
    const result = await openDrawingByName({ name: "C-101 GRADING", project: ELM });
    expect(calls).toEqual([["openDrawing", { path: join(job, "Packages\\2026-02\\C-101 GRADING rev1.dwg"), readOnly: false }]]);
    expect(result).toMatchObject({ opened: join(job, "Packages\\2026-02\\C-101 GRADING rev1.dwg"), older: [{ path: "Packages\\2026-01\\C-101 GRADING.dwg" }] });
    const headings = await openDrawingByName({ name: "C-200", project: ELM });
    expect(headings).toMatchObject({ folders: 2 });
    expect(String(headings.hint)).toContain("different areas");
  });

  it("asks instead of guessing when several open drawings match", async () => {
    open = [{ name: "C-200.dwg", path: join(job, "Phase 2\\C-200.dwg"), active: false }, { name: "C-200.dwg", path: join(job, "Phase 7\\C-200.dwg"), active: true }];
    const result = await openDrawingByName({ name: "C-200", project: ELM });
    expect(result).toMatchObject({ opened: null, reason: "several open drawings match" });
    expect(calls).toEqual([]);
    const picked = await openDrawingByName({ name: "C-200", near: "phase 2", project: ELM });
    expect(picked.opened).toBe(join(job, "Phase 2\\C-200.dwg"));
  });

  it("is wired into civil3d_drawing and civil3d_project_notes without approval", () => {
    const definition = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.open;
    expect(definition.pluginMethods).toContain("openDrawing");
    expect(hasApprovalRisk({ toolName: "civil3d_drawing", action: "open", capabilities: definition.capabilities, safeForRetry: definition.safeForRetry })).toBe(false);
    expect(definition.inputSchema.safeParse({ action: "open" }).success).toBe(false);
    expect(definition.inputSchema.safeParse({ action: "open", name: "C-101" }).success).toBe(true);
    const notes = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_project_notes")!;
    expect(notes.resolveAction({ action: "latest", file: "C-101" }).action).toBe("project_latest");
  });
});
