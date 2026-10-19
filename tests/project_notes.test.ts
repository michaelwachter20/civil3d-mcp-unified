import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  addProjectNote, linkProjectFolder, listProjectNotes, listProjects, projectForDrawing, removeProjectNote, unlinkProjectFolder, updateProjectNote,
} from "../src/tools/projectNotes.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { SCRIPTING_DOMAIN_DEFINITION } from "../src/tools/domains/scriptingDomain.js";

describe("project notes (kept on this computer, matched by folder)", () => {
  let root: string;
  const previous = process.env.CIVIL3D_KNOWLEDGE_DIR;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "civil3d-projects-"));
    process.env.CIVIL3D_KNOWLEDGE_DIR = root;
  });

  afterEach(() => {
    if (previous === undefined) delete process.env.CIVIL3D_KNOWLEDGE_DIR; else process.env.CIVIL3D_KNOWLEDGE_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  });

  it("links folders to a project and finds it for drawings inside them (deepest folder wins)", async () => {
    const jobs = join(root, "jobs", "site-a");
    await linkProjectFolder("Site A", jobs);
    await linkProjectFolder("Site A sub", join(jobs, "phase 2"));
    expect(projectForDrawing(join(jobs, "plan.dwg"))?.name).toBe("Site A");
    expect(projectForDrawing(join(jobs, "phase 2", "x.dwg"))?.name).toBe("Site A sub");
    expect(projectForDrawing(join(root, "elsewhere", "y.dwg"))).toBeUndefined();
    expect(listProjects().map((project) => project.name).sort()).toEqual(["Site A", "Site A sub"]);
  });

  it("adds, updates and removes notes, and files them under the knowledge folder", async () => {
    await linkProjectFolder("Site A", join(root, "jobs", "site-a"));
    const added = await addProjectNote("The go-by drawing is the previous month's submittal.", "Site A");
    expect(added.action).toBe("added");
    expect(added.path).toContain(join(root, "projects"));
    expect((await addProjectNote("the go-by drawing is the previous month's submittal.", "Site A")).action).toBe("unchanged");
    await updateProjectNote(added.note.id, "Go-by: previous submittal.", "Site A");
    const listed = await listProjectNotes("Site A");
    expect(listed.notes).toEqual([expect.objectContaining({ id: "note-1", note: "Go-by: previous submittal." })]);
    await removeProjectNote("note-1", "Site A");
    expect((await listProjectNotes("Site A")).notes).toHaveLength(0);
  });

  it("adds more folders later and unlinks one without losing notes", async () => {
    const test = join(root, "testing");
    const real = join(root, "projects", "real job");
    await linkProjectFolder("Site A", test);
    await addProjectNote("A note.", "Site A");
    expect((await linkProjectFolder("Site A", real)).folders).toHaveLength(2);
    expect(projectForDrawing(join(real, "x.dwg"))?.name).toBe("Site A");
    expect(unlinkProjectFolder("Site A", test).folders).toHaveLength(1);
    expect(projectForDrawing(join(test, "x.dwg"))).toBeUndefined();
    expect((await listProjectNotes("Site A")).notes).toHaveLength(1);
    expect(() => unlinkProjectFolder("Site A", test)).toThrow(/isn't linked/);
  });

  it("rejects unknown projects", async () => {
    await expect(addProjectNote("x", "Nope")).rejects.toThrow(/No project named/);
  });

  it("asks for approval only to remove a note", () => {
    const risk = (action: string) => {
      const definition = SCRIPTING_DOMAIN_DEFINITION.actions[action];
      return hasApprovalRisk({ toolName: "civil3d_project_notes", action, capabilities: definition.capabilities, safeForRetry: definition.safeForRetry });
    };
    expect(["project_list", "project_link", "project_add", "project_update"].map(risk)).toEqual([false, false, false, false]);
    expect(risk("project_remove")).toBe(true);
  });
});
