// Guards the edges where Civil 3D MCP meets the job database that the other suite programs also write (repo rule 2):
// folder ownership between jobs, archive and moved/missing file paths, superseded facts, and civil3d_drawing open
// choosing among same-named drawings. Uses a copy of tests/fixtures/fixture.db in temp folders; no Civil 3D.

import { copyFileSync, mkdirSync, mkdtempSync, rmSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync as Database } from "node:sqlite";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { openDrawingByName, setOpenDrawingHooksForTesting } from "../src/tools/openDrawing.js";
import { invalidateJobIndex, storedPath } from "../src/tools/projectDb.js";
import {
  addProjectNote, linkProjectFolder, locatePath, projectChecks, projectFileDetails, projectOverview, searchProject,
  setActiveDrawingResolverForTesting,
} from "../src/tools/projectNotes.js";

const FIXTURES = join(process.cwd(), "tests", "fixtures");
const ELM = "Elm Street Drainage";

describe("where Civil 3D MCP meets the job's database", () => {
  let root: string;
  let job: string;
  let file: string;
  const previous = process.env.PROJECT_KNOWLEDGE_DIR;

  const sql = (run: (db: Database) => void) => {
    const db = new Database(file);
    try {
      run(db);
    } finally {
      db.close();
    }
    invalidateJobIndex();
  };

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-meet-"));
    job = join(root, "Job");
    mkdirSync(job, { recursive: true });
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    mkdirSync(join(root, "projects"), { recursive: true });
    file = join(root, "projects", "elm-street-drainage.db");
    copyFileSync(join(FIXTURES, "fixture.db"), file);
    sql((db) => db.prepare("UPDATE meta SET value = ? WHERE key = 'source_root'").run(job));
  });

  afterEach(() => {
    setOpenDrawingHooksForTesting(undefined);
    setActiveDrawingResolverForTesting(undefined);
    process.env.PROJECT_KNOWLEDGE_DIR = previous;
    invalidateJobIndex();
    rmSync(root, { recursive: true, force: true });
  });

  it("a second job can't take a folder that belongs to a job", async () => {
    await expect(linkProjectFolder("1234 Elm St", job)).rejects.toThrow(/already belongs to the job 'Elm Street Drainage'/);
    expect((await linkProjectFolder(ELM, job)).action).toBe("unchanged");
    expect((await linkProjectFolder("Elm subfolder job", join(job, "Phase 2"))).action).toBe("created");
  });

  it("a drawing never saved has no folder to link", async () => {
    setActiveDrawingResolverForTesting(async () => "Drawing1.dwg");
    await expect(linkProjectFolder(ELM)).rejects.toThrow(/no folder yet/);
  });

  it("says when the connections are being rebuilt, so an empty answer isn't read as none", async () => {
    const quiet = await projectOverview(ELM);
    expect(quiet.referenceOnly).not.toContain("REBUILDING");
    sql((db) => {
      db.exec("INSERT OR REPLACE INTO meta(key, value) VALUES ('insights_built', '2026-03-01T10:00:00Z'), ('intel_started', '2026-03-02T10:00:00Z')");
      db.exec("DELETE FROM intel_checks");
    });
    expect((await projectOverview(ELM)).referenceOnly).toContain("REBUILDING");
    expect((await projectChecks(ELM)).count).toBe(0);
    sql((db) => db.exec("UPDATE meta SET value = '2026-03-03T10:00:00Z' WHERE key = 'insights_built'"));
    expect((await projectOverview(ELM)).referenceOnly).not.toContain("REBUILDING");
  });

  it("finds a file inside an archive by its stored path, and any file in a job on a drive's root", async () => {
    sql((db) => {
      for (const [id, path] of [["zip1", "Bid.zip::Addenda/spec.pdf"], ["zip2", "Old.zip::Addenda/spec.pdf"]]) {
        db.prepare("INSERT INTO files (id, root_id, depth, path, name, ext, kind, status) VALUES (?, ?, 1, ?, 'spec.pdf', '.pdf', 'pdf', 'done')").run(id, id, path);
      }
    });
    expect((await projectFileDetails("Bid.zip::Addenda/spec.pdf", ELM)).file).toMatchObject({ id: "zip1" });
    await expect(projectFileDetails("spec.pdf", ELM)).rejects.toThrow(/Several files match/);
    const db = new Database(":memory:");
    db.exec("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES ('source_root', 'E:\\')");
    expect(storedPath(db, "E:\\Drawings\\C-104.dwg")).toBe("Drawings\\C-104.dwg");
    db.close();
  });

  it("says when a file moved or is gone", async () => {
    sql((db) => {
      const add = db.prepare("INSERT INTO files (id, root_id, depth, path, name, ext, kind, status) VALUES (?, ?, 0, ?, ?, '.dwg', 'cad', 'done')");
      add.run("old", "old", "Moved\\Z-104.dwg", "Z-104.dwg");
      add.run("lost", "lost", "Moved\\Z-105.dwg", "Z-105.dwg");
      db.exec("INSERT INTO props(file_id, key, value) VALUES ('old', 'moved_to', 'Issued\\C-104.dwg'), ('lost', 'gone', '2026-03-01')");
    });
    expect(await projectFileDetails("Moved\\Z-104.dwg", ELM)).toMatchObject({ movedTo: "Issued\\C-104.dwg", exists: false });
    expect(await projectFileDetails("Moved\\Z-105.dwg", ELM)).toMatchObject({ gone: true, exists: false });
  });

  it("a path that means two files says so", async () => {
    sql((db) => {
      const add = db.prepare("INSERT INTO refs (file_id, root_id, kind, raw, normalized, status, target_path) VALUES (?, ?, 'xref', ?, ?, 'found', ?)");
      add.run("a", "a", "..\\Base\\Topo.dwg", "..\\Base\\Topo.dwg", "Phase 1\\Base\\Topo.dwg");
      add.run("b", "b", "..\\Base\\Topo.dwg", "..\\Base\\Topo.dwg", "Phase 2\\Base\\Topo.dwg");
    });
    const found = await locatePath("..\\Base\\Topo.dwg", ELM);
    expect(found).toMatchObject({ status: "ambiguous" });
    expect((found as { targets: unknown[] }).targets).toHaveLength(2);
  });

  it("a superseded statement can be said again, and search marks it as replaced", async () => {
    const first = await addProjectNote("MH-3 rim is 702.15", ELM, { source: "user" });
    const firstId = (first as { note: { id: string } }).note.id;
    await addProjectNote("MH-3 rim is 702.40", ELM, { source: "user", supersedes: firstId });
    const hits = (await searchProject("702.15", ELM)).facts as Array<{ id: string; superseded?: boolean }>;
    expect(hits.find((fact) => fact.id === firstId)?.superseded).toBe(true);
    expect((await addProjectNote("MH-3 rim is 702.15", ELM, { source: "user" })).action).toBe("added");
  });

  describe("civil3d_drawing open", () => {
    let calls: string[];
    const series = (rows: Array<[string, string, string]>) => sql((db) => {
      db.exec("CREATE TABLE IF NOT EXISTS intel_series (series_id TEXT, key TEXT, kind TEXT, position INTEGER, file_id TEXT, path TEXT, " +
        "modified TEXT, title_date TEXT, revision TEXT, latest INTEGER, note TEXT); DELETE FROM intel_series;");
      rows.forEach(([path, key, modified], index) => {
        db.prepare("INSERT INTO intel_series VALUES ('s-1', ?, 'cad', ?, ?, ?, ?, NULL, NULL, ?, NULL)").run(key, index + 1, `f${index}`, path, modified, index === rows.length - 1 ? 1 : 0);
        mkdirSync(join(job, path, ".."), { recursive: true });
        writeFileSync(join(job, path), "AC1032");
      });
    });

    beforeEach(() => {
      calls = [];
      setOpenDrawingHooksForTesting({ open: async () => [], plugin: async (method) => { calls.push(method); return {}; } });
    });

    it("another job's open drawing of the same name doesn't answer for this job", async () => {
      series([["CAD\\C-104.dwg", "c 104", "2026-02-01T10:00:00+00:00"]]);
      setOpenDrawingHooksForTesting({
        open: async () => [{ name: "C-104.dwg", path: "Q:\\Northside\\C-104.dwg", active: true }],
        plugin: async (method) => { calls.push(method); return {}; },
      });
      const result = await openDrawingByName({ name: "C-104", project: ELM });
      expect(result.opened).toBe(join(job, "CAD\\C-104.dwg"));
      expect(calls).toEqual(["openDrawing"]);
    });

    it("a version is newer on the drive only if it really was saved later", async () => {
      series([["Packages\\2026-01\\C-101.dwg", "c 101", "2026-01-10T10:00:00+00:00"], ["Packages\\2026-02\\C-101.dwg", "c 101", "2026-02-10T10:00:00+00:00"]]);
      const opened = join(job, "Packages\\2026-01\\C-101.dwg");
      const other = join(job, "Packages\\2026-02\\C-101.dwg");
      setOpenDrawingHooksForTesting({ open: async () => [{ name: "C-101.dwg", path: opened, active: true }], plugin: async () => ({}) });
      utimesSync(other, new Date("2026-02-10"), new Date("2026-02-10"));
      utimesSync(opened, new Date("2026-05-01"), new Date("2026-05-01"));   // the open drawing was saved since the snapshot
      expect(await openDrawingByName({ name: "C-101", project: ELM })).not.toHaveProperty("newerOnDrive");
      utimesSync(opened, new Date("2026-01-10"), new Date("2026-01-10"));
      expect(await openDrawingByName({ name: "C-101", project: ELM })).toHaveProperty("newerOnDrive");
      rmSync(other);
      expect(await openDrawingByName({ name: "C-101", project: ELM })).not.toHaveProperty("newerOnDrive");
    });

    it("the same name in different areas is asked about before anything opens; dated deliveries aren't", async () => {
      series([["Phase 1\\C-109.dwg", "c 109", "2026-02-01T10:00:00+00:00"], ["Phase 2\\Area B\\C-109.dwg", "c 109", "2026-03-01T10:00:00+00:00"]]);
      expect(await openDrawingByName({ name: "C-109", project: ELM })).toMatchObject({ opened: null, reason: "this name is in several folders" });
      expect(calls).toEqual([]);
      expect((await openDrawingByName({ name: "C-109", project: ELM, near: "phase 2 area b" })).opened).toBe(join(job, "Phase 2\\Area B\\C-109.dwg"));
      series([["Sent\\2026-01-10\\C-110.dwg", "c 110", "2026-01-10T10:00:00+00:00"], ["Sent\\2026-02-10\\C-110.dwg", "c 110", "2026-02-10T10:00:00+00:00"]]);
      expect((await openDrawingByName({ name: "C-110", project: ELM })).opened).toBe(join(job, "Sent\\2026-02-10\\C-110.dwg"));
    });

    it("a newer .dxf of the name doesn't replace the drawing", async () => {
      series([["CAD\\C-120.dwg", "c 120", "2026-01-10T10:00:00+00:00"], ["CAD\\C-120.dxf", "c 120", "2026-02-10T10:00:00+00:00"]]);
      expect((await openDrawingByName({ name: "C-120", project: ELM })).opened).toBe(join(job, "CAD\\C-120.dwg"));
    });
  });
});
