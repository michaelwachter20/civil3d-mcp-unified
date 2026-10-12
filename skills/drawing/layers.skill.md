---
name: layers
category: drawing
description: List layers with their state, or create a layer if it does not exist (color, linetype-free)
requires_write: false
parameters:
  - name: name
    type: string
    required: false
    description: Layer to create (write template only)
  - name: colorIndex
    type: integer
    required: false
    description: AutoCAD color index 1-255 for a new layer (default 7)
---

## Code Template — List layers (civil3d_query)

```csharp
var layerTable = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
var layers = new List<object>();
foreach (ObjectId id in layerTable)
{
    var layer = (LayerTableRecord)Transaction.GetObject(id, OpenMode.ForRead);
    layers.Add(new
    {
        name = layer.Name,
        colorIndex = layer.Color.ColorIndex,
        off = layer.IsOff,
        frozen = layer.IsFrozen,
        locked = layer.IsLocked,
        plottable = layer.IsPlottable,
    });
}
return new { count = layers.Count, current = ((LayerTableRecord)Transaction.GetObject(Database.Clayer, OpenMode.ForRead)).Name, layers };
```

## Code Template — Ensure a layer exists (civil3d_execute)

```csharp
var name = Args.GetString("name");
var colorIndex = (short)Args.GetInt("colorIndex", 7);

var layerTable = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
if (layerTable.Has(name))
{
    return new { created = false, name };
}

SymbolUtilityServices.ValidateSymbolName(name, false);
layerTable.UpgradeOpen();
var record = new LayerTableRecord
{
    Name = name,
    Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
};
layerTable.Add(record);
Transaction.AddNewlyCreatedDBObject(record, true);
return new { created = true, name, colorIndex };
```

## Usage Notes
- `Color` and `ColorMethod` come from Autodesk.AutoCAD.Colors (auto-imported).
- `ValidateSymbolName` throws on invalid characters such as `<>/\":;?*|=`.
