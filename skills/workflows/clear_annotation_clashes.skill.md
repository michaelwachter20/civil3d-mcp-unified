---
name: clear_annotation_clashes
category: workflows
description: Find every annotation clash on plan sheets with exact geometry (in-memory Explode of Civil labels and MLeaders - box against box, and every leader line against every box) and fix them the user's way - uniform, not random - station/offset labels back to the run's standard drag (or its mirror), paired callout leaders all on one layout (or its mirror) slid along their line when blocked. Saved scripts annotation-clash-check and annotation-uniform-fix.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: fromTab
    type: integer
    required: true
    description: First layout (tab order) to check
  - name: toTab
    type: integer
    required: true
    description: Last layout (tab order) to check
  - name: viewportLayer
    type: string
    required: true
    description: Layer of each sheet's plan viewport
  - name: shownGroup
    type: string
    required: true
    description: Point group of the points whose labels are shown (only those count as obstacles; their label style gives the box size)
  - name: pairTexts
    type: string[]
    required: false
    description: Fix only - texts of the two paired MLeaders on each sheet (e.g. centerline / alignment callouts)
  - name: offsets
    type: json
    required: false
    description: Fix only - [[dx,dy],[dx,dy]] text offsets from each arrowhead on the sheet for the pair, taken from a sheet the user laid out
  - name: tipSpacing
    type: number
    required: false
    description: Fix only - distance along the line from the first arrowhead to the second (negative = other way)
  - name: labelHandles
    type: string[]
    required: false
    description: Fix only - the only station/offset labels that may be re-standardised (never the user's own placements)
  - name: dryRun
    type: boolean
    required: false
    description: Fix only - plan only (default true)
---

> Verified on Civil 3D 2027.

## When to use
Plan sheets where labels and leaders overlap - typically after rotating viewports, adding labels, or moving a few by
hand - and the user wants them clean and uniform, not "smallest random shift".

## Rules (from the user's corrections)
- Uniform, concise, neat. Station/offset labels follow the run's standard drag: the common label-to-point offset on
  that sheet and its neighbours (clusters within 6 ft, 3+ labels); a second row further out is also a standard. A
  one-off goes to the standard spot (same side first), else the mirror across the line, only if clear.
- Paired callout leaders (e.g. centerline + alignment) use ONE layout on every sheet - the same text offsets from the
  arrowhead and the same arrowhead spacing - or that layout mirrored across the line; if blocked the pair slides
  along its line together. A pair already on either layout and clash-free is left alone.
- Never move the user's deliberate placements: only pass `labelHandles` for labels a script moved.
- Show the dry run and get a yes before applying; verify with the check afterwards.

## Exact geometry
- In-memory `Entity.Explode` (copies only - the drawing is never changed by exploding): a Civil label explodes to a
  block reference -> border lines, MText, Leader; an MLeader -> MText, landing line, leader polyline from the
  arrowhead, arrow solid. Points cannot be exploded: their label box comes from the label style (text height and
  X offset, plotted feet x 12 / viewport scale).
- **Civil labels explode only in the write (command) context** - in a read-only run they return nothing. Both
  scripts run through `civil3d_execute` (the check never writes; the fix writes only when dryRun is false) and stop
  if a label explodes empty.
- Exploded geometry follows the ACTIVE viewport's twist and annotation scale: activate the viewport of one of the
  sheets; sheets whose viewport twist/scale differ are skipped and reported - activate one of them and run again.
- A clash is: two boxes overlapping, or any leader line (labels', points', MLeaders') crossing another box - or an MLeader whose own leader runs through its own text (checked for every paper-space leader too, e.g. a cover location map). For that, measure the text glyphs (`MText.GetBoundingPoints`), not the MText frame width, and keep the landing on the side the text is on: a right-aligned text in a wide frame leaves a gap after the landing.
- Count only what is visible on that sheet: layers on and not frozen in its viewport, and only shown points.

## How to run
1. Activate a plan sheet's viewport. `annotation-clash-check` (civil3d_execute) for the sheet range -> clash list.
2. Read the user's preferred pair layout from a sheet they laid out (text offsets from each arrowhead, spacing).
3. `annotation-uniform-fix` dry run -> show the user; apply on a yes (one undo step); QSAVE.
4. Run the check again: it must come back empty for every measured sheet.

## Script
```csharp
// Read-only: every annotation clash on plan sheets, in sheet (DCS) coordinates, using exact in-memory Explode for
// station/offset labels and MLeaders, calibrated boxes for point labels, extents for MText.
// Items: box (text/border) + lines (leaders). Clash = two boxes overlap, or a line of one item crosses the box of another.
int from = Args.GetInt("fromTab"), to = Args.GetInt("toTab"); var vpLayer = Args.GetString("viewportLayer"); double gap = Args.GetDouble("margin", 1);
double ptH = 0, ptX = 0;
var dict = Get<DBDictionary>(Database.LayoutDictionaryId); var lt = Get<LayerTable>(Database.LayerTableId);
bool On(string n) { var r = Get<LayerTableRecord>(lt[n]); return !r.IsFrozen && !r.IsOff; }
// MLeaders whose own leader line runs through their own text (model-space leaders on the measured sheets and every
// paper-space leader, e.g. on a cover/location map). Text box = the glyphs (GetBoundingPoints), not the MText frame;
// the landing line into the text is not counted.
List<string> SelfThrough(MLeader ml, Func<Point3d, Point2d> D) { var r = new List<string>(); var parts = new DBObjectCollection(); try { ml.Explode(parts); } catch { return r; }
  List<Point2d> box = null; var segs = new List<(Point2d a, Point2d b)>();
  foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) { if (o is MText mt) box = mt.GetBoundingPoints().Cast<Point3d>().Select(D).ToList(); else if (o is Polyline p2) for (int i = 0; i + 1 < p2.NumberOfVertices; i++) segs.Add((D(p2.GetPoint3dAt(i)), D(p2.GetPoint3dAt(i + 1)))); o.Dispose(); }
  if (box == null) return r; double x0 = box.Min(p => p.X), x1 = box.Max(p => p.X), y0 = box.Min(p => p.Y), y1 = box.Max(p => p.Y); double tol = Math.Min(x1 - x0, y1 - y0) * 0.05;
  foreach (var s in segs) { int n = 200; for (int k = 1; k < n; k++) { var q = k / (double)n; double x = s.a.X + (s.b.X - s.a.X) * q, y = s.a.Y + (s.b.Y - s.a.Y) * q; if (x > x0 + tol && x < x1 - tol && y > y0 + tol && y < y1 - tol) { r.Add($"mleader {ml.Handle} '{ml.MText.Text.Trim()}' leader runs through its own text"); return r; } } }
  return r; }var rows = new List<string>(); var shown = new HashSet<uint>(Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("shownGroup")]).GetPointNumbers());
// Exploded geometry follows the ACTIVE viewport (twist + annotation scale): require one, and only measure sheets whose
// plan viewport has the same twist and scale.
if (Database.TileMode) throw new InvalidOperationException("Open a plan sheet and activate its viewport (double-click inside it) first.");
var av = Get<Viewport>(Editor.CurrentViewportObjectId); if (av.Number < 2) throw new InvalidOperationException("Activate a model-space viewport on a plan sheet (double-click inside it) first.");
bool SameView(Viewport x) { double dt = Math.Abs(Math.IEEERemainder(x.TwistAngle - av.TwistAngle, 2 * Math.PI)); return dt < 1e-4 && Math.Abs(x.CustomScale / av.CustomScale - 1) < 1e-3; }
// Point (elevation) labels cannot be exploded: box from the shown group's label style (first text component):
// style heights/offsets are plotted feet, x 12 / CustomScale = model units.
double styleH = 0.0016667, styleX = 0.0020833;
{ var g0 = Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("shownGroup")]); var lsid = g0.IsPointLabelStyleOverridden ? g0.PointLabelStyleId : ObjectId.Null;
  if (!lsid.IsNull) { var ls = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(lsid); foreach (ObjectId cid in ls.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text)) { var c = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent>(cid); styleH = c.Text.Height.Value; styleX = c.Text.XOffset.Value; break; } } }ptH = styleH * 12 / av.CustomScale; ptX = styleX * 12 / av.CustomScale;
// Civil 3D labels only explode in the write (command) context; in a read-only run they come back empty. Stop then
// rather than treating labels as having no size.
{ foreach (ObjectId pid in ModelSpace) { var pe = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(pid); if (!(pe is Autodesk.Civil.DatabaseServices.Label)) continue; var pp = new DBObjectCollection(); try { pe.Explode(pp); } catch { } bool empty = pp.Count == 0; foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in pp) o.Dispose();
    if (empty) throw new InvalidOperationException("Civil 3D labels explode to nothing in this context - run this script with civil3d_execute (write mode; a dry run changes nothing)."); break; } }foreach (DBDictionaryEntry en in dict) { var lay = Get<Layout>(en.Value); if (lay.ModelType || lay.TabOrder < from || lay.TabOrder > to) continue;
  Viewport v = null; foreach (ObjectId id in Get<BlockTableRecord>(lay.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is Viewport vp && vp.Layer == vpLayer) v = vp; if (v == null) continue; if (!SameView(v)) { rows.Add($"sheet {lay.LayoutName}: skipped - its viewport twist/scale differs from the active one (activate a viewport like it to check this sheet)"); continue; }
  var m = (Matrix3d.Rotation(-v.TwistAngle, v.ViewDirection, v.ViewTarget) * Matrix3d.Displacement(v.ViewTarget - Point3d.Origin) * Matrix3d.PlaneToWorld(v.ViewDirection)).Inverse();
  double hw = v.Width / v.CustomScale / 2, hh = v.Height / v.CustomScale / 2; var frozen = new HashSet<string>(v.GetFrozenLayers().Cast<ObjectId>().Select(i => Get<LayerTableRecord>(i).Name));
  Point2d D(Point3d p) { var d = p.TransformBy(m); return new Point2d(d.X, d.Y); }
  bool InView(Point3d p) { var d = p.TransformBy(m); return Math.Abs(d.X - v.ViewCenter.X) <= hw + 80 && Math.Abs(d.Y - v.ViewCenter.Y) <= hh + 80; }
  var items = new List<(string name, double x0, double y0, double x1, double y1, List<(Point2d a, Point2d b)> lines)>();
  void AddBox(string name, IEnumerable<Point2d> pts, List<(Point2d, Point2d)> lines) { var l = pts.ToList(); if (l.Count == 0) return; items.Add((name, l.Min(p => p.X), l.Min(p => p.Y), l.Max(p => p.X), l.Max(p => p.Y), lines)); }
  foreach (ObjectId id in ModelSpace) { var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id); if (!On(e.Layer) || frozen.Contains(e.Layer)) continue;
    if (e is StationOffsetLabel sl) { if (!InView(sl.LabelLocation)) continue; var pts = new List<Point2d>(); var lines = new List<(Point2d, Point2d)> { (D(sl.AnchorInfo.Location), D(sl.LabelLocation)) }; var l1 = new DBObjectCollection(); sl.Explode(l1);
      foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in l1) { if (o is BlockReference br) { var l2 = new DBObjectCollection(); br.Explode(l2); foreach (Autodesk.AutoCAD.DatabaseServices.DBObject q in l2) { if (q is Line ln) { pts.Add(D(ln.StartPoint)); pts.Add(D(ln.EndPoint)); } else if (q is MText mt) { var ex = mt.GeometricExtents; pts.Add(D(ex.MinPoint)); pts.Add(D(ex.MaxPoint)); } q.Dispose(); } } o.Dispose(); }
      AddBox($"label {sl.Handle}", pts, lines); continue; }
    if (e is CogoPoint cp) { if (!shown.Contains(cp.PointNumber) || !InView(cp.Location)) continue; var L = D(cp.LabelLocation); var P = D(cp.Location); string t = $"{cp.Elevation:0.00}'"; double w = t.Length * ptH * 0.85;
      AddBox($"elev {cp.PointNumber}", new[] { new Point2d(L.X + ptX, L.Y - ptH * 0.7), new Point2d(L.X + ptX + w, L.Y + ptH * 0.7) }, cp.IsLabelDragged ? new List<(Point2d, Point2d)> { (P, L) } : new List<(Point2d, Point2d)>()); continue; }
    if (e is MLeader ml && ml.ContentType == ContentType.MTextContent) { if (!InView(ml.MText.Location)) continue; var parts = new DBObjectCollection(); ml.Explode(parts); var pts = new List<Point2d>(); var lines = new List<(Point2d, Point2d)>();
      foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) { if (o is MText t) { var ex = t.GeometricExtents; pts.Add(D(ex.MinPoint)); pts.Add(D(ex.MaxPoint)); } else if (o is Polyline pl) { for (int i = 0; i + 1 < pl.NumberOfVertices; i++) lines.Add((D(pl.GetPoint3dAt(i)), D(pl.GetPoint3dAt(i + 1)))); } else if (o is Line ln) lines.Add((D(ln.StartPoint), D(ln.EndPoint))); o.Dispose(); }
      AddBox($"mleader {ml.Handle} '{ml.MText.Text.Trim()}'", pts, lines); continue; }
    if (e is MText mx) { if (!InView(mx.Location)) continue; var ex = mx.GeometricExtents; AddBox($"mtext {mx.Handle} '{mx.Text}'", new[] { D(ex.MinPoint), D(ex.MaxPoint) }, new List<(Point2d, Point2d)>()); } }
  bool SegBox((Point2d a, Point2d b) s, (string name, double x0, double y0, double x1, double y1, List<(Point2d a, Point2d b)> lines) o) { int n = Math.Max(2, (int)(s.a.GetDistanceTo(s.b) / 0.4)); for (int k = 1; k < n; k++) { var t = k / (double)n; double x = s.a.X + (s.b.X - s.a.X) * t, y = s.a.Y + (s.b.Y - s.a.Y) * t; if (x > o.x0 - gap && x < o.x1 + gap && y > o.y0 - gap && y < o.y1 + gap) return true; } return false; }
  var seen = new HashSet<string>();
  for (int i = 0; i < items.Count; i++) for (int j = 0; j < items.Count; j++) { if (i == j) continue; var A = items[i]; var B = items[j];
    // only report clashes where something is on this sheet
    var cx = (A.x0 + A.x1) / 2; var cy = (A.y0 + A.y1) / 2; if (Math.Abs(cx - v.ViewCenter.X) > hw || Math.Abs(cy - v.ViewCenter.Y) > hh) continue;
    if (i < j && A.x0 - gap < B.x1 && A.x1 + gap > B.x0 && A.y0 - gap < B.y1 && A.y1 + gap > B.y0) { var k = $"{A.name} box x {B.name} box"; if (seen.Add(k)) rows.Add($"sheet {lay.LayoutName}: {k}"); }
    foreach (var s in A.lines) { // ignore a leader touching the box of the thing it labels at its own end (e.g. elevation label's leader at its point)
      if (SegBox(s, B)) { var k = $"{A.name} line x {B.name} box"; if (seen.Add(k)) rows.Add($"sheet {lay.LayoutName}: {k}"); break; } } } }
foreach (DBDictionaryEntry pe in Get<DBDictionary>(Database.LayoutDictionaryId)) { var pl = Get<Layout>(pe.Value); if (pl.ModelType) continue;
  foreach (ObjectId pid in Get<BlockTableRecord>(pl.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(pid) is MLeader pml && pml.ContentType == ContentType.MTextContent) foreach (var s in SelfThrough(pml, p => new Point2d(p.X, p.Y))) rows.Add($"layout {pl.LayoutName} (paper): {s}"); }
foreach (ObjectId mid in ModelSpace) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(mid) is MLeader mml && mml.ContentType == ContentType.MTextContent) foreach (var s in SelfThrough(mml, p => new Point2d(p.X, p.Y))) rows.Add($"model: {s}");
return rows;
```

## Fix script (annotation-uniform-fix)
```csharp
// Make plan-sheet annotation uniform, clash-free (sheet/DCS coordinates, exact in-memory Explode geometry):
//  1. station/offset labels: each sheet's most common drag (label - anchor) is the standard; odd labels go to the
//     standard if clear, else its mirror across the line (dx,-dy), else stay.
//  2. MLeader pairs (texts in "pairTexts"): every pair gets the same text offsets from its arrowhead ("offsets");
//     if blocked, the pair slides along its lines (same distance for both) to the nearest clear spot.
// Everything is checked against all labels, elevation labels, leaders and MText on the sheet. Labels/leaders only.
var dryRun = Args.GetBool("dryRun", true); int from = Args.GetInt("fromTab"), to = Args.GetInt("toTab"); var vpLayer = Args.GetString("viewportLayer"); double gap = Args.GetDouble("margin", 1.5);
var pairTexts = Args.GetStringList("pairTexts"); var offs = Args.Get<List<List<double>>>("offsets"); double ptH = 0, ptX = 0;
var shown = new HashSet<uint>(Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("shownGroup")]).GetPointNumbers());
var dict = Get<DBDictionary>(Database.LayoutDictionaryId); var lt = Get<LayerTable>(Database.LayerTableId); bool On(string n) { var r = Get<LayerTableRecord>(lt[n]); return !r.IsFrozen && !r.IsOff; }
var dragsBySheet = new Dictionary<int, List<Vector2d>>();
foreach (DBDictionaryEntry en0 in dict) { var l0 = Get<Layout>(en0.Value); if (l0.ModelType || l0.TabOrder < from - 1 || l0.TabOrder > to + 1) continue; Viewport v0 = null; foreach (ObjectId id in Get<BlockTableRecord>(l0.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is Viewport vp && vp.Layer == vpLayer) v0 = vp; if (v0 == null) continue;
  var m0 = (Matrix3d.Rotation(-v0.TwistAngle, v0.ViewDirection, v0.ViewTarget) * Matrix3d.Displacement(v0.ViewTarget - Point3d.Origin) * Matrix3d.PlaneToWorld(v0.ViewDirection)).Inverse(); double w0 = v0.Width / v0.CustomScale / 2, h0 = v0.Height / v0.CustomScale / 2; var list = new List<Vector2d>();
  foreach (ObjectId id in ModelSpace) if (id.ObjectClass.DxfName == "AECC_STATION_OFFSET_LABEL") { var sl = Get<StationOffsetLabel>(id); var a = sl.AnchorInfo.Location.TransformBy(m0); if (Math.Abs(a.X - v0.ViewCenter.X) > w0 || Math.Abs(a.Y - v0.ViewCenter.Y) > h0) continue; var L = sl.LabelLocation.TransformBy(m0); list.Add(new Vector2d(L.X - a.X, L.Y - a.Y)); }
  dragsBySheet[l0.TabOrder] = list; }
List<(Vector2d c, int n)> Standards(int tab) { var all = new List<Vector2d>(); for (int k = tab - 1; k <= tab + 1; k++) if (dragsBySheet.ContainsKey(k)) all.AddRange(dragsBySheet[k]);
  var cl = new List<(Vector2d c, int n)>(); foreach (var d in all) { int i = cl.FindIndex(c => (c.c - d).Length < 6); if (i < 0) cl.Add((d, 1)); else cl[i] = ((cl[i].c * cl[i].n + d) / (cl[i].n + 1), cl[i].n + 1); }
  return cl.Where(c => c.n >= 3).OrderByDescending(c => c.n).ToList(); }
var rows = new List<string>();// Exploded geometry follows the ACTIVE viewport (twist + annotation scale): require one, and only measure sheets whose
// plan viewport has the same twist and scale.
if (Database.TileMode) throw new InvalidOperationException("Open a plan sheet and activate its viewport (double-click inside it) first.");
var av = Get<Viewport>(Editor.CurrentViewportObjectId); if (av.Number < 2) throw new InvalidOperationException("Activate a model-space viewport on a plan sheet (double-click inside it) first.");
bool SameView(Viewport x) { double dt = Math.Abs(Math.IEEERemainder(x.TwistAngle - av.TwistAngle, 2 * Math.PI)); return dt < 1e-4 && Math.Abs(x.CustomScale / av.CustomScale - 1) < 1e-3; }
// Point (elevation) labels cannot be exploded: box from the shown group's label style (first text component):
// style heights/offsets are plotted feet, x 12 / CustomScale = model units.
double styleH = 0.0016667, styleX = 0.0020833;
{ var g0 = Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("shownGroup")]); var lsid = g0.IsPointLabelStyleOverridden ? g0.PointLabelStyleId : ObjectId.Null;
  if (!lsid.IsNull) { var ls = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(lsid); foreach (ObjectId cid in ls.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text)) { var c = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent>(cid); styleH = c.Text.Height.Value; styleX = c.Text.XOffset.Value; break; } } }ptH = styleH * 12 / av.CustomScale; ptX = styleX * 12 / av.CustomScale;
// Civil 3D labels only explode in the write (command) context; in a read-only run they come back empty. Stop then
// rather than treating labels as having no size.
{ foreach (ObjectId pid in ModelSpace) { var pe = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(pid); if (!(pe is Autodesk.Civil.DatabaseServices.Label)) continue; var pp = new DBObjectCollection(); try { pe.Explode(pp); } catch { } bool empty = pp.Count == 0; foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in pp) o.Dispose();
    if (empty) throw new InvalidOperationException("Civil 3D labels explode to nothing in this context - run this script with civil3d_execute (write mode; a dry run changes nothing)."); break; } }foreach (DBDictionaryEntry en in dict) { var lay = Get<Layout>(en.Value); if (lay.ModelType || lay.TabOrder < from || lay.TabOrder > to) continue;
  Viewport v = null; foreach (ObjectId id in Get<BlockTableRecord>(lay.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is Viewport vp && vp.Layer == vpLayer) v = vp; if (v == null) continue; if (!SameView(v)) { rows.Add($"sheet {lay.LayoutName}: skipped - its viewport twist/scale differs from the active one (activate a viewport like it to check this sheet)"); continue; }
  var m = (Matrix3d.Rotation(-v.TwistAngle, v.ViewDirection, v.ViewTarget) * Matrix3d.Displacement(v.ViewTarget - Point3d.Origin) * Matrix3d.PlaneToWorld(v.ViewDirection)).Inverse(); var mi = m.Inverse();
  double hw = v.Width / v.CustomScale / 2, hh = v.Height / v.CustomScale / 2; var frozen = new HashSet<string>(v.GetFrozenLayers().Cast<ObjectId>().Select(i => Get<LayerTableRecord>(i).Name));
  Point2d D(Point3d p) { var d = p.TransformBy(m); return new Point2d(d.X, d.Y); }
  bool Near(Point3d p) { var d = p.TransformBy(m); return Math.Abs(d.X - v.ViewCenter.X) <= hw + 80 && Math.Abs(d.Y - v.ViewCenter.Y) <= hh + 80; }
  // item: box + lines; kind; for labels: anchor; for mleaders: tip + text location
  var it = new List<(ObjectId id, string kind, string name, double x0, double y0, double x1, double y1, List<(Point2d a, Point2d b)> lines, Point2d anchor, Point2d loc)>();
  foreach (ObjectId id in ModelSpace) { var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id); if (!On(e.Layer) || frozen.Contains(e.Layer)) continue;
    if (e is StationOffsetLabel sl && Near(sl.LabelLocation)) { var pts = new List<Point2d>(); var l1 = new DBObjectCollection(); sl.Explode(l1);
      foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in l1) { if (o is BlockReference br) { var l2 = new DBObjectCollection(); br.Explode(l2); foreach (Autodesk.AutoCAD.DatabaseServices.DBObject q in l2) { if (q is Line ln) { pts.Add(D(ln.StartPoint)); pts.Add(D(ln.EndPoint)); } else if (q is MText mt) { var ex = mt.GeometricExtents; pts.Add(D(ex.MinPoint)); pts.Add(D(ex.MaxPoint)); } q.Dispose(); } } o.Dispose(); }
      if (pts.Count == 0) continue; var A = D(sl.AnchorInfo.Location); var L = D(sl.LabelLocation); it.Add((id, "label", $"label {sl.Handle}", pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y), new List<(Point2d, Point2d)> { (A, L) }, A, L)); continue; }
    if (e is CogoPoint cp && shown.Contains(cp.PointNumber) && Near(cp.Location)) { var L = D(cp.LabelLocation); var P = D(cp.Location); double w = ($"{cp.Elevation:0.00}'").Length * ptH * 0.85;
      it.Add((id, "elev", $"elev {cp.PointNumber}", L.X + ptX, L.Y - ptH * 0.7, L.X + ptX + w, L.Y + ptH * 0.7, cp.IsLabelDragged ? new List<(Point2d, Point2d)> { (P, L) } : new List<(Point2d, Point2d)>(), P, L)); continue; }
    if (e is MLeader ml && ml.ContentType == ContentType.MTextContent && Near(ml.MText.Location)) { var parts = new DBObjectCollection(); ml.Explode(parts); var pts = new List<Point2d>(); var lines = new List<(Point2d, Point2d)>();
      foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) { if (o is MText t) { var ex = t.GeometricExtents; pts.Add(D(ex.MinPoint)); pts.Add(D(ex.MaxPoint)); } else if (o is Polyline pl) { for (int i = 0; i + 1 < pl.NumberOfVertices; i++) lines.Add((D(pl.GetPoint3dAt(i)), D(pl.GetPoint3dAt(i + 1)))); } else if (o is Line ln) lines.Add((D(ln.StartPoint), D(ln.EndPoint))); o.Dispose(); }
      var li = ml.GetLeaderIndexes(); var lns = ml.GetLeaderLineIndexes((int)li[0]); it.Add((id, "mleader", $"mleader {ml.Handle} '{ml.MText.Text.Trim()}'", pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y), lines, D(ml.GetFirstVertex((int)lns[0])), D(ml.MText.Location))); continue; }
    if (e is MText mx && Near(mx.Location)) { var ex = mx.GeometricExtents; var a = D(ex.MinPoint); var b = D(ex.MaxPoint); it.Add((id, "mtext", $"mtext {mx.Handle}", Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), new List<(Point2d, Point2d)>(), a, a)); } }
  bool SegBox((Point2d a, Point2d b) s, double x0, double y0, double x1, double y1) { int n = Math.Max(2, (int)(s.a.GetDistanceTo(s.b) / 0.4)); for (int k = 1; k < n; k++) { var t = k / (double)n; double x = s.a.X + (s.b.X - s.a.X) * t, y = s.a.Y + (s.b.Y - s.a.Y) * t; if (x > x0 - gap && x < x1 + gap && y > y0 - gap && y < y1 + gap) return true; } return false; }
  bool Clash(int i, double x0, double y0, double x1, double y1, List<(Point2d a, Point2d b)> lines, HashSet<int> skip) {
    if (x0 < v.ViewCenter.X - hw || x1 > v.ViewCenter.X + hw || y0 < v.ViewCenter.Y - hh || y1 > v.ViewCenter.Y + hh) return true;
    for (int j = 0; j < it.Count; j++) { if (j == i || skip.Contains(j)) continue; var B = it[j];
      if (x0 - gap < B.x1 && x1 + gap > B.x0 && y0 - gap < B.y1 && y1 + gap > B.y0) return true;
      foreach (var s in lines) if (SegBox(s, B.x0, B.y0, B.x1, B.y1)) return true; foreach (var s in B.lines) if (SegBox(s, x0, y0, x1, y1)) return true; }
    return false; }
  // ---- 1. labels to the sheet standard
  var onSheet = Enumerable.Range(0, it.Count).Where(i => it[i].kind == "label" && Math.Abs(it[i].anchor.X - v.ViewCenter.X) <= hw && Math.Abs(it[i].anchor.Y - v.ViewCenter.Y) <= hh).ToList();
  var stds = Standards(lay.TabOrder);
  var only = new HashSet<string>(Args.GetStringList("labelHandles"));
  foreach (var i in onSheet) { if (!only.Contains(it[i].name.Substring(6))) continue; var cur = it[i].loc - it[i].anchor; if (stds.Count == 0 || stds.Any(c => (c.c - cur).Length < 6)) continue;
    var order = stds.OrderBy(c => Math.Sign(c.c.Y) == Math.Sign(cur.Y) ? 0 : 1).ThenByDescending(c => c.n).Select(c => c.c).ToList();
    foreach (var tg in order) { var d = (it[i].anchor + tg) - it[i].loc; var nl = new List<(Point2d, Point2d)> { (it[i].anchor, it[i].loc + d) };
      if (Clash(i, it[i].x0 + d.X, it[i].y0 + d.Y, it[i].x1 + d.X, it[i].y1 + d.Y, nl, new HashSet<int>())) continue;
      rows.Add($"sheet {lay.LayoutName}: {it[i].name} drag ({cur.X:0},{cur.Y:0}) -> standard ({tg.X:0},{tg.Y:0})");
      if (!dryRun) { var w = Get<StationOffsetLabel>(it[i].id, OpenMode.ForWrite); w.LabelLocation = w.LabelLocation + new Vector3d(d.X, d.Y, 0).TransformBy(mi); }
      var o = it[i]; it[i] = (o.id, o.kind, o.name, o.x0 + d.X, o.y0 + d.Y, o.x1 + d.X, o.y1 + d.Y, nl, o.anchor, o.loc + d); goto nextLabel; }
    rows.Add($"sheet {lay.LayoutName}: {it[i].name} drag ({cur.X:0},{cur.Y:0}) is a one-off but no standard spot is clear - left");
    nextLabel:; }  // ---- 2. mleader pairs: same offsets everywhere, slide along the line if blocked
  var pair = pairTexts.Select(tx => Enumerable.Range(0, it.Count).FirstOrDefault(i => it[i].kind == "mleader" && it[i].name.Contains($"'{tx}'") && Math.Abs(it[i].anchor.X - v.ViewCenter.X) <= hw && Math.Abs(it[i].anchor.Y - v.ViewCenter.Y) <= hh, -1)).ToList();
  var skipTabs = Args.Has("skipLeaderTabs") ? Args.Get<List<int>>("skipLeaderTabs") : new List<int>();
  if (skipTabs.Contains(lay.TabOrder)) rows.Add($"sheet {lay.LayoutName}: leader pair kept (already uniform)");
  else if (pair.All(i => i >= 0)) {
    // the line direction at each tip: nearest visible curve/alignment tangent (sheet coords)
    Vector2d Dir(Point2d tip) { var w = new Point3d(tip.X, tip.Y, 0).TransformBy(mi); Vector3d best = Vector3d.XAxis; double bd = 1.0;
      foreach (ObjectId id in ModelSpace) { var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id); if (!(e is Curve c) || e is Autodesk.Civil.DatabaseServices.Entity || !On(e.Layer) || frozen.Contains(e.Layer)) continue; try { var cp = c.GetClosestPointTo(w, false); var dd = cp.DistanceTo(w); if (dd < bd) { bd = dd; best = c.GetFirstDerivative(cp); } } catch { } }
      foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (!On(a.Layer) || frozen.Contains(a.Layer)) continue; double s = 0, o = 0; try { a.StationOffset(w.X, w.Y, ref s, ref o); } catch { continue; } if (Math.Abs(o) < bd) { bd = Math.Abs(o); double x0 = 0, y0 = 0, x1 = 0, y1 = 0; a.PointLocation(s - 1, 0, ref x0, ref y0); a.PointLocation(s + 1, 0, ref x1, ref y1); best = new Vector3d(x1 - x0, y1 - y0, 0); } }
      var d = best.TransformBy(m); var v2 = new Vector2d(d.X, d.Y).GetNormal(); return v2.X < 0 ? -v2 : v2; }
    var dirs = pair.Select(i => Dir(it[i].anchor)).ToList();
    // already uniform (either layout, within 3 ft; arrowhead spacing within 3 ft)? leave it
    bool Matches(int sd) { for (int k = 0; k < pair.Count; k++) { var rel = it[pair[k]].loc - it[pair[k]].anchor; if (Math.Abs(rel.X - offs[k][0]) > 3 || Math.Abs(rel.Y - sd * offs[k][1]) > 3) return false; } return Math.Abs(Math.Abs((it[pair[1]].anchor - it[pair[0]].anchor).Length) - Math.Abs(Args.GetDouble("tipSpacing", 14))) < 3; }
    bool clashNow = pair.Any(i => Clash(i, it[i].x0, it[i].y0, it[i].x1, it[i].y1, it[i].lines, new HashSet<int>(pair)));
    if ((Matches(1) || Matches(-1)) && !clashNow) { rows.Add($"sheet {lay.LayoutName}: leader pair already uniform - kept"); goto pairDone; }
    // geometry of a leader placed with tip T and text offset O: text box keeps its size relative to the text location; leader = T -> text-side point
    (double x0, double y0, double x1, double y1, List<(Point2d a, Point2d b)> lines) Placed(int i, Point2d T, Vector2d O) { var o = it[i]; var d = (T + O) - o.loc;
      var nl = o.lines.Select(s => (s.a.GetDistanceTo(o.anchor) < 0.5 ? T : s.a + d, s.b.GetDistanceTo(o.anchor) < 0.5 ? T : s.b + d)).ToList(); return (o.x0 + d.X, o.y0 + d.Y, o.x1 + d.X, o.y1 + d.Y, nl); }
    double spacing = Args.GetDouble("tipSpacing", 14);
    // put a point on pair member k's own line (nearest point on that line to p, along the line's normal)
    Point2d TipOn(int k, Point2d p) { var a = it[pair[k]].anchor; var n = new Vector2d(-dirs[k].Y, dirs[k].X); return p - n * ((p - a).DotProduct(n)); }
    var skip = new HashSet<int>(pair); (double s, bool ok) best = (0, false);
    int side = 1; foreach (var sd in new[] { 1, -1 }) { side = sd; foreach (var s in Enumerable.Range(0, 121).Select(k => (k % 2 == 0 ? 1 : -1) * (k + 1) / 2 * 5.0)) { bool ok = true; var placed = new List<(double, double, double, double, List<(Point2d, Point2d)>)>();
      for (int k = 0; k < pair.Count; k++) { var i = pair[k]; var T = k == 0 ? it[pair[0]].anchor + dirs[0] * s : TipOn(k, it[pair[0]].anchor + dirs[0] * (s + spacing)); var p = Placed(i, T, new Vector2d(offs[k][0], side * offs[k][1])); placed.Add(p); if (Clash(i, p.x0, p.y0, p.x1, p.y1, p.lines, skip)) { ok = false; break; } }
      if (ok && placed.Count == 2) { var a = placed[0]; var b = placed[1]; if (a.Item1 - gap < b.Item3 && a.Item3 + gap > b.Item1 && a.Item2 - gap < b.Item4 && a.Item4 + gap > b.Item2) ok = false;
        foreach (var l in a.Item5) if (SegBox(l, b.Item1, b.Item2, b.Item3, b.Item4)) ok = false; foreach (var l in b.Item5) if (SegBox(l, a.Item1, a.Item2, a.Item3, a.Item4)) ok = false; }
      if (ok) { best = (s, true); break; } } if (best.ok) break; }
    if (!best.ok) rows.Add($"sheet {lay.LayoutName}: leader pair - no clear uniform spot within 300 ft, left as is");
    else { rows.Add($"sheet {lay.LayoutName}: leader pair -> uniform offsets {(side > 0 ? "(standard side)" : "(mirrored side)")}, slid {best.s:0} ft along the line");
      for (int k = 0; k < pair.Count; k++) { var i = pair[k]; var T = k == 0 ? it[pair[0]].anchor + dirs[0] * best.s : TipOn(k, it[pair[0]].anchor + dirs[0] * (best.s + spacing)); var tipMove = T - it[i].anchor; var textMove = (T + new Vector2d(offs[k][0], side * offs[k][1])) - it[i].loc;
        if (!dryRun) { var w = Get<MLeader>(it[i].id, OpenMode.ForWrite); w.TransformBy(Matrix3d.Displacement(new Vector3d(tipMove.X, tipMove.Y, 0).TransformBy(mi))); w.MoveMLeader(new Vector3d(textMove.X - tipMove.X, textMove.Y - tipMove.Y, 0).TransformBy(mi), MoveType.MoveContentAndDoglegPoints); } } } }
  else rows.Add($"sheet {lay.LayoutName}: leader pair not found");
  pairDone:; }
return new { dryRun, rows };
```
