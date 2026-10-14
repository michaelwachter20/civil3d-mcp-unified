// Compiles the Civil 3D plugin on this machine (needs Civil 3D 2027 for the Autodesk references)
// and stores the result in prebuilt/Civil3DMcpPlugin, so the GitHub release workflow can package
// it without Civil 3D. Only this project's DLL and its redistributable dependencies are kept;
// Autodesk assemblies are never copied.
//
//   npm run plugin:prebuild     then commit prebuilt/ together with the plugin source change
import { spawnSync } from "node:child_process";
import { cp, mkdir, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { pluginSourceHash, repositoryRoot } from "./plugin-source-hash.mjs";

const packageJson = JSON.parse(await readFile(path.join(repositoryRoot, "package.json"), "utf8"));
const target = path.join(repositoryRoot, "prebuilt", "Civil3DMcpPlugin");
const buildDir = await mkdtemp(path.join(os.tmpdir(), "c3dmcp-prebuild-"));

const result = spawnSync("dotnet", [
  "build", path.join("Civil3D-MCP-Plugin", "Civil3DMcpPlugin.csproj"),
  "-c", "Release", "-o", buildDir, "--no-incremental", "-v:minimal",
], { cwd: repositoryRoot, stdio: "inherit" });
if (result.error) throw result.error;
if (result.status !== 0) process.exit(result.status ?? 1);

const files = (await readdir(buildDir)).filter((file) => /\.(dll|pdb|json)$/i.test(file));
const autodesk = files.filter((file) => /^(ac|aec|aecc|adw|adwindows)\w*\.dll$/i.test(file) || /^accore|^acdbmgd|^acmgd/i.test(file));
if (autodesk.length > 0) throw new Error(`Autodesk assemblies in the build output; they must never be committed: ${autodesk.join(", ")}`);

// Privacy gate: no user name or user-profile paths in what gets committed.
const userName = os.userInfo().username;
const forbidden = [new RegExp(`\\b${userName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\b`, "i"), /[A-Za-z]:[\\/]+Users[\\/]+(?!Public)/i];
for (const file of files) {
  const buffer = await readFile(path.join(buildDir, file));
  const text = buffer.toString("latin1") + "\n" + buffer.toString("utf16le");
  if (forbidden.some((pattern) => pattern.test(text))) throw new Error(`${file} contains a user name or user-profile path.`);
}

await rm(target, { recursive: true, force: true });
await mkdir(target, { recursive: true });
for (const file of files) await cp(path.join(buildDir, file), path.join(target, file));
await writeFile(path.join(target, "BUILD-INFO.json"), `${JSON.stringify({
  version: packageJson.version,
  sourceHash: await pluginSourceHash(),
  note: "Built locally against Civil 3D 2027 by npm run plugin:prebuild. Autodesk assemblies are not included.",
}, null, 2)}\n`);
await rm(buildDir, { recursive: true, force: true });

console.log(`\nPrebuilt plugin ${packageJson.version} written to prebuilt/Civil3DMcpPlugin (${files.length} files).`);
console.log("Commit it together with the plugin source, then push a v* tag to publish a release.");
