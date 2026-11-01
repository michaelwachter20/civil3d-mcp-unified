---
name: create_polyline
category: geometry
description: Create a 2D or 3D polyline from a list of vertices
requires_write: true
parameters:
  - name: vertices
    type: array
    required: true
    description: Array of {x, y} or {x, y, z} coordinates
  - name: closed
    type: boolean
    required: false
    description: Whether to close the polyline
  - name: layer
    type: string
    required: false
    description: Layer name
---

## Code Template — 2D Polyline

```csharp
var polyline = new Polyline();
var at = 0;
foreach (var v in Args.Raw("vertices").AsArray())
    polyline.AddVertexAt(at++, new Point2d(v["x"].GetValue<double>(), v["y"].GetValue<double>()), 0, 0, 0);
polyline.Closed = Args.GetBool("closed", false);
if (Args.Has("layer")) polyline.Layer = Args.GetString("layer");

var bt = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var btr = (BlockTableRecord)Transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
btr.AppendEntity(polyline);
Transaction.AddNewlyCreatedDBObject(polyline, true);

return new { success = true, handle = polyline.Handle.ToString(), vertexCount = polyline.NumberOfVertices };
```

## Code Template — 3D Polyline

```csharp
var pts = new Point3dCollection();
foreach (var v in Args.Raw("vertices").AsArray())
    pts.Add(new Point3d(v["x"].GetValue<double>(), v["y"].GetValue<double>(), v["z"]?.GetValue<double>() ?? 0));

var polyline3d = new Polyline3d(Poly3dType.SimplePoly, pts, Args.GetBool("closed", false));

var bt = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var btr = (BlockTableRecord)Transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
btr.AppendEntity(polyline3d);
Transaction.AddNewlyCreatedDBObject(polyline3d, true);

return new { success = true, handle = polyline3d.Handle.ToString() };
```
