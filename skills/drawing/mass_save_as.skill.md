---
name: mass_save_as
category: drawing
description: Save As several open drawings at once to new names (e.g. a naming pattern into one folder) so the open tabs BECOME the new files - never overwriting an existing file. Plan with a read-only check, then switch + SAVEAS per drawing.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: folder
    type: string
    required: true
    description: Target folder for the new files
  - name: names
    type: json
    required: true
    description: '{"<open drawing name>": "<new file name>.dwg", ...} - the mapping the user asked for'
---

> Verified on Civil 3D 2027.

## When to use
The user wants open drawings saved under new names / in another folder and to keep working in them (a real Save As:
the window tab and the drawing's file change to the new file).

## Rules
- Never overwrite: check every target right before saving that drawing; if one exists, stop and tell the user. Do not
  invent another name.
- Show the mapping (old tab -> new path) before saving if it is not exactly what the user said.

## How to run
1. Run the plan script below in `civil3d_query`. It lists the open drawings, the target for each, and which targets
   already exist.
2. For each drawing: `civil3d_drawing action=switch name=<tab>`, check the target is still free, then
   `acad_command ["FILEDIA","0","_.SAVEAS","","<full path>","FILEDIA","1"]` ("" keeps the current file format).
3. Verify with `civil3d_drawing action=list_open` (each tab now has the new name and path), list the files, and read
   FILEDIA back as 1.

## Pitfalls
- `Database.SaveAs(path, ...)` from a script only writes a COPY: the file is created but the open document keeps its
  old name and file (an unsaved DrawingN stays DrawingN and QSAVE still asks for a name). Use the SAVEAS command in
  each document instead.
- `civil3d_drawing action=save saveAs=...` only writes inside the configured project roots; for other folders use
  SAVEAS through `acad_command`.
- With FILEDIA=1 SAVEAS opens a dialog; set it to 0 for the command and back to 1 in the same command list.

## Script
```csharp
// Read-only plan: target path per open drawing and whether it already exists.
var folder = Args.GetString("folder");
var names = Args.Get<Dictionary<string, string>>("names");
var plan = OpenDrawings.Select(d => {
  string target = names.TryGetValue(d.Name, out var n) ? Path.Combine(folder, n) : null;
  return new { tab = d.Name, d.Path, target, exists = target != null && File.Exists(target) };
}).ToList();
return new { plan, blocked = plan.Where(p => p.exists).Select(p => p.target), unmapped = plan.Where(p => p.target == null).Select(p => p.tab) };
```
