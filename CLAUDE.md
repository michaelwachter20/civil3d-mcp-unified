# Working on Civil 3D MCP (guide for AI assistants and developers)

This file is for whoever changes this repository: an AI coding assistant or a person. It says how the code is laid
out, how to check a change, and the rules that keep it safe. For what the tool does for its users, read
[README.md](README.md); for how it works inside, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## What this is

An MCP server and a Civil 3D plugin that let an AI drive the drawing open in **Autodesk Civil 3D 2027** on the user's
own PC:

```
AI client --stdio (MCP)--> build/index.js (TypeScript, Node.js 22.15+) --JSON-RPC over TCP 127.0.0.1:8080--> Civil3DMcpPlugin.dll (inside Civil 3D) --> AutoCAD / Civil 3D .NET API
```

- `src/` is the **MCP server** in TypeScript (ESM, `zod` schemas, `@modelcontextprotocol/sdk`). It validates calls,
  enforces approvals, keeps the knowledge base and the job notes, and sends one JSON-RPC request per TCP connection
  to the plugin. A local HTTP bridge (`127.0.0.1:3000`) exposes the same tools to HTTP clients. **Nothing in Node.js
  touches a drawing.**
- `Civil3D-MCP-Plugin/` is the **plugin** in C# (`net10.0-windows`, required by AutoCAD 2027). It listens on port 8080
  only after the user turns the AI connection on (`C3DMCPSTART` or the ribbon toggle), runs every request through one
  serialized host queue, and does all drawing reads and writes. It also compiles and runs AI-written C# (Roslyn) and
  saved `.csx` scripts.
- Tested only on Civil 3D 2027 English (see the README's version notes). Civil 3D 2026 and earlier can't load it.

## Where things are

| Path | What |
|---|---|
| `src/index.ts` | Entry point: builds the `McpServer` with the server instructions, registers the tools, starts the HTTP bridge and the stdio transport |
| `src/serverInstructions.ts` | Instructions sent to every client on connect, plus the user's preferences, the suite's practices and the list of their skills |
| `src/tools/register.ts` | Registration order: manifest domains, approval tools, `civil3d_help`, MCP resources |
| `src/tools/toolManifest.ts` | `MIGRATED_DOMAIN_DEFINITIONS`, the list of every domain; which exposures are public (compact mode vs `CIVIL3D_ENABLE_TOOL_ALIASES=true`) |
| `src/tools/domainRuntime.ts` | Turns a domain definition into MCP tools: schema validation, approval, idempotency keys, progress, the error result shape |
| `src/tools/domains/*Domain.ts` | One file per domain (alignment, surface, pipe, hydrology, workflow, scripting, ...): each action's input schema, plugin methods, capabilities and `execute`; the canonical tool and its aliases |
| `src/tools/toolMetadata.ts`, `tool_catalog.ts` | Catalog types (`ToolDomain`, `ToolCapability`) and the flat catalog built from the manifest (used by docs, resources, orchestration) |
| `src/tools/approvalPolicy.ts`, `approvalTool.ts` | Which actions need approval, and the `civil3d_preview_action` / `civil3d_request_approval` tools (single-use tokens bound to parameters and drawing) |
| `src/tools/toolHandlerRegistry.ts`, `src/httpBridge.ts` | Every handler captured by name, and the HTTP bridge that calls them (`/execute`, `/tools`, `/health*`) |
| `src/orchestration/`, `src/tools/civil3d_orchestrate.ts` | `civil3d_orchestrate`: route a plain-language request to a tool and action, fill names from the selection, plan, optionally run |
| `src/tools/knowledgeBase.ts`, `skillsLibrary.ts`, `generalKnowledge.ts` | Know-how on how to drive Civil 3D: preferences, lessons, skills; the job-data check every save goes through |
| `src/tools/projectDb.ts`, `projectNotes.ts`, `userKnowledge.ts`, `openDrawing.ts` | The suite's project database (one SQLite file per job), `civil3d_project_notes`, a job's User Knowledge folder, `civil3d_drawing action=open` |
| `src/tools/practices.ts` | The suite's good practices, `<shared folder>\practices.json` (`civil3d_practices`) |
| `src/tools/project-db.sql` | Copy of Project Analyzer's `schema/project-db.sql` (a test checks it) |
| `src/help/`, `src/tools/helpTool.ts` | `civil3d_help`: search of the Autodesk Offline Help installed with Civil 3D, plus the tutorial video catalog (`autodesk_videos.json`) |
| `src/standards/` | Framework standards data and service (`civil3d://standards/framework`, standards domain) |
| `src/utils/` | `SocketClient.ts` / `ConnectionManager.ts` (the TCP client), `logger.ts` (stderr), `requestContext.ts` (request ids, cancellation), `atomicFiles.ts` (locks, backups, atomic writes) |
| `Civil3D-MCP-Plugin/PluginEntry.cs`, `McpRibbon.cs` | Plugin load, the `C3DMCP*` / `MCPRUN` / `MCPSCRIPTS` commands, the ribbon tab |
| `Civil3D-MCP-Plugin/RpcTcpServer.cs`, `PluginRuntime.cs`, `JsonRpcProtocol.cs` | Loopback listener, JSON-RPC parsing and error codes, queue state for health |
| `Civil3D-MCP-Plugin/CommandDispatcher.cs` | The method name -> handler table (`"listAlignments" => AlignmentCommands.ListAlignmentsAsync()`) |
| `Civil3D-MCP-Plugin/CivilExecution.cs` | The host gate: `ReadAsync` (main thread, no undo step) and `WriteAsync` (command context, document lock, one transaction) |
| `Civil3D-MCP-Plugin/*Commands.cs` | The handlers, one class per area (surfaces, pipes, QC, labels, hydrology, ...) |
| `Civil3D-MCP-Plugin/Script*.cs` | Roslyn code execution, the source sandbox, saved scripts, result serialization |
| `Civil3D-MCP-Plugin/FileBoundary.cs`, `Civil3DCompatibility.cs`, `SharedFolder.cs` | The only places for caller-supplied paths and file writes, for reflection, and for finding the suite's shared folder |
| `skills/` | Built-in skills (`*.skill.md`) and `lessons.seed.jsonl`: general know-how, shipped read-only |
| `tests/` | Vitest tests (`*.test.ts`), `fixtures/` (copies of Project Analyzer's made-up job), `integration/`, `FileBoundaryHarness/` (.NET) |
| `scripts/` | Build helpers, the tool-docs generator, version sync, live smoke tests, packaging and release |
| `packaging/`, `prebuilt/` | Release bundle and Claude Desktop extension files; the prebuilt plugin DLL committed for the release workflow |
| `docs/` | `ARCHITECTURE.md`, `DEPLOYMENT.md`, `tools.md` (extended reference), `tools.generated.md` (generated, don't edit) |

## Checking a change

Offline (no Civil 3D needed), run these before committing, one at a time:

```bash
npm run build
```

```bash
npm test
```

```bash
npm run docs:check
```

```bash
npm run version:check
```

`npm run docs:check` builds and then fails if `docs/tools.generated.md` doesn't match the tool manifest; CI runs the
same check (`node ./scripts/generate-tool-docs.mjs --check`), plus `node ./scripts/startup-smoke.mjs` and
`node ./scripts/inspect-package.mjs`, after the build. When you change the plugin, also compile it:

```powershell
dotnet build .\Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release
```

It only compiles against the Civil 3D 2027 reference DLLs (found in the default install, or with
`/p:Civil3DInstallPath=...` / `/p:Civil3DReferencesPath=...`); it doesn't start Civil 3D. If Civil 3D has the plugin
loaded, the DLL is locked: build into a new folder with `-o`. `npm run test:p2-boundaries` builds and runs the .NET
file-boundary harness.

The live suites (`npm run test:live-plugin`, `test:live-host`, `test:live-mcp-client`) need Civil 3D open with the AI
connection on, and `test:live-host` changes disposable drawings only when `CIVIL3D_LIVE_SMOKE_CONFIRM=DISPOSABLE_DRAWING`
is set. They are for a person sitting at Civil 3D: an automated session must not start Civil 3D, AutoCAD or
`accoreconsole`.

## Rules

1. **No job data in the repository.** Code, tests, skills, lessons and docs hold general know-how only. Examples are
   made up ("Elm Street Drainage", "RFI 12", "C-104", "MH-3"); never a real job's name, number, people, folders or
   file names. Every preference, lesson, skill and script is checked before it is saved (`generalKnowledge.ts`), and
   `tests/knowledge_audit.test.ts` applies the same check to everything git tracks under `skills/`. Facts about a job
   go in its project database, which lives in the user's shared folder, never here. `.gitignore` keeps out PDFs,
   images, templates, help files and local references; never `git add -f` around it.
2. **The shared project database follows the suite contract** in Project Analyzer's `docs/suite-knowledge.md`
   (`..\project-analyzer\docs\suite-knowledge.md`). `src/tools/project-db.sql` must stay a byte-for-byte copy of
   Project Analyzer's `schema/project-db.sql`, and `tests/fixtures/fixture.db` / `fixture-practices.json` copies of
   its fixtures: `tests/project_notes.test.ts` records their SHA-256 and compares them with `../project-analyzer/schema/`
   when that repository is checked out next to this one. A new contract means new copies (Project Analyzer's
   `schema/sync_copies.py`) and new hashes, on purpose. In the database Civil 3D MCP writes only its own `facts` and
   `folders` rows (`tool = 'civil3d'`), retires instead of deleting, opens it read-only to read, and treats it as
   reference only (never data for edits or imports). In `practices.json` it changes only `source: "civil3d"` entries.
3. **Drawings change only inside the plugin, through `CivilExecution`.** A handler reads with `CivilExecution.ReadAsync`
   and writes with `CivilExecution.WriteAsync`; don't open your own transaction or lock outside it, or the request
   skips the host queue, the drawing check and the one-undo-step guarantee.
4. **Declare actions truthfully.** Approval is derived from an action's `capabilities`, its name and `safeForRetry`
   (`approvalPolicy.ts`). A write declared as `query` would skip approval.
5. **Repo boundaries enforced by tests:** every `pluginMethods` name must be in `CommandDispatcher.cs`
   (`manifest_dispatcher_parity.test.ts`), reflection stays in `Civil3DCompatibility.cs`
   (`reflection_boundary.test.ts`), and plugin file writes go through `FileBoundary.cs` (`security_boundary.test.ts`).
6. **Keep `docs/tools.generated.md` regenerated.** After adding, renaming or removing a tool, action or plugin method,
   run `npm run docs:generate` and commit the result; `docs:check` and CI fail otherwise. Don't edit it by hand.
7. **Versions come from `package.json`.** `npm run version:sync` writes `Civil3D-MCP-Plugin/GeneratedVersion.props`
   and `packaging/claude-desktop/manifest.json`; `version:check` fails when they disagree.
8. **Three kinds of knowledge** (README, "Learning on the job"): how to drive Civil 3D -> preferences, lessons, skills,
   scripts (`civil3d\` in the shared folder); facts about one job -> `civil3d_project_notes`; practice true on every
   job -> `civil3d_practices`. Keep the server instructions, tool descriptions and the `working_safely_in_civil3d`
   skill saying the same.
9. **Comments say why.** Each file starts with a header saying what it is for and how it fits; exported functions,
   classes and command methods that aren't trivial get a `/** */` (TS) or `/// <summary>` (C#) doc comment.

## Adding a tool

A tool is a domain action on the TypeScript side that calls one or more plugin methods on the C# side.

1. **Plugin method.** In the matching `Civil3D-MCP-Plugin/*Commands.cs`, add
   `public static Task<object?> DoThingAsync(JsonObject? parameters)`: read arguments with
   `PluginRuntime.GetRequiredString` / `GetOptional*`, do the work inside `CivilExecution.ReadAsync` or `WriteAsync`,
   return a dictionary or anonymous object (serialized as the JSON-RPC result), and throw
   `JsonRpcDispatchException("CIVIL3D.<CODE>", message)` for expected failures, with a message that tells the AI what
   to do. Paths go through `FileBoundary`.
2. **Dispatcher.** Add `"doThing" => SomeCommands.DoThingAsync(parameters),` to `CommandDispatcher.cs`.
3. **Domain action.** In `src/tools/domains/<domain>Domain.ts`, add a zod args schema and an entry in `actions`:
   `action`, `inputSchema`, `responseSchema`, `capabilities`, `requiresActiveDrawing`, `safeForRetry`,
   `pluginMethods: ["doThing"]`, and `execute` calling `withApplicationConnection(c => c.sendCommand("doThing", ...))`.
   Add the action to the canonical exposure's `action` enum and `supportedActions`, and an alias exposure if wanted.
   A new domain is a new file exporting a `DomainToolDefinition`, listed in `MIGRATED_DOMAIN_DEFINITIONS`
   (`toolManifest.ts`) with its name added to `ToolDomain` (`toolMetadata.ts`).
4. **Tests and docs.** Extend the domain's tests (`tests/domain_manifest.test.ts` lists expected actions), run
   `npm run docs:generate`, and mention user-visible changes in `CHANGELOG.md` and the README's tool reference.
   `civil3d_orchestrate` picks the new action up automatically; add an entry to `EXPLICIT_TOOL_CATALOG`
   (`src/orchestration/ToolCatalog.ts`) only for better keywords or required fields.

A tool that doesn't need the drawing (knowledge, notes, practices) is a domain action whose `execute` doesn't call the
plugin; see `scriptingDomain.ts`.
