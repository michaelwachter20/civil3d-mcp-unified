// Fingerprint of the Civil 3D plugin source (C#, project and version files), line endings
// normalized so Windows and Linux checkouts agree. The prebuilt plugin records it, and the
// release workflow refuses to package a prebuilt plugin that doesn't match the source.
// Imported by scripts/prebuild-plugin.mjs (writes it to prebuilt/Civil3DMcpPlugin/BUILD-INFO.json) and
// scripts/package-release.mjs (compares with --prebuilt, as .github/workflows/release.yml does). Run directly
// (`node scripts/plugin-source-hash.mjs`) it prints the hash. Read-only.
// The whole text counts, comments included: any edit to a .cs/.csproj/.props/.targets file, even a comment, means
// `npm run plugin:prebuild` and committing prebuilt/ again before the next release.
import { createHash } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The repository root (the parent of scripts/); also used by prebuild-plugin.mjs. */
export const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const pluginRoot = path.join(repositoryRoot, "Civil3D-MCP-Plugin");

/** Every .cs/.csproj/.props/.targets file under `dir`, recursively, skipping bin/ and obj/ build output. */
async function sourceFiles(dir) {
  const files = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!["bin", "obj"].includes(entry.name.toLowerCase())) files.push(...await sourceFiles(full));
    } else if (/\.(cs|csproj|props|targets)$/i.test(entry.name)) {
      files.push(full);
    }
  }
  return files;
}

/**
 * SHA-256 (hex) over the plugin's source files in sorted forward-slash relative-path order: each file contributes its
 * path and its text with a leading BOM removed and CRLF turned into LF, so the result doesn't depend on the OS or
 * git's line-ending setting.
 */
export async function pluginSourceHash() {
  const hash = createHash("sha256");
  const files = (await sourceFiles(pluginRoot))
    .map((file) => path.relative(pluginRoot, file).split(path.sep).join("/"))
    .sort();
  for (const relative of files) {
    const text = (await readFile(path.join(pluginRoot, relative), "utf8")).replace(/^﻿/, "").replace(/\r\n/g, "\n");
    hash.update(`${relative}\n${text}\n`);
  }
  return hash.digest("hex");
}

// Print the hash only when run as a script, not when imported.
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(await pluginSourceHash());
}
