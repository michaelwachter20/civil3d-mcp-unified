---
name: working_safely_in_civil3d
category: basics
description: Read at the start of every chat - how to work in a live Civil 3D drawing safely, and how to keep the knowledge base growing
requires_write: false
civil3d_versions: 2027
parameters: []
---

> Verified on Civil 3D 2027.

## Before you touch anything

- The user must turn the AI connection on in Civil 3D (ribbon **Civil 3D MCP → AI Connection**, or `C3DMCPSTART`).
  If tools report that Civil 3D can't be reached, ask them to turn it on rather than retrying.
- You act on the drawing that is active in that Civil 3D window. Confirm which drawing it is (`civil3d_health` or a
  `civil3d_query` returning `Document.Name`) before changing anything, and say which drawing you're about to change.
- Check `civil3d_preferences action=list` and follow the user's standing rules, and the good practices
  (`civil3d_practices action=list`) that apply on every job.
- Check `civil3d_project_notes action=overview` for what is already known about this drawing's job. Project
  Analyzer may have read the whole job folder: before asking the user what a spec, email or sheet says, look it up
  with `action=search` (words or codes like `MH-3`, `12+50`, `C-104`), `file`, `entity` or `timeline`.
- Job folders are usually copies, so paths written in drawings (xrefs, images) point at the original location.
  Translate them with `civil3d_project_notes action=locate`; if it can't tell, ask the user rather than guess.
- The overview also lists the job's **user knowledge**: the user's own documents for this job (proposal guidelines,
  building-type requirements, standards, code books). Read the ones that apply with `action=read`, follow them and
  name the document; when one conflicts with a fact, a practice or the drawings, tell the user rather than choose.

## Finding the right way to do something

1. A dedicated `civil3d_*` tool, if one fits.
2. A saved script (`civil3d_script action=list`), ready to run.
3. A skill (`civil3d_skills action=search`): a proven recipe to adapt.
4. Only then write new C#, starting read-only with `civil3d_query`.

Lessons related to the API you use are returned with `civil3d_execute`/`civil3d_query` results (`relatedLessons`).
Read them: they are mistakes someone already made.

## Changing a drawing

- Look first: run the read-only version (`civil3d_query`, or a script's `dryRun: true`) and report what would change.
- Each `civil3d_execute` run is one undo step (`UNDO 1` reverts it), runs in one transaction, and rolls back
  completely if it throws. Keep runs focused so undo stays meaningful.
- Never move, explode or edit COGO points, survey figures or other survey data unless the user explicitly asks.
  Point labels can be moved without touching the point.
- Ask before deleting objects, overwriting files, or anything that can't be undone with `UNDO`.
- Don't print or store the licensee name: `ACADVER` contains it. Record the release (e.g. 2027) instead.
- Long loops must check `CancellationToken`; a tight loop that never does can freeze Civil 3D.
- End every write with the view on the area you worked on: call `FocusOn(...)` in the script (the points or
  objects the change was about). Without it the view follows everything the change touched, which for a
  surface or point group edit can zoom out to the whole drawing.
- Use `civil3d_drawing action=command_line` to see what the user sees on the command line, and whenever a
  request is slow or reports that Civil 3D is busy.

## Verify, don't assume

- After a change, read back what you changed (counts, a few sample values) and compare with what you intended.
- Your geometry model of labels, text or viewports can be wrong. When the user says it looks wrong, ask them to
  select or trace the problem (`Editor.SelectImplied()` reads their selection) and measure it instead of guessing.

## Keep the knowledge base growing (always ask first)

Knowledge comes in three kinds. Decide which one it is before saving:

- **How to drive Civil 3D** (true for this program on any job): preferences, lessons, skills, scripts.
- **This job** (true only here): project notes, in the suite's project database shared with Project Analyzer and
  TBC MCP.
- **Engineering practice** (true on every job, in any program): good practices, shared by the suite.

| You found | Save it as |
|---|---|
| A standing rule from the user about working in Civil 3D (plot scale, styles, never move COGO points) | `civil3d_preferences action=add` |
| A non-obvious gotcha or fact about Civil 3D that applies to any project | `civil3d_memory action=record` with topics (API names) |
| A multi-step task that worked and the user is happy with | `civil3d_skills action=write` (when to use, rules, pitfalls, script) |
| Something the user will re-run | also `civil3d_script action=save` (runnable with `MCPRUN`) |
| A fact about this job (what a point group or survey date is for, which drawing is the go-by, a decision, where an xref is in the copy) | `civil3d_project_notes action=add` (one line, one statement; this computer only) |
| A correction of a fact another tool recorded | `civil3d_project_notes action=add` with `supersedes` = that fact's id |
| Engineering or company practice that holds on every job (minimum slopes, structure naming, the survey datum) | `civil3d_practices action=add` |

You can change or remove only what Civil 3D MCP recorded; facts from Project Analyzer or TBC MCP and their
practices are read-only. Write references the way every suite tool does: `2026-10-03`, `12+50`, `MH-3`, `C-104`,
`RFI 12`, `$12,500.00`.

Lessons, skills, scripts and practices must be general: write the technique, not this job. No job, drawing or
file names, folders, point numbers, coordinates, elevations, dates, crew initials, point group or layer names,
company details or site features. Make project-specific values parameters, or steps that ask the user. Those facts
belong in project notes. Saving is refused when the text has a job's name, a real drawing or survey file name, or
an absolute, network or profile path; write `<drawing>.dwg`, "the project folder" or a script parameter instead.

Add, never delete: improving a skill archives the old version; a skill verified on another Civil 3D release gets a
new section (`append_version_section`) instead of being overwritten.
