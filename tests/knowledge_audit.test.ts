// Guards repo rule 1 for everything git tracks under skills/ (skills and lessons.seed.jsonl): runs the same job-data check
// as generalKnowledge.ts against the job names found in this PC's shared projects folder (read only; none on a build
// server). It asks git which files are tracked: git on PATH, else the copy GitHub Desktop bundles (as
// scripts/publish-release.mjs finds it). Without git it is skipped with a message, except on CI (CI set), where it fails.

import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
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

/** git: PATH first, then the newest copy bundled with GitHub Desktop; undefined when there is none. */
function findGit(): string | undefined {
  if (!spawnSync("git", ["--version"]).error) return "git";
  const desktop = path.join(process.env.LOCALAPPDATA ?? "", "GitHubDesktop");
  if (!existsSync(desktop)) return undefined;
  for (const app of readdirSync(desktop).filter((name) => name.startsWith("app-")).sort().reverse()) {
    const candidate = path.join(desktop, app, "resources", "app", "git", "cmd", "git.exe");
    if (existsSync(candidate)) return candidate;
  }
  return undefined;
}

const git = findGit();
if (!git && process.env.CI) throw new Error("knowledge_audit needs git, and none was found on this CI runner.");
if (!git) console.warn("knowledge_audit skipped: git wasn't found (not on PATH, no GitHub Desktop copy). Install Git to run it.");

describe("shipped know-how is general", () => {
  it.skipIf(!git)("names no job, path, profile folder or drawing file", () => {
    const tracked = execFileSync(git!, ["ls-files", "-z", "skills"], { cwd: repository, encoding: "utf8" }).split("\0").filter(Boolean);
    expect(tracked.length).toBeGreaterThan(0);
    const names = jobNames(path.join(sharedRoot(), "projects"));
    const hits = tracked.flatMap((file) =>
      readFileSync(path.join(repository, file), "utf8").replace(/^﻿/, "").split(/\r?\n/)
        .flatMap((line, index) => jobDataProblems(line, names).map((problem) => `${file}:${index + 1}: ${problem}`)));
    expect(hits, "generalize these, or move job facts to civil3d_project_notes").toEqual([]);
  });
});
