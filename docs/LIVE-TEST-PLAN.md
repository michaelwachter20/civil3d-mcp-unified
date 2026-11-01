# Live test plan: Civil 3D MCP with Project Analyzer

For a day at Civil 3D 2027. Everything runs on throwaway drawings and a made-up job. Nothing here touches a real job or
a real drawing. An automated session never starts Civil 3D: a person opens it and turns the connection on.

Written from a review of the code against the plugin's source and the names in Civil 3D's own API, and first run on
2026-11-01: part 5 gives the results.

Mark each test **pass**, **fail** (with what happened) or **skipped**. A crash of Civil 3D stops the session: note the
test, restart, and go on with the next one.

## 0. Before starting

The person at Civil 3D:

1. Opens Civil 3D 2027. If it asks about the plugin, chooses **Always Load**.
2. Opens a new blank drawing and saves it as `Documents\Test Jobs\9999 Aardvark Ln\CAD\Elm-Test.dwg`.
3. Turns on **AI Connection** on the Civil 3D MCP ribbon tab.
4. Starts a new Code tab session, which has the `civil3d_*` tools.

Ready when `civil3d_health` reports plugin 2.4.1, and `civil3d_project_notes action=overview` on that drawing names the
job (from `testjob.py`; see part 6 for how to rebuild it). First in any session, read the skill
`working_safely_in_civil3d`.

Shared setup, made once with the tools below: 12 points numbered 1-12 with descriptions such as `MH-3`; a TIN surface
`EG`; an alignment `Elm St` of 3 vertices; profiles `EG` and `FG`; a site `Elm Site`.

## 1. The automated suites (run these first)

| # | Command | Expected |
|---|---|---|
| 1.1 | `npm run test:live-plugin` | Read-only checks pass; drawing context returned |
| 1.2 | `npm run test:live-mcp-client` | Typed tools, resources and progress pass |
| 1.3 | `CIVIL3D_LIVE_SMOKE_CONFIRM=DISPOSABLE_DRAWING`, then `npm run test:live-host` | Create, undo, rollback, delete, cancel pass on throwaway drawings |

## 2. What changed in 2.4.0 and 2.4.1 (the main point of the day)

### Points

| # | Do | Expected |
|---|---|---|
| 2.1 | `civil3d_point action=list_formats` | The drawing's point file formats |
| 2.2 | `import_file` of `Survey\topo PNEZD.csv` into group `Elm` | 40 points, group made |
| 2.3 | `import_file` of `Survey\topo PENZD.txt` with a PENZD format | 20 points, northing and easting not swapped |
| 2.4 | `import_file` of `duplicate numbers.csv` | Civil 3D's own duplicate handling; reported, not silent |
| 2.5 | `import_file` of `bad line.csv` | Good lines in; the bad one reported |
| 2.6 | `import_file` of `commas in description.csv` | Description keeps its comma |
| 2.7 | `import_file` of `unicode description.csv` | `±`, `Ø`, `°` intact |
| 2.8 | `import_file` of `negative and huge.csv`, `no descriptions.csv`, `with header.csv`, `empty.csv` | Clear result each; no crash |
| 2.9 | `create` one point; the person chooses **Allow** | Point made |
| 2.10 | Same; the person chooses **Don't allow** | Nothing made; `CIVIL3D.USER_DECLINED` |
| 2.11 | Same; nobody answers for 60 s | Nothing made, and nothing appears later |
| 2.12 | `civil3d_execute` calling `CogoPoints.Add`, then `acad_command` CREATEPOINTS | The same window each time |
| 2.13 | `transform` with no numbers or group | Refused; asks for `allPoints: true` |
| 2.14 | `transform scaleFactor: 0` | Refused by the schema |

### Names, guards and undo

| # | Do | Expected |
|---|---|---|
| 2.20 | Create a surface with a misspelled style | Refused, listing the names the drawing has |
| 2.21 | Same for a layer and a site | Refused the same way (a review suspects layers still slip through) |
| 2.22 | `save` on a new drawing never saved | Refused; the template isn't touched |
| 2.23 | Two AI writes, `undo steps=1`, then `redo steps=1`, then `undo steps=2`, `redo steps=2` | Each step exact; `command_line` shows MREDO working |
| 2.24 | Person runs LINE, then `undo` | Refused as a conflict; `force=true` overrides |
| 2.25 | `extract_contours` | Needs approval now |
| 2.26 | `check_k_values` on a curve from -2% to +3%, L=20, in a foot drawing at 30 mph | K=4 reported, fails (needs 37); result says mph |
| 2.27 | Same in a metre drawing at 50 km/h | K in metres; needs 13 for sag |
| 2.28 | `delete_pvi station=250` with PVIs at 100, 300 and 500 | Refused, naming the nearest; nothing deleted |
| 2.29 | `add_curve` at a station with no PVI | The same refusal |

### Scripts

| # | Do | Expected |
|---|---|---|
| 2.30 | `civil3d_query` with `while (true) { }`, `CIVIL3D_SCRIPT_TIMEOUT_SECONDS=10` set where Civil 3D starts | **Stops at the timeout; Civil 3D stays alive** |
| 2.31 | Same with `for (;;) ;` and a lambda holding a loop | Stops |
| 2.32 | `civil3d_execute` that makes a line, then loops forever | Stops; the line isn't committed |
| 2.33 | A script with a variable named `CancellationToken` | Runs as written, without the guard |
| 2.34 | Runtime error on line 5 of a script with a loop on line 2 | The error says line 5 |
| 2.35 | `civil3d_script action=compile` on every skill's template | All compile (skills moved to `Args` without Civil 3D) |
| 2.36 | Run `create_tin_surface`, `create_polyline`, `station_offset`, `surface_volume` with `args` | Work from their arguments |
| 2.37 | Sandbox: `Process.Start`, file delete, reflection | Blocked |
| 2.38 | 24 scripts never run before, reading `memoryUsageMb` from `civil3d_health` after each | About 450 MB for the first (read once), then flat |

## 3. Civil 3D MCP with the job's database

`PN` is `civil3d_project_notes`, `D` is `civil3d_drawing`. The active drawing is inside the test job unless it says so.

### Reading

| # | Do | Expected |
|---|---|---|
| 3.1 | PN `overview` | Job named; `referenceOnly` present; people, high checks and practices listed |
| 3.2 | First `civil3d_query` of the session | Carries a short briefing, once |
| 3.3 | PN `search` for `MH-3`, `12+50`, `C-104`, `RFI 12` | Hits with paths |
| 3.4 | PN `search` for `José Núñez`, `O'Brien`, `Smith-Lee` | Found, as people too |
| 3.5 | PN `entity` for `RFI 12` and `Robin Lee` | Files, events, story |
| 3.6 | PN `timeline` from 2026-02-01 to 2026-04-01 | Events in order |
| 3.7 | PN `checks` | RFI 13 no reply, CO 4's two amounts, missing Topo and Missing Sheet |
| 3.8 | PN `changes` | "Going forward" and "please use" sentences, newest first |
| 3.9 | PN `locate` for `P:\Jobs\1234 Elm St\CAD\Topo.dwg` | Missing, with where it should be |
| 3.10 | PN `locate` for `CAD\Base.dwg` as an xref of C-104 | Found in the job folder |
| 3.11 | PN `file` for `archive.zip::inside/nested.zip::deep note.txt` | That file, found by its stored path |
| 3.12 | PN `latest` for `C-101` | `current` is the latest; revisions listed |
| 3.13 | PN `add` a note, then `search` for it | Found; visible to `kb.py search` too |
| 3.14 | Add a note, supersede it, add the first text again | Added, not "unchanged"; search marks the old one superseded |
| 3.15 | PN `files` and `read` after dropping a `.docx` in User Knowledge | Listed and read |

### Open

| # | Do | Expected |
|---|---|---|
| 3.20 | D `open` name `C-101` | Opens `C-101 GRADING current.dwg` |
| 3.21 | D `open` `C-101` with an older revision already open | Stays on the open drawing; says a newer one is on the drive |
| 3.22 | Save that open drawing, then D `open` `C-101` again | No "newer" any more |
| 3.23 | D `open` `C-109` | **Asks**: two phases; nothing opens |
| 3.24 | D `open` `C-109` near `phase 2 area b` | Opens that one |
| 3.25 | Open both C-109s, then D `open` with a full path | Switches to the right one |
| 3.26 | D `open` `C-101` with project `Aardvark Lane Test` while `5678 Oak Ave\CAD\C-101.dwg` is open | Opens this job's C-101 |
| 3.27 | The person opens a drawing, then a second session or user holds it | Opens read-only and says who has it |
| 3.28 | D `open` on a job outside `CIVIL3D_FILE_ROOTS` | Clear PATH_NOT_ALLOWED; nothing opens |
| 3.29 | D `open` `CAD\Odd\damaged.dwg`, `empty.dwg`, `not really a drawing.dwg` | Clear refusals; Civil 3D survives |

### While the analyzer works

| # | Do | Expected |
|---|---|---|
| 3.30 | `python testjob.py update ...`, then `analyze.py update` while calling PN `overview` in a loop | No raw "locked"; a note added meanwhile is kept |
| 3.31 | During the rebuild, PN `checks` and `latest` | Say connections are being rebuilt, not just "none" |
| 3.32 | PN `file` for the moved minutes and the removed RFI 13 | Says moved, and gone |
| 3.33 | PN `entity` for `Dana Reyes` | New person, linked to the older file naming them |
| 3.34 | `kb.py whats-new` | 2 added, 1 changed, 1 moved, 1 removed |

### Linking and trouble

| # | Do | Expected |
|---|---|---|
| 3.40 | PN `link` project `Another Job`, folder the job's own | Refused, naming the job |
| 3.41 | PN `link` on a new unsaved drawing | Refused: save it first |
| 3.42 | Drawing outside any job: PN `overview` | Asks which job |
| 3.43 | Copy a job database, damage the copy, restart the server | The drawing's answer names the damaged job and how to restore |
| 3.44 | Hold a write lock on the database for 15 s, then PN `add` | Busy message near 10 s; `overview` still answers |

## 4. Every tool area, on `Elm-Test.dwg`

**[D]** marks something destructive: only on the throwaway drawing. Writes go preview, approval, call.

| Area | Steps | Edge cases |
|---|---|---|
| Drawing | `info`, `settings`, `new` | Read-only drawing; a Unicode file name |
| Surfaces | `create` EG, `get` while empty, `add_points`, `add_boundary`, `add_breakline`, volume between EG and FG twice | `get` on a volume surface; duplicate XY; points off the surface; 255-character name; net sign matches the report |
| Alignments | `create` Elm St, `report`, `station_to_point` at start and end, `set_station_equation` | Station end+0.001; `offset_create`; duplicate points; [D] `delete` with a dependent profile |
| Profiles | `create_from_surface`, `create_layout`, `add_pvi` ×3, `add_curve` L=20, `view_create` | `get` on an empty layout |
| Corridors (build one by hand) | `get`, `rebuild`, `target_mapping_get`, `target_mapping_set`, `region_add`, [D] `region_delete` | Verify each in Civil 3D's own panels |
| Sections | `create_sample_lines interval=50`, `view_create`, `view_export` | End station included; misspelled surface; stations 100.001 and 100.004; width 0; beyond the end |
| Pipes | `catalog_list` against `civil3d_query`, `create`, `add_structure` rim 102.5, `add_pipe`, `calculate_hgl`, `hydraulic_analysis`, `get_structure_properties` | Metric drawing; no rim; a reversed pipe |
| Grading | `feature_line_create`, list it, again on a layout tab, `group_list` after making a group by hand | Misspelled layer; no Z |
| Parcels (make one by hand) | `list_sites`, `list`, `get`, `report includeCoordinates`, `create` with 4 points | No site; 3 collinear points |
| Labels and styles | `label_list_styles` for surface, pipe, structure; `label_add` surface spot, alignment station, pipe plan; `style_list` | A valid style, a misspelled one, a frozen target layer |
| QC | `check_alignment`, `check_profile maxGrade=0.06` then `6`, `check_pipe_network minCover=3` against known cover, `generate_report`, [D] `fix_drawing_standards` dry run then real, with an xref layer present | `maxNameLength=0` |
| Quantity | `surface_volume` with and without `region`, `pipe_network_lengths groupBySize`, `export_to_csv` with each flag alone | Existing file; `overwrite`; a comma in a name; pipe sizes read in inches or mm |
| Plan production | `sheet_view_create` on Layout1, `sheet_view_set_scale`, `sheet_set_list`, `sheet_publish_pdf` | Layout "Model"; scale 0 and negative; a layout with no viewport |
| Approvals | A write with no token; a token with changed parameters; reuse a token; token, then save and pan, then call | Expiry after 5 minutes; a token after `switch` |
| View following | Zoom away and write a far point; `C3DMCPFOLLOW` off; write on a layout tab | Stray object at 0,0 with large coordinates |
| Command line | `acad_command ["_.LINE", ...]` left waiting, `command_line` shows it, next read says HOST_BUSY after 30 s, Esc recovers | One string `"_.LINE 0,0 10,10"`; `_.SHELL` blocked; a dialog command |
| Several drawings | Open two, `list_open`, `switch`, write lands in the active one | A long read while switching tabs; the same file name in two folders; undo per drawing |

Also try a locked layer and a frozen layer for point delete and label add: only the target should change.

## 5. Results of the first live day (2026-11-01, Civil 3D 2027)

Parts 2 and 4 ran on a blank drawing; every fix was retested live.

Part 5 of this plan used to list what a review expected to fail. All of it is fixed and proven unless listed here.

| Still open | Why |
|---|---|
| Grading groups, parcel `create`, `watershed_add` | Civil 3D's API has no call for them; the tools say so |
| `sheet_set_create`, `sheet_set_export`, survey writes | Refused, plainly |
| Parcel report vertices, `lot_line_adjust` | Need a parcel drawn by hand; not run |
| Point windows (2.9 to 2.12), undo after a person's command (2.24), a person-held lock (3.27), view following | Need someone at Civil 3D |
| A damaged database (3.43), a held write lock (3.44) | Not run live; covered by the suite test |

Civil 3D facts learned, worth knowing before testing again:

- It starts on a blank Drawing1, not the last drawing: check the active drawing before any write.
- AI Connection is off at every start.
- A new script costs nothing after the first (about 450 MB, once). Read `memoryUsageMb` from `civil3d_health` as you go.
- A script run between undo and redo empties the redo list.
- A command left at its prompt doesn't block: reads answer, and the next write ends it. (The plan expected HOST_BUSY.)

## 6. The test job

`testjob.py` in Project Analyzer. The job is made up; its database is the only thing in the shared folder that needs
removing afterwards.

```bash
python testjob.py build "C:\Users\<you>\Documents\Test Jobs" --folder "9999 Aardvark Ln"
python testjob.py check "C:\Users\<you>\Documents\Test Jobs" --folder "9999 Aardvark Ln" --project "Aardvark Lane Test"
python testjob.py update "C:\Users\<you>\Documents\Test Jobs" --folder "9999 Aardvark Ln"
```

Afterwards, remove `Documents\Test Jobs` and `projects\aardvark-lane-test.*` from the shared folder. While the job's
database is there, Project Analyzer's self-test reports its name wherever a tracked file holds it.
