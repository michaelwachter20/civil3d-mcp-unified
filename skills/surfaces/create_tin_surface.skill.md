---
name: create_tin_surface
category: surfaces
description: Create a new TIN surface and optionally add 3D points to it
requires_write: true
parameters:
  - name: surfaceName
    type: string
    required: true
    description: Name for the new surface
  - name: points
    type: Point3d[]
    required: false
    description: Array of {x, y, z} points to add
  - name: description
    type: string
    required: false
    description: Surface description
---

## Code Template

```csharp
// Create the TIN surface
var surfaceId = TinSurface.Create(Database, Args.GetString("surfaceName"));
var surface = Transaction.GetObject(surfaceId, OpenMode.ForWrite) as TinSurface;

// Optionally set description
if (Args.Has("description")) surface.Description = Args.GetString("description");

// Optionally add points
var pts = new Point3dCollection();
if (Args.Has("points"))
{
    foreach (var p in Args.Raw("points").AsArray())
        pts.Add(new Point3d(p["x"].GetValue<double>(), p["y"].GetValue<double>(), p["z"].GetValue<double>()));
}

if (pts.Count > 0)
{
    surface.AddVertices(pts);
}

return new {
    success = true,
    name = surface.Name,
    handle = surface.Handle.ToString(),
    pointCount = surface.GetGeneralProperties().NumberOfPoints
};
```

## Usage Notes
- Surface name must be unique in the drawing
- Points should be in the drawing's coordinate system
- Minimum 3 non-collinear points needed for a valid TIN surface
- Style can be set after creation via surface.StyleId
