// Prints the CHANGELOG.md section for a version (default: package.json version) as release notes.
//   node scripts/release-notes.mjs [x.y.z] > notes.md
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const version = process.argv[2] ?? JSON.parse(readFileSync(path.join(root, "package.json"), "utf8")).version;
const changelog = readFileSync(path.join(root, "CHANGELOG.md"), "utf8").replace(/\r\n/g, "\n");
const start = changelog.search(new RegExp(`^## v?${version.replace(/\./g, "\\.")}\\b.*$`, "m"));
if (start < 0) {
  console.error(`CHANGELOG.md has no "## v${version}" section.`);
  process.exit(1);
}
const next = changelog.indexOf("\n## ", start + 1);
const section = changelog.slice(changelog.indexOf("\n", start) + 1, next < 0 ? undefined : next).trim();
process.stdout.write(`${section}\n\n**Install:** download \`civil3d-mcp-unified-${version}.zip\`, unzip it, and follow \`INSTALL.md\`.\n`);
