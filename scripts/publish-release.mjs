// Builds the release zip on this machine and publishes it as a GitHub Release on your fork.
//
//   npm run release:publish            build, then publish v<package.json version>
//   npm run release:publish -- --draft publish as a draft to review on GitHub first
//
// Nothing runs on GitHub's machines: the plugin is compiled here against your local Civil 3D,
// and only the zip and its checksum are uploaded. Requires the GitHub CLI (gh), signed in once
// with `gh auth login`, and the release commit already pushed (e.g. with GitHub Desktop).
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageJson = JSON.parse(readFileSync(path.join(repositoryRoot, "package.json"), "utf8"));
const version = packageJson.version;
const tag = `v${version}`;
const draft = process.argv.includes("--draft");

function fail(message) {
  console.error(`\nrelease:publish stopped: ${message}`);
  process.exit(1);
}

function capture(command, args) {
  const result = spawnSync(command, args, { cwd: repositoryRoot, encoding: "utf8" });
  if (result.error) return { ok: false, out: "", err: result.error.message };
  return { ok: result.status === 0, out: (result.stdout ?? "").trim(), err: (result.stderr ?? "").trim() };
}

function run(command, args) {
  const result = spawnSync(command, args, { cwd: repositoryRoot, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) fail(`${path.basename(command)} ${args.join(" ")} exited with code ${result.status}.`);
}

// git: PATH first, then the copy bundled with GitHub Desktop.
function findGit() {
  if (capture("git", ["--version"]).ok) return "git";
  const desktop = path.join(process.env.LOCALAPPDATA ?? "", "GitHubDesktop");
  if (existsSync(desktop)) {
    const apps = readdirSync(desktop).filter((name) => name.startsWith("app-")).sort().reverse();
    for (const app of apps) {
      const candidate = path.join(desktop, app, "resources", "app", "git", "cmd", "git.exe");
      if (existsSync(candidate)) return candidate;
    }
  }
  fail("git was not found. Install Git or GitHub Desktop.");
}

function findGh() {
  if (capture("gh", ["--version"]).ok) return "gh";
  const candidate = path.join(process.env.ProgramFiles ?? "C:\\Program Files", "GitHub CLI", "gh.exe");
  if (existsSync(candidate)) return candidate;
  fail("the GitHub CLI (gh) was not found. Install it with `winget install --id GitHub.cli`, then run `gh auth login`.");
}

const git = findGit();
const gh = findGh();

// 1. Preconditions: signed in, clean tree, commit pushed to your fork, tag not used yet.
if (!capture(gh, ["auth", "status"]).ok) fail("the GitHub CLI is not signed in. Run `gh auth login` once.");

const origin = capture(git, ["remote", "get-url", "origin"]).out;
const match = origin.match(/github\.com[:/]([^/]+)\/([^/.]+?)(?:\.git)?$/i);
if (!match) fail(`origin (${origin}) is not a GitHub repository.`);
const repo = `${match[1]}/${match[2]}`;
const login = capture(gh, ["api", "user", "--jq", ".login"]).out;
if (login && match[1].toLowerCase() !== login.toLowerCase()) {
  fail(`origin points at ${repo}, which isn't your account (${login}). Releases are only published to your own fork.`);
}

if (capture(git, ["status", "--porcelain"]).out) fail("there are uncommitted changes. Commit them first.");
run(git, ["fetch", "--quiet", "origin"]);
const head = capture(git, ["rev-parse", "HEAD"]).out;
if (!capture(git, ["branch", "-r", "--contains", head]).out.includes("origin/")) {
  fail("this commit isn't on GitHub yet. Push it (GitHub Desktop: Push origin), then run this again.");
}
if (capture(gh, ["release", "view", tag, "--repo", repo]).ok) {
  fail(`release ${tag} already exists on ${repo}. Bump the version in package.json (npm version <x.y.z> --no-git-tag-version, npm run version:sync) first.`);
}

// 2. Test, then build the zip (includes the Autodesk-DLL and privacy checks).
if (!process.env.npm_execpath) fail("run this through npm: npm run release:publish");
run(process.execPath, [process.env.npm_execpath, "run", "build"]);
run(process.execPath, [process.env.npm_execpath, "test"]);
run(process.execPath, [process.env.npm_execpath, "run", "package:release"]);
const releaseRoot = path.join(repositoryRoot, "dist", "release");
const zip = path.join(releaseRoot, `civil3d-mcp-unified-${version}.zip`);
const sums = path.join(releaseRoot, "SHA256SUMS.txt");
if (!existsSync(zip)) fail(`${zip} was not produced.`);

// 3. Release notes: this version's CHANGELOG section.
const changelog = readFileSync(path.join(repositoryRoot, "CHANGELOG.md"), "utf8").replace(/\r\n/g, "\n");
const start = changelog.search(new RegExp(`^## v?${version.replace(/\./g, "\\.")}\\b.*$`, "m"));
if (start < 0) fail(`CHANGELOG.md has no "## v${version}" section.`);
const next = changelog.indexOf("\n## ", start + 1);
const section = changelog.slice(changelog.indexOf("\n", start) + 1, next < 0 ? undefined : next).trim();
const notes = `${section}\n\n**Install:** download \`${path.basename(zip)}\`, unzip it, and follow \`INSTALL.md\`.\n`;
const notesFile = path.join(os.tmpdir(), `civil3d-mcp-release-notes-${version}.md`);
writeFileSync(notesFile, notes, "utf8");

// 4. Publish: gh creates the tag on GitHub at this commit.
run(gh, [
  "release", "create", tag, zip, sums,
  "--repo", repo,
  "--target", head,
  "--title", `civil3d-mcp-unified ${version}`,
  "--notes-file", notesFile,
  ...(draft ? ["--draft"] : []),
]);
console.log(`\n${draft ? "Draft release" : "Release"} ${tag} published: https://github.com/${repo}/releases/tag/${tag}`);
