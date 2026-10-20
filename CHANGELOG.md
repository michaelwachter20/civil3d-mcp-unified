# Changelog

## Unreleased

- The knowledge base folder is now `Documents\Civil3DMcp-KnowledgeBase` (was `Documents\Civil3DMcp`): preferences,
  lessons, project notes, skills and saved scripts. The MCP server and the plugin each move the old folder on first
  use when the new one doesn't exist. `CIVIL3D_KNOWLEDGE_DIR` and `CIVIL3D_SCRIPTS_DIR` still override it.
- Project files folder: `projects\<project>\` next to `<project>.json`, for PDFs, spreadsheets, images or anything else
  the user wants read for that project (subfolders included). Created with the project; `civil3d_project_notes
  action=files` lists it, and the first code result of a session names it with its files.

## v2.1.0 — 2026-10-19

> Tested only with Autodesk Civil 3D 2027 (English) on Windows 11, like v2.0.0.

### Working with several drawings

- Scripts can read other drawings: `OpenDrawings` lists the open tabs and `Reference(nameOrPath)` opens
  another open drawing, or reads a `.dwg` in the background. Reads only: the transaction is always aborted,
  and changes still go to the active drawing. Useful for comparing a drawing against a finished "go-by".
- `civil3d_drawing` actions `list_open` and `switch` (changes the active tab; no approval needed).
- New skill `compare_to_goby`.

### Seeing what Civil 3D is doing, and never hanging

- `civil3d_drawing action=command_line` returns the last command-line lines (oldest first, like F2), active
  commands and the recent command history. It works even while a request is stuck.
- Reads no longer fall back to `ExecuteInCommandContextAsync` while a command is active. That call could stay
  pending forever and block every later request. Reads now retry until Civil 3D is idle and fail with
  `CIVIL3D.HOST_BUSY` after `CIVIL3D_IDLE_WAIT_SECONDS` (default 30).
- Writes are queued in command context from the main thread. If one hasn't started in time, it is cancelled
  atomically, so it never runs after its caller gave up.
- Health reports active commands, and the Civil 3D version without the licensee name.

### Safer edits

- `civil3d_drawing undo` refuses when the user ran commands after the AI's last change, because UNDO would revert
  the user's work. `force=true` overrides it once the user agrees.
- Scripts: `RemovePointGroup(name)` deletes a point group through the collection. `PointGroup.Erase()` leaves an
  empty slot that breaks reordering in the Point Groups dialog.
- Scripts: `FocusOn(min,max | points | ids)` names the area a write worked on, and the view zooms there after the
  change instead of to everything it touched.
- Scripts: `SampleLineGroup.GetSectionSources()` is blocked in every mode. It updates the group, and on a group
  opened for read it crashes Civil 3D (`eNotOpenForWrite`). The new `SectionSources(groupId)` helper (write mode)
  opens the group for write first and returns a copy of the sources; `SetSectionSources(groupId, names, styleId)` turns sampling on for named sources.
- Scripts no longer fail when Civil 3D has loaded an assembly with a malformed `InternalsVisibleTo` attribute.
  Such assemblies are left out of the script references and logged.

### Knowledge

- New tool `civil3d_project_notes` (list/link/unlink/add/update/remove) for facts about one project. They are
  stored on this computer in `<knowledge dir>\projects\<project>.json`, matched to the active drawing by folder
  (a project can have several folders), and returned with the first code result of a session.
- Lessons, skills and scripts are kept general. The server instructions and the `working_safely_in_civil3d`
  skill say to keep project specifics in project notes instead.
- New skill `swap_edges_to_follow_lines`: make a TIN surface follow top/toe lines using edge swaps only.
- Skill `declutter_point_labels` v3:
  - each display group's labels go on their own side;
  - close shots stack in the points' order on the sheet;
  - labels in crowded spots move a little or switch sides to the closest clear spot instead of going far away;
  - shots with protected codes are never hidden, and hand-placed labels stay where they are;
  - label size is checked against the user's own hand placements;
  - a workflow for refining the result with the user.

## v2.0.0 — 2026-10-14: civil3d-mcp-unified

> Tested only with Autodesk Civil 3D 2027 (English), build 13.9.1308 / AutoCAD R26.0.118, .NET 10.0.12,
> on Windows 11. Not tested with any other Civil 3D version, update, language or build, or with plain AutoCAD.

### Knowledge base that grows with use

- New tools `civil3d_preferences` (standing user rules, sent with every chat's instructions) and
  `civil3d_memory` (lessons tagged with API topics). Lessons that match the code are returned with
  `civil3d_execute` / `civil3d_query` results and with `civil3d_skills action=read`. The release includes seed lessons.
- `civil3d_skills` can now `write`, `append_version_section` and `retire` skills. Skills record the Civil 3D
  releases they were verified on, replaced versions are archived, and a skill from another release gets a new
  section instead of being overwritten.
- Saved scripts carry a `script_version`. Re-saving archives the previous version to `scripts\_archive`, and
  deleting retires the script to the archive.
- New built-in skill `working_safely_in_civil3d`, read at the start of each chat.
- The knowledge base lives in `CIVIL3D_KNOWLEDGE_DIR` (default `Documents\Civil3DMcp`). This replaces
  `CIVIL3D_KNOWLEDGE_FILE`.

### Release packaging and AI connection control

- `npm run package:release` builds one zip: the Civil 3D plugin as an Autodesk auto-load bundle with a
  one-click installer, the Claude Desktop `.mcpb`, the MCP server for other clients, and `INSTALL.md`.
  It fails if any file contains the builder's user name or a user-profile path.
- The AI connection is opt-in per Civil 3D session: loading the plugin no longer starts the listener.
  Turn it on from the new **Civil 3D MCP** ribbon tab (ON/OFF toggle with a green/red status line) or
  `C3DMCPSTART`. `CIVIL3D_MCP_AUTOSTART=true` restores the old behavior.
- The listener claims port 8080 exclusively. Only one Civil 3D window can have the AI connection on; turning it
  on in a second window reports that the port is in use and leaves the first one working.
- Local build paths are no longer embedded in the plugin DLL/PDB.

### Civil 3D 2027

- Retargeted the plugin to `net10.0-windows`, as AutoCAD 2027 requires. 2025 and 2026
  builds are not binary compatible.
- Autodesk references now resolve per DLL from the default 2027 install
  (root, `C3D\`, and `ACA\`), from `Civil3DReferencesPath`, or from `C_References`.
- CI now targets .NET 10 and a `Civil3D-2027` runner. Help search prefers 2027 docs.

### Code execution (merged from barbosaihan/civil3d-mcp)

- Added the `civil3d_execute` (write, approval-gated) and `civil3d_query` (read-only)
  tools, which run Roslyn C# against the full AutoCAD and Civil 3D .NET API.
  They use the serialized host queue, drawing-identity checks, and cancellation.
- Compilation happens off the AutoCAD main thread and is cached by SHA-256.
  Diagnostics include line and column numbers, and runtime errors report the
  script line.
- Each write run is grouped into one undo step (`UNDO BEGIN/END`).
- A result serializer turns ObjectId, Point3d, Extents3d, and entity values into
  compact JSON instead of walking their full property graphs.
- The sandbox has host-protection rules plus strict read-only rules for queries.
- Added the `acad_command` tool for native commands with prompt responses.
  Dangerous commands and AutoLISP are blocked.
- Added the `civil3d_skills` tool: 16 C# templates (11 from barbosaihan, 5 new
  Args-based templates) with a CRLF-safe parser.
- Added the `civil3d_script` tool for saved, parameterized, compile-checked `.csx`
  scripts, with save/list/get/run/delete/compile/status actions.
- Added the `MCPRUN` and `MCPSCRIPTS` commands, which run saved scripts from the
  Civil 3D command line with interactive parameter prompts.

### Working alongside the user

- Read-only requests run on the AutoCAD main thread outside command context and
  without a document lock, so they no longer add empty `EXECUTEFUNCTION` (or
  blank) undo steps. `CIVIL3D_READ_CONTEXT=command` restores the old behavior.
- After a write, the view pans to the changed objects, zooming out only if they
  don't fit. Toggle it with `C3DMCPFOLLOW` or `CIVIL3D_FOLLOW_VIEW=false`.

### Proven scripts in the skill library

- Added `declutter_point_labels` (saved script `declutter-labels`): de-overlaps COGO point labels on a
  layout sheet (labels only), with hide-group thinning. Proven on a live Civil 3D 2027 drawing.
- Added `reset_declutter_run` to revert a declutter run without UNDO.
- Scripts are compiled in Release mode (Debug-mode script code crashed the .NET 10 runtime in Civil 3D 2027).
- Tool descriptions now warn about `DBObject` ambiguity, unsupported `dynamic`, and dragged-label
  `LabelLocation` behavior.

### Fixes and housekeeping

- Whole-number script arguments are no longer rejected as "not a number".
- `version:check` no longer fails on Windows checkouts with CRLF line endings.
- `npm audit fix` patched vulnerable transitive dependencies.
- Refreshed the bundled Autodesk video catalog from the 2027 help.
- API-limit error messages no longer name a specific Civil 3D version.
- Removed an unused drawing template, an empty `README!.md`, and two scripts for
  an external project (`Civil3D-AI-CoPilot`) that isn't part of this repository.
- Attribution: credited both upstream projects in the README, notices, package
  metadata, and Claude Desktop manifest; pointed URLs at this fork; removed the
  upstream banner image.

## v1.2.1 — 2026-07-14

### Production readiness

- Added a self-contained Claude Desktop MCPB package, a byte-identical legacy
  DXT compatibility artifact, installer configuration, checksum generation, and
  official manifest validation through `npm run package:claude`.
- Added a compact 33-tool default MCP surface while keeping 216 manifest-backed
  canonical and alias routes available internally; set
  `CIVIL3D_ENABLE_TOOL_ALIASES=true` to expose the full 218-tool MCP surface.
- Added first-class MCP output schemas, structured content, annotations,
  resources, progress notifications, and report-resource retrieval.
- Added parameter-bound, drawing-bound approval receipts, bounded serialized
  host execution, disconnect cancellation, idempotency, and stable errors.
- Added bounded background jobs for publishing, imports, corridor rebuilds, and
  bulk QC, with terminal retention and cancellation telemetry.
- Added liveness, readiness, plugin, queue, and version endpoints on the local
  HTTP bridge at port 3000.
- Added rotating native plugin logs with file-health and last-error telemetry.
- Added CI, generated tool documentation, manifest/dispatcher parity, package
  inspection, startup smoke, and opt-in live Civil 3D host validation.
- Removed tracked Autodesk assemblies; Release builds resolve licensed Civil 3D
  2026 references through `Civil3DReferencesPath`.

### Live validation

- Validated plugin 1.2.1.0 against Civil 3D 2026 on port 8080.
- Validated the spawned stdio MCP client, HTTP registration and routing,
  approvals, concurrency, document switching, create/undo/delete, temporary
  TIN-volume rollback, job cancellation/completion, and response limits.

## v1.1.0 — 2026-03-17

### Summary

Adds 12 new tools across 3 categories (parcel editing, survey processing, data shortcut management) to bring the total MCP tool count to 162.

---

### MCP Server (TypeScript)

**New tools (+12):**

| Category | New Tools | Actions |
|---|---|---|
| Parcel Editing | 4 | create, edit, lot-line-adjust, report |
| Survey Processing | 4 | observation-list, network-adjust, figure-create, landxml-import |
| Data Shortcut Management | 4 | create, promote, reference, sync |

**New files:**
- `src/tools/civil3d_parcel_editing.ts` — parcel CRUD and lot-line adjustment
- `src/tools/civil3d_survey_processing.ts` — survey observation, network adjustment, LandXML import
- `src/tools/civil3d_data_shortcut_mgmt.ts` — data shortcut lifecycle management
- `tests/parcel_survey_shortcuts.test.ts` — 34 schema-level tests

**Deferred:** gravity pipe HGL solver and APS 3D viewer (high complexity, no operational demand).

---

## v1.0.0 — 2026-03-17

### Summary

First stable release of the Civil3D MCP Ecosystem: a 150-tool MCP server for Autodesk Civil 3D paired with an AI CoPilot WPF plugin and a full hydrology analysis engine.

---

### MCP Server (TypeScript)

**150 tools across 16 categories:**

| Category | Tools | Highlights |
|---|---|---|
| Alignment | 22 | Create, edit, stationing, offsets, superelevation, widening |
| Surface | 18 | TIN create/edit, breaklines, contours, volume analysis, sampling |
| Corridor | 12 | Create, rebuild, extract solids, edit frequency, targets |
| Profile | 14 | Surface profiles, layout profiles, PVI edit, design speeds |
| Pipe Networks | 10 | Pipes, structures, network analysis, interference |
| Grading | 8 | Feature lines, grading objects, volume balance |
| COGO / Points | 10 | Import/export, groups, inverse, traverse, intersection |
| Plan Production | 12 | Sheet sets, view frames, match lines, north arrows |
| QC | 18 | Design standard checks, clearance, slope, cross-fall |
| Quantity Takeoff | 10 | Cut/fill volumes, earthwork reports, material summary |
| Hydrology | 5 | Flow paths, low point, watershed delineation, catchment, rational method |
| Sample Lines | 4 | Group creation, section views, material table |
| Data Shortcuts | 4 | Create, reference, synchronize, export |
| Labels / Styles | 4 | Apply label sets, import/export styles |
| Utilities | 9 | Drawing context, layer management, export formats |

**Architecture:**
- MCP protocol over HTTP POST `/execute`
- Tool registry with per-tool Zod validation
- `civil3d_hydrology` tool handles 5 hydrological analysis actions

---

### AI CoPilot Plugin (C#)

**Command routing (all 150 categories):**
- `IsMcpDirectCommand()` — routes `civil3d_*` and `create_cogo_point` to MCP async handler
- `IsHydrologyCommand()` — routes hydrology commands to async MCP → CAD draw pipeline
- `CommandRouter.Execute()` — synchronous CAD transaction handler for 14 draw command types
- `HandleMcpDirectCommandAsync()` — async MCP caller with formatted JObject result display

**Hydrology integration:**
- `HydrologyDrawer.cs` — draws flow paths (cyan), watershed boundaries (yellow), outlet markers (red)
- `McpClient.cs` — typed async methods for all 5 hydrology tools with result model classes
- Sequential workflow support: `lastHydroOutlet` chains `FindLowPoint` → `DelineateWatershed`

**AI providers supported:** Gemini, OpenAI, Anthropic

---

### Integration Tests

- 13 test files, **372 tests**, all passing
- End-to-end workflow tests covering alignment → surface → corridor → profile pipeline
- Hydrology workflow tests covering flow path → low point → watershed → catchment → runoff

---

### Bug Fixes

- **JFS-15**: Fixed structural C# compile error — hydrology methods were accidentally placed outside the `McpClient` class body
- **JFS-15**: Made `ExecuteMcpToolAsync` public (was private) to enable direct MCP command routing

---

### Known Limitations

- Civil 3D API calls require an active drawing session — no headless mode
- Hydrology delineation uses grid sampling (not DEM-optimized); large surfaces may be slow
- C# plugin targets .NET Framework 4.8 (AutoCAD/Civil 3D requirement)
