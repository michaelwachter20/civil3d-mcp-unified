import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  addPreference, allLessons, knowHowRoot, lessonsFile, listPreferences, preferencesFile, recordLesson, removeLesson, searchLessons,
  setReleaseForTesting, sweepKnowledgeTemporaryFiles,
} from "../src/tools/knowledgeBase.js";
import { addPractice, practicesFile } from "../src/tools/practices.js";
import { linkProjectFolder, listProjectFiles, readUserKnowledge, setActiveDrawingResolverForTesting } from "../src/tools/projectNotes.js";
import { invalidateJobIndex } from "../src/tools/projectDb.js";
import { keepBackup, setFileLockTimingForTesting, withFileLock, writeAtomically } from "../src/utils/atomicFiles.js";
import { longPath } from "../src/tools/userKnowledge.js";

// A process id no process has: Windows ids are multiples of 4, and this one is far above any in use.
const DEAD_PID = 99_999_997;

describe("knowledge files shared by several programs", () => {
  let root: string;
  const previous = { shared: process.env.PROJECT_KNOWLEDGE_DIR, own: process.env.CIVIL3D_MCP_KNOWLEDGE_DIR };

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-knowledge-files-"));
    process.env.PROJECT_KNOWLEDGE_DIR = root;
    delete process.env.CIVIL3D_MCP_KNOWLEDGE_DIR;
    setReleaseForTesting("2027");
    setFileLockTimingForTesting({ waitMs: 2_000, pollMs: 10 });
    invalidateJobIndex();
  });

  afterEach(() => {
    setFileLockTimingForTesting(undefined);
    setReleaseForTesting(undefined);
    setActiveDrawingResolverForTesting(undefined);
    invalidateJobIndex();
    if (previous.shared === undefined) delete process.env.PROJECT_KNOWLEDGE_DIR; else process.env.PROJECT_KNOWLEDGE_DIR = previous.shared;
    if (previous.own !== undefined) process.env.CIVIL3D_MCP_KNOWLEDGE_DIR = previous.own;
    rmSync(root, { recursive: true, force: true });
  });

  it("takes over a lock left by a process that is gone, or one older than a minute", async () => {
    const lock = `${preferencesFile()}.lock`;
    mkdirSync(knowHowRoot(), { recursive: true });
    writeFileSync(lock, `${DEAD_PID}\n`);
    expect((await addPreference("Plot at 1\" = 30'.")).action).toBe("added");
    expect(existsSync(lock)).toBe(false);

    writeFileSync(lock, `${process.pid}\n`);   // alive, but left two minutes ago
    const old = new Date(Date.now() - 120_000);
    utimesSync(lock, old, old);
    expect((await addPreference("Hide group is 'HIDDEN POINTS'.")).action).toBe("added");
    expect(listPreferences().map((preference) => preference.id)).toEqual(["pref-1", "pref-2"]);
  });

  it("waits for a lock that is held, then gives a clear error naming the lock file", async () => {
    setFileLockTimingForTesting({ waitMs: 150, pollMs: 10 });
    mkdirSync(knowHowRoot(), { recursive: true });
    writeFileSync(`${preferencesFile()}.lock`, `${process.pid}\n`);
    await expect(addPreference("Plot at 1\" = 30'.")).rejects.toThrow(/preferences\.json is being changed by another program .*preferences\.json\.lock/);
    expect(existsSync(preferencesFile())).toBe(false);
  });

  it("loses no lesson, preference or practice when several are added at once", async () => {
    const lessons = Array.from({ length: 12 }, (_, at) => `Lesson ${at}: Alignment.GetStationAtPoint wants a point on the alignment's plane.`);
    const recorded = await Promise.all(lessons.map((lesson) => recordLesson(lesson, ["Alignment"], "2027")));
    expect(new Set(recorded.map((result) => result.lesson.id)).size).toBe(12);
    expect(allLessons().filter((lesson) => lesson.source === "learned").map((lesson) => lesson.lesson).sort()).toEqual([...lessons].sort());

    await Promise.all(["Rule A for labels.", "Rule B for labels.", "Rule C for labels."].map((rule) => addPreference(rule)));
    expect(listPreferences().map((preference) => preference.id).sort()).toEqual(["pref-1", "pref-2", "pref-3"]);
    await Promise.all(["Minimum cover is 2 ft.", "Minimum slope is 0.5%.", "Label structures MH-n."].map((practice) => addPractice(practice)));
    expect(JSON.parse(readFileSync(practicesFile(), "utf8")).practices).toHaveLength(3);
    expect(readdirSync(join(knowHowRoot(), "memory")).filter((name) => name.endsWith(".lock") || name.endsWith(".tmp"))).toEqual([]);
  });

  it("skips a lessons line that isn't a lesson, says so, and keeps it in the file", async () => {
    mkdirSync(join(knowHowRoot(), "memory"), { recursive: true });
    const good = (id: string) => JSON.stringify({ id, lesson: `Lesson ${id} about FeatureLine elevations.`, topics: ["FeatureLine"], civil3d_release: "2027", added: "2026-10-01T00:00:00.000Z" });
    writeFileSync(lessonsFile(), `${good("lesson-1")}\n{"id": "lesson-2", "lesson": "cut off\n${good("lesson-3")}\n`);
    expect(allLessons().filter((lesson) => lesson.source === "learned").map((lesson) => lesson.id)).toEqual(["lesson-1", "lesson-3"]);
    expect(searchLessons("FeatureLine", "2027")).toMatchObject({ count: 2, note: expect.stringMatching(/1 line of .*lessons\.jsonl isn't a lesson/) });

    const recorded = await recordLesson("Profile views need a style before labels.", ["ProfileView"], "2027");
    expect(recorded).toMatchObject({ lesson: { id: "lesson-4" }, note: expect.stringMatching(/isn't a lesson/) });
    await removeLesson("lesson-1", "2027");
    expect(readFileSync(lessonsFile(), "utf8").split("\n").filter(Boolean)).toEqual([
      '{"id": "lesson-2", "lesson": "cut off', good("lesson-3"), expect.stringContaining('"id":"lesson-4"'),
    ]);
  });

  it("never rewrites a preferences file that doesn't parse, and keeps a copy of it", async () => {
    mkdirSync(knowHowRoot(), { recursive: true });
    writeFileSync(preferencesFile(), "{ \"preferences\": [ oops");
    expect(() => listPreferences()).toThrow(/preferences\.json doesn't parse .*isn't rewritten/);
    await expect(addPreference("Plot at 1\" = 30'.")).rejects.toThrow(/preferences\.json doesn't parse .*A copy of it is kept at .*preferences\.json\.unreadable-/);
    expect(readFileSync(preferencesFile(), "utf8")).toBe("{ \"preferences\": [ oops");
    const copy = readdirSync(knowHowRoot()).find((name) => name.startsWith("preferences.json.unreadable-"));
    expect(readFileSync(join(knowHowRoot(), copy!), "utf8")).toBe("{ \"preferences\": [ oops");
  });

  it("keeps the newest 3 earlier versions of preferences, lessons and practices before rewriting them", async () => {
    await addPreference("Plot at 1\" = 30'.");
    const backups = join(knowHowRoot(), "preferences.backups");
    expect(existsSync(backups)).toBe(false);   // nothing to keep before the first version
    mkdirSync(backups);
    for (const day of ["2026-01-01", "2026-01-02", "2026-01-03"]) writeFileSync(join(backups, `preferences ${day} 120000.json`), "{}");
    await addPreference("Hide group is 'HIDDEN POINTS'.");
    const kept = readdirSync(backups).sort();
    expect(kept).toHaveLength(3);
    expect(kept.slice(0, 2)).toEqual(["preferences 2026-01-02 120000.json", "preferences 2026-01-03 120000.json"]);
    expect(JSON.parse(readFileSync(join(backups, kept[2]), "utf8")).preferences).toHaveLength(1);   // the version before the change

    await recordLesson("Profile views need a style before labels.", ["ProfileView"], "2027");
    await recordLesson("Sample lines need an alignment.", ["SampleLine"], "2027");
    expect(readdirSync(join(knowHowRoot(), "memory", "lessons.backups"))).toHaveLength(1);
    await addPractice("Minimum cover is 2 ft.");
    await addPractice("Minimum slope is 0.5%.");
    expect(readdirSync(join(root, "practices.backups"))).toHaveLength(1);
    expect(keepBackup(join(root, "nothing-here.json"))).toBeUndefined();
  });

  it("leaves no temp file behind when a write fails, and sweeps old ones at startup", async () => {
    const target = join(root, "civil3d", "a-folder.json");
    mkdirSync(target, { recursive: true });   // a folder where the file should be: the rename fails
    expect(() => writeAtomically(target, "{}")).toThrow();
    expect(readdirSync(join(root, "civil3d")).filter((name) => name.endsWith(".tmp"))).toEqual([]);

    const old = join(knowHowRoot(), "memory", `.lessons.jsonl.1234.${Date.now() - 7_200_000}.tmp`);
    const fresh = join(knowHowRoot(), `.preferences.json.1234.${Date.now()}.tmp`);
    const practices = join(root, `.practices.json.1234.${Date.now() - 7_200_000}.tmp`);
    const other = join(knowHowRoot(), "notes.tmp");
    mkdirSync(join(knowHowRoot(), "memory"), { recursive: true });
    for (const file of [old, fresh, practices, other]) writeFileSync(file, "x");
    const hours = new Date(Date.now() - 7_200_000);
    for (const file of [old, practices, other]) utimesSync(file, hours, hours);
    expect(sweepKnowledgeTemporaryFiles().sort()).toEqual([old, practices].sort());
    expect([old, fresh, practices, other].map((file) => existsSync(file))).toEqual([false, true, false, true]);
  });

  it("runs locked work for one file at a time in this process too", async () => {
    const file = join(root, "counter.json");
    writeFileSync(file, "0");
    await Promise.all(Array.from({ length: 8 }, () => withFileLock(file, async () => {
      const value = Number(readFileSync(file, "utf8"));
      await new Promise((resolve) => setTimeout(resolve, 5));
      writeAtomically(file, String(value + 1));
    })));
    expect(readFileSync(file, "utf8")).toBe("8");
  });

  it("lists and reads User Knowledge documents whose paths are longer than Windows' usual limit", async () => {
    setActiveDrawingResolverForTesting(async () => undefined);
    const linked = await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    let folder = linked.userKnowledgeFolder;
    for (let at = 0; at < 5; at += 1) folder = join(folder, `Design manual chapter ${at} with a long descriptive folder name`);
    const relative = `${folder.slice(linked.userKnowledgeFolder.length + 1)}\\requirements.txt`;
    const file = join(folder, "requirements.txt");
    expect(file.length).toBeGreaterThan(260);
    mkdirSync(longPath(folder), { recursive: true });
    writeFileSync(longPath(file), "Culverts under the access road are 24-inch minimum.");
    if (process.platform === "win32") expect(longPath(file)).toBe(`\\\\?\\${file}`);

    const listed = await listProjectFiles("Site A");
    expect(listed.files.map((entry) => entry.path)).toEqual([relative]);
    expect(listed).not.toHaveProperty("unreadable");
    expect((await readUserKnowledge(relative, "Site A")).text).toBe("Culverts under the access road are 24-inch minimum.");
    expect((await readUserKnowledge("requirements.txt", "Site A")).source).toBe("direct");
  });
});
