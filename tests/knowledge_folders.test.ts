import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  EARLIER_SHARED_FOLDER_NAMES, knowHowRoot, knowledgeStatus, lessonsFile, listPreferences, moveKnowHowIntoOwnFolder,
  preferencesFile, SHARED_FOLDER_NAME, sharedRoot,
} from "../src/tools/knowledgeBase.js";
import { practicesFile } from "../src/tools/practices.js";
import { databaseFileFor, projectsDirectory } from "../src/tools/projectDb.js";
import { invalidateSkillCache, personalSkillsDirectory } from "../src/tools/skillsLibrary.js";

// Every variable that picks a knowledge folder; each test starts with none of them set.
const VARIABLES = [
  "PROJECT_KNOWLEDGE_DIR", "CIVIL3D_KNOWLEDGE_DIR", "CIVIL3D_MCP_KNOWLEDGE_DIR", "CIVIL3D_USER_SKILLS_DIR", "CIVIL3D_SCRIPTS_DIR",
  "USERPROFILE", "HOME",
];

const tree = (root: string): string[] =>
  readdirSync(root, { withFileTypes: true }).flatMap((entry) => {
    const name = path.join(root, entry.name);
    const relative = path.relative(base, name).split(path.sep).join("/");
    return entry.isDirectory() ? [`${relative}/`, ...tree(name)] : [relative];
  }).sort();
let base = "";

/** The know-how Civil 3D MCP kept at the top of the shared folder before 2.4.0. */
function oldKnowHow(shared: string) {
  mkdirSync(path.join(shared, "memory"), { recursive: true });
  mkdirSync(path.join(shared, "skills", "mine"), { recursive: true });
  mkdirSync(path.join(shared, "scripts"), { recursive: true });
  writeFileSync(path.join(shared, "preferences.json"), JSON.stringify({ preferences: [{ id: "pref-1", rule: "Old rule.", added: "2026-01-01T00:00:00.000Z" }] }));
  writeFileSync(path.join(shared, "memory", "lessons.jsonl"), "");
  writeFileSync(path.join(shared, "skills", "mine", "a.skill.md"), "skill");
  writeFileSync(path.join(shared, "scripts", "a.csx"), "script");
  writeFileSync(path.join(shared, "practices.json"), "{\"practices\": []}\n");
  mkdirSync(path.join(shared, "projects"));
  writeFileSync(path.join(shared, "projects", "job.db"), "db");
}

describe("the suite's shared folder and Civil 3D's own know-how folder", () => {
  const saved = new Map<string, string | undefined>();

  beforeEach(() => {
    base = mkdtempSync(path.join(os.tmpdir(), "c3d-knowledge-"));
    for (const variable of VARIABLES) {
      saved.set(variable, process.env[variable]);
      delete process.env[variable];
    }
    invalidateSkillCache();
  });

  afterEach(() => {
    for (const [variable, value] of saved) {
      if (value === undefined) delete process.env[variable]; else process.env[variable] = value;
    }
    invalidateSkillCache();
    rmSync(base, { recursive: true, force: true });
  });

  describe("which shared folder", () => {
    const documents = () => path.join(base, "Documents");

    it("is PROJECT_KNOWLEDGE_DIR first, then CIVIL3D_KNOWLEDGE_DIR (its earlier name); blank values don't count", () => {
      mkdirSync(path.join(documents(), SHARED_FOLDER_NAME), { recursive: true });
      process.env.CIVIL3D_KNOWLEDGE_DIR = path.join(base, "earlier-variable");
      expect(sharedRoot(documents())).toBe(path.join(base, "earlier-variable"));
      process.env.PROJECT_KNOWLEDGE_DIR = `  ${path.join(base, "configured")}  `;
      expect(sharedRoot(documents())).toBe(path.join(base, "configured"));
      process.env.PROJECT_KNOWLEDGE_DIR = "   ";
      expect(sharedRoot(documents())).toBe(path.join(base, "earlier-variable"));
    });

    it("is Documents\\Project Knowledge when it exists, even next to earlier folders", () => {
      for (const name of [SHARED_FOLDER_NAME, ...EARLIER_SHARED_FOLDER_NAMES]) mkdirSync(path.join(documents(), name), { recursive: true });
      expect(sharedRoot(documents())).toBe(path.join(documents(), "Project Knowledge"));
    });

    it("uses an earlier default that exists as it is: Civil3DMcp-KnowledgeBase, then Civil3DMcp", () => {
      for (const name of EARLIER_SHARED_FOLDER_NAMES) mkdirSync(path.join(documents(), name), { recursive: true });
      expect(sharedRoot(documents())).toBe(path.join(documents(), "Civil3DMcp-KnowledgeBase"));
      rmSync(path.join(documents(), "Civil3DMcp-KnowledgeBase"), { recursive: true });
      expect(sharedRoot(documents())).toBe(path.join(documents(), "Civil3DMcp"));
      expect(tree(base)).toEqual(["Documents/", "Documents/Civil3DMcp/"]);
    });

    it("is Documents\\Project Knowledge otherwise, and nothing is created, renamed or moved to find it", () => {
      mkdirSync(documents());
      writeFileSync(path.join(documents(), "Civil3DMcp-KnowledgeBase"), "a file, not a folder");
      expect(sharedRoot(documents())).toBe(path.join(documents(), "Project Knowledge"));
      expect(tree(base)).toEqual(["Documents/", "Documents/Civil3DMcp-KnowledgeBase"]);
    });

    it("defaults to the user's Documents folder", () => {
      process.env.USERPROFILE = base;
      process.env.HOME = base;
      expect(sharedRoot()).toBe(path.join(base, "Documents", "Project Knowledge"));
      expect(knowHowRoot()).toBe(path.join(base, "Documents", "Project Knowledge", "civil3d"));
    });
  });

  describe("moving the know-how into civil3d\\", () => {
    it("moves preferences, memory, skills and scripts once; practices and projects stay at the top", () => {
      const shared = path.join(base, "shared");
      oldKnowHow(shared);
      process.env.PROJECT_KNOWLEDGE_DIR = shared;

      expect(preferencesFile()).toBe(path.join(shared, "civil3d", "preferences.json"));
      expect(listPreferences().map((preference) => preference.rule)).toEqual(["Old rule."]);
      expect(lessonsFile()).toBe(path.join(shared, "civil3d", "memory", "lessons.jsonl"));
      expect(personalSkillsDirectory()).toBe(path.join(shared, "civil3d", "skills"));
      expect(practicesFile()).toBe(path.join(shared, "practices.json"));
      expect(projectsDirectory()).toBe(path.join(shared, "projects"));
      expect(databaseFileFor("Job")).toBe(path.join(shared, "projects", "job.db"));
      expect(tree(shared).map((entry) => entry.replace(/^shared\//, ""))).toEqual([
        "civil3d/", "civil3d/memory/", "civil3d/memory/lessons.jsonl", "civil3d/preferences.json", "civil3d/scripts/",
        "civil3d/scripts/a.csx", "civil3d/skills/", "civil3d/skills/mine/", "civil3d/skills/mine/a.skill.md",
        "practices.json", "projects/", "projects/job.db",
      ]);
      expect(readFileSync(path.join(shared, "civil3d", "scripts", "a.csx"), "utf8")).toBe("script");
      expect(knowledgeStatus()).toEqual({ sharedFolder: shared, civil3dFolder: path.join(shared, "civil3d") });
    });

    it("moves inside an earlier default folder, which keeps its name", () => {
      process.env.USERPROFILE = base;
      process.env.HOME = base;
      const earlier = path.join(base, "Documents", "Civil3DMcp-KnowledgeBase");
      oldKnowHow(earlier);
      expect(preferencesFile()).toBe(path.join(earlier, "civil3d", "preferences.json"));
      expect(existsSync(path.join(earlier, "civil3d", "scripts", "a.csx"))).toBe(true);
      expect(readdirSync(path.join(base, "Documents"))).toEqual(["Civil3DMcp-KnowledgeBase"]);
    });

    it("moves inside CIVIL3D_KNOWLEDGE_DIR, the shared folder's earlier variable", () => {
      const shared = path.join(base, "configured");
      oldKnowHow(shared);
      process.env.CIVIL3D_KNOWLEDGE_DIR = shared;
      expect(lessonsFile()).toBe(path.join(shared, "civil3d", "memory", "lessons.jsonl"));
      expect(existsSync(path.join(shared, "memory"))).toBe(false);
    });

    it("uses civil3d\\ when both exist, leaves the top-level item alone and says so", () => {
      const shared = path.join(base, "shared");
      oldKnowHow(shared);
      mkdirSync(path.join(shared, "civil3d"));
      writeFileSync(path.join(shared, "civil3d", "preferences.json"), JSON.stringify({ preferences: [{ id: "pref-1", rule: "New rule.", added: "2026-10-01T00:00:00.000Z" }] }));
      process.env.PROJECT_KNOWLEDGE_DIR = shared;

      expect(listPreferences().map((preference) => preference.rule)).toEqual(["New rule."]);
      expect(JSON.parse(readFileSync(path.join(shared, "preferences.json"), "utf8")).preferences[0].rule).toBe("Old rule.");
      expect(existsSync(path.join(shared, "civil3d", "skills", "mine", "a.skill.md"))).toBe(true);
      const status = knowledgeStatus();
      expect(status.notes).toHaveLength(1);
      expect(status.notes![0]).toMatch(/^preferences\.json also at the top of .*not used.*Nothing was deleted/);
    });

    it("uses an item from the top while it can't be moved, and moves it on a later start", () => {
      const shared = path.join(base, "shared");
      oldKnowHow(shared);
      writeFileSync(path.join(shared, "civil3d"), "a file where the folder should be, so nothing can be moved");
      process.env.PROJECT_KNOWLEDGE_DIR = shared;

      expect(preferencesFile()).toBe(path.join(shared, "preferences.json"));
      expect(listPreferences().map((preference) => preference.rule)).toEqual(["Old rule."]);
      expect(personalSkillsDirectory()).toBe(path.join(shared, "skills"));
      expect(knowledgeStatus().notes![0]).toMatch(/^Not moved into .* yet.*preferences\.json, memory, skills, scripts\.$/);

      rmSync(path.join(shared, "civil3d"));
      expect(moveKnowHowIntoOwnFolder(shared)).toEqual({ moved: ["preferences.json", "memory", "skills", "scripts"], notMoved: [] });
      expect(preferencesFile()).toBe(path.join(shared, "civil3d", "preferences.json"));
      expect(knowledgeStatus().notes).toBeUndefined();
    });

    it("moves nothing into CIVIL3D_MCP_KNOWLEDGE_DIR and reads only from there", () => {
      const shared = path.join(base, "shared");
      const own = path.join(base, "my-civil3d");
      oldKnowHow(shared);
      process.env.PROJECT_KNOWLEDGE_DIR = shared;
      process.env.CIVIL3D_MCP_KNOWLEDGE_DIR = own;

      expect(knowHowRoot()).toBe(own);
      expect(preferencesFile()).toBe(path.join(own, "preferences.json"));
      expect(listPreferences()).toEqual([]);
      expect(personalSkillsDirectory()).toBe(path.join(own, "skills"));
      expect(practicesFile()).toBe(path.join(shared, "practices.json"));
      expect(existsSync(own)).toBe(false);
      expect(existsSync(path.join(shared, "civil3d"))).toBe(false);
      expect(existsSync(path.join(shared, "preferences.json"))).toBe(true);
      expect(knowledgeStatus()).toEqual({ sharedFolder: shared, civil3dFolder: own });
    });

    it("leaves skills and scripts alone when CIVIL3D_USER_SKILLS_DIR or CIVIL3D_SCRIPTS_DIR points elsewhere", () => {
      const shared = path.join(base, "shared");
      oldKnowHow(shared);
      process.env.PROJECT_KNOWLEDGE_DIR = shared;
      process.env.CIVIL3D_USER_SKILLS_DIR = path.join(base, "team-skills");
      process.env.CIVIL3D_SCRIPTS_DIR = path.join(shared, "scripts");

      expect(personalSkillsDirectory()).toBe(path.join(base, "team-skills"));
      expect(preferencesFile()).toBe(path.join(shared, "civil3d", "preferences.json"));
      expect(existsSync(path.join(shared, "skills", "mine", "a.skill.md"))).toBe(true);
      expect(existsSync(path.join(shared, "scripts", "a.csx"))).toBe(true);
      expect(existsSync(path.join(shared, "civil3d", "skills"))).toBe(false);
      expect(existsSync(path.join(shared, "civil3d", "scripts"))).toBe(false);
      expect(knowledgeStatus().notes).toBeUndefined();
    });
  });
});
