---
name: section_grid_with_offset_titles
category: sections
description: Place section views for a sample line group in a sheet grid (origins at given x's, bottoms at given y's, row by row in station order) with a fixed style and ranges, each titled with MText "OFFSET n.nn" = the sample line's offset from the project centerline, positioned from the view's top-left corner; adds NON PLOT vertical guides at the origins.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: group
    type: string
    required: true
    description: Handle of the sample line group (on the feature alignment)
  - name: mainAlignment
    type: string
    required: true
    description: Project centerline alignment (for the OFFSET values)
  - name: style
    type: string
    required: true
    description: Section view style (one with the graph title hidden, since the title is MText)
  - name: originX
    type: json
    required: true
    description: Grid column x's of the view origins (offset 0), e.g. [x1, x2]
  - name: bottomY
    type: json
    required: true
    description: Grid row y's of the view bottoms (elevation min), top row first, e.g. [y1, y2]
  - name: elevMin
    type: number
    required: true
    description: Elevation range bottom
  - name: elevMax
    type: number
    required: true
    description: Elevation range top
  - name: viewLayer
    type: string
    required: true
    description: Layer for views and titles
  - name: titleStyle
    type: string
    required: false
    description: Text style for the titles
  - name: titleHeight
    type: number
    required: false
    description: Title text height (default 1.5)
  - name: guideTop
    type: number
    required: true
    description: Top y of the NON PLOT guides
  - name: guideBottom
    type: number
    required: true
    description: Bottom y of the NON PLOT guides
---

> Verified on Civil 3D 2027.

## Notes
- A section view's `Location` is its origin: offset 0 at the view's elevation minimum (bottom), not the top.
- Measure the go-by's grid (view origins/bottoms) and title offset from the view's top-left corner and reuse them;
  here the title's bottom-left is 0.99 right / 0.63 up.
- Each view's offset range is taken from its sample line's own width.
- If the views show no sections, the group's sources were sampled after the lines were created: run
  `SetSectionSources` (it rebuilds such lines and keeps their views).

## Script
```csharp
// Section views for a sample line group in a grid (origins at given x's, bottoms at given y's, row by row),
// style/ranges given, each titled "OFFSET n.nn" = the sample line's offset from the project centerline, as MText
// 0.99 right / 0.63 up from the view's top-left (go-by placement). Adds NON PLOT vertical guides at the origin x's.
var group = Get<SampleLineGroup>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("group"), 16)), 0));
Alignment ditch = Get<Alignment>(group.ParentAlignmentId);
Alignment main = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == Args.GetString("mainAlignment")) main = a; }
ObjectId styleId = ObjectId.Null; foreach (ObjectId id in CivilDoc.Styles.SectionViewStyles) if (Get<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(id).Name == Args.GetString("style")) styleId = id;
var xs = Args.Get<double[]>("originX"); var ys = Args.Get<double[]>("bottomY");
double elMin = Args.GetDouble("elevMin"), elMax = Args.GetDouble("elevMax");
var ts = Get<TextStyleTable>(Database.TextStyleTableId);
var ms = Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForWrite);
var lines = group.GetSampleLineIds().Cast<ObjectId>().Select(id => Get<SampleLine>(id)).OrderBy(s => s.Station).ToList();
var made = new List<string>();
for (int i = 0; i < lines.Count; i++) {
  var sl = lines[i];
  var origin = new Point3d(xs[i % xs.Length], ys[i / xs.Length], 0);
  double minO = 1e9, maxO = -1e9; foreach (SampleLineVertex vx in sl.Vertices) { double st = 0, off = 0; ditch.StationOffset(vx.Location.X, vx.Location.Y, ref st, ref off); minO = Math.Min(minO, off); maxO = Math.Max(maxO, off); }
  var vid = SectionView.Create(sl.Name, sl.ObjectId, origin);
  var v = Get<SectionView>(vid, OpenMode.ForWrite);
  v.StyleId = styleId; v.Layer = Args.GetString("viewLayer");
  v.IsOffsetRangeAutomatic = false; v.OffsetLeft = Math.Round(minO, 2); v.OffsetRight = Math.Round(maxO, 2);
  v.IsElevationRangeAutomatic = false; v.ElevationMin = elMin; v.ElevationMax = elMax;
  // offset of this sample line (its ditch-centerline point) from the project centerline
  double cx = 0, cy = 0; ditch.PointLocation(sl.Station, 0, ref cx, ref cy);
  double mst = 0, moff = 0; main.StationOffset(cx, cy, ref mst, ref moff);
  var mt = new MText(); mt.SetDatabaseDefaults(Database);
  mt.Contents = $"OFFSET {moff:0.00}"; mt.TextHeight = Args.GetDouble("titleHeight", 1.5); mt.Attachment = AttachmentPoint.BottomLeft;
  if (ts.Has(Args.GetString("titleStyle"))) mt.TextStyleId = ts[Args.GetString("titleStyle")];
  mt.Location = new Point3d(origin.X + v.OffsetLeft + 0.99, origin.Y + (elMax - elMin) + 0.63, 0); mt.Layer = Args.GetString("viewLayer");
  ms.AppendEntity(mt); Transaction.AddNewlyCreatedDBObject(mt, true);
  made.Add($"{v.Name} at {origin.X:0.00},{origin.Y:0.00} off {v.OffsetLeft}..{v.OffsetRight} title '{mt.Text}'");
}
foreach (var x in xs) {
  var pl = new Polyline(); pl.SetDatabaseDefaults(Database);
  pl.AddVertexAt(0, new Point2d(x, Args.GetDouble("guideTop")), 0, 0, 0); pl.AddVertexAt(1, new Point2d(x, Args.GetDouble("guideBottom")), 0, 0, 0);
  pl.Layer = "NON PLOT"; ms.AppendEntity(pl); Transaction.AddNewlyCreatedDBObject(pl, true);
}
FocusOn(new Point3d(xs.Min() - 60, ys.Min() - 10, 0), new Point3d(xs.Max() + 60, ys.Max() + (elMax - elMin) + 10, 0));
return made;
```
