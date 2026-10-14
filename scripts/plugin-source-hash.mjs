// Fingerprint of the Civil 3D plugin source (C#, project and version files), line endings
// normalized so Windows and Linux checkouts agree. The prebuilt plugin records it, and the
// release workflow refuses to package a prebuilt plugin that doesn't match the source.
import { createHash } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const pluginRoot = path.join(repositoryRoot, "Civil3D-MCP-Plugin");

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

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(await pluginSourceHash());
}
