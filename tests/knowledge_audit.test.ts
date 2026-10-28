import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { jobDataProblems, jobNames } from "../src/tools/generalKnowledge.js";
import { sharedRoot } from "../src/tools/knowledgeBase.js";

/**
 * Audit: no job data in the know-how shipped with the repository (suite contract: no job data in any tool's
 * repository). Every file under skills\ that git tracks (skills and lessons.seed.jsonl) is checked, line by line,
 * with the rules applied before anything is saved (generalKnowledge.ts), against the names of the jobs in this
 * computer's shared projects folder (read only; none on a build server). Run it before committing know-how.
 */
const repository = path.resolve(import.meta.dirname, "..");

describe("shipped know-how is general", () => {
  it("names no job, path, profile folder or drawing file", () => {
    const tracked = execFileSync("git", ["ls-files", "-z", "skills"], { cwd: repository, encoding: "utf8" }).split("\0").filter(Boolean);
    expect(tracked.length).toBeGreaterThan(0);
    const names = jobNames(path.join(sharedRoot(), "projects"));
    const hits = tracked.flatMap((file) =>
      readFileSync(path.join(repository, file), "utf8").replace(/^﻿/, "").split(/\r?\n/)
        .flatMap((line, index) => jobDataProblems(line, names).map((problem) => `${file}:${index + 1}: ${problem}`)));
    expect(hits, "generalize these, or move job facts to civil3d_project_notes").toEqual([]);
  });
});
