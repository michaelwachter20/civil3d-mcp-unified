---
name: clear_section_sheet_windows
category: sections
description: Clear section-sheet windows in model space before laying out a new set of views - erase a sample line group's sample lines (with their section views and sections) and every entity inside the given windows except the NON PLOT sheet frame, its corner diagonals and left-edge ticks.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: sampleLineGroup
    type: string
    required: true
    description: Handle of the sample line group whose lines (and views) are erased
  - name: windows
    type: json
    required: true
    description: Sheet windows [[minX,minY,maxX,maxY], ...] (the section layouts' viewport windows)
  - name: dryRun
    type: boolean
    required: false
    description: List what would be erased (default true)
---

> Verified on Civil 3D 2027.

## When to use
A drawing that started as a save-as of another job still has the old job's section views and their annotation sets
(notes, dimensions, helper lines) in the section-sheet windows. Run a dry run, show the summary to the user, then run.
Keep the frame and guide ticks: new views are placed against them.

## Script
```csharp
// Clear the section-sheet windows for a new set of views: erase the listed sample lines (and their section views),
// and every entity inside the windows except the NON PLOT sheet frame, its corner diagonals and left-edge ticks.
var dryRun = Args.GetBool("dryRun", true);
var group = Get<SampleLineGroup>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("sampleLineGroup"), 16)), 0));
var windows = Args.Get<double[][]>("windows");   // [minX, minY, maxX, maxY]
var toErase = new List<ObjectId>(); var report = new List<string>();
foreach (ObjectId sid in group.GetSampleLineIds()) {
  var sl = Get<SampleLine>(sid);
  foreach (ObjectId vid in sl.GetSectionViewIds()) { toErase.Add(vid); report.Add("view " + Get<SectionView>(vid).Name); }
  toErase.Add(sid); report.Add("sample line " + sl.Name);
}
bool Keep(Autodesk.AutoCAD.DatabaseServices.Entity e, double[] w) {
  if (e.Layer != "NON PLOT" || !(e is Polyline pl)) return false;
  var pts = Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetPoint2dAt(i)).ToList();
  if (pl.Closed && pl.NumberOfVertices == 4) return true;                                  // sheet frame
  return pts.All(p => p.X <= w[0] + 25);                                                     // corner diagonals and left-edge ticks
}
int kept = 0;
foreach (ObjectId id in Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Database))) {
  if (toErase.Contains(id)) continue;
  var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id);
  var dxf = id.ObjectClass.DxfName;
  if (dxf == "AECC_SECTION" || dxf.StartsWith("AECC_GRAPH_SECTION")) continue;   // erased with their sample line / view
  Extents3d x; try { x = e.GeometricExtents; } catch { continue; }
  foreach (var w in windows) {
    bool inside = x.MinPoint.X >= w[0] - 0.5 && x.MaxPoint.X <= w[2] + 0.5 && x.MinPoint.Y >= w[1] - 0.5 && x.MaxPoint.Y <= w[3] + 0.5;
    if (!inside) continue;
    if (Keep(e, w)) { kept++; break; }
    toErase.Add(id); report.Add($"{dxf} [{e.Layer}] {(e is MLeader ml && ml.ContentType == ContentType.MTextContent ? ml.MText.Text.Replace("\r\n", " ") : e is MText mt ? mt.Text : "")}".Trim());
    break;
  }
}
if (!dryRun) {
  foreach (var id in toErase) { var o = Get<Autodesk.AutoCAD.DatabaseServices.DBObject>(id, OpenMode.ForWrite); if (!o.IsErased) o.Erase(); }
  FocusOn(new Point3d(windows.Min(w => w[0]), windows.Min(w => w[1]), 0), new Point3d(windows.Max(w => w[2]), windows.Max(w => w[3]), 0));
}
return new { dryRun, erase = toErase.Count, kept, summary = report.GroupBy(r => r.StartsWith("view") ? "view" : r.StartsWith("sample line") ? "sample line" : r).Select(g => $"{g.Count()} x {g.Key}") };
```
