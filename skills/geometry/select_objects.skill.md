---
name: select_objects
category: geometry
description: Find entities by layer and DXF type with a selection filter (wildcards allowed) and summarize them
requires_write: false
parameters:
  - name: layer
    type: string
    required: false
    description: Layer name or wildcard pattern, e.g. "C-ROAD*" (default "*")
  - name: dxfType
    type: string
    required: false
    description: DXF entity type, e.g. LINE, LWPOLYLINE, INSERT, AECC_COGO_POINT, or "*" (default "*")
  - name: limit
    type: integer
    required: false
    description: Maximum items returned (default 500)
---

## Code Template (civil3d_query)

```csharp
var layer = Args.GetString("layer", "*");
var dxfType = Args.GetString("dxfType", "*");
var limit = Args.GetInt("limit", 500);

var filter = new SelectionFilter(new[]
{
    new TypedValue((int)DxfCode.Start, dxfType),
    new TypedValue((int)DxfCode.LayerName, layer),
});

var selection = Editor.SelectAll(filter);
if (selection.Status != PromptStatus.OK)
{
    return new { count = 0, items = new List<object>() };
}

var ids = selection.Value.GetObjectIds();
var byType = new Dictionary<string, int>();
var items = new List<object>();
foreach (var id in ids)
{
    var entity = (Autodesk.AutoCAD.DatabaseServices.Entity)Transaction.GetObject(id, OpenMode.ForRead);
    var typeName = entity.GetType().Name;
    byType[typeName] = byType.TryGetValue(typeName, out var n) ? n + 1 : 1;
    if (items.Count < limit)
    {
        items.Add(new { handle = entity.Handle.ToString(), type = typeName, layer = entity.Layer });
    }
}

return new { count = ids.Length, byType, truncated = ids.Length > limit, items };
```

## Usage Notes
- `Entity` exists in both AutoCAD and Civil namespaces, so it is fully qualified here.
- `SelectAll` covers model space and all layouts. Filter with `DxfCode.LayoutName` ("Model") to restrict.
- Combine conditions with `<OR` / `OR>` TypedValues (DxfCode.Operator) for OR filters.
