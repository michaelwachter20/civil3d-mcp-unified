---
name: change_surface_boundary_type
category: surfaces
description: Change a TIN surface boundary's type (e.g. Hide to Outer) - remove the one existing boundary and add the same polyline again with the new type at the end of the definition, rebuild, and count visible triangles inside/outside the polyline.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: surface
    type: string
    required: true
    description: TIN surface name
  - name: polyline
    type: string
    required: true
    description: Handle of the closed boundary polyline
---

> Verified on Civil 3D 2027.

## Notes
- Destructive boundary (whole triangles), no triangles split, no points added.
- Added at the end of the definition, so earlier edge swaps and line deletions are covered by it.
- Expects exactly one boundary on the surface; adjust the script if there are more.
- Hide vs outer matters per deliverable: hiding a feature from a surface for one sheet set can blank that feature's
  sections in another drawing made from it.

## Script
```csharp
// Change a surface boundary's type: remove the existing boundary built from a polyline and add the same polyline
// again with the new type (at the end of the definition), then rebuild and check which triangles show.
var surfName = Args.GetString("surface");
var plId = Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("polyline"), 16)), 0);
var pl = Get<Polyline>(plId);
TinSurface surf = null; foreach (ObjectId sid in CivilDoc.GetSurfaceIds()) { var s = Get<Autodesk.Civil.DatabaseServices.Surface>(sid) as TinSurface; if (s != null && s.Name == surfName) surf = s; }
surf = Get<TinSurface>(surf.ObjectId, OpenMode.ForWrite);
var before = new List<string>(); for (int i = 0; i < surf.BoundariesDefinition.Count; i++) before.Add($"{surf.BoundariesDefinition[i].Name} {surf.BoundariesDefinition[i].BoundaryType}");
if (surf.BoundariesDefinition.Count != 1) throw new InvalidOperationException("Expected exactly one boundary: " + string.Join(", ", before));
surf.BoundariesDefinition.RemoveAt(0);
surf.BoundariesDefinition.AddBoundaries(new ObjectIdCollection { plId }, 0.1, Autodesk.Civil.SurfaceBoundaryType.Outer, false);
surf.Rebuild();
var after = new List<string>(); for (int i = 0; i < surf.BoundariesDefinition.Count; i++) after.Add($"{surf.BoundariesDefinition[i].Name} {surf.BoundariesDefinition[i].BoundaryType}");
var poly = Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetPoint2dAt(i)).ToList();
bool Inside(double x, double y) { bool c = false; for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) if (((poly[i].Y > y) != (poly[j].Y > y)) && (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)) c = !c; return c; }
int visIn = 0, visOut = 0;
foreach (var t in surf.GetTriangles(false)) { double cx = (t.Vertex1.Location.X + t.Vertex2.Location.X + t.Vertex3.Location.X) / 3, cy = (t.Vertex1.Location.Y + t.Vertex2.Location.Y + t.Vertex3.Location.Y) / 3; if (Inside(cx, cy)) visIn++; else visOut++; }
FocusOn(new[] { plId });
return new { before, after, surf.IsOutOfDate, visibleInside = visIn, visibleOutside = visOut, lastOp = surf.Operations[surf.Operations.Count - 1].GetType().Name };
```
