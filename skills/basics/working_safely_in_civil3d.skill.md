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
- Check `civil3d_preferences action=list` and follow the user's standing rules.

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

## Verify, don't assume

- After a change, read back what you changed (counts, a few sample values) and compare with what you intended.
- Your geometry model of labels, text or viewports can be wrong. When the user says it looks wrong, ask them to
  select or trace the problem (`Editor.SelectImplied()` reads their selection) and measure it instead of guessing.

## Keep the knowledge base growing (always ask first)

| You found | Save it as |
|---|---|
| A standing rule or standard from the user | `civil3d_preferences action=add` |
| A non-obvious gotcha or fact | `civil3d_memory action=record` with topics (API names) |
| A multi-step task that worked and the user is happy with | `civil3d_skills action=write` (when to use, rules, pitfalls, script) |
| Something the user will re-run | also `civil3d_script action=save` (runnable with `MCPRUN`) |

Add, never delete: improving a skill archives the old version; a skill verified on another Civil 3D release gets a
new section (`append_version_section`) instead of being overwritten.
