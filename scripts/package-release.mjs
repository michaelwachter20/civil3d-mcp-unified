// Builds the complete release zip: Civil 3D plugin bundle + installer, Claude Desktop .mcpb,
// and a standalone MCP server folder, then refuses to finish if any file contains the builder's
// user name or a local user-profile path.
//
//   npm run package:release
//
// Requires the .NET 10 SDK and a Civil 3D 2027 install (for the plugin references), plus Node 18+.
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { cp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { pluginSourceHash } from "./plugin-source-hash.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageJson = JSON.parse(await readFile(path.join(repositoryRoot, "package.json"), "utf8"));
const version = packageJson.version;
const repoUrl = String(packageJson.homepage ?? "").replace(/#.*$/, "");
const author = typeof packageJson.author === "string" ? packageJson.author : packageJson.author?.name ?? "";

const releaseRoot = path.join(repositoryRoot, "dist", "release");
const releaseName = `civil3d-mcp-unified-${version}`;
const stage = path.join(releaseRoot, releaseName);
const pluginBuild = path.join(releaseRoot, "plugin-build");
const zipPath = path.join(releaseRoot, `${releaseName}.zip`);
const bundleSource = path.join(repositoryRoot, "packaging", "civil3d-bundle");

function run(command, args, cwd = repositoryRoot) {
  // Run npm through the same npm CLI that launched this script (no shell needed).
  if (command === "npm") {
    if (!process.env.npm_execpath) throw new Error("Run this through npm: npm run package:release");
    [command, args] = [process.execPath, [process.env.npm_execpath, ...args]];
  }
  const result = spawnSync(command, args, { cwd, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} exited with code ${result.status}.`);
}
const fill = (text) => text.replaceAll("{{VERSION}}", version).replaceAll("{{URL}}", repoUrl).replaceAll("{{AUTHOR}}", author);

await rm(releaseRoot, { recursive: true, force: true });
await mkdir(stage, { recursive: true });

// 1. Civil 3D plugin as an Autodesk application bundle. --prebuilt uses prebuilt/Civil3DMcpPlugin
//    (from npm run plugin:prebuild) instead of compiling, for machines without Civil 3D.
if (process.argv.includes("--prebuilt")) {
  const prebuilt = path.join(repositoryRoot, "prebuilt", "Civil3DMcpPlugin");
  const info = JSON.parse(await readFile(path.join(prebuilt, "BUILD-INFO.json"), "utf8").catch(() => "{}"));
  if (info.version !== version) {
    throw new Error(`prebuilt plugin is version ${info.version ?? "(none)"}, package.json is ${version}. Run npm run plugin:prebuild and commit it.`);
  }
  if (info.sourceHash !== await pluginSourceHash()) {
    throw new Error("prebuilt plugin doesn't match the plugin source. Run npm run plugin:prebuild and commit it.");
  }
  await cp(prebuilt, pluginBuild, { recursive: true });
} else {
  run("dotnet", ["build", path.join("Civil3D-MCP-Plugin", "Civil3DMcpPlugin.csproj"), "-c", "Release", "-o", pluginBuild, "--no-incremental", "-v:minimal"]);
}
const bundle = path.join(stage, "Civil3DMcpPlugin.bundle");
await mkdir(path.join(bundle, "Contents"), { recursive: true });
for (const file of await readdir(pluginBuild)) {
  if (/\.(dll|pdb|json)$/i.test(file)) await cp(path.join(pluginBuild, file), path.join(bundle, "Contents", file));
}
if ((await readdir(path.join(bundle, "Contents"))).some((f) => /^(ac|aec|aecc)\w*mgd\.dll$/i.test(f))) {
  throw new Error("Autodesk assemblies ended up in the bundle; they must never be redistributed.");
}
await writeFile(path.join(bundle, "PackageContents.xml"), fill(await readFile(path.join(bundleSource, "PackageContents.xml"), "utf8")));
for (const file of ["Install-Civil3DPlugin.ps1", "Install-Civil3DPlugin.cmd", "Uninstall-Civil3DPlugin.cmd", "claude-desktop-config.mjs"]) {
  await cp(path.join(bundleSource, file), path.join(stage, file));
}
await writeFile(path.join(stage, "INSTALL.md"), fill(await readFile(path.join(bundleSource, "INSTALL.md"), "utf8")));

// 2. Claude Desktop extension (.mcpb), and the same server unpacked for other MCP clients
run("npm", ["run", "package:claude"]);
const mcpbName = `${packageJson.name}-${version}.mcpb`;
await cp(path.join(repositoryRoot, "dist", "claude-desktop", mcpbName), path.join(stage, mcpbName));
const serverDir = path.join(stage, "mcp-server");
await mkdir(serverDir, { recursive: true });
run("tar", ["-xf", path.join(stage, mcpbName), "-C", serverDir]);

for (const file of ["LICENSE", "THIRD_PARTY_NOTICES.md"]) await cp(path.join(repositoryRoot, file), path.join(stage, file));

// 3. Privacy gate: no builder user name or local profile paths anywhere in the release
const userName = os.userInfo().username;
const forbidden = [new RegExp(`\\b${userName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\b`, "i"), /[A-Za-z]:[\\/]+Users[\\/]+(?!Public)/i];
const offenders = [];
async function scan(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) { await scan(full); continue; }
    const buf = await readFile(full);
    const text = buf.toString("latin1") + "\n" + buf.toString("utf16le");
    if (forbidden.some((re) => re.test(text))) offenders.push(path.relative(stage, full));
  }
}
await scan(stage);
if (offenders.length > 0) {
  throw new Error(`Release contains local user paths or the user name; fix before publishing:\n  ${offenders.join("\n  ")}`);
}

// 4. Zip + checksum
run("tar", ["-a", "-c", "-f", zipPath, "-C", releaseRoot, releaseName]);
const hash = createHash("sha256").update(await readFile(zipPath)).digest("hex");
await writeFile(path.join(releaseRoot, "SHA256SUMS.txt"), `${hash}  ${path.basename(zipPath)}\n`);
await rm(pluginBuild, { recursive: true, force: true });

console.log(`\nRelease ready: ${path.relative(repositoryRoot, zipPath)}`);
console.log(`SHA-256: ${hash}`);
console.log("Privacy check passed: no user name or user-profile paths in any file.");
