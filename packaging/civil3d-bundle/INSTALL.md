# Civil 3D MCP {{VERSION}}: installation

Requires **Autodesk Civil 3D 2027** on Windows. Everything below runs locally on your PC.

> **Tested only with Civil 3D 2027 (English), build 13.9.1308 / AutoCAD R26.0.118, .NET 10.0.12, on
> Windows 11.** It has not been tested with any other Civil 3D version, update, language or build, or with
> plain AutoCAD. Civil 3D 2026 and earlier can't load it. On any other setup, try it on a copy of a drawing first.

| In this zip | What it is |
|---|---|
| `Install-Civil3DPlugin.cmd` | Installs (or updates) the Civil 3D plugin so it loads automatically |
| `Uninstall-Civil3DPlugin.cmd` | Removes the plugin again |
| `Civil3DMcpPlugin.bundle\` | The Civil 3D plugin (Autodesk auto-load bundle) |
| `civil3d-mcp-{{VERSION}}.mcpb` | One-click install for the **Claude Desktop** app |
| `mcp-server\` | The same MCP server, for **Claude Code**, Cursor and other MCP clients |

## 1. Install the Civil 3D plugin (everyone)

1. Extract the whole zip to a folder (for example `Documents\civil3d-mcp`). Don't run anything from inside the zip.
2. Close Civil 3D if you're updating an older version of this plugin.
3. Double-click **`Install-Civil3DPlugin.cmd`**. It copies the plugin to
   `%APPDATA%\Autodesk\ApplicationPlugins\Civil3DMcpPlugin.bundle` and adds "Civil 3D MCP plugin" to
   **Windows Settings → Apps → Installed apps**. No admin rights are needed; it only affects your Windows user.
4. Start Civil 3D 2027. A **Civil 3D MCP** tab appears on the ribbon; no `NETLOAD` needed. If Civil 3D asks
   whether to load the plugin, choose **Always Load**.

## Turning the AI connection on and off (per Civil 3D session)

The AI connection is **off** every time Civil 3D starts. AI assistants can only work in a session after you turn it on.

- **Ribbon:** on the **Civil 3D MCP** tab, click **AI Connection** to switch it on or off. Under the button,
  a green dot means **Running** and a red dot means **Off**. **Details** shows what it's doing.
- **Commands:** `C3DMCPSTART` (on), `C3DMCPSTOP` (off), `C3DMCPSTATUS` (show state).
- `MCPRUN` / `MCPSCRIPTS` run and list saved scripts, and work with the connection off.

Prefer the old behavior (on as soon as Civil 3D starts)? Set the Windows environment variable
`CIVIL3D_MCP_AUTOSTART=true`.

Only one Civil 3D window can have the connection on at a time (it uses port 8080). AI tools act on the drawing
that is active in that window.

## Uninstalling

Close Civil 3D, then either double-click **`Uninstall-Civil3DPlugin.cmd`**, or go to **Windows Settings → Apps →
Installed apps → Civil 3D MCP plugin → Uninstall**. This removes the plugin, its Apps entry, and the Civil 3D
tools from Claude Desktop if the installer added them (restart Claude Desktop afterwards). Your saved
scripts (`Documents\Civil3DMcp-KnowledgeBase\scripts`) and the log (`%LOCALAPPDATA%\Civil3DMcpPlugin`) are kept; delete them by
hand if you no longer want them. To remove the AI side, uninstall the extension in Claude Desktop or run
`claude mcp remove civil3d-mcp` for Claude Code.

## 2. Connect your AI app (pick one)

**Claude Desktop (automatic):** the installer from step 1 asks *"Add the Civil 3D tools to Claude Desktop
now?"*. Answer **Y**, quit Claude Desktop when asked, and reopen it: the Civil 3D tools are there. It needs
[Node.js](https://nodejs.org) 18 or newer, keeps a copy of the server in `%LOCALAPPDATA%\civil3d-mcp`, and
backs up Claude's settings file before adding the one `civil3d-mcp` entry.

**Claude Desktop (manual, no Node.js needed):** open **Settings → Extensions → Advanced settings → Install
Extension**, select `civil3d-mcp-{{VERSION}}.mcpb`, and accept the defaults (host `localhost`, port `8080`).

**Claude Code:** needs Node.js 18 or newer. To make it available only in one project folder, run this inside
that folder (use `--scope user` instead to have it in every project):

```
claude mcp add --scope project civil3d-mcp -- node "C:\path\to\extracted\mcp-server\server\index.js"
```

In a session, `/mcp` lets you disable or re-enable it.

**Other MCP clients (Cursor, Cline, …):** add a stdio server with command `node` and argument
`C:\path\to\extracted\mcp-server\server\index.js`.

## 3. Try it

With a drawing open in Civil 3D, ask your AI app to run `civil3d_health`, or something like
"what surfaces and alignments are in this drawing?".

## Good to know

- Changes that edit the drawing ask for approval first, and each one is a single `UNDO` step.
- The assistant learns as you work. With your OK it saves, under `Documents\Civil3DMcp-KnowledgeBase`: your preferences and rules
  (`preferences.json`, loaded into every new chat), lessons about the Civil 3D API (`memory\lessons.jsonl`), skills
  (`skills\`) and scripts (`scripts\`, runnable in Civil 3D with `MCPRUN`). Nothing is thrown away: older versions of
  skills and scripts go to an `_archive` folder. These are plain files you can open and edit, and updates never
  overwrite them.
- The plugin log is at `%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log`.
- If you also `NETLOAD` the plugin from another folder, only one copy can use port 8080. Use one or the other.
- Full documentation: {{URL}}
