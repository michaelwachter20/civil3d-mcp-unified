import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { KNOWLEDGE_FOLDER_NAME, LEGACY_KNOWLEDGE_FOLDER_NAME, migrateLegacyKnowledgeFolder } from "../src/tools/knowledgeBase.js";

describe("knowledge base folder migration", () => {
  const dirs: string[] = [];
  const documents = () => { const d = mkdtempSync(path.join(os.tmpdir(), "c3dkb-")); dirs.push(d); return d; };
  afterEach(() => { for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true }); });

  it("moves the old folder to the new name with its contents", () => {
    const docs = documents();
    mkdirSync(path.join(docs, LEGACY_KNOWLEDGE_FOLDER_NAME, "scripts"), { recursive: true });
    writeFileSync(path.join(docs, LEGACY_KNOWLEDGE_FOLDER_NAME, "preferences.json"), "[]");
    const note = migrateLegacyKnowledgeFolder(docs);
    expect(note).toContain(KNOWLEDGE_FOLDER_NAME);
    expect(existsSync(path.join(docs, LEGACY_KNOWLEDGE_FOLDER_NAME))).toBe(false);
    expect(readFileSync(path.join(docs, KNOWLEDGE_FOLDER_NAME, "preferences.json"), "utf8")).toBe("[]");
    expect(existsSync(path.join(docs, KNOWLEDGE_FOLDER_NAME, "scripts"))).toBe(true);
  });

  it("leaves both folders alone when the new one already exists", () => {
    const docs = documents();
    mkdirSync(path.join(docs, LEGACY_KNOWLEDGE_FOLDER_NAME));
    mkdirSync(path.join(docs, KNOWLEDGE_FOLDER_NAME));
    expect(migrateLegacyKnowledgeFolder(docs)).toBeUndefined();
    expect(existsSync(path.join(docs, LEGACY_KNOWLEDGE_FOLDER_NAME))).toBe(true);
  });

  it("does nothing when there is no old folder", () => {
    const docs = documents();
    expect(migrateLegacyKnowledgeFolder(docs)).toBeUndefined();
    expect(existsSync(path.join(docs, KNOWLEDGE_FOLDER_NAME))).toBe(false);
  });
});
