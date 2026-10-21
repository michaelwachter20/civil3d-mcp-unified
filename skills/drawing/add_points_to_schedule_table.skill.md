---
name: add_points_to_schedule_table
category: drawing
description: Add a batch of shown points (a point group) to a model-space coordinate schedule table in station order, mirror the rows into the companion spreadsheet (sorted, formatting kept with each row), give each point a station/offset label like its neighbours, then place the labels clear of everything on their plan sheets. Saved scripts add-points-to-table and place-labels-clear.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: group
    type: string
    required: true
    description: Point group holding the points to add (they should already be shown)
  - name: alignment
    type: string
    required: true
    description: Main alignment for stations
  - name: eqAlignment
    type: string
    required: false
    description: Alignment whose stations carry an "EQ" prefix in the table
  - name: dryRun
    type: boolean
    required: false
    description: Plan only (default true)
---
> Verified on Civil 3D 2027.

## When to use
A drawing keeps one model-space ACAD_TABLE as a point schedule (here N | E | Elev | Station | Offset | Date YY/MM/DD,
no header row, sorted by station) shown on table sheets through stepped viewports, plus a spreadsheet copy of the same
rows. New points to show arrive as a point group; each needs a table row, a spreadsheet row and a station/offset label.

## Rules
- Station order: rows go in their station slot (prefix-stationed alignment first, then the main one), never appended.
- Values: N/E/Elev 3 dp, station 2 dp (prefix on the prefixed alignment), offset 3 dp, date from the point's layer
  date. Skip points already in the table (same N/E).
- New rows copy the neighbour row's format (InsertRowsAndInherit), then rebuild the grid (RecomputeTableBlock) or the
  new rows show without borders.
- Labels: same style and layer as the nearest labelled neighbour on the same alignment; then place them clear.
- Placement (the user's way): text out in the band the neighbouring labels use, not tucked against the line; if the
  neighbours sit on one side use the open side, else a second row beyond theirs; nudging a neighbour a little is fine;
  no text over text, labels, leaders, visible linework or the alignment; inside the sheet's viewport.
- The user reviews placement; read back what they shimmied and learn from it.
- Spreadsheet: back it up first, check it is not open (no ~$ lock file), move each row's formatting with its row when
  sorting (a coloured cell must stay with its value), keep the sort-key formula per row, verify it matches the table.
- Save (QSAVE) after the drawing changes.

## How to run
1. `add-points-to-table` dry run, then run: table rows + labels.
2. `place-labels-clear` with the new label handles (and `shownGroup`): activate a viewport of the sheets first and run it through `civil3d_execute` - dry run, then run. It measures everything with exact in-memory Explode geometry (Civil labels only explode in the write context; it stops if they come back empty), mirrors a label's box when it changes side, and skips sheets whose viewport twist/scale differs from the active one. Recheck with `annotation-clash-check` (skill clear_annotation_clashes).
3. Spreadsheet (Python + openpyxl, no Excel needed): read rows A-H with each cell's style, append the new rows
   (point #, N, E, Elev, code, station, offset, date from the table), sort by station key, write values + styles back,
   rewrite the sort-key formula per row, save; compare every row with the drawing table.

## Pitfalls
- `Alignment.GetPolyline()` creates a real polyline in the drawing - sample `PointLocation` by station instead.
- StationOffsetLabel.Create takes a Point2d and the anchor marker style (`AnchorMarkerStyleId` of a neighbour).
- Inserting several rows: insert from the bottom up so earlier indexes stay valid.
- openpyxl keeps values/formulas/styles but drops cached formula results; Excel recalculates on open.

## Script
```csharp
// Add a point group's points to the COW table (station order, EQ prefix on the EQ alignment) and give each a
// station/offset label like its nearest labelled neighbour (same style, layer and relative drag).
var dryRun = Args.GetBool("dryRun", true);
var g = Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("group")]);
var eqAl = CivilDoc.GetAlignmentIds().Cast<ObjectId>().Select(i => Get<Alignment>(i)).First(a => a.Name == Args.GetString("eqAlignment"));
var mainAl = CivilDoc.GetAlignmentIds().Cast<ObjectId>().Select(i => Get<Alignment>(i)).First(a => a.Name == Args.GetString("alignment"));
Autodesk.AutoCAD.DatabaseServices.Table tbl = null; foreach (ObjectId id in ModelSpace) if (id.ObjectClass.DxfName == "ACAD_TABLE") tbl = Get<Autodesk.AutoCAD.DatabaseServices.Table>(id, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
double Key(string s) => (s.StartsWith("EQ") ? 0 : 1e7) + double.Parse(s.Replace("EQ", "").Replace("+", ""), CultureInfo.InvariantCulture);
string Sta(double s) => $"{Math.Floor(s / 100):0}+{s - Math.Floor(s / 100) * 100:00.00}";
var existing = new HashSet<string>(); for (int i = 0; i < tbl.Rows.Count; i++) existing.Add(tbl.Cells[i, 0].TextString + "|" + tbl.Cells[i, 1].TextString);
var sols = new List<StationOffsetLabel>(); foreach (ObjectId id in ModelSpace) if (id.ObjectClass.DxfName == "AECC_STATION_OFFSET_LABEL") sols.Add(Get<StationOffsetLabel>(id));
var adds = new List<(CogoPoint p, Alignment al, string[] vals, double key)>();
foreach (var n in g.GetPointNumbers()) { var p = Get<CogoPoint>(CivilDoc.CogoPoints.GetPointByPointNumber(n));
  Alignment al = null; double st = 0, off = 0;
  foreach (var a in new[] { eqAl, mainAl }) { double s = 0, o = 0; try { a.StationOffset(p.Easting, p.Northing, ref s, ref o); } catch { continue; } if (Math.Abs(o) < 20) { al = a; st = s; off = o; break; } }
  if (al == null) throw new InvalidOperationException($"point {n} is not on either alignment");
  var m = System.Text.RegularExpressions.Regex.Match(p.Layer, @"(\d{4}) (\d\d) (\d\d)"); if (!m.Success) throw new InvalidOperationException($"point {n}: no date in layer {p.Layer}");
  var staTxt = (al == eqAl ? "EQ" : "") + Sta(Math.Round(st, 2));
  var vals = new[] { $"{p.Northing:0.000}", $"{p.Easting:0.000}", $"{p.Elevation:0.000}", staTxt, $"{off:0.000}", $"{m.Groups[1].Value.Substring(2)}/{m.Groups[2].Value}/{m.Groups[3].Value}" };
  if (existing.Contains(vals[0] + "|" + vals[1])) continue;
  adds.Add((p, al, vals, Key(staTxt))); }
adds = adds.OrderBy(a => a.key).ToList();
var rows = new List<string>(); var made = new List<ObjectId>();
// table: insert from the bottom up so earlier row indexes stay valid
foreach (var a in adds.OrderByDescending(x => x.key)) {
  int at = tbl.Rows.Count; for (int i = 0; i < tbl.Rows.Count; i++) if (Key(tbl.Cells[i, 3].TextString) > a.key) { at = i; break; }
  rows.Add($"row {at}: {string.Join(" | ", a.vals)}  (between {tbl.Cells[Math.Max(0, at - 1), 3].TextString} and {(at < tbl.Rows.Count ? tbl.Cells[at, 3].TextString : "end")})");
  if (!dryRun) { int src = Math.Max(0, at - 1); tbl.InsertRowsAndInherit(at, src, 1); for (int c = 0; c < 6; c++) tbl.Cells[at, c].TextString = a.vals[c]; } }
if (!dryRun) { tbl.GenerateLayout(); tbl.RecomputeTableBlock(true); }
// labels like the nearest labelled neighbour on the same alignment
foreach (var a in adds) {
  if (sols.Any(l => l.FeatureId == a.al.ObjectId && new Point3d(l.AnchorInfo.Location.X, l.AnchorInfo.Location.Y, 0).DistanceTo(new Point3d(a.p.Easting, a.p.Northing, 0)) < 0.05)) { rows.Add($"{a.p.PointNumber} already labelled"); continue; }
  var nb = sols.Where(l => l.FeatureId == a.al.ObjectId).OrderBy(l => new Point3d(l.AnchorInfo.Location.X, l.AnchorInfo.Location.Y, 0).DistanceTo(new Point3d(a.p.Easting, a.p.Northing, 0))).First();
  var rel = nb.LabelLocation - new Point3d(nb.AnchorInfo.Location.X, nb.AnchorInfo.Location.Y, nb.LabelLocation.Z);
  rows.Add($"label {a.p.PointNumber} {a.vals[3]} like {nb.Handle} rel ({rel.X:0.0},{rel.Y:0.0})");
  if (!dryRun) { var id = StationOffsetLabel.Create(a.al.ObjectId, nb.StyleId, nb.AnchorMarkerStyleId, new Point2d(a.p.Easting, a.p.Northing)); var l = Get<StationOffsetLabel>(id, OpenMode.ForWrite); l.Layer = nb.Layer; l.LabelLocation = new Point3d(a.p.Easting, a.p.Northing, 0) + new Vector3d(rel.X, rel.Y, 0); made.Add(id); } }
if (!dryRun && made.Count > 0) FocusOn(made);
return new { dryRun, adds = adds.Count, rows, tableRows = tbl.Rows.Count };
```
