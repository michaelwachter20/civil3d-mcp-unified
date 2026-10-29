import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { createHash } from "node:crypto";
import { closeSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, rmSync, truncateSync, writeFileSync, writeSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  addProjectNote, linkProjectFolder, listProjects, locatePath, projectChanges, projectChecks, projectEntity, projectFileDetails, projectLatest,
  projectOverview, projectTimeline, resetProjectNotesAnnouncements, searchProject, setActiveDrawingResolverForTesting,
} from "../src/tools/projectNotes.js";
import {
  BUSY_MESSAGE, forgetDamageForTesting, invalidateJobIndex, jobIndex, quickCheck, schemaIndexes, setProjectDbTimingForTesting,
} from "../src/tools/projectDb.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";

const { DatabaseSync: Database } = createRequire(import.meta.url)("node:sqlite") as typeof import("node:sqlite");
const FIXTURES = join(import.meta.dirname, "fixtures");
// The fixture job (made up): source_root C:\Jobs\Elm Street, written by Project Analyzer.
const ELM = "Elm Street Drainage";

const sha = (file: string) => createHash("sha256").update(readFileSync(file)).digest("hex");

/** Garbage (0xFF) over pages 4-11: tables a note doesn't touch, so the note's own pages read fine. */
function damagePages(file: string) {
  const fd = openSync(file, "r+");
  try {
    writeSync(fd, Buffer.alloc(4096 * 8, 0xff), 0, 4096 * 8, 4096 * 3);
  } finally {
    closeSync(fd);
  }
}

describe("opening the project database: read-only reads, busy, damaged, backups", () => {
  let root: string;
  const previous = { shared: process.env.PROJECT_KNOWLEDGE_DIR, own: process.env.CIVIL3D_MCP_KNOWLEDGE_DIR };
  const projects = () => join(root, "projects");
  const elm = () => join(projects(), "elm-street-drainage.db");
  const indexNames = (file: string) => {
    const db = new Database(file, { readOnly: true });
    try {
      return (db.prepare("SELECT name FROM sqlite_master WHERE type = 'index'").all() as Array<{ name: string }>).map((row) => row.name);
    } finally {
      db.close();
    }
  };

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-projectdb-"));
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    delete process.env.CIVIL3D_MCP_KNOWLEDGE_DIR;
    mkdirSync(projects(), { recursive: true });
    copyFileSync(join(FIXTURES, "fixture.db"), elm());
    setActiveDrawingResolverForTesting(async () => undefined);
    setProjectDbTimingForTesting({ busyTimeoutMs: 50, retryWaitMs: 50, retryTotalMs: 600 });
    invalidateJobIndex();
    resetProjectNotesAnnouncements();
  });

  afterEach(() => {
    setActiveDrawingResolverForTesting(undefined);
    setProjectDbTimingForTesting(undefined);
    invalidateJobIndex();
    forgetDamageForTesting();
    if (previous.shared === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous.shared;
    if (previous.own !== undefined) process.env.CIVIL3D_MCP_KNOWLEDGE_DIR = previous.own;
    rmSync(root, { recursive: true, force: true });
  });

  it("works installed alone: an empty shared folder, a job it links, a note, then every read action answers", async () => {
    rmSync(elm());
    const run = (action: string, args: Record<string, unknown> = {}) =>
      SCRIPTING_DOMAIN_DEFINITION.actions[`project_${action}`].execute({ action: `project_${action}`, project: "Site A", ...args }) as Promise<Record<string, unknown>>;
    expect(await run("link", { folder: join(root, "jobs", "site-a") })).toMatchObject({ action: "created" });
    expect(await run("add", { note: "Storm pipes are on layer C-STRM-PIPE." })).toMatchObject({ action: "added", note: { id: "fact-1" } });

    expect(await run("list")).toMatchObject({ project: "Site A", notes: [{ id: "fact-1" }] });
    expect(await run("overview")).toMatchObject({ project: "Site A", files: { total: 0 }, keyFacts: [], highChecks: [], people: [] });
    expect(await run("search", { query: "C-STRM-PIPE" })).toMatchObject({ hits: [], facts: [{ id: "fact-1" }] });
    expect(await run("entity", { entity: "John Smith" })).toMatchObject({ entity: null, hint: expect.stringMatching(/search/) });
    expect(await run("timeline")).toMatchObject({ count: 0, timeline: [] });
    expect(await run("locate", { path: "P:\\Jobs\\1234 Elm St\\CAD\\Topo.dwg" })).toMatchObject({ method: null, path: null });
    expect(await run("latest", { file: "C-101" })).toMatchObject({ series: [], hint: expect.stringMatching(/No file by that name/) });
    expect(await run("checks")).toMatchObject({ count: 0, checks: [], bySeverity: {}, hint: expect.stringMatching(/Project Analyzer makes them/) });
    expect(await run("changes")).toMatchObject({ count: { changes: 0, fileChanges: 0 }, changes: [], fileChanges: [] });
    expect(await run("files")).toMatchObject({ files: [], hint: expect.stringMatching(/Empty/) });
    // There is nothing to show for one file yet: a clear "not found", not a database error.
    await expect(run("file", { file: "C-104.dwg" })).rejects.toThrow(/No file 'C-104.dwg' in the project database/);
    await expect(run("read", { file: "brief.docx" })).rejects.toThrow(/No document 'brief.docx'/);
    await expect(run("changes", { file: "C-104.dwg" })).rejects.toThrow(/No file 'C-104.dwg'/);
  });

  it("opens the database read-only for every read action: the file isn't changed and no index is added", async () => {
    const db = new Database(elm());
    for (const { name } of schemaIndexes()) db.exec(`DROP INDEX IF EXISTS ${name}`);
    db.close();
    const before = sha(elm());
    const missing = indexNames(elm());
    expect(missing).not.toContain("intel_events_date");

    await projectOverview(ELM);
    await searchProject("MH-3", ELM);
    await projectFileDetails("C-104.dwg", ELM);
    await projectEntity("j. smith", ELM);
    await projectTimeline(ELM);
    await locatePath("P:\\Jobs\\1234 Elm St\\CAD\\Topo.dwg", ELM);
    await projectLatest("C-104", ELM);
    await projectChecks(ELM);
    await projectChanges(ELM);
    listProjects();
    expect(sha(elm())).toBe(before);
    expect(existsSync(`${elm()}-wal`)).toBe(false);
    expect(indexNames(elm())).toEqual(missing);

    // The first write adds the readers' indexes (the schema's CREATE INDEX IF NOT EXISTS lines) and sets WAL.
    await addProjectNote("Sheets are 24x36.", ELM, { source: "user" });
    expect(indexNames(elm())).toEqual(expect.arrayContaining(schemaIndexes().map((index) => index.name)));
    const after = new Database(elm(), { readOnly: true });
    try {
      expect((after.prepare("PRAGMA journal_mode").get() as { journal_mode: string }).journal_mode).toBe("wal");
    } finally {
      after.close();
    }
  });

  it("waits for a busy database without stopping the server, then says it is busy", async () => {
    await projectOverview(ELM);   // the job list knows the job before it is locked
    const holder = new Database(elm());
    holder.exec("BEGIN EXCLUSIVE");
    holder.prepare("UPDATE meta SET value = value WHERE key = 'project_name'").run();
    let ticks = 0;
    const timer = setInterval(() => { ticks += 1; }, 10);
    try {
      const started = Date.now();
      await expect(projectOverview(ELM)).rejects.toThrow(BUSY_MESSAGE);
      await expect(addProjectNote("A note.", ELM, { source: "user" })).rejects.toThrow(BUSY_MESSAGE);
      expect(Date.now() - started).toBeLessThan(5_000);
      expect(ticks).toBeGreaterThan(5);   // timers ran while it waited
    } finally {
      clearInterval(timer);
      holder.exec("ROLLBACK");
      holder.close();
    }
    expect((await projectOverview(ELM)).project).toBe(ELM);
  });

  it("goes on as soon as the other program lets go of the database", async () => {
    setProjectDbTimingForTesting({ busyTimeoutMs: 20, retryWaitMs: 50, retryTotalMs: 5_000 });
    await projectOverview(ELM);
    const holder = new Database(elm());
    holder.exec("BEGIN EXCLUSIVE");
    holder.prepare("UPDATE meta SET value = value WHERE key = 'project_name'").run();
    setTimeout(() => { holder.exec("COMMIT"); holder.close(); }, 200);
    expect((await addProjectNote("Pond 2 outlet is a 24-inch RCP.", ELM, { source: "user" })).action).toBe("added");
  });

  it("never writes or re-creates a damaged database: a clear error, the job still listed with a note", async () => {
    await projectOverview(ELM);   // known as Project Analyzer's job (source_root) before it is damaged
    const bytes = readFileSync(elm());
    bytes.write("this is not a database header", 0, "latin1");
    writeFileSync(elm(), bytes);
    const before = sha(elm());
    jobIndex(true);

    const listed = listProjects().find((project) => project.path === elm());
    expect(listed).toMatchObject({ name: ELM, folders: expect.arrayContaining(["C:\\Jobs\\Elm Street"]), note: expect.stringMatching(/damaged/) });
    await expect(projectOverview(ELM)).rejects.toThrow(/elm-street-drainage\.db is damaged .*python analyze\.py restore --project "Elm Street Drainage"/);
    await expect(addProjectNote("A note.", ELM, { source: "user" })).rejects.toThrow(/is damaged/);
    await expect(linkProjectFolder(ELM, "C:\\Jobs\\Elm Street\\More")).rejects.toThrow(/is damaged/);
    expect(sha(elm())).toBe(before);
    expect(readdirSync(projects()).filter((name) => name.startsWith("elm-street-drainage."))).toEqual(["elm-street-drainage.db"]);
  });

  it("refuses a write into a database a read found damaged, until a good copy is put back", async () => {
    damagePages(elm());
    const before = sha(elm());
    await expect(projectOverview(ELM)).rejects.toThrow(/is damaged/);
    // The note's own pages read fine, so SQLite alone wouldn't notice: the damage found by the read is remembered.
    await expect(addProjectNote("Sheets are 24x36.", ELM, { source: "user" })).rejects.toThrow(/elm-street-drainage\.db is damaged/);
    await expect(addProjectNote("Sheets are 24x36.", ELM, { source: "user" })).rejects.toThrow(/is damaged/);
    expect(sha(elm())).toBe(before);

    copyFileSync(join(FIXTURES, "fixture.db"), elm());   // a backup put back
    expect((await addProjectNote("Sheets are 24x36.", ELM, { source: "user" })).action).toBe("added");
  });

  it("refuses the day's first write when the backup copy of a database it made fails its check", async () => {
    await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    const file = join(projects(), "site-a.db");
    damagePages(file);
    const before = sha(file);
    // No read first: the write's daily backup is what finds the damage (as Project Analyzer's backups do).
    await expect(addProjectNote("Storm pipes are on layer C-STRM-PIPE.", "Site A", { source: "user" })).rejects.toThrow(/site-a\.db is damaged/);
    expect(sha(file)).toBe(before);
    expect(readdirSync(join(projects(), "site-a.backups"))).toEqual([]);
    await expect(addProjectNote("Storm pipes are on layer C-STRM-PIPE.", "Site A", { source: "user" })).rejects.toThrow(/is damaged/);
    expect(sha(file)).toBe(before);
  });

  it("says a database is damaged rather than answering with empty sections", async () => {
    const size = readFileSync(elm()).length;
    truncateSync(elm(), Math.floor(size / 3));
    const before = sha(elm());
    expect(quickCheck(elm())).not.toBe("ok");
    await expect(projectOverview(ELM)).rejects.toThrow(/is damaged/);
    expect(sha(elm())).toBe(before);
  });

  it("points to the newest backup for a database it made itself", async () => {
    await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    await addProjectNote("Storm pipes are on layer C-STRM-PIPE.", "Site A", { source: "user" });
    const file = join(projects(), "site-a.db");
    writeFileSync(file, "not a database at all, just text that is long enough to be read as a header by SQLite");
    jobIndex(true);
    await expect(projectOverview("Site A")).rejects.toThrow(/site-a\.db is damaged .*copy the newest backup in .*site-a\.backups \(site-a \d{4}-\d{2}-\d{2} \d{6}\.db\)/);
  });

  it("reads a newer schema's database and leaves it unchanged", async () => {
    const db = new Database(elm());
    db.prepare("UPDATE meta SET value = '2' WHERE key = 'schema_version'").run();
    db.close();
    const before = sha(elm());
    expect((await projectChecks(ELM)).count).toBe(2);
    await expect(addProjectNote("A note.", ELM, { source: "user" })).rejects.toThrow(/newer suite tool/);
    expect(sha(elm())).toBe(before);
  });

  it("backs up a database it made before the first write of each day, checked, the newest 3 kept; never Project Analyzer's", async () => {
    await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    const backups = join(projects(), "site-a.backups");
    expect(existsSync(backups)).toBe(false);   // nothing to keep of a database just made

    await addProjectNote("Storm pipes are on layer C-STRM-PIPE.", "Site A", { source: "user" });
    let copies = readdirSync(backups);
    expect(copies).toHaveLength(1);
    expect(copies[0]).toMatch(/^site-a \d{4}-\d{2}-\d{2} \d{6}\.db$/);
    expect(quickCheck(join(backups, copies[0]))).toBe("ok");
    await addProjectNote("Pond 2 outlet is a 24-inch RCP.", "Site A", { source: "user" });
    expect(readdirSync(backups)).toEqual(copies);   // once a day

    // Older days' copies: the newest 3 are kept.
    rmSync(join(backups, copies[0]));
    for (const day of ["2026-01-01", "2026-01-02", "2026-01-03"]) copyFileSync(join(projects(), "site-a.db"), join(backups, `site-a ${day} 120000.db`));
    writeFileSync(join(backups, "notes.txt"), "not a backup");
    await addProjectNote("Benchmark 3 is on the hydrant.", "Site A", { source: "user" });
    copies = readdirSync(backups).sort();
    expect(copies.filter((name) => name.endsWith(".db"))).toEqual(["site-a 2026-01-02 120000.db", "site-a 2026-01-03 120000.db", expect.stringMatching(/^site-a \d{4}-\d{2}-\d{2} \d{6}\.db$/)]);
    expect(copies).toContain("notes.txt");

    // Project Analyzer backs up its own databases.
    await addProjectNote("Sheets are 24x36.", ELM, { source: "user" });
    expect(existsSync(join(projects(), "elm-street-drainage.backups"))).toBe(false);
  });

  it("lists checks high first, filtered by severity or kind, with their files' paths and counts", async () => {
    const all = await projectChecks(ELM);
    expect(all).toMatchObject({ count: 2, bySeverity: { high: 1, medium: 1 }, byKind: { "missing file": 1, "no reply": 1 } });
    expect(all.checks).toEqual([
      { id: "check-1", severity: "high", kind: "missing file", title: "Image AERIAL of C-104.dwg is missing", detail: "Expected at Images\\aerial.jpg", files: ["Drawings\\C-104.dwg"] },
      { id: "check-2", severity: "medium", kind: "no reply", title: "RFI 12 has no reply", detail: "Asked 2026-10-05", files: ["Email\\RFI 12.msg"] },
    ]);
    expect((await projectChecks(ELM, { severity: "MEDIUM" })).checks.map((check) => check.id)).toEqual(["check-2"]);
    expect((await projectChecks(ELM, { kind: "missing file" })).checks.map((check) => check.id)).toEqual(["check-1"]);
    expect(await projectChecks(ELM, { limit: 1 })).toMatchObject({ count: 2, truncated: true, checks: [{ id: "check-1" }] });

    const db = new Database(elm());
    const many = Array.from({ length: 12 }, (_, at) => `f${at}`);
    db.prepare("INSERT INTO intel_checks(id, severity, kind, title, detail, refs, files) VALUES ('check-3', 'info', 'amounts', 'CO 4 total differs', NULL, '[]', ?)")
      .run(JSON.stringify(many));
    db.close();
    const info = (await projectChecks(ELM, { severity: "info" })).checks[0];
    expect(info).toMatchObject({ id: "check-3", files: many.slice(0, 10), moreFiles: 2 });
    expect(info).not.toHaveProperty("detail");
  });

  it("lists requirement and file changes newest first, since a date and for one file", async () => {
    // The default is the last 30 days; the fixture has none.
    expect(await projectChanges(ELM)).toMatchObject({ count: { changes: 0, fileChanges: 0 }, hint: expect.stringMatching(/pass since/) });
    const db = new Database(elm());
    const event = db.prepare("INSERT INTO intel_events(date, precision, kind, confidence, title, file_id, location) VALUES (?, 'day', 'change', ?, ?, ?, NULL)");
    event.run("2026-10-04", "high", "Going forward use 36-inch RCP at MH-3", "0000000000000001");
    event.run("2026-10-06", "medium", "Revised spec: outfall elevation per RFI 12", "0000000000000004");
    event.run("2025-12-01", "high", "No longer use the old title block", "0000000000000004");
    db.prepare("INSERT INTO file_changes(file_id, path, change, seen, mtime, old_path, summary) VALUES (?, ?, ?, ?, ?, ?, ?)")
      .run("0000000000000003", "Drawings\\C-104.dwg", "changed", "2026-10-11T08:00:00.000Z", "2026-10-10T17:00:00.000Z", null, "title block revision 1 -> 2");
    db.close();

    const recent = await projectChanges(ELM, { since: "2026-10" });
    expect(recent).toMatchObject({ since: "2026-10", count: { changes: 2, fileChanges: 1 } });
    expect(recent.changes).toEqual([
      { date: "2026-10-06", title: "Revised spec: outfall elevation per RFI 12", file: "Email\\RFI 12.msg", confidence: "medium" },
      { date: "2026-10-04", title: "Going forward use 36-inch RCP at MH-3", file: "Documents\\minutes.docx" },
    ]);
    expect(recent.fileChanges).toEqual([
      { seen: "2026-10-11T08:00:00.000Z", change: "changed", path: "Drawings\\C-104.dwg", modified: "2026-10-10T17:00:00.000Z", summary: "title block revision 1 -> 2" },
    ]);
    // One file: every change of it, unless a date is given too.
    expect((await projectChanges(ELM, { file: "RFI 12.msg" })).changes.map((change) => change.date)).toEqual(["2026-10-06", "2025-12-01"]);
    expect((await projectChanges(ELM, { file: "RFI 12.msg", since: "2026" })).changes.map((change) => change.date)).toEqual(["2026-10-06"]);
    expect(await projectChanges(ELM, { since: "2026", limit: 1 })).toMatchObject({ truncated: true, changes: [{ date: "2026-10-06" }] });
    await expect(projectChanges(ELM, { since: "10/3/26" })).rejects.toThrow(/YYYY/);
  });
});
