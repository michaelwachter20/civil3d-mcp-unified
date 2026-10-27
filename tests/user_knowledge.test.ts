import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { deflateRawSync } from "node:zlib";
import {
  linkProjectFolder, listProjectFiles, listProjectNotes, projectNotesOnce, projectOverview, readUserKnowledge, resetProjectNotesAnnouncements,
  searchProject, setActiveDrawingResolverForTesting,
} from "../src/tools/projectNotes.js";
import { invalidateJobIndex, schemaSql } from "../src/tools/projectDb.js";
import { epochSeconds, extractText, readZip } from "../src/tools/userKnowledge.js";
import { buildServerInstructions } from "../src/serverInstructions.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";

const { DatabaseSync: Database } = createRequire(import.meta.url)("node:sqlite") as typeof import("node:sqlite");
const FIXTURES = join(import.meta.dirname, "fixtures");
const ELM = "Elm Street Drainage";
const ELM_DRAWING = "C:\\Jobs\\Elm Street\\Drawings\\C-104.dwg";
// The fixture's user-knowledge document: <user knowledge>\Guidelines\Proposal guide.pdf, 2048 bytes, modified then.
const GUIDE_MTIME = new Date("2026-10-20T12:00:00Z");

// ---------------------------------------------------------------- small Office files, built here

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (data: Buffer) => {
  let c = 0xffffffff;
  for (const byte of data) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

/** A zip file (deflated entries; `stored` names are kept uncompressed). */
function zip(entries: Record<string, string>, stored: string[] = []): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, content] of Object.entries(entries)) {
    const data = Buffer.from(content, "utf8");
    const method = stored.includes(name) ? 0 : 8;
    const packed = method ? deflateRawSync(data) : data;
    const nameBytes = Buffer.from(name, "utf8");
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x800, 6); local.writeUInt16LE(method, 8);
    local.writeUInt32LE(crc32(data), 14); local.writeUInt32LE(packed.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBytes.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(0x800, 8); central.writeUInt16LE(method, 10);
    central.writeUInt32LE(crc32(data), 16); central.writeUInt32LE(packed.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28); central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, packed);
    centrals.push(central, nameBytes);
    offset += 30 + nameBytes.length + packed.length;
  }
  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  const count = Object.keys(entries).length;
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(count, 8); end.writeUInt16LE(count, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}

const W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"';
const DOCX = zip({
  "[Content_Types].xml": "<Types/>",
  "word/document.xml": `<?xml version="1.0"?><w:document ${W}><w:body>` +
    "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Retaining walls over 4 ft need a geotechnical review &amp; a fence.</w:t></w:r></w:p>" +
    "<w:p><w:r><w:t xml:space=\"preserve\">Parking: </w:t></w:r><w:r><w:tab/><w:t>1 space per 300 sf</w:t><w:br/><w:t>of floor area</w:t></w:r></w:p>" +
    "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Use</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Setback</w:t></w:r></w:p></w:tc></w:tr>" +
    "<w:tr><w:tc><w:p><w:r><w:t>Retail</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>25 ft</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
    "</w:body></w:document>",
});

const R = 'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"';
const XLSX = zip({
  "xl/workbook.xml": `<workbook ${R}><sheets><sheet name="Setbacks" sheetId="1" r:id="rId1"/><sheet name="Notes &amp; misc" sheetId="2" r:id="rId2"/></sheets></workbook>`,
  "xl/_rels/workbook.xml.rels": "<Relationships><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
    "<Relationship Id=\"rId2\" Type=\"worksheet\" Target=\"/xl/worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>",
  "xl/sharedStrings.xml": "<sst><si><t>Zone</t></si><si><t>Front setback (ft)</t></si><si><r><t>R-</t></r><r><t>1</t></r><rPh><t>x</t></rPh></si></sst>",
  "xl/worksheets/sheet1.xml": "<worksheet><sheetData>" +
    "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
    "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>25</v></c></row>" +
    "<row r=\"3\"><c r=\"C3\" t=\"inlineStr\"><is><t>corner lots</t></is></c><c r=\"D3\" t=\"b\"><v>1</v></c></row>" +
    "<row r=\"4\"/></sheetData></worksheet>",
  "xl/worksheets/sheet2.xml": "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"str\"><f>A2</f><v>Detention sized for the 100-year storm</v></c></row></sheetData></worksheet>",
}, ["xl/worksheets/sheet2.xml"]);

const P = 'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"';
const slide = (lines: string[]) => `<p:sld ${P}><p:cSld><p:spTree><p:sp><p:txBody>${lines.map((line) => `<a:p><a:r><a:t>${line}</a:t></a:r></a:p>`).join("")}</p:txBody></p:sp></p:spTree></p:cSld></p:sld>`;
const PPTX = zip({
  // Slide 2's file comes first in the deck.
  "ppt/presentation.xml": `<p:presentation ${P} ${R}><p:sldIdLst><p:sldId id="256" r:id="rId3"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst></p:presentation>`,
  "ppt/_rels/presentation.xml.rels": "<Relationships><Relationship Id=\"rId2\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Target=\"slides/slide2.xml\"/></Relationships>",
  "ppt/slides/slide1.xml": slide(["Schedule", "Bids due 2026-11-15"]),
  "ppt/slides/slide2.xml": slide(["Kickoff", "Client wants permeable pavers"]),
});

// ---------------------------------------------------------------- tests

describe("user knowledge (the job's User Knowledge folder)", () => {
  let root: string;
  let drawing: string | undefined;
  const previous = process.env.PROJECT_KNOWLEDGE_DIR;
  const projects = () => join(root, "projects");
  const jobDir = () => join(projects(), "elm-street-drainage");
  const knowledge = () => join(jobDir(), "User Knowledge");
  const database = () => join(projects(), "elm-street-drainage.db");

  /** Puts a document in the User Knowledge folder (relative path with / separators). */
  const put = (relative: string, content: string | Buffer, modified?: Date) => {
    const file = join(knowledge(), ...relative.split("/"));
    mkdirSync(join(file, ".."), { recursive: true });
    writeFileSync(file, content);
    if (modified) utimesSync(file, modified, modified);
    return file;
  };
  /** The document the fixture database has read: same size and modified time. */
  const putGuide = (modified = GUIDE_MTIME) => put("Guidelines/Proposal guide.pdf", Buffer.concat([Buffer.from("%PDF-1.7\n"), Buffer.alloc(2048 - 9, 32)]), modified);

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-user-knowledge-"));
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    mkdirSync(projects(), { recursive: true });
    copyFileSync(join(FIXTURES, "fixture.db"), database());
    drawing = undefined;
    setActiveDrawingResolverForTesting(async () => drawing);
    invalidateJobIndex();
    resetProjectNotesAnnouncements();
  });

  afterEach(() => {
    setActiveDrawingResolverForTesting(undefined);
    invalidateJobIndex();
    if (previous === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  });

  it("creates the (empty) folder when the job is listed, overviewed, or its files are listed or read, and writes nothing else", async () => {
    for (const open of [() => listProjectNotes(ELM), () => projectOverview(ELM), () => listProjectFiles(ELM), () => readUserKnowledge("x.txt", ELM).catch(() => undefined)]) {
      rmSync(jobDir(), { recursive: true, force: true });
      await open();
      expect(existsSync(knowledge())).toBe(true);
      expect(readdirSync(knowledge())).toEqual([]);
      expect(readdirSync(jobDir())).toEqual(["User Knowledge"]);
    }
    expect((await listProjectNotes(ELM)).userKnowledgeFolder).toBe(knowledge());
    expect((await listProjectFiles(ELM)).hint).toMatch(/proposal guidelines/);
  });

  it("lists documents with readByAnalyzer (same size and whole-second time) and readableHere, and older files elsewhere", async () => {
    putGuide(new Date("2026-10-20T12:00:00.700Z"));   // the analyzer stores whole seconds
    put("notes.txt", "Use the county's 2024 detail sheets.");
    put("Client/standards.docx", DOCX);
    put("Client/~$standards.docx", "lock");
    put("Thumbs.db", "litter");
    // Before User Knowledge existed, reference files lay directly in <slug>\.
    writeFileSync(join(jobDir(), "old spec.pdf"), "pdf");
    mkdirSync(join(jobDir(), "specs"));
    writeFileSync(join(jobDir(), "specs", "table.xlsx"), "xlsx");

    const listed = await listProjectFiles(ELM);
    expect(listed.userKnowledgeFolder).toBe(knowledge());
    expect(listed.files.map((file) => [file.path, file.readByAnalyzer, file.readableHere])).toEqual([
      ["Client\\standards.docx", false, true],
      ["Guidelines\\Proposal guide.pdf", true, false],
      ["notes.txt", false, true],
    ]);
    expect(listed.files[1]).toMatchObject({ size: 2048, modified: "2026-10-20T12:00:00.700Z" });
    expect(listed.elsewhere!.map((file) => file.path)).toEqual(["old spec.pdf", "specs\\table.xlsx"]);
    expect(listed.elsewhereHint).toContain(knowledge());
    expect(listed.hint).toMatch(/follow it and name the document/);

    // Changed since the analyzer read it: no longer readByAnalyzer, and Civil 3D MCP can't read a PDF itself.
    putGuide(new Date("2026-10-21T08:00:00Z"));
    const changed = (await listProjectFiles(ELM)).files.find((file) => file.path.endsWith(".pdf"))!;
    expect(changed).toMatchObject({ readByAnalyzer: false, changedSinceAnalyzerRead: true, readableHere: false });
    expect((await listProjectFiles(ELM)).hint).toContain('python analyze.py knowledge --project "Elm Street Drainage"');
  });

  it("compares the analyzer's modified time in whole seconds, in the Z and +00:00 forms", async () => {
    expect(epochSeconds("2026-10-20T12:00:00+00:00")).toBe(GUIDE_MTIME.getTime() / 1000);
    expect(epochSeconds("2026-10-20T12:00:00Z")).toBe(GUIDE_MTIME.getTime() / 1000);
    expect(epochSeconds("2026-10-20T12:00:00.999999Z")).toBe(GUIDE_MTIME.getTime() / 1000);
    expect(epochSeconds("not a time")).toBeUndefined();
    putGuide();
    for (const stored of ["2026-10-20T12:00:00Z", "2026-10-20T12:00:00.000Z", "2026-10-20T12:00:00+00:00"]) {
      const db = new Database(database());
      db.prepare("UPDATE files SET mtime = ? WHERE id = '000000000000000b'").run(stored);
      db.close();
      expect((await listProjectFiles(ELM)).files[0].readByAnalyzer).toBe(true);
    }
  });

  it("reads a document Project Analyzer read from the database, in pieces", async () => {
    putGuide();
    const read = await readUserKnowledge("Guidelines\\Proposal guide.pdf", ELM);
    expect(read).toMatchObject({ source: "project-analyzer", readByAnalyzer: true, truncated: false, file: "Guidelines\\Proposal guide.pdf" });
    expect(read.text).toMatch(/^## p\.1\n\n# Proposal guide/);
    expect(read.text).toContain("alternatives");
    expect(read.text).toContain("## p.2");
    expect(read.text).toContain("setback");
    expect(read.hint).toMatch(/tell the user rather than choose/);

    // Other ways to name it: '/' separators, the database's prefix, the bare file name.
    for (const name of ["Guidelines/Proposal guide.pdf", "<user knowledge>\\Guidelines\\Proposal guide.pdf", "proposal guide.pdf"]) {
      expect((await readUserKnowledge(name, ELM)).text).toBe(read.text);
    }

    const first = await readUserKnowledge("Proposal guide.pdf", ELM, { maxChars: 40 });
    expect(first).toMatchObject({ truncated: true, nextOffset: 40, chars: read.chars });
    expect(first.text).toHaveLength(40);
    const rest = await readUserKnowledge("Proposal guide.pdf", ELM, { offset: 40 });
    expect(rest).toMatchObject({ truncated: false, offset: 40 });
    expect(`${first.text}${rest.text}`).toBe(read.text);
  });

  it("points to Project Analyzer for a PDF it hasn't read as it is now", async () => {
    const file = putGuide(new Date("2026-10-22T00:00:00Z"));
    const read = await readUserKnowledge("Guidelines/Proposal guide.pdf", ELM);
    expect(read).toMatchObject({ text: null, readByAnalyzer: false, readableHere: false, path: file });
    expect(read.hint).toContain("It changed since Project Analyzer read it.");
    expect(read.hint).toContain('python analyze.py knowledge --project "Elm Street Drainage"');
    expect(read.hint).toContain(file);
    const scan = put("Scans/plat.tif", "II*");
    expect((await readUserKnowledge("Scans/plat.tif", ELM)).hint).not.toContain("changed since");
    expect((await readUserKnowledge("Scans/plat.tif", ELM)).path).toBe(scan);
  });

  it("reads plain text, Word, Excel and PowerPoint documents directly", async () => {
    put("notes.md", "\uFEFF# Notes\n\nUse the county's 2024 detail sheets.");
    put("legacy.txt", Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from("UTF-16 text", "utf16le")]));
    put("page.html", "<html><style>p{}</style><body><p>Max slope 3:1</p><p>Fence &amp; gate</p></body></html>");
    put("standards.docx", DOCX);
    put("tables.xlsx", XLSX);
    put("kickoff.pptx", PPTX);

    expect(await readUserKnowledge("notes.md", ELM)).toMatchObject({ source: "direct", readByAnalyzer: false, text: "# Notes\n\nUse the county's 2024 detail sheets." });
    expect((await readUserKnowledge("legacy.txt", ELM)).text).toBe("UTF-16 text");
    expect((await readUserKnowledge("page.html", ELM)).text).toBe("Max slope 3:1\nFence & gate");

    expect((await readUserKnowledge("standards.docx", ELM)).text).toBe(
      "Retaining walls over 4 ft need a geotechnical review & a fence.\nParking: \t1 space per 300 sf\nof floor area\nUse\tSetback\nRetail\t25 ft",
    );
    expect((await readUserKnowledge("tables.xlsx", ELM)).text).toBe(
      "## Setbacks\n\nZone\tFront setback (ft)\nR-1\t25\n\t\tcorner lots\tTRUE\n\n## Notes & misc\n\nDetention sized for the 100-year storm",
    );
    expect((await readUserKnowledge("kickoff.pptx", ELM)).text).toBe(
      "## Slide 1\n\nKickoff\nClient wants permeable pavers\n\n## Slide 2\n\nSchedule\nBids due 2026-11-15",
    );
  });

  it("reports a damaged Office file instead of failing", async () => {
    put("broken.docx", "this is not a zip file");
    const read = await readUserKnowledge("broken.docx", ELM);
    expect(read).toMatchObject({ text: null, readableHere: true });
    expect(read.error).toMatch(/not a zip file/);
    put("empty.xlsx", zip({ "docProps/app.xml": "<x/>" }));
    expect((await readUserKnowledge("empty.xlsx", ELM)).error).toMatch(/not an Excel workbook/);
    expect(() => readZip(Buffer.alloc(10))).toThrow(/not a zip file/);
    expect(extractText(put("drawing.dwg", "AC1032"))).toBeUndefined();
  });

  it("refuses paths outside the User Knowledge folder", async () => {
    put("inside.txt", "ok");
    writeFileSync(join(jobDir(), "old spec.txt"), "older version's file");
    for (const outside of [
      "..\\old spec.txt", "../old spec.txt", "..\\..\\elm-street-drainage.db", "Guidelines/../../old spec.txt", database(),
      join(root, "practices.json"), ".", "",
    ]) {
      await expect(readUserKnowledge(outside, ELM)).rejects.toThrow(/isn't in the job's User Knowledge folder/);
    }
    await expect(readUserKnowledge(join(knowledge(), "inside.txt"), ELM)).resolves.toMatchObject({ text: "ok" });
    await expect(readUserKnowledge("missing.txt", ELM)).rejects.toThrow(/No document 'missing.txt'/);
    mkdirSync(join(knowledge(), "Sub"));
    await expect(readUserKnowledge("Sub", ELM)).rejects.toThrow(/is a folder/);
    put("a/same.txt", "1");
    put("b/same.txt", "2");
    await expect(readUserKnowledge("same.txt", ELM)).rejects.toThrow(/Several documents/);
  });

  it("searches user knowledge in the database and the documents it doesn't have yet", async () => {
    putGuide();
    put("Client/standards.docx", DOCX);
    put("tables.xlsx", XLSX);
    put("Scans/plat.pdf", "%PDF");

    const setback = await searchProject("setback", ELM);
    const fromDb = setback.hits.find((hit) => hit.file === "000000000000000b")!;
    expect(fromDb).toMatchObject({ path: "<user knowledge>\\Guidelines\\Proposal guide.pdf", part: "p.2", userKnowledge: true });
    expect(fromDb.snippet).toContain("20 ft setback");
    // Read directly: every word must be there (case-insensitive); the snippet comes from the part that has them all.
    expect(setback.hits.filter((hit) => hit.readDirectly).map((hit) => [hit.path, hit.part])).toEqual([
      ["<user knowledge>\\Client\\standards.docx", null], ["<user knowledge>\\tables.xlsx", "Setbacks"],
    ]);
    expect(setback.userKnowledgeNotSearched).toEqual(["Scans\\plat.pdf"]);
    expect(setback.hint).toMatch(/userKnowledge/);

    const walls = await searchProject("RETAINING geotechnical", ELM);
    expect(walls.hits).toEqual([{
      file: null, path: "<user knowledge>\\Client\\standards.docx", part: null, userKnowledge: true, readDirectly: true,
      snippet: "Retaining walls over 4 ft need a geotechnical review & a fence. Parking: 1 space per 300 sf of floor area Use Setback Retail 25 ft",
    }]);
    expect((await searchProject("retaining zzzmissing", ELM)).hits).toEqual([]);
    expect((await searchProject("100-year", ELM)).hits.map((hit) => hit.part)).toEqual(["Notes & misc"]);

    // Job files are not user knowledge.
    const job = await searchProject("MH-3", ELM);
    expect(job.hits.length).toBeGreaterThan(0);
    expect(job.hits.some((hit) => hit.userKnowledge)).toBe(false);
    expect(job.hint).toBeUndefined();

    // A document the database has as it is now isn't read again from the file.
    expect((await searchProject("alternatives", ELM)).hits.map((hit) => hit.readDirectly ?? false)).toEqual([false]);
  });

  it("counts job files apart from user knowledge in the overview, and lists the documents", async () => {
    putGuide();
    put("notes.txt", "x");
    writeFileSync(join(jobDir(), "old spec.pdf"), "pdf");
    const overview = await projectOverview(ELM);
    expect(overview.files).toEqual({ total: 10, byStatus: { done: 8, duplicate: 1, failed: 1 } });
    expect(overview.userKnowledge).toMatchObject({
      folder: knowledge(), documents: 2, names: ["Guidelines\\Proposal guide.pdf", "notes.txt"], readByAnalyzer: 1, elsewhere: 1,
    });
    expect(overview.userKnowledge.hint).toMatch(/follow it and name the document; when it conflicts with a fact, a practice or the drawings, tell the user/);
    for (let index = 0; index < 35; index += 1) put(`many/doc-${String(index).padStart(2, "0")}.txt`, "x");
    expect((await projectOverview(ELM)).userKnowledge).toMatchObject({ documents: 37 });
    expect((await projectOverview(ELM)).userKnowledge.names).toHaveLength(30);
  });

  it("mentions user knowledge in the session briefing, the tool description and the server instructions", async () => {
    drawing = ELM_DRAWING;
    put("notes.txt", "x");
    const briefing = await projectNotesOnce();
    expect(briefing!.userKnowledge).toEqual({ documents: 1, names: ["notes.txt"] });
    expect(briefing!.hint).toMatch(/action=read/);
    const tool = SCRIPTING_DOMAIN_DEFINITION.exposures.find((exposure) => exposure.toolName === "civil3d_project_notes")!;
    expect(tool.description).toContain("User Knowledge");
    expect(buildServerInstructions()).toContain("User Knowledge folder");
  });

  it("works on its own: a job database it created, or one another tool made, with no analyzer data", async () => {
    // Created by link: an empty database.
    const linked = await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    const doc = join(linked.userKnowledgeFolder, "brief.docx");
    writeFileSync(doc, DOCX);
    expect((await listProjectFiles("Site A")).files).toEqual([
      { path: "brief.docx", size: statSync(doc).size, modified: statSync(doc).mtime.toISOString(), readByAnalyzer: false, readableHere: true },
    ]);
    expect((await readUserKnowledge("brief.docx", "Site A")).source).toBe("direct");
    expect((await searchProject("geotechnical", "Site A")).hits).toHaveLength(1);
    expect((await projectOverview("Site A"))).toMatchObject({ files: { total: 0 }, userKnowledge: { documents: 1, readByAnalyzer: 0 } });

    // Made by TBC MCP: schema, meta and a folder of its own, nothing else.
    const tbc = new Database(join(projects(), "tbc-job.db"));
    tbc.exec(schemaSql());
    tbc.prepare("INSERT INTO meta(key, value) VALUES ('schema_version', '1'), ('project_name', 'TBC Job')").run();
    tbc.prepare("INSERT INTO folders(path, tool, added) VALUES ('D:\\Survey\\TBC Job', 'tbc', '2026-10-27T00:00:00.000Z')").run();
    tbc.close();
    invalidateJobIndex();
    expect((await projectOverview("TBC Job"))).toMatchObject({ files: { total: 0 }, userKnowledge: { documents: 0, names: [] } });
    expect((await listProjectFiles("TBC Job")).files).toEqual([]);
    expect((await searchProject("anything", "TBC Job")).hits).toEqual([]);
    expect(existsSync(join(projects(), "tbc-job", "User Knowledge"))).toBe(true);
  });
});

describe("standalone", () => {
  it("never imports, requires or starts Project Analyzer or TBC MCP", () => {
    const sources: string[] = [];
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) walk(full);
        else if (/\.(ts|mts|js|mjs)$/.test(entry.name)) sources.push(full);
      }
    };
    walk(join(import.meta.dirname, "..", "src"));
    expect(sources.length).toBeGreaterThan(10);
    for (const file of sources) {
      const text = readFileSync(file, "utf8");
      for (const line of text.split("\n").filter((candidate) => /\bimport\b|\brequire\s*\(|\bspawn|\bexecFile|\bexecSync|\bfork\s*\(/.test(candidate))) {
        expect(line, file).not.toMatch(/project-analyzer|project_analyzer|analyze\.py|kb\.py|tbc-mcp|tbc_mcp/i);
      }
      expect(text, file).not.toMatch(/child_process/);
    }
  });
});
