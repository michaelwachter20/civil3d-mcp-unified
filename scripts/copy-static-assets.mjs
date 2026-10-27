import { cp, copyFile, mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = path.join(repositoryRoot, "src", "standards", "data");
const outputDirectory = path.join(repositoryRoot, "build", "standards", "data");

await mkdir(outputDirectory, { recursive: true });
await cp(sourceDirectory, outputDirectory, { recursive: true, force: true });

const videoCatalogSource = path.join(repositoryRoot, "autodesk_videos.json");
const videoCatalogOutput = path.join(repositoryRoot, "build", "help", "data", "autodesk_videos.json");
await mkdir(path.dirname(videoCatalogOutput), { recursive: true });
await copyFile(videoCatalogSource, videoCatalogOutput);

// C# code skill templates used by civil3d_skills (resolved from build/skills at runtime).
const skillsSource = path.join(repositoryRoot, "skills");
const skillsOutput = path.join(repositoryRoot, "build", "skills");
await mkdir(skillsOutput, { recursive: true });
await cp(skillsSource, skillsOutput, { recursive: true, force: true });

// Table layout of the suite's project database (a copy of project-analyzer's schema/project-db.sql).
await mkdir(path.join(repositoryRoot, "build", "tools"), { recursive: true });
await copyFile(path.join(repositoryRoot, "src", "tools", "project-db.sql"), path.join(repositoryRoot, "build", "tools", "project-db.sql"));
