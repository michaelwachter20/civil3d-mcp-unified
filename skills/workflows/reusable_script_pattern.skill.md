---
name: reusable_script_pattern
category: workflows
description: Template and checklist for writing a parameterized script that is saved with civil3d_script and re-run later (or by the user via MCPRUN)
requires_write: false
parameters: []
---

## When to save a script
Save one when a working civil3d_execute/civil3d_query snippet solves a task the user is likely to repeat
(renumbering, relayering, labeling, exporting, QC checks). Search `civil3d_script action=list` first.

## Template

```csharp
// Inputs come from Args. Declare each one as a parameter when saving:
// [{"name":"layer","type":"string","required":true,"description":"Target layer"},
//  {"name":"dryRun","type":"boolean","default":true}]
var layer = Args.GetString("layer");
var dryRun = Args.GetBool("dryRun", true);

var processed = 0;
var problems = new List<string>();

foreach (ObjectId id in ModelSpace)
{
    CancellationToken.ThrowIfCancellationRequested();   // keeps long runs cancellable
    // ... inspect / modify objects here ...
    processed++;
}

Print($"Processed {processed} objects{(dryRun ? " (dry run)" : string.Empty)}");
return new { processed, problems = problems.Take(50), dryRun };   // return summaries, not whole objects
```

## Checklist
- Read every input through `Args` with sensible defaults; never hard-code names, layers or coordinates.
- Use `mode: "read"` for reports and checks, `mode: "write"` only when the script changes the drawing.
- Destructive scripts take a `dryRun` boolean that defaults to true.
- Catch `Autodesk.AutoCAD.Runtime.Exception` per object to skip locked or invalid items instead of failing the whole run.
- Use `point` parameters for locations; MCPRUN lets the user pick them in the drawing.
- Name scripts with lowercase-hyphenated verbs: `relayer-entities`, `export-cogo-points-csv`.
- Write a description that says what it does and when to use it — that is how it is found later.
