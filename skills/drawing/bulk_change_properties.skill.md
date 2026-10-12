---
name: bulk_change_properties
category: drawing
description: Move entities matching a layer/type filter to another layer and optionally set their color, with a dry-run mode
requires_write: true
parameters:
  - name: fromLayer
    type: string
    required: true
    description: Source layer name or wildcard
  - name: toLayer
    type: string
    required: true
    description: Destination layer (must exist)
  - name: dxfType
    type: string
    required: false
    description: DXF type filter, default "*"
  - name: colorIndex
    type: integer
    required: false
    description: Optional ACI color to apply (256 = ByLayer)
  - name: dryRun
    type: boolean
    required: false
    description: When true, only counts matches (default true)
---

## Code Template (civil3d_execute, or civil3d_query when dryRun=true)

```csharp
var fromLayer = Args.GetString("fromLayer");
var toLayer = Args.GetString("toLayer");
var dxfType = Args.GetString("dxfType", "*");
var dryRun = Args.GetBool("dryRun", true);
short? colorIndex = Args.Has("colorIndex") ? (short)Args.GetInt("colorIndex") : null;

var layerTable = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
if (!layerTable.Has(toLayer))
{
    return new { error = $"Layer '{toLayer}' does not exist. Create it first (see the layers skill)." };
}

var filter = new SelectionFilter(new[]
{
    new TypedValue((int)DxfCode.Start, dxfType),
    new TypedValue((int)DxfCode.LayerName, fromLayer),
});
var selection = Editor.SelectAll(filter);
if (selection.Status != PromptStatus.OK)
{
    return new { matched = 0, changed = 0, dryRun };
}

var ids = selection.Value.GetObjectIds();
if (dryRun || !IsWriteMode)
{
    return new { matched = ids.Length, changed = 0, dryRun = true };
}

var changed = 0;
var skipped = new List<string>();
foreach (var id in ids)
{
    CancellationToken.ThrowIfCancellationRequested();
    try
    {
        var entity = (Autodesk.AutoCAD.DatabaseServices.Entity)Transaction.GetObject(id, OpenMode.ForWrite);
        entity.Layer = toLayer;
        if (colorIndex.HasValue)
        {
            entity.ColorIndex = colorIndex.Value;
        }
        changed++;
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex)
    {
        skipped.Add($"{id.Handle}: {ex.ErrorStatus}");   // e.g. eOnLockedLayer
    }
}

Print($"Moved {changed} of {ids.Length} entities to {toLayer}");
return new { matched = ids.Length, changed, skipped = skipped.Take(50), dryRun = false };
```

## Usage Notes
- Run first with `dryRun: true` through civil3d_query to confirm the count.
- Entities on locked layers are skipped and reported instead of failing the whole run.
- A good candidate to save with civil3d_script action=save for reuse.
