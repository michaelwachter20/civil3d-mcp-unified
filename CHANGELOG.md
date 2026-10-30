# Changelog

## v2.4.0 — 2026-10-27

- **Points from a file go through Civil 3D's own importer; any other point-making asks you in Civil 3D.**
  `civil3d_point` `import_file` imports a point file with `CogoPoints.ImportPoints` and a Civil 3D point file format
  by name (`list_formats`), as Import Points does by hand. Creating points any other way (`create`, `import` with
  typed-in data, C# or a saved script that adds points, the CREATEPOINTS commands) first opens a window in Civil 3D
  that only the user can answer (`PointCreationGate`); no answer in 60 s counts as no, so nothing is made after the
  request has timed out. `CIVIL3D_POINT_CREATION=off` refuses them without asking. The server instructions and
  `working_safely_in_civil3d` say so; the daily field import skill (which applies duplicate offsets itself) asks
  once per run.

- **Guards on actions that could do harm by mistake.** `civil3d_point` `transform` with no `pointNumbers` or
  `groupName` used to move every point in the drawing; that now takes `allPoints: true`. The parcel report only reads;
  writing it to a CSV is the new `civil3d_parcel` `export_report`, which needs approval like any export, and so does
  `civil3d_qc` `generate_report`, which writes a file. Profile sampling (`sample_elevations`) refuses an interval of
  zero or less, which made the plugin loop forever and freeze Civil 3D, and any interval giving over 100,000 samples.

- **Point import and export: easting and northing were swapped.** `pnezd` read the northing as X; `penz` read the
  elevation as Y and the northing as Z; the export wrote the same way, so files from it were wrong in other programs.
  Now X is the easting and Y the northing in every format, both ways (`PointText`). Numbers are read and written with
  a dot for decimals whatever the PC's regional settings, a description keeps its spaces ("EP 12"), and a bad line
  creates no points. Points keep their numbers from the file when free in the drawing (`keepPointNumbers`, default
  true); the answer lists any renumbered.

- **Pipe sizing (`size_network`) in the drawing's units.** Catalog sizes ("15 inch", "375 mm") are converted to drawing
  units, Manning's uses k = 1.486 in feet and 1 in meters, and each pipe's slope comes from its ends (`PipeMath`; Civil
  3D's own `Pipe.Slope` isn't documented as a percent or a ratio). Applying swaps only the pipes whose part isn't the
  chosen one, and sends the part only: before, every pipe with a design flow was swapped, then resized to the number in
  the part name (inches) as if it were drawing units. Nothing is changed in a drawing whose units aren't feet or meters.
  The hydraulics checks (`hydraulic_analysis`, `calculate_hgl`) use the same slope, Manning's, head loss and gravity
  in the drawing's units, and say which units their numbers are in.

- **Interference check (`check_interference`) works.** It used to check the names and always answer "no conflicts".
  Against a pipe network: the clearance between pipe outsides for every pair; against a surface: the cover over each
  pipe, sampled along it (`minClearance`, `sampleSpacing`). Pipes only; structures aren't checked.

- **Surface QC:** the triangle count is read from a TIN surface's TIN properties; it was read where Civil 3D keeps none,
  so every surface with points was reported as having no triangles.

- **Fixing drawing standards:** `colorIndex` and `lineweight` go to the layers that break the check's color or
  lineweight rule (or every layer with `applyToAllLayers`), not to every layer; xref layers are left alone, and a layer
  that already has the value isn't listed as changed.

- **The user's own standards stay usable:** a drawing in a job's User Knowledge folder can be read as a go-by
  (`Reference(name)`), used as a template or title block, or opened read-only (`FileBoundary.ResolveReferencePath`).
  Data imports (points, surfaces, LandXML) and drawings opened to edit are still refused from anywhere in the shared
  folder.

- **More than one folder per job** (project-analyzer's `docs/suite-knowledge.md`, "More than one folder per job"):
  a job's other folders (`meta.job_folders`, files under `<job folder: NAME>\`) count for finding a drawing's job;
  `locate`, `latest` and `file` (new `onDisk`) give each file's place in its own folder, and `file` also takes a path
  on this computer in any of them (`projectDb.onDisk`, `storedPath`). The tool description explains the
  `<job folder: …>\`, `<outlook>\` and `<user knowledge>\` paths.
- **The job's database is for reference only** (project-analyzer's `docs/suite-knowledge.md`, "Reference only"): it
  is a snapshot of the job's files for finding and understanding things, never a source of coordinates, elevations,
  points, quantities or text for editing or importing. Those come from the current file the user names, and with
  none named the AI asks which file is current. The rule is in the server instructions, the `civil3d_project_notes`
  description and the `working_safely_in_civil3d` skill; every read answer carries it as `referenceOnly`, with the
  date of the last Project Analyzer run. The plugin refuses to import or open files inside the suite's shared
  folder (job databases, backups, User Knowledge): `FileBoundary.ResolveImportPath`, using the shared-folder lookup
  now in `SharedFolder.cs`.
- **The job's database is opened the way the suite contract now says** (project-analyzer's
  `docs/suite-knowledge.md`, "Opening"; `project-db.sql` and the test fixture updated from project-analyzer):
  - **Read actions open it read-only.** Only `link`, `unlink` and the note actions open it read-write; the job list
    reads read-only too. A read changes nothing: WAL is set only when a database is created or written, and nothing
    is created or imported.
  - **The readers' indexes:** a write adds the schema's `CREATE INDEX IF NOT EXISTS` indexes an older database lacks
    (paths, series keys and entity names case-insensitively, the timeline by date and by file, story steps).
  - **Busy:** SQLite waits 2 s at a time instead of 30 s, and the server waits between tries on a timer, so it keeps
    answering. After about 10 s it says "The job's database is busy (Project Analyzer is probably writing to it); try
    again in a moment." instead of "database is locked".
  - **Damaged:** a database SQLite can't read, or whose `PRAGMA quick_check` isn't ok, is never written or made again
    from the schema (an unreadable `meta` no longer counts as a new database). The error says how to restore a
    backup: `python analyze.py restore --project "<job>"`, or the newest copy in `projects\<job>.backups\`. The job
    stays in the list with a `note`, and sections no longer come back silently empty from a damaged file.
  - **Backups of the databases it makes:** before the first write of each day to a job database Civil 3D MCP made
    itself (no `source_root`), a copy goes to `projects\<job>.backups\<job> <date> <time>.db` (Project Analyzer's
    naming), checked with `PRAGMA quick_check`; the newest 3 are kept. A failed backup never stops the write.
- **`civil3d_project_notes action=checks`:** what Project Analyzer found worth a look, high first, then medium and
  info, by kind and title, with up to 10 file paths each and the counts by severity and kind. Optional `severity`,
  `kind`, `limit` (default 50).
- **`civil3d_project_notes action=changes`:** requirement changes (events of kind `change`) and the file changes an
  update found (`file_changes`), newest first. The last 30 days by default; optional `since` (YYYY[-MM[-DD]]),
  `file`, `limit`.
- **Faster on big jobs, same answers:** `entity` finds the name and its stories in SQLite instead of reading every
  entity and story; each answer looks up its file paths in one query; `file`, `locate` and `latest` use the
  case-insensitive indexes; `latest` reads all its series in one query; `search` keeps the blocks it decompressed;
  the job-data check on every save of know-how no longer reopens databases that didn't change.
- **Knowledge files several programs share:**
  - `preferences.json`, `memory\lessons.jsonl` and `practices.json` are changed under a lock file (`<file>.lock`,
    holding the process id): two AI apps adding at the same moment no longer lose one of the two. A lock whose process
    is gone, or older than a minute, is taken over; after 5 s of waiting the error names the lock file.
  - Before each change the file as it was goes to `<name>.backups\` next to it (the newest 3).
  - Writes go through a temp file that never stays behind, and the rename is retried while Windows holds the file
    (virus scanner, indexer). Temp files a crash left (older than an hour) are removed at startup.
  - A line of `lessons.jsonl` that isn't a lesson is skipped and reported, and kept in the file, instead of breaking
    every lesson. A `preferences.json` that doesn't parse gives an error naming it and is never rewritten; a copy of
    it is kept next to it when a change is refused.
- **User Knowledge paths longer than Windows' limit** are listed and read (the `\\?\` form); a document or folder that
  can't be read is left out and named under `unreadable` in `action=files`.
- **The plugin finds the same shared folder as the server** when Documents is redirected (OneDrive):
  `%USERPROFILE%\Documents`, not the shell's Documents folder. The uninstaller's "Kept" message matches. Prebuilt
  plugin rebuilt (still 2.4.0).
- Tests: the three contract copies (`project-db.sql`, `fixture.db`, `fixture-practices.json`) are checked against
  their recorded SHA-256 always, and against project-analyzer's `schema\` when it is checked out next to this
  repository.

- **`civil3d_project_notes action=entity` stays a readable size:** at most 10 story lines of 30 steps each (as TBC
  MCP), with `stepsTotal` when a story has more. On a real job one RFI's answer was 143,000 characters; it is now
  26,000.
- **`civil3d_project_notes action=timeline` and `entity` ask SQLite for just the events they show,** instead of
  reading every event and filtering them here. On a real job a timeline went from 1.5 s to 15 ms and an entity from
  about 1 s to 0.1–0.3 s, with the same answers.

- **`civil3d_drawing action=open`** opens a drawing the way the user means it:
  - **A drawing already open** that matches the name comes first: with or without `.dwg`, or the same name without
    "rev N", dates or "current". It becomes the active tab.
  - **Otherwise the latest version** in the job's database: the DWG modified last (`civil3d_project_notes
    action=latest`).
  - **`near`** gives words about the folder, for one name used for different drawings in several folders.
  - The result names the file, why it was chosen, its older versions, and a newer version on the drive when an open
    drawing was used. Several matches give `choices` to ask about.
  - The plugin's new `openDrawing` opens a path inside the allowed folders (`CIVIL3D_FILE_ROOTS`). It opens
    read-only when asked or when someone else has the file open, and names them from the `.dwl` lock file. A drawing
    already open becomes the active tab.
- **`civil3d_project_notes action=latest`** (file, optional near): a file's versions in the job folder, the latest
  last, with full paths, title-block date and revision. It is read from Project Analyzer's `intel_series` (schema
  updated).
- **The go-by skills** (`compare_to_goby`, `convert_drawing_from_goby`) find the go-by by name. They try an open
  drawing first, then its latest version. They say which file they used, and ask when the folders look like different
  areas.

- **The shared folder is `Documents\Project Knowledge`, and no tool owns it** (project-analyzer's
  `docs/suite-knowledge.md`, "Where"). The folder is the first of `PROJECT_KNOWLEDGE_DIR`; `CIVIL3D_KNOWLEDGE_DIR`
  (its earlier name, still honoured); `Documents\Project Knowledge` if it exists; an earlier default that exists,
  used as it is (`Documents\Civil3DMcp-KnowledgeBase`, then `Documents\Civil3DMcp`); else
  `Documents\Project Knowledge`. `practices.json` and `projects\` stay at its top.
  - Nothing renames or moves the shared folder any more (the 2.2.0 rename from `Documents\Civil3DMcp` is gone, in
    the server and in the plugin): an older tool on the same PC would lose it. An existing folder keeps working; to
    rename it, update every suite tool, close them and rename it to `Project Knowledge`.
  - Civil 3D's own know-how moves into `<shared>\civil3d\` (new `CIVIL3D_MCP_KNOWLEDGE_DIR` overrides it):
    `preferences.json`, `memory\`, `skills\` (`CIVIL3D_USER_SKILLS_DIR` still overrides) and `scripts\`. On the first
    start each of them still at the top of the shared folder is renamed into `civil3d\` when `civil3d\` doesn't have
    it yet. One that can't be moved (in use) is used where it is and moved on a later start. When both exist,
    `civil3d\` is used and the old one is left alone. Nothing is deleted. Nothing is moved into a folder set with
    `CIVIL3D_MCP_KNOWLEDGE_DIR`, nor skills or scripts whose own variable is set.
  - `civil3d_health` returns `knowledge` (`sharedFolder`, `civil3dFolder`, and `notes` about know-how still at the
    top); the server instructions name both folders and carry the same notes.
  - The plugin's `MCPRUN` / `MCPSCRIPTS` scripts folder is `CIVIL3D_SCRIPTS_DIR`, else
    `CIVIL3D_MCP_KNOWLEDGE_DIR\scripts`, else `<shared>\civil3d\scripts`, found the same way, using the earlier
    `<shared>\scripts` while `civil3d\scripts` doesn't exist yet. Prebuilt plugin rebuilt (still 2.4.0).
  - `project-db.sql` updated from project-analyzer (header comment only). README, INSTALL.md, the installer's
    uninstall message and the tool descriptions say `Documents\Project Knowledge` / `<shared folder>`.
- **User knowledge per job.** Each job has a `User Knowledge` folder,
  `<shared folder>\projects\<job>\User Knowledge\`, for the user's own documents about that job: proposal
  guidelines, building-type requirements, a client's standards, design manuals, code books (subfolders are fine).
  It is created, empty, when the job is listed, opened or linked; Civil 3D MCP never writes anything else there.
  Project Analyzer reads it into the job's database (paths `<user knowledge>\...`); Civil 3D MCP uses that text
  when the database has the document as it is now, and otherwise reads text, Word (`.docx`), Excel (`.xlsx`) and
  PowerPoint (`.pptx`) documents itself with Node's built-ins.
  - `civil3d_project_notes action=files` now lists that folder: each document's size, modified time,
    `readByAnalyzer` and `readableHere`. Files earlier versions kept directly in `projects\<job>\` are listed under
    `elsewhere`, with a suggestion to move them into `User Knowledge`.
  - New `action=read` (file, optional `maxChars`, default 20,000, and `offset`) returns a document's text in pieces,
    or says that Project Analyzer reads it (`python analyze.py knowledge --project "<job>"`) or that the AI app can
    open it itself (PDFs, scans, drawings, older Office formats). Paths outside the folder are refused.
  - `search` marks user-knowledge hits (`userKnowledge: true`) and also searches the folder's documents the database
    doesn't have yet, when Civil 3D MCP can read them. `overview` counts job files without user knowledge and lists
    the documents under `userKnowledge`; the session briefing names them too.
  - The tool description, the server instructions and the `working_safely_in_civil3d` skill tell the assistant to
    follow a document that applies to the task and name it, and to tell the user when one conflicts with a fact, a
    practice or the drawings.
- `link`, `list` and `files` return `userKnowledgeFolder` (was `filesFolder`).
- README: "Works on its own, better together" (what Civil 3D MCP does alone, with Project Analyzer and with TBC MCP)
  and the User Knowledge folder; also described in the release's INSTALL.md.
- **No job data in Civil 3D's know-how, checked on every save.** Before 2.4.0 only practices were checked. Now every
  preference, lesson (and its topics), skill (name, category, description, content, parameters), skill section and
  saved script (name, description, code, parameters) is refused when it contains:
  - an absolute path outside `C:\Program Files`, `C:\ProgramData` and `C:\Windows`, also escaped in C# code;
  - a network path (`\\server\share`), also escaped;
  - a path inside the user's profile;
  - the name of a job in `<shared>\projects` (its name, its slug, or the last folder name of its folders; generic
    names such as `Survey` left out), read without changing anything (`jobIdentities`);
  - a drawing or survey file named for real (`C-104.dwg`, `Elm St.vce`; `<drawing>.dwg`, `*.dwg` and `.dwg` are
    fine).

  The rules are TBC MCP's (`knowledge.job_data_problems`), so both tools refuse the same things
  (`src/tools/generalKnowledge.ts`). The refusal says what was found and points to `civil3d_project_notes`.
  Practices keep their stricter rules (no path or file name at all) and get the job-name check by slug and folder.
- **Audit test for the shipped know-how** (`tests/knowledge_audit.test.ts`): every file under `skills\` that git
  tracks, `lessons.seed.jsonl` included, is checked line by line with the same rules, against the jobs on the PC
  running the tests. All 38 pass.

## v2.3.1 — 2026-10-27

- **Security updates for the server's dependencies**, so CI's `npm audit` passes again: zod 3 → 4 (zod 3 had a
  memory-exhaustion advisory with no fix in 3.x), plus fixed versions of `@modelcontextprotocol/sdk`, body-parser,
  undici and fast-uri. Tool parameter schemas updated for zod 4 (`z.record` names its key type); the job tools'
  `createdAt` / `completedAt` are described as strings (they always arrive from the plugin as ISO text).

## v2.3.0 — 2026-10-26

- **Project notes moved to the suite's project database.** Each job now has one SQLite database,
  `<knowledge dir>\projects\<job>.db`, shared with Project Analyzer (which reads the whole job folder into it:
  every file's text, compressed, plus references, people, events and checks) and TBC MCP. It replaces
  `projects\<job>.json`. A note is a fact owned by Civil 3D MCP: one line, a duplicate check, ids `fact-N`, a source
  (the active drawing or the user), optional topics, and `supersedes` to correct another tool's fact. Civil 3D MCP
  changes only its own notes and folders; `remove` retires a note (kept, hidden, id never reused). Saving no longer
  drops fields written by other tools.
- The first time a job is opened, its old `<job>.json` is imported once (name, folders, and each note with its
  author tool and times) and renamed to `<job>.json.imported`.
- A drawing's job is found by the job folder Project Analyzer read (`source_root`) and every linked folder, deepest
  folder wins, case-insensitive.
- New `civil3d_project_notes` read actions: `overview` (key and newest facts, open questions, rules, high-severity
  checks, people, practices), `search` (full text of every file, codes such as `MH-3`, `12+50`, `C-104` kept
  together, plus facts), `file`, `entity`, `timeline` and `locate` (where a path written in a file, such as an xref,
  is in a copied job folder). The first code result of a session now carries a short overview of the job (key facts,
  high-severity checks, missing referenced files).
- New tool `civil3d_practices`: good practices shared by the suite in `<knowledge dir>\practices.json` (list,
  search, add, update, remove = retire; only entries added by Civil 3D MCP can be changed; practices naming a job,
  folder or file are refused). Current practices are sent with the server instructions next to the preferences.
- The server instructions, tool descriptions and the `working_safely_in_civil3d` skill explain the three kinds of
  knowledge: how to drive Civil 3D (preferences, lessons, skills, scripts), this job (project notes) and engineering
  practice (practices).
- **Requires Node.js 22.15 or newer** (was 18.17): the project database uses Node's built-in `node:sqlite` and zstd,
  no native packages. Only project notes need it; the rest of the server still starts on an older Node.js. The
  Docker image is now based on `node:22-alpine`.
- The knowledge folder should stay on a local disk (SQLite databases can't be shared safely over a network folder);
  share a job with Project Analyzer's `kb.py export` / `merge`.

## v2.2.4 — 2026-10-22

- New skill `add_points_to_schedule_table`: add a point group's points to a model-space coordinate schedule table in
  station order, mirror the rows into the companion spreadsheet (sorted, formatting kept with each row), give each
  point a station/offset label like its neighbours and place the labels clear on their plan sheets.
- Label placement uses exact in-memory Explode geometry (`declutter_point_labels` v4 with annotation obstacles). New
  skill `clear_annotation_clashes`: per-sheet clash check (box vs box, leader lines vs boxes, MLeaders whose leader
  runs through their own text, labels running off their sheet unless another sheet shows them in full) and a
  uniform fix. Explode follows the active viewport's twist and annotation scale, so each twist group is checked from
  a viewport like it; Civil 3D labels explode only in the write context.
- `points_by_segment_report`: the day is reported combined, not per crew; job numbers matched from a read-only
  request tracker in a table headed by the field date; edge tie-in shots go to the main work's job; a COMPLETE /
  IN PROGRESS status column (complete only when the whole area is covered and both edges are shot).
- Fix: `selected_objects_info` failed on objects without a name or description.
## v2.2.3 — 2026-10-20

- New skill `mass_save_as`: Save As several open drawings to new names so the open tabs become the new files.
  Checks every target first and never overwrites. It switches to each drawing and runs SAVEAS, because
  `Database.SaveAs` from a script only writes a copy and the tab keeps its old file.
- New skill `sheet_station_titles`: sets each sheet's station-range subtitle to what its plan viewport shows, to the
  nearest foot, chained page to page with the smaller station first. A read-only plan flags gaps, overlaps and
  sheets with two alignments for the user; apply mode only replaces the station text.

## v2.2.2 — 2026-10-20

- The knowledge base folder is now `Documents\Civil3DMcp-KnowledgeBase` (was `Documents\Civil3DMcp`): preferences,
  lessons, project notes, skills and saved scripts. The MCP server and the plugin each move the old folder on first
  use when the new one doesn't exist. `CIVIL3D_KNOWLEDGE_DIR` and `CIVIL3D_SCRIPTS_DIR` still override it.
- Project files folder: `projects\<project>\` next to `<project>.json`, for PDFs, spreadsheets, images or anything else
  the user wants read for that project (subfolders included). Created with the project; `civil3d_project_notes
  action=files` lists it, and the first code result of a session names it with its files.
- New skills: `daily_field_import` (import a day's checked field files into an all-points drawing: a layer and a
  point group per crew, duplicate point numbers offset by a fixed step, point data exactly as in the file, field files
  read only) and `points_by_segment_report` (shot types, segment and station range to the nearest +25 ft per group).
- README brought up to date with 2.1.0 (several drawings, command line, HOST_BUSY, UNDO refusal, script helpers, crash
  guard) and garbled characters (bullets, dashes, arrows) fixed in README.md and docs/DEPLOYMENT.md.
- Release tags v2.2.0 and v2.2.1 did not publish (the tag didn't match package.json); this is the first 2.2 release.

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
