// Packs the compiled MCP server (build/) as a Claude Desktop extension: dist/claude-desktop/<name>-<version>.mcpb,
// a byte-identical legacy .dxt copy, and SHA256SUMS.txt. Stages build/, LICENSE, the extension README and
// packaging/claude-desktop/manifest.json (version set from package.json), installs production dependencies with
// `npm ci --omit=dev --ignore-scripts`, then validates and packs with the @anthropic-ai/mcpb CLI.
//
//   npm run package:claude                    (builds first; also called by scripts/package-release.mjs)
//   npm run package:claude:validate           (--validate-only: re-checks existing artifacts, builds nothing)
//
// Must run through npm (it needs npm_execpath). Writes only under dist/claude-desktop; no Civil 3D needed.
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  access,
  copyFile,
  cp,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const packageJsonPath = path.join(repositoryRoot, "package.json");
const packageLockPath = path.join(repositoryRoot, "package-lock.json");
const manifestSourcePath = path.join(
  repositoryRoot,
  "packaging",
  "claude-desktop",
  "manifest.json",
);
const extensionReadmePath = path.join(
  repositoryRoot,
  "packaging",
  "claude-desktop",
  "README.md",
);
const outputDirectory = path.join(repositoryRoot, "dist", "claude-desktop");
const stagingDirectory = path.join(outputDirectory, "civil3d-mcp");
const npmCliPath = process.env.npm_execpath;

if (!npmCliPath) {
  throw new Error("Run this script through an npm command so npm_execpath is available.");
}

/** Runs the npm CLI that launched this script (node + npm_execpath, no shell); throws on a non-zero exit. */
function runNpm(args, cwd = repositoryRoot) {
  run(process.execPath, [npmCliPath, ...args], cwd);
}

/** Runs a command with inherited output; throws if it can't start or exits non-zero. */
function run(command, args, cwd = repositoryRoot) {
  const result = spawnSync(command, args, {
    cwd,
    env: process.env,
    stdio: "inherit",
  });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} exited with code ${result.status}.`);
  }
}

/** Hex SHA-256 of a file's bytes. */
async function sha256(filePath) {
  const contents = await readFile(filePath);
  return createHash("sha256").update(contents).digest("hex");
}

/** Throws `message: filePath` when the file is missing. */
async function requireFile(filePath, message) {
  try {
    await access(filePath);
  } catch {
    throw new Error(`${message}: ${filePath}`);
  }
}

const packageJson = JSON.parse(await readFile(packageJsonPath, "utf8"));
const artifactBaseName = `${packageJson.name}-${packageJson.version}`;
const mcpbPath = path.join(outputDirectory, `${artifactBaseName}.mcpb`);
const dxtPath = path.join(outputDirectory, `${artifactBaseName}.dxt`);
const checksumPath = path.join(outputDirectory, "SHA256SUMS.txt");
const validateOnly = process.argv.includes("--validate-only");

if (validateOnly) {
  await requireFile(mcpbPath, "MCPB artifact not found; run npm run package:claude first");
  await requireFile(dxtPath, "Legacy DXT artifact not found; run npm run package:claude first");

  runNpm(["exec", "--", "mcpb", "info", mcpbPath]);

  const mcpbHash = await sha256(mcpbPath);
  const dxtHash = await sha256(dxtPath);
  if (mcpbHash !== dxtHash) {
    throw new Error("The legacy .dxt copy does not match the validated .mcpb artifact.");
  }

  console.log(`Validated ${path.relative(repositoryRoot, mcpbPath)}`);
  console.log(`Legacy copy matches (${mcpbHash}).`);
  process.exit(0);
}

await requireFile(
  path.join(repositoryRoot, "build", "index.js"),
  "Compiled server entry point not found; run npm run build first",
);

const manifest = JSON.parse(await readFile(manifestSourcePath, "utf8"));
manifest.version = packageJson.version;

await rm(stagingDirectory, { recursive: true, force: true });
await mkdir(path.join(stagingDirectory, "server"), { recursive: true });
await cp(path.join(repositoryRoot, "build"), path.join(stagingDirectory, "server"), {
  recursive: true,
});
await copyFile(path.join(repositoryRoot, "LICENSE"), path.join(stagingDirectory, "LICENSE"));
await copyFile(extensionReadmePath, path.join(stagingDirectory, "README.md"));
await copyFile(packageJsonPath, path.join(stagingDirectory, "package.json"));
await copyFile(packageLockPath, path.join(stagingDirectory, "package-lock.json"));
await writeFile(
  path.join(stagingDirectory, "manifest.json"),
  `${JSON.stringify(manifest, null, 2)}\n`,
  "utf8",
);

runNpm(
  ["ci", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund"],
  stagingDirectory,
);

// After the install, swap in a minimal package.json (runtime dependencies only, no scripts) and drop the lock file,
// so the packed extension carries no dev tooling.
const runtimePackageJson = {
  name: packageJson.name,
  version: packageJson.version,
  description: packageJson.description,
  type: packageJson.type,
  private: true,
  // The server's own requirement (node:sqlite needs 22.15+), taken from package.json so the two can't disagree.
  engines: packageJson.engines,
  dependencies: packageJson.dependencies,
};
await writeFile(
  path.join(stagingDirectory, "package.json"),
  `${JSON.stringify(runtimePackageJson, null, 2)}\n`,
  "utf8",
);
await rm(path.join(stagingDirectory, "package-lock.json"), { force: true });

runNpm(["exec", "--", "mcpb", "validate", path.join(stagingDirectory, "manifest.json")]);
await rm(mcpbPath, { force: true });
await rm(dxtPath, { force: true });
runNpm(["exec", "--", "mcpb", "pack", stagingDirectory, mcpbPath]);
runNpm(["exec", "--", "mcpb", "info", mcpbPath]);

await copyFile(mcpbPath, dxtPath);
const mcpbHash = await sha256(mcpbPath);
const dxtHash = await sha256(dxtPath);
if (mcpbHash !== dxtHash) {
  throw new Error("Failed to create an exact legacy .dxt compatibility copy.");
}

const mcpbSize = (await stat(mcpbPath)).size;
await writeFile(
  checksumPath,
  `${mcpbHash}  ${path.basename(mcpbPath)}\n${dxtHash}  ${path.basename(dxtPath)}\n`,
  "utf8",
);
await rm(stagingDirectory, { recursive: true, force: true });

console.log("Claude Desktop extension package created successfully:");
console.log(`  MCPB: ${mcpbPath}`);
console.log(`  DXT:  ${dxtPath}`);
console.log(`  Size: ${mcpbSize.toLocaleString()} bytes`);
console.log(`  SHA-256: ${mcpbHash}`);
