# How Civil 3D MCP works inside

This is the map for someone about to change the code. What the tools do for users is in the
[README](../README.md); how to check a change and the repository's rules are in [CLAUDE.md](../CLAUDE.md).

There are two programs. The **MCP server** (`src/`, TypeScript on Node.js 22.15+) talks to AI clients, validates
and approves calls, and keeps everything that isn't a drawing: the knowledge base, the job notes, the help index.
The **plugin** (`Civil3D-MCP-Plugin/`, C# on .NET 10, loaded into Civil 3D 2027) is the only code that reads or
changes a drawing. They talk JSON-RPC 2.0 over a loopback TCP socket, one request per connection.

```text
MCP client  --stdio-------------> src/index.ts (McpServer)
HTTP client --HTTP 127.0.0.1:3000-> src/httpBridge.ts
                                    |
                                    v
                    domainRuntime: schema -> approval -> idempotency -> action.execute
                                    |
                                    v  withApplicationConnection / sendCommand  (src/utils)
                          JSON-RPC over TCP 127.0.0.1:8080
                                    |
                                    v
        RpcTcpServer -> PluginRuntime.HandleRawRequestAsync -> CommandDispatcher -> XxxCommands.YyyAsync
                                    |
                                    v
                 CivilExecution.ReadAsync / WriteAsync  (one host gate, one at a time)
                                    |
                                    v
                       AutoCAD + Civil 3D .NET API  (and Roslyn scripts)
```

## The path of one request

1. **Client to server.** `src/index.ts` creates the `McpServer` with the instructions from `serverInstructions.ts`
   (rules for the AI, plus the user's preferences, the suite's practices and the list of their skills), registers
   the tools (`src/tools/register.ts`) and connects the stdio transport. It also starts the HTTP bridge.
2. **Tool handler.** Every manifest tool's handler is made by `createExposureHandler` in `src/tools/domainRuntime.ts`.
   It gives the call a request id (`src/utils/requestContext.ts`, carried with `AsyncLocalStorage` together with the
   client's abort signal), sends MCP progress notifications when the client asked for them, and logs start and end
   to stderr.
3. **Action and validation.** The exposure's `resolveAction` maps the raw arguments to an action name and its
   arguments (for the canonical tool it reads `action`; an alias fixes the action and renames fields, e.g.
   `alignmentName` to `name`). The action's zod `inputSchema` parses them.
4. **Approval.** `approvalPolicy.enforce` (`src/tools/approvalPolicy.ts`) decides from the action's `capabilities`,
   its name and `safeForRetry` whether approval is needed (delete, import, export; names such as `save`, `undo`,
   `publish`, `sync`, `fix`; any non-retryable mutation). If it is, the call must carry an `approvalToken` from
   `civil3d_request_approval`: single use, five minutes, bound to the tool, action, a hash of the exact parameters
   and a fingerprint of the active drawing (a hash of `getDrawingInfo`). `CIVIL3D_APPROVAL_MODE=disabled` turns this
   off for disposable test setups.
5. **Idempotency.** With an `idempotencyKey` (only on `safeForRetry` actions), `idempotencyStore` shares the result of
   an identical call for two minutes and refuses the same key with different parameters.
6. **Execute.** The action's `execute` calls `withApplicationConnection(client => client.sendCommand(method, params))`
   (`src/utils/ConnectionManager.ts`). Each `sendCommand` opens its own TCP connection to `CIVIL3D_HOST:CIVIL3D_PORT`
   (default `localhost:8080`), writes one JSON-RPC request, reads one response, and closes. A composite action calls
   several plugin methods in sequence and combines the answers in TypeScript (for example the surface comparison and
   pipe sizing actions). If the caller aborts, the socket is destroyed, and the plugin cancels the request.
7. **Plugin side.** `RpcTcpServer` accepts on loopback only (exclusive port, so only one Civil 3D window can have the
   connection on), reads one JSON object (at most 1 MiB, within 30 s, strict UTF-8) and watches for the client
   disconnecting. `PluginRuntime.HandleRawRequestAsync` checks the JSON-RPC envelope, records the method and request id
   for health and logs, and calls `CommandDispatcher.DispatchAsync`, a single `switch` from method name to handler.
8. **The host gate.** A handler does its drawing work inside `CivilExecution.ReadAsync` or `WriteAsync`. Both go
   through one `SemaphoreSlim`: at most 64 operations wait (`CIVIL3D.HOST_BUSY` beyond that) and one runs at a time.
   Every request runs on the drawing that is active when its turn comes; background work (jobs, corridor rebuilds)
   remembers the drawing it was started for and refuses with `CIVIL3D.CONFLICT`, changing nothing, if another
   drawing is active by then.
   - **Writes** wait until no user command is active (`CMDACTIVE`), then run in Autodesk command context with a
     document lock and one transaction, committed on success. One command context is one undo step. Afterwards
     `ViewFollower` pans the view to what changed.
   - **Reads** run on the AutoCAD main thread outside command context, without a lock, so they add no undo step;
     while a user command is active they retry until it ends. `CIVIL3D_READ_CONTEXT=command` restores the old
     command-context reads.
   - Waiting longer than `CIVIL3D_IDLE_WAIT_SECONDS` (default 30) fails with `CIVIL3D.HOST_BUSY` naming the active
     command, and work that never started is abandoned so it can't run late.
9. **Back to the client.** The handler's return value becomes the JSON-RPC `result`. In Node, the response schema (if
   any) validates it, a result over 32 KB or from a report/export/summary action is also kept for an hour as an MCP
   resource (`civil3d://reports/{id}`, `reportResourceStore.ts`), and the MCP result
   carries the JSON as text, as `structuredContent: { action, result }`, and the request id in `_meta`.

The HTTP bridge (`src/httpBridge.ts`) doesn't have its own path: `POST /execute` looks the tool up in
`toolHandlerRegistry.ts`, which captured every handler during registration (aliases too, even when they aren't
exposed over MCP), and calls the same handler. It binds to loopback unless `MCP_HTTP_TOKEN` is set.

## Tool domains (`src/tools/domains/`)

Each domain file exports one `DomainToolDefinition` (types in `domainRuntime.ts`):

- `domain`: a `ToolDomain` name from `toolMetadata.ts`.
- `actions`: a map from action name to `{ inputSchema, responseSchema?, capabilities, requiresActiveDrawing,
  safeForRetry, pluginMethods?, execute }`. `capabilities` (`query`, `create`, `edit`, `delete`, `import`, ...) drive
  approval and the MCP annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`); `pluginMethods` lists the
  C# methods the action calls, which a test checks against `CommandDispatcher.cs`.
- `exposures`: the MCP tools that reach those actions. The **first** exposure is the canonical tool (for example
  `civil3d_surface` with an `action` argument); the rest are aliases (`civil3d_surface_volume_calculate`). By default
  only the canonical tool of each domain is listed to MCP clients, plus all scripting tools and `civil3d_orchestrate`
  (`selectManifestExposures` in `toolManifest.ts`); `CIVIL3D_ENABLE_TOOL_ALIASES=true` lists every alias.
  An action can exist in `actions` without being in the canonical tool's `supportedActions`; then only an alias or
  the orchestrator reaches it.

`toolManifest.ts` lists every domain in `MIGRATED_DOMAIN_DEFINITIONS`. From it come the flat catalog
(`GENERATED_TOOL_CATALOG_ENTRIES`, `tool_catalog.ts`), the `civil3d://catalog/tools` resource, `civil3d_docs`, the
orchestrator's routes and `docs/tools.generated.md`.

Most domains are thin: each action forwards its arguments to one plugin method. The ones with logic in TypeScript:

| Domain file | What runs in Node |
|---|---|
| `workflowDomain.ts` | Multi-step workflows. Most are one plugin call (`*Workflow` methods in `WorkflowCommands.cs`); `grading_surface_volume`, `pipe_network_design` and `survey_import_adjust_figures` call other domains' actions in turn and report each step. Those inner calls use the actions' `execute` directly, so approval is checked once, on the workflow action |
| `surfaceDomain.ts` | `comparison_workflow` (two surfaces plus their volume) and `drainage_workflow` (flow path, elevations along it, Rational runoff) |
| `pipeDomain.ts` | `size_network` (Manning's equation picks a catalog part per pipe, optionally resizes) and `automate_profile_view` |
| `profileDomain.ts`, `alignmentDomain.ts` | `report` actions that sample stations and summarize |
| `hydrologyDomain.ts` | Nothing: every action, the runoff workflows included, is one call to `HydrologyCommands.cs` and its neighbours |
| `scriptingDomain.ts` | Code execution (`civil3d_execute`, `civil3d_query`, `acad_command`, `civil3d_script`) and every knowledge tool (below), which never call the plugin |
| `docsDomain.ts` | `civil3d_docs` (the catalog) and `civil3d_orchestrate` |

`civil3d_help` (`helpTool.ts`, `src/help/`) and the approval tools (`approvalTool.ts`) are registered directly, not
through the manifest. Help searches the Autodesk Offline Help installed with Civil 3D (index cached under
`%LOCALAPPDATA%\Civil3DMcp\help-index`) and doesn't need the plugin.

## The orchestration layer (`src/orchestration/`, `src/tools/civil3d_orchestrate.ts`)

`civil3d_orchestrate` takes either a plain-language `request` or an explicit `toolName` (+ `toolAction`,
`toolParameters`) and returns a plan; with `execute: true` it also runs it.

- `ToolCatalog.ts` holds the routes: hand-written entries (`EXPLICIT_TOOL_CATALOG`, with keywords and required
  fields) plus one derived entry per catalog tool and operation, so every new action is routable without editing it.
- `IntentRouter.ts` scores the request against every route: an exact tool name wins outright; otherwise keyword,
  title and domain matches add points and explicit entries get a bonus. It also pulls values out of the text
  (quoted names, "alignment X", paths, points, numbers).
- `ProjectContextService.ts` asks the plugin (`getProjectContext`) for drawing info, object types and the selection;
  `SelectionResolver.ts` fills a missing alignment, surface or object name from what the user has selected.
- `WorkflowPlanner.ts` turns the routed intent into a plan: the steps, which are ready, missing input or blocked
  (for example no surfaces in the drawing), and questions to ask the user. It only plans; it never executes.
- `civil3d_orchestrate.ts` puts these together and, when executing, calls the chosen tool through
  `executeRegisteredTool`, so the call goes through the same validation and approval as a direct call.

## The plugin (`Civil3D-MCP-Plugin/`)

| File(s) | Role |
|---|---|
| `PluginEntry.cs` | `IExtensionApplication`: captures the main-thread dispatcher, starts the command-line monitor and the ribbon; the commands `C3DMCPSTART` / `STOP` / `STATUS` / `FOLLOW`, `MCPRUN`, `MCPSCRIPTS`. The listener starts only on `C3DMCPSTART` (or `CIVIL3D_MCP_AUTOSTART=true`) |
| `McpRibbon.cs` | The Civil 3D MCP ribbon tab with the AI Connection toggle and status light |
| `RpcTcpServer.cs`, `JsonRpcProtocol.cs` | Loopback listener; result and error serialization, domain code to numeric code |
| `PluginRuntime.cs` | Request handling, per-request context (`AsyncLocal` method, id, cancellation, expected drawing), queue counters for health, argument helpers (`GetRequiredString`, `GetOptional*`) |
| `CommandDispatcher.cs` | Method name to handler; unknown names give `CIVIL3D.METHOD_NOT_FOUND` |
| `CivilExecution.cs` | The host gate described above |
| `*Commands.cs` | Handlers, one static class per area: `SurfaceCommands`, `AlignmentCommands`, `ProfileCommands`, `CorridorCommands`, `PipeNetworkCommands`, `PressureNetworkCommands`, `GradingCommands`, `HydrologyCommands`, `CatchmentCommands`, `LabelCommands`, `QcCommands`, `QuantityCommands`, `PlanProductionCommands`, `WorkflowCommands`, ... Each public method is `Task<object?> NameAsync(JsonObject? parameters)` |
| `Civil3DCompatibility.cs`, `CivilObjectUtils.cs`, `LookupUtils.cs` | Late-bound (reflection) access to API members that differ between releases, kept in one file; shared lookups by name |
| `FileBoundary.cs`, `SharedFolder.cs` | Every caller-supplied path: canonicalized, inside `CIVIL3D_FILE_ROOTS` / `IMPORT_ROOTS` / `EXPORT_ROOTS`, extension allowlist, atomic writes, no overwrite without `overwrite: true`; never importing from the suite's shared folder |
| `ScriptCommands.cs`, `ScriptEngine.cs`, `ScriptSandbox.cs`, `ScriptContext.cs`, `ScriptLibrary.cs`, `ScriptResultSerializer.cs`, `ReferenceDrawing.cs` | Roslyn code execution: source checks, the script globals and helpers, the read-only rollback for `civil3d_query`, `UNDO BEGIN/END` around write runs, saved `.csx` scripts, other drawings opened read-only |
| `JobCommands.cs`, `JobRegistry.cs` | Background jobs for a few long operations (PDF publish, DEM import, bulk QC) with status and cancel |
| `ViewFollower.cs`, `CommandLineMonitor.cs`, `PluginLog.cs` | View follows writes; command-line text and history (and the guard that stops UNDO reverting the user's work); rotating log in `%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log` |

## Knowledge: know-how, the job, practices

All of it lives outside the repository, in the suite's shared folder (`PROJECT_KNOWLEDGE_DIR`, by default
`Documents\Project Knowledge`), shared with Project Analyzer and TBC MCP. The contract is Project Analyzer's
`docs/suite-knowledge.md`. These tools run in Node only (`scriptingDomain.ts`) and don't need Civil 3D.

- **Know-how on driving Civil 3D** (`civil3d\` in the shared folder): `knowledgeBase.ts` keeps `preferences.json`
  and `memory\lessons.jsonl` (`civil3d_preferences`, `civil3d_memory`); `skillsLibrary.ts` merges the built-in skills
  in `skills\` with the user's (`civil3d_skills`); saved scripts are `.csx` files the plugin manages
  (`civil3d_script`). Before anything is saved, `generalKnowledge.ts` refuses text that names a job, its folders or
  files, a drawing file, an absolute path or the user's profile. Lessons whose topics appear in code sent to
  `civil3d_execute` / `civil3d_query` come back with the result.
- **The job** (`projects\<slug>.db`, one SQLite file per job): `projectDb.ts` opens it with Node's built-in
  `node:sqlite` (read-only for reads; short `BEGIN IMMEDIATE` writes; busy retries on a timer so the server keeps
  answering; damaged files never written), finds which job a drawing belongs to by folder, and reads Project
  Analyzer's zstd-compressed text and FTS index. `projectNotes.ts` implements `civil3d_project_notes`; Civil 3D MCP
  writes only its own notes and folders (`tool = 'civil3d'`). Every read answer carries a "reference only" note: the
  database is a snapshot for finding things, never data for edits. `userKnowledge.ts` reads the job's
  `User Knowledge` folder (text, .docx, .xlsx, .pptx) and only ever creates it. `openDrawing.ts` uses the database to
  open "the latest C-101".
- **Good practices** (`practices.json`): `practices.ts`, `civil3d_practices`; only entries with
  `source: "civil3d"` are changed by this tool.
- Shared files are changed under a lock file with a backup of the previous version and an atomic rename
  (`src/utils/atomicFiles.ts`).

The table layout of the job database is `src/tools/project-db.sql`, a copy of Project Analyzer's schema;
`tests/project_notes.test.ts` checks the copy and the fixture job in `tests/fixtures/`.

## How a tool is registered on both sides

```text
TypeScript                                            C#
src/tools/domains/fooDomain.ts                        Civil3D-MCP-Plugin/FooCommands.cs
  actions.do_thing = {                                  public static Task<object?> DoThingAsync(JsonObject? p)
    pluginMethods: ["doThing"],                           => CivilExecution.WriteAsync((doc, civilDoc, db, tr) => ...)
    execute: c.sendCommand("doThing", {...}) }  ---->   CommandDispatcher.cs
  exposures[0].supportedActions += "do_thing"             "doThing" => FooCommands.DoThingAsync(parameters),
src/tools/toolManifest.ts (new domains only)
docs/tools.generated.md (npm run docs:generate)
```

At startup `registerManifestTools` captures a handler for every exposure (for HTTP and orchestration) and registers
the selected ones with the MCP server, adding the shared `approvalToken` and `idempotencyKey` arguments, an output
schema and annotations. Nothing on the C# side is registered at startup: the dispatcher's `switch` is the table, and
`tests/manifest_dispatcher_parity.test.ts` keeps the two sides in step. See "Adding a tool" in
[CLAUDE.md](../CLAUDE.md) for the steps.

## Errors

- **Plugin.** Expected failures throw `JsonRpcDispatchException(code, message)` with a domain code such as
  `CIVIL3D.OBJECT_NOT_FOUND`, `CIVIL3D.INVALID_INPUT`, `CIVIL3D.HOST_BUSY`, `CIVIL3D.PATH_NOT_ALLOWED`;
  `JsonRpcProtocol.NumericErrorCode` gives the JSON-RPC number and the domain code goes in `error.data.code`.
  Cancellation becomes `CIVIL3D.CANCELLED` (-32010). Anything else becomes `CIVIL3D.INTERNAL_ERROR` (-32603) with the
  exception type and message and the path of the plugin log, which has the stack trace. Malformed or non-UTF-8
  requests are refused before dispatch.
- **Node.** `SocketClient.ts` turns a JSON-RPC error into a `Civil3DRpcError(message, code, rpcCode)`; connection
  failures and timeouts (`CIVIL3D_CONNECT_TIMEOUT`, `CIVIL3D_COMMAND_TIMEOUT`) are plain errors with clear messages.
  `domainRuntime.ts` catches everything a handler throws and returns an MCP result with `isError: true`, the text
  `"<tool> action '<action>' failed: <message>"`, `errorCode` and `rpcCode`: taken from the error when it has them,
  otherwise inferred from the message (timeout, connection, not found, ...). Knowledge tools throw
  `knowledgeError(code, message)` the same way. Messages are written for the AI: they say what to do next.
- **HTTP bridge.** `executeRegisteredTool` turns an error result back into a thrown error, and the bridge maps it to
  an HTTP status and `{ error: { code, message } }`.
