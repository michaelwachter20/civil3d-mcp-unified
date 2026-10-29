import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os, { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";
import { checkGeneral, jobDataProblems, jobNames } from "../src/tools/generalKnowledge.js";
import { addPreference, lessonsFile, listPreferences, preferencesFile, recordLesson, setReleaseForTesting, updatePreference } from "../src/tools/knowledgeBase.js";
import { invalidateJobIndex } from "../src/tools/projectDb.js";
import { appendVersionSection, invalidateSkillCache, personalSkillsDirectory, writeSkill } from "../src/tools/skillsLibrary.js";

const FIXTURES = join(import.meta.dirname, "fixtures");

describe("no job data in Civil 3D's general know-how", () => {
  let root: string;
  const previous = process.env.PROJECT_KNOWLEDGE_DIR;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-general-"));
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    mkdirSync(join(root, "projects"));
    // The fixture job: "Elm Street Drainage", analysed from a folder named elm-street-drainage
    copyFileSync(join(FIXTURES, "fixture.db"), join(root, "projects", "elm-street-drainage.db"));
    // An old notes file not imported yet, with a linked folder
    writeFileSync(join(root, "projects", "oak-ridge.json"), JSON.stringify({ name: "Oak Ridge Estates", folders: ["P:\\Jobs\\2024-117 Oak Ridge\\Survey", "P:\\Jobs\\OR-Topo"] }));
    setReleaseForTesting("2027");
    invalidateSkillCache();
    invalidateJobIndex();
  });

  afterEach(() => {
    if (previous === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous;
    setReleaseForTesting(undefined);
    invalidateSkillCache();
    invalidateJobIndex();
    rmSync(root, { recursive: true, force: true });
  });

  it("knows the jobs by name, slug and folder, reading without changing anything", () => {
    const names = jobNames();
    expect(names).toEqual(expect.arrayContaining(["Elm Street Drainage", "elm-street-drainage", "Oak Ridge Estates", "oak-ridge", "OR-Topo"]));
    expect(names).not.toContain("Survey");   // too generic to name a job
    expect(existsSync(join(root, "projects", "oak-ridge.db"))).toBe(false);   // nothing imported or created
  });

  it("finds paths, profile paths, network paths, job names and drawing file names", () => {
    const problems = (text: string) => jobDataProblems(text, ["Elm Street Drainage"]);
    expect(problems("Templates are in P:\\Standards\\C3D")[0]).toMatch(/absolute path/);
    expect(problems('var folder = "P:\\\\Jobs\\\\Current";')[0]).toMatch(/absolute path/);   // escaped in C#
    expect(problems(`Saved to ${join(os.homedir(), "Desktop")}`)[0]).toMatch(/user profile/);
    expect(problems("Open \\\\fileserver\\projects\\job")[0]).toMatch(/network path/);
    expect(problems('var share = "\\\\\\\\fileserver\\\\projects";')[0]).toMatch(/network path/);   // escaped in C#
    expect(problems("On elm street drainage the crown is 2%")[0]).toMatch(/the job 'Elm Street Drainage'/);
    expect(problems("Copy the layers from C-104.dwg")[0]).toMatch(/drawing or job file name \(C-104\.dwg\)/);
    expect(problems("Import from the field (Elm St.vce)").join()).toMatch(/St\.vce/);

    expect(problems("Civil 3D is in C:\\Program Files\\Autodesk\\AutoCAD 2027")).toEqual([]);
    expect(problems("Save as <drawing>.dwg, list the folder's *.dwg files, or check the .dwg extension")).toEqual([]);
    expect(problems('var target = $"{name}.dwg"; var job = record.Job; doc.Name.EndsWith(".DWG")')).toEqual([]);
    expect(problems("Use the project folder, e.g. <project folder>\\Survey")).toEqual([]);
    expect(problems("Streets in the drainage area")).toEqual([]);
  });

  it("refuses preferences and lessons that name a job, and saves general ones", async () => {
    await expect(addPreference("On Elm Street Drainage plot at 1\" = 20'.")).rejects.toThrow(/Not saved: this preference .*the job 'Elm Street Drainage'.*civil3d_project_notes/);
    expect(existsSync(preferencesFile())).toBe(false);
    const added = await addPreference("Plot at 1\" = 30'.");
    await expect(updatePreference(added.preference.id, "Plot the OR-Topo sheets at 1\" = 20'.")).rejects.toThrow(/the job 'OR-Topo'/);
    expect(listPreferences().map((preference) => preference.rule)).toEqual(["Plot at 1\" = 30'."]);

    await expect(recordLesson("Points from P:\\Jobs\\OR-Topo\\pts.csv import in PNEZD order.", ["points"], "2027")).rejects.toThrow(/Not saved: this lesson/);
    await expect(recordLesson("Import PNEZD points with the default format.", ["oak-ridge"], "2027")).rejects.toThrow(/oak-ridge/);   // topics too
    expect(existsSync(lessonsFile())).toBe(false);
    expect((await recordLesson("CogoPoint.LabelLocation can't be set before the label exists.", ["CogoPoint.LabelLocation"], "2027")).action).toBe("recorded");
  });

  it("refuses skills and skill sections with job data, in any field", () => {
    const skill = { name: "label_structures", category: "pipes", description: "Label structures MH-n.", content: "Run the script." };
    expect(() => writeSkill({ ...skill, content: "Open C-104.dwg first." }, "2027")).toThrow(/Not saved: this skill .*C-104\.dwg/);
    expect(() => writeSkill({ ...skill, description: "Labels for Elm Street Drainage." }, "2027")).toThrow(/Elm Street Drainage/);
    expect(() => writeSkill({ ...skill, parameters: [{ name: "folder", type: "string", default: "P:\\Jobs\\Current" }] } as never, "2027")).toThrow(/absolute path/);
    expect(existsSync(personalSkillsDirectory())).toBe(false);
    expect(writeSkill(skill, "2027").action).toBe("created");
    expect(() => appendVersionSection("label_structures", "On oak-ridge use MH-100.", "2028")).toThrow(/Not saved: this skill section/);
  });

  it("refuses to send a script with job data to Civil 3D", async () => {
    const save = SCRIPTING_DOMAIN_DEFINITION.actions.script_save;
    await expect(save.execute({
      action: "script_save", name: "import_points", description: "Imports the crew's points.", mode: "write",
      code: 'var file = @"P:\\Jobs\\2024-117 Oak Ridge\\Survey\\pts.csv";',
    } as never, {} as never)).rejects.toThrow(/Not saved: this script .*absolute path/);
    expect(() => checkGeneral("script", "import_points", "Imports points.", "var file = Args.Get(\"file\");", [{ name: "file", type: "string" }])).not.toThrow();
  });
});
