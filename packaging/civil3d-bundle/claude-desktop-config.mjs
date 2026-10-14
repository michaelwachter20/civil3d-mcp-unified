// Adds or removes the "civil3d-mcp" server in Claude Desktop's claude_desktop_config.json.
// Used by Install-Civil3DPlugin.ps1. Touches only that one entry, and backs the file up first.
//
//   node claude-desktop-config.mjs add <path-to-mcp-server\server\index.js>
//   node claude-desktop-config.mjs remove
import fs from "node:fs";
import path from "node:path";

const SERVER_NAME = "civil3d-mcp";
const [action, serverEntry] = process.argv.slice(2);

// Classic installs use %APPDATA%\Claude; the Microsoft Store app keeps a redirected copy under
// %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude. Update every one that exists.
function configFiles() {
  const dirs = [];
  if (process.env.APPDATA) dirs.push(path.join(process.env.APPDATA, "Claude"));
  const packages = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "Packages") : null;
  if (packages && fs.existsSync(packages)) {
    for (const name of fs.readdirSync(packages)) {
      if (/^Claude_/i.test(name)) dirs.push(path.join(packages, name, "LocalCache", "Roaming", "Claude"));
    }
  }
  return dirs.filter((dir) => fs.existsSync(dir)).map((dir) => path.join(dir, "claude_desktop_config.json"));
}

function readConfig(file) {
  if (!fs.existsSync(file)) return {};
  const text = fs.readFileSync(file, "utf8").replace(/^﻿/, "");
  return text.trim() ? JSON.parse(text) : {};
}

const files = configFiles();
if (files.length === 0) {
  console.log("Claude Desktop settings folder not found; is Claude Desktop installed and opened at least once?");
  process.exit(2);
}
if (action === "add" && !(serverEntry && fs.existsSync(serverEntry))) {
  console.error(`MCP server entry point not found: ${serverEntry}`);
  process.exit(1);
}

let changed = 0;
for (const file of files) {
  let config;
  try {
    config = readConfig(file);
  } catch (error) {
    console.error(`Skipped ${file}: it isn't valid JSON (${error.message}). Nothing was changed.`);
    continue;
  }
  const servers = config.mcpServers ?? {};
  if (action === "add") {
    servers[SERVER_NAME] = { command: process.execPath, args: [path.resolve(serverEntry)] };
  } else if (action === "remove") {
    if (!(SERVER_NAME in servers)) continue;
    delete servers[SERVER_NAME];
  } else {
    console.error("Usage: node claude-desktop-config.mjs add <server index.js> | remove");
    process.exit(1);
  }
  config.mcpServers = servers;
  if (fs.existsSync(file)) fs.copyFileSync(file, `${file}.before-civil3d-mcp.bak`);
  fs.writeFileSync(file, `${JSON.stringify(config, null, 2)}\n`, "utf8");
  changed++;
}
console.log(action === "add"
  ? `Added "${SERVER_NAME}" to Claude Desktop (${changed} settings file${changed === 1 ? "" : "s"}).`
  : changed ? `Removed "${SERVER_NAME}" from Claude Desktop.` : `"${SERVER_NAME}" was not in Claude Desktop's settings.`);
