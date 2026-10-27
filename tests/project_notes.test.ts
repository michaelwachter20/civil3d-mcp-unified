import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import type { DatabaseSync } from "node:sqlite";
import {
  addProjectNote, linkProjectFolder, listProjectFiles, listProjectNotes, listProjects, locatePath, projectEntity, projectFileDetails,
  projectForDrawing, projectNotesOnce, projectOverview, projectTimeline, removeProjectNote, resetProjectNotesAnnouncements, searchProject,
  setActiveDrawingResolverForTesting, unlinkProjectFolder, updateProjectNote,
} from "../src/tools/projectNotes.js";
import { ftsQuery, invalidateJobIndex, jobIndex, pathKey, slug } from "../src/tools/projectDb.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";

const { DatabaseSync: Database } = createRequire(import.meta.url)("node:sqlite") as typeof import("node:sqlite");
const FIXTURES = join(import.meta.dirname, "fixtures");
// The fixture job (made up): source_root C:\Jobs\Elm Street, folders ...\Drawings (civil3d) and ...\TBC (tbc).
const ELM = "Elm Street Drainage";
const ELM_DRAWING = "C:\\Jobs\\Elm Street\\Drawings\\C-104.dwg";

describe("project notes on the suite's project database", () => {
  let root: string;
  let drawing: string | undefined;
  const previous = process.env.CIVIL3D_KNOWLEDGE_DIR;
  const projects = () => join(root, "projects");
  const open = (file = join(projects(), "elm-street-drainage.db")): DatabaseSync => new Database(file);
  const query = <T>(sql: string, ...params: Array<string | number>) => {
    const db = open();
    try { return db.prepare(sql).all(...params) as unknown as T[]; } finally { db.close(); }
  };

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-projects-"));
    process.env.CIVIL3D_KNOWLEDGE_DIR = root;
    mkdirSync(projects(), { recursive: true });
    copyFileSync(join(FIXTURES, "fixture.db"), join(projects(), "elm-street-drainage.db"));
    drawing = undefined;
    setActiveDrawingResolverForTesting(async () => drawing);
    invalidateJobIndex();
    resetProjectNotesAnnouncements();
  });

  afterEach(() => {
    setActiveDrawingResolverForTesting(undefined);
    invalidateJobIndex();
    if (previous === undefined) delete process.env.CIVIL3D_KNOWLEDGE_DIR; else process.env.CIVIL3D_KNOWLEDGE_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  });

  it("finds a drawing's job by source_root and folders, deepest folder wins, case-insensitive", async () => {
    expect(projectForDrawing("c:\\jobs\\elm street\\Documents\\x.dwg")?.name).toBe(ELM);
    expect(projectForDrawing(ELM_DRAWING)?.name).toBe(ELM);
    await linkProjectFolder("Elm Phase 2", "C:\\Jobs\\Elm Street\\Drawings\\Phase 2");
    expect(projectForDrawing("C:\\JOBS\\Elm Street\\Drawings\\phase 2\\P2.dwg")?.name).toBe("Elm Phase 2");
    expect(projectForDrawing(ELM_DRAWING)?.name).toBe(ELM);
    expect(projectForDrawing("C:\\Jobs\\Elm Streetcar\\x.dwg")).toBeUndefined();
    expect(listProjects().map((project) => project.name)).toEqual(["Elm Phase 2", ELM]);
  });

  it("creates a new job database by the contract when linking", async () => {
    const folder = join(root, "jobs", "site-a");
    const linked = await linkProjectFolder("Site A", folder);
    expect(linked).toMatchObject({ action: "created", project: "Site A", path: join(projects(), "site-a.db") });
    expect(existsSync(linked.filesFolder)).toBe(true);
    const db = open(linked.path);
    try {
      const meta = Object.fromEntries((db.prepare("SELECT key, value FROM meta").all() as Array<{ key: string; value: string }>).map((row) => [row.key, row.value]));
      expect(meta.schema_version).toBe("1");
      expect(meta.project_name).toBe("Site A");
      expect(meta.project_id).toMatch(/^[0-9a-f]{12}$/);
      expect(meta.created).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
      expect((db.prepare("PRAGMA journal_mode").get() as { journal_mode: string }).journal_mode).toBe("wal");
      expect(db.prepare("SELECT path, tool FROM folders").all()).toEqual([{ path: folder, tool: "civil3d" }]);
    } finally {
      db.close();
    }
    expect(projectForDrawing(join(folder, "plan.dwg"))?.name).toBe("Site A");
    expect((await linkProjectFolder("site a", folder)).action).toBe("unchanged");
    expect((await linkProjectFolder("Site A", join(root, "jobs", "site-a-2"))).folders).toHaveLength(2);
  });

  it("links the active drawing's folder by default and unlinks only its own folders", async () => {
    drawing = "C:\\Work\\Elm extra\\Extra.dwg";
    expect((await linkProjectFolder(ELM)).action).toBe("linked");
    expect(projectForDrawing(drawing)?.name).toBe(ELM);
    expect(unlinkProjectFolder(ELM, "c:\\work\\elm extra\\").folders).not.toContain("C:\\Work\\Elm extra");
    expect(projectForDrawing(drawing)).toBeUndefined();
    expect(() => unlinkProjectFolder(ELM, "C:\\Jobs\\Elm Street\\TBC")).toThrow(/linked by tbc/);
    expect(() => unlinkProjectFolder(ELM, "C:\\Jobs\\Elm Street")).toThrow(/source_root/);
    expect(() => unlinkProjectFolder(ELM, "C:\\Elsewhere")).toThrow(/isn't linked/);
    expect(unlinkProjectFolder(ELM, "C:\\Jobs\\Elm Street\\Drawings").action).toBe("unlinked");
  });

  it("adds notes as civil3d facts with the next fact id, a drawing source and duplicate checks", async () => {
    drawing = ELM_DRAWING;
    const added = await addProjectNote("  The go-by drawing is\nC-103.  ", undefined, { topics: ["sheet:C-103"] });
    expect(added).toMatchObject({ action: "added", project: ELM, note: { id: "fact-7", fact: "The go-by drawing is C-103.", tool: "civil3d", topics: ["sheet:C-103"] } });
    expect(added.note.sources).toEqual([{ file: "drawing:C:\\Jobs\\Elm Street\\Drawings\\C-104.dwg", path: "C-104.dwg" }]);

    // Same text again: unchanged; from the user: the user is added as a second source of the own fact.
    expect((await addProjectNote("the go-by drawing is c-103.")).action).toBe("unchanged");
    const fromUser = await addProjectNote("The go-by drawing is C-103.", ELM, { source: "user" });
    expect(fromUser.action).toBe("source_added");
    expect(fromUser.note.sources).toHaveLength(2);

    // Another tool's fact is never touched: the duplicate is reported, not changed.
    expect(await addProjectNote("The contractor is Acme Paving Inc.", ELM)).toMatchObject({ action: "unchanged", note: { id: "fact-3", tool: "project-analyzer" } });
    expect(query<{ sources: string }>("SELECT sources FROM facts WHERE id = 'fact-3'")[0].sources).not.toContain("user");

    // A retired fact's id is never reused.
    expect((await addProjectNote("Point 102 is a control point.", ELM)).note.id).toBe("fact-8");
  });

  it("corrects another tool's fact with supersedes, and changes or retires only its own facts", async () => {
    const correction = await addProjectNote("The TBC project uses NAD83 Illinois West.", ELM, { supersedes: "fact-5" });
    expect(correction.note).toMatchObject({ supersedes: "fact-5", tool: "civil3d", sources: [{ file: "user", path: "said by the user" }] });
    await expect(addProjectNote("x", ELM, { supersedes: "fact-99" })).rejects.toThrow(/No fact 'fact-99'/);

    await expect(updateProjectNote("fact-5", "changed", ELM)).rejects.toThrow(/belongs to tbc/);
    await expect(removeProjectNote("fact-1", ELM)).rejects.toThrow(/belongs to project-analyzer/);
    await expect(updateProjectNote("fact-404", "changed", ELM)).rejects.toThrow(/No note 'fact-404'/);

    const updated = await updateProjectNote("fact-4", "Storm pipes are on layer C-STRM-PIPE.", ELM, { topics: ["layers", "storm"] });
    expect(updated.note).toMatchObject({ fact: "Storm pipes are on layer C-STRM-PIPE.", topics: ["layers", "storm"] });
    expect(updated.note.updated).toBeTruthy();

    const removed = await removeProjectNote("fact-4", ELM);
    expect(removed.action).toBe("retired");
    expect(query<{ retired: string | null }>("SELECT retired FROM facts WHERE id = 'fact-4'")[0].retired).toBeTruthy();
    await expect(updateProjectNote("fact-4", "again", ELM)).rejects.toThrow(/was removed/);
    expect((await listProjectNotes(ELM)).notes!.map((note) => note.id)).toEqual([correction.note.id]);
  });

  it("lists this tool's notes, the job's folders and the other tools' fact counts", async () => {
    drawing = ELM_DRAWING;
    const listed = await listProjectNotes();
    expect(listed).toMatchObject({ project: ELM, notes: [{ id: "fact-4", tool: "civil3d" }], otherFacts: { "project-analyzer": 3, tbc: 1 } });
    expect(listed.folders).toEqual([
      { path: "C:\\Jobs\\Elm Street", tool: "project-analyzer" },
      { path: "C:\\Jobs\\Elm Street\\Drawings", tool: "civil3d" },
      { path: "C:\\Jobs\\Elm Street\\TBC", tool: "tbc" },
    ]);
    drawing = "C:\\Elsewhere\\x.dwg";
    expect(await listProjectNotes()).toMatchObject({ project: null, activeDrawing: "x.dwg", knownProjects: [{ name: ELM }] });
    await expect(addProjectNote("x")).rejects.toThrow(/isn't in a known project folder/);
    await expect(addProjectNote("x", "Nope")).rejects.toThrow(/No project named/);
  });

  it("lists the job's reference files folder next to its database", async () => {
    const dir = join(projects(), "elm-street-drainage");
    mkdirSync(join(dir, "specs"), { recursive: true });
    writeFileSync(join(dir, "standards.pdf"), "pdf");
    writeFileSync(join(dir, "specs", "table.xlsx"), "xlsx");
    const listed = await listProjectFiles(ELM);
    expect(listed.filesFolder).toBe(dir);
    expect(listed.files.map((file) => file.path)).toEqual([join("specs", "table.xlsx"), "standards.pdf"]);
  });

  it("gives an overview built from meta, files, facts, questions, rules, checks, people and practices", async () => {
    copyFileSync(join(FIXTURES, "fixture-practices.json"), join(root, "practices.json"));
    const overview = await projectOverview(ELM);
    expect(overview).toMatchObject({
      project: ELM, sourceRoot: "C:\\Jobs\\Elm Street", copyStatus: "copy of P:\\Jobs\\1234 Elm St", originalRoots: ["P:\\Jobs\\1234 Elm St"],
      files: { total: 10, byStatus: { done: 8, duplicate: 1, failed: 1 } },
      openQuestions: [{ id: "q-1" }], rules: [{ id: "rule-1" }], highChecks: [{ id: "check-1" }],
      practices: [{ id: "practice-1", source: "civil3d" }, { id: "practice-2", source: "tbc" }],
    });
    // fact-3 supersedes fact-2; fact-6 is retired.
    expect(overview.keyFacts.map((fact) => fact.id)).toEqual(["fact-1", "fact-3", "fact-5"]);
    expect(overview.newestFacts.map((fact) => fact.id)).toEqual(["fact-5", "fact-4", "fact-3", "fact-2", "fact-1"]);
    expect(overview.people).toEqual([{ name: "John Smith", aliases: ["J. Smith"], files: 2 }, { name: "Robin Lee", files: 2 }]);
  });

  it("searches the compressed text (codes kept together) and the facts", async () => {
    expect(ftsQuery("MH-3 culvert")).toBe('"MH 3" "culvert"');
    expect(ftsQuery("12+50 C-104")).toBe('"12 50" "C 104"');
    const found = await searchProject("MH-3", ELM);
    expect(found.hits.length).toBeGreaterThan(0);
    expect(found.hits[0].snippet.toLowerCase()).toContain("mh-3");
    expect(found.hits.every((hit) => typeof hit.path === "string")).toBe(true);
    expect(found.facts.map((fact) => fact.id)).toEqual(["fact-1"]);
    expect((await searchProject("Acme", ELM)).facts.map((fact) => fact.id).sort()).toEqual(["fact-2", "fact-3"]);
    expect((await searchProject("zzzunknownword", ELM)).hits).toEqual([]);
    await expect(searchProject("--", ELM)).rejects.toThrow(/words or codes/);
  });

  it("shows one file: details, props, summary, references, citing facts and its text", async () => {
    const minutes = await projectFileDetails("documents/MINUTES.docx", ELM);
    expect(minutes.file).toMatchObject({ id: "0000000000000001", kind: "word", author: "J. Smith" });
    expect(minutes.props).toMatchObject({ "doc.title": "Drainage design review" });
    expect(minutes.summary?.summary).toMatch(/upsized to 36-inch RCP/);
    expect(minutes.facts.map((fact) => fact.id)).toEqual(["fact-1"]);
    expect(minutes.text).toMatch(/MH-3/);

    const sheet = await projectFileDetails("C-104.dwg", ELM);
    expect(sheet.refsFrom.map((ref) => ref.status)).toEqual(["found", "missing"]);
    expect(sheet.text).toMatch(/## Layout C-104/);
    // A duplicate shows the text of the file it duplicates; pages of a PDF come back in order.
    expect((await projectFileDetails("0000000000000009", ELM)).text).toBe(minutes.text);
    expect((await projectFileDetails("specs.pdf", ELM)).text.indexOf("## p.1")).toBeLessThan((await projectFileDetails("specs.pdf", ELM)).text.indexOf("## p.3"));
    await expect(projectFileDetails("nothing.pdf", ELM)).rejects.toThrow(/No file/);
  });

  it("looks up an entity by name or alias with its files, events, stories and facts", async () => {
    const smith = await projectEntity("j. smith", ELM);
    expect(smith.entity).toMatchObject({ id: "person:john-smith", name: "John Smith" });
    expect(smith.files!.map((file) => file.path).sort()).toEqual(["Documents\\minutes.docx", "Email\\RFI 12.msg"]);
    expect(smith.events!.map((event) => event.date)).toEqual(["2026-10-03", "2026-10-05"]);
    expect(smith.stories!.map((story) => story.id)).toEqual(["story-1"]);

    const manhole = await projectEntity("MH-3", ELM);
    expect(manhole.facts.map((fact) => fact.id)).toEqual(["fact-1"]);
    expect(manhole.events!.map((event) => event.kind)).toContain("photo");

    const unknown = await projectEntity("Smith", ELM);
    expect(unknown).toMatchObject({ entity: null, similar: ["John Smith"] });
  });

  it("puts events and dated facts on a timeline, optionally between two dates", async () => {
    const all = await projectTimeline(ELM);
    expect(all.timeline.map((entry) => `${entry.date} ${entry.kind}`)).toEqual([
      "2026-09-14 photo", "2026-10-03 meeting", "2026-10-03 fact", "2026-10-05 email", "2026-10-10 drawing",
    ]);
    expect((await projectTimeline(ELM, "2026-10", "2026-10-05")).timeline.map((entry) => entry.date)).toEqual(["2026-10-03", "2026-10-03", "2026-10-05"]);
    expect((await projectTimeline(ELM, undefined, "2026-09")).count).toBe(1);
    await expect(projectTimeline(ELM, "10/3/26")).rejects.toThrow(/YYYY/);
  });

  it("translates paths written in the job's files to the copy (refs, then reference_roots, else says so)", async () => {
    const xref = await locatePath("p:\\jobs\\1234 elm st\\cad\\topo.dwg", ELM);
    expect(xref).toMatchObject({ method: "refs", status: "found", relative: "Drawings\\Topo.dwg", path: "C:\\Jobs\\Elm Street\\Drawings\\Topo.dwg", writtenIn: ["Drawings\\C-104.dwg"] });
    expect(await locatePath("P:/Jobs/1234 Elm St/Images/aerial.jpg", ELM)).toMatchObject({ method: "refs", status: "missing", expected: "Images\\aerial.jpg" });
    expect(await locatePath("P:\\Jobs\\1234 Elm St\\Survey\\EG.xml", ELM)).toMatchObject({
      method: "reference_roots", relative: "Survey\\EG.xml", path: "C:\\Jobs\\Elm Street\\Survey\\EG.xml",
    });
    expect(await locatePath("Q:\\Other\\file.dwg", ELM)).toMatchObject({ method: null, path: null });
  });

  it("surfaces a short overview once per job on the first code result", async () => {
    drawing = ELM_DRAWING;
    const briefing = await projectNotesOnce();
    expect(briefing).toMatchObject({
      project: ELM, notes: 1, missingFiles: 1, highChecks: ["Image AERIAL of C-104.dwg is missing"],
    });
    expect(briefing!.keyFacts).toEqual([
      "[fact-1] The culvert at MH-3 is upsized to 36-inch RCP.",
      "[fact-3] The contractor is Acme Paving Inc.",
      "[fact-5] The TBC project uses the NAD83 Illinois East zone.",
    ]);
    expect(await projectNotesOnce()).toBeUndefined();
    drawing = "C:\\Elsewhere\\x.dwg";
    resetProjectNotesAnnouncements();
    expect(await projectNotesOnce()).toBeUndefined();
  });

  it("imports an old JSON notes file once, keeping each note's tool, times and folders", async () => {
    writeFileSync(join(projects(), "old-job.json"), `\uFEFF${JSON.stringify({
      name: "Old Job",
      folders: ["D:\\Jobs\\Old"],
      notes: [
        { id: "note-1", note: "The go-by is\r\nthe March submittal.", added: "2026-01-02T03:04:05.000Z", updated: "2026-02-01T00:00:00.000Z" },
        { id: "note-2", note: "Control is on NAD83 Illinois East.", added: "2026-01-03T00:00:00.000Z", source: "tbc" },
        { id: "note-3", note: "Plans dated 2026-01-15 are final.", added: "2026-01-04T00:00:00.000Z", source: "project-analyzer" },
        { id: "note-4", note: "   ", added: "2026-01-05T00:00:00.000Z" },
      ],
    })}`);
    expect(projectForDrawing("D:\\Jobs\\Old\\Sub\\x.dwg")?.name).toBe("Old Job");
    expect(existsSync(join(projects(), "old-job.json"))).toBe(false);
    expect(existsSync(join(projects(), "old-job.json.imported"))).toBe(true);
    const db = open(join(projects(), "old-job.db"));
    try {
      const rows = db.prepare("SELECT id, fact, tool, sources, origin, added, updated FROM facts ORDER BY id").all();
      expect(rows).toEqual([
        { id: "fact-1", fact: "The go-by is the March submittal.", tool: "civil3d", sources: '[{"file":"user","path":"project note"}]', origin: '{"note_id":"note-1"}', added: "2026-01-02T03:04:05.000Z", updated: "2026-02-01T00:00:00.000Z" },
        { id: "fact-2", fact: "Control is on NAD83 Illinois East.", tool: "tbc", sources: '[{"file":"user","path":"project note"}]', origin: '{"note_id":"note-2"}', added: "2026-01-03T00:00:00.000Z", updated: null },
        { id: "fact-3", fact: "Plans dated 2026-01-15 are final.", tool: "project-analyzer", sources: '[{"file":"user","path":"project note"}]', origin: '{"note_id":"note-3"}', added: "2026-01-04T00:00:00.000Z", updated: null },
      ]);
      expect(db.prepare("SELECT path, tool FROM folders").all()).toEqual([{ path: "D:\\Jobs\\Old", tool: "civil3d" }]);
      expect((db.prepare("SELECT value FROM meta WHERE key = 'json_notes_imported'").get() as { value: string }).value).toBeTruthy();
    } finally {
      db.close();
    }
    // Only its own imported note can be changed; the TBC and analyzer notes stay theirs.
    expect((await listProjectNotes("Old Job")).notes!.map((note) => note.id)).toEqual(["fact-1"]);
    await expect(removeProjectNote("fact-2", "Old Job")).rejects.toThrow(/belongs to tbc/);
    // Never imported twice, even when a copy of the JSON file comes back.
    writeFileSync(join(projects(), "old-job.json"), JSON.stringify({ name: "Old Job", notes: [{ id: "note-9", note: "Again." }] }));
    invalidateJobIndex();
    jobIndex(true);
    const again = open(join(projects(), "old-job.db"));
    try {
      expect((again.prepare("SELECT COUNT(*) AS n FROM facts").get() as { n: number }).n).toBe(3);
    } finally {
      again.close();
    }
  });

  it("imports a JSON file next to an existing database into it, after the facts already there", async () => {
    writeFileSync(join(projects(), "elm-street-drainage.json"), JSON.stringify({
      name: "Elm St", folders: ["C:\\Jobs\\Elm Street\\Drawings", "C:\\Jobs\\Elm Street\\Sheets"],
      notes: [{ id: "note-1", note: "Sheets are 24x36.", added: "2026-01-01T00:00:00.000Z" }],
    }));
    // Notes are listed by when they were added; the imported note keeps its 2026-01-01 date.
    expect((await listProjectNotes(ELM)).notes!.map((note) => [note.id, note.fact])).toEqual([
      ["fact-7", "Sheets are 24x36."], ["fact-4", "Storm pipes in this drawing are on layer C-STRM."],
    ]);
    expect(query<{ value: string }>("SELECT value FROM meta WHERE key = 'project_name'")[0].value).toBe(ELM);
    expect(projectForDrawing("C:\\Jobs\\Elm Street\\Sheets\\S1.dwg")?.name).toBe(ELM);
  });

  it("reads but never writes a database from a newer schema", async () => {
    const db = open();
    db.prepare("UPDATE meta SET value = '2' WHERE key = 'schema_version'").run();
    db.close();
    expect((await projectOverview(ELM)).project).toBe(ELM);
    await expect(addProjectNote("A note.", ELM)).rejects.toThrow(/newer suite tool/);
  });

  it("uses the same slug as the other suite tools", () => {
    expect(slug("Elm Street Drainage")).toBe("elm-street-drainage");
    expect(slug("  --Job #12 / Phase II-- ")).toBe("job-12-phase-ii");
    expect(slug("x".repeat(80))).toHaveLength(64);
    expect(pathKey("C:\\Jobs\\Elm Street\\")).toBe(pathKey("c:/jobs/elm street"));
  });

  it("asks for approval only to remove a note", () => {
    const risk = (action: string) => {
      const definition = SCRIPTING_DOMAIN_DEFINITION.actions[action];
      return hasApprovalRisk({ toolName: "civil3d_project_notes", action, capabilities: definition.capabilities, safeForRetry: definition.safeForRetry });
    };
    expect([
      "project_list", "project_link", "project_unlink", "project_add", "project_update", "project_files",
      "project_overview", "project_search", "project_file", "project_entity", "project_timeline", "project_locate",
    ].map(risk).every((value) => value === false)).toBe(true);
    expect(risk("project_remove")).toBe(true);
    const tool = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_project_notes")!;
    expect(tool.resolveAction({ action: "locate", path: "x" }).action).toBe("project_locate");
  });
});

describe("shipped project database schema", () => {
  it("matches project-analyzer's schema/project-db.sql when that repository is checked out (else skipped)", ({ skip }) => {
    // PROJECT_ANALYZER_DIR, else a project-analyzer folder next to this repository or next to any folder above it
    // (a git worktree of this repository lives a few levels below the main checkout).
    const candidates = process.env.PROJECT_ANALYZER_DIR ? [join(process.env.PROJECT_ANALYZER_DIR, "schema", "project-db.sql")] : [];
    for (let dir = join(import.meta.dirname, ".."); dirname(dir) !== dir; dir = dirname(dir)) {
      candidates.push(join(dirname(dir), "project-analyzer", "schema", "project-db.sql"));
    }
    const found = candidates.find((candidate) => existsSync(candidate));
    if (!found) skip("project-analyzer is not checked out next to this repository");
    const normalize = (text: string) => text.replace(/\r\n/g, "\n");
    expect(normalize(readFileSync(join(import.meta.dirname, "..", "src", "tools", "project-db.sql"), "utf8"))).toBe(normalize(readFileSync(found!, "utf8")));
  });
});
