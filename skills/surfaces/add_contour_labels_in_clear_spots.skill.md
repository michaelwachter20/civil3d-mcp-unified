---
name: add_contour_labels_in_clear_spots
category: surfaces
description: Add single contour labels here and there along a TIN surface's contours on a layout sheet, only where each label touches nothing but its own contour (point labels and markers, multileaders, text, lines, alignments and station labels, sample lines, other contours, existing contour labels). Labels only; the surface is not changed. Suggested saved script name contour-labels.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: surface
    type: string
    required: true
    description: TIN surface name
  - name: labelStyle
    type: string
    required: true
    description: Contour label style used for the major, minor and user contours (ask the user; one style for all three is common)
  - name: viewport
    type: string
    required: true
    description: Handle of the layout viewport the labels are for (untwisted viewports only)
  - name: spacingIn
    type: number
    required: false
    description: Min distance along one contour between its labels, sheet inches (default 3)
  - name: minGapIn
    type: number
    required: false
    description: Min distance between any two labels, sheet inches (default 0.5)
  - name: minContourIn
    type: number
    required: false
    description: Skip contour pieces shorter than this on the sheet (default 0.6)
  - name: alignmentBandIn
    type: number
    required: false
    description: Clear band on each side of an alignment for its station labels (default 0.12)
  - name: bendIn
    type: number
    required: false
    description: Max contour bend under a label, sheet inches (default 0.04)
  - name: probe
    type: string[]
    required: false
    description: "Debug: label-line spots 'x1,y1 x2,y2' (e.g. labels the user added by hand) - explains what blocks each one"
  - name: allowOtherContours
    type: boolean
    required: false
    description: Let labels cross neighbouring contours (dense contours such as ditch side slopes; default false)
  - name: ignoreLayers
    type: string[]
    required: false
    description: Layers whose entities are not obstacles (e.g. an alignment drawn with a no-display style)
  - name: labelLineHalfFt
    type: number
    required: false
    description: Half length of the hidden label line in feet (default 0.3; a 1 ft line crosses only one contour)
  - name: dryRun
    type: boolean
    required: false
    description: Plan only, change nothing (default true)
---

> Verified on Civil 3D 2027.

## When to use
The user wants contour elevations labeled on a plan sheet "here and there", with no label sitting on other
annotation. Each label is its own contour label group (like Add Labels > Contour - Single) with a short hidden
label line across exactly one contour.

## How it works
- Contours are traced from the surface's **visible** triangles at the style's minor interval, so areas hidden by a
  boundary get no labels.
- A label box (text width + margin, text height x 1.3) is centered on the contour and turned along it. A spot is
  used only if nothing but the label's own contour (the stretch under the label) crosses the box, and the contour
  runs nearly straight under it.
- Obstacles: every visible model-space entity in the viewport on a layer that is on, thawed and not frozen in the
  viewport (curves as segments, multileaders and blocks exploded in memory, text as boxes), alignments with a band
  for station labels, sample lines, COGO point markers and labels as displayed (point group style overrides
  respected, dragged labels as leader + horizontal box), all other contour pieces, and existing contour labels on
  the surface.
- Major contours are tried first, then longer contours. Labels are spaced along each contour and kept apart.

## How to run
1. Ask which surface and label style. Find the sheet's viewport handle (`civil3d_query`: layouts, viewports,
   scale).
2. Dry run (`civil3d_query`, `dryRun: true`) and report the count by elevation and the rejections.
3. Run with `civil3d_execute`. The labels are a preview until the user looks at the sheet.
4. If the user adds labels by hand where the script found none, run again with `probe` set to their label-line
   points (`SurfaceContourLabelGroup.LabelLinePoints`) to see what blocked each spot. Loosen the matching
   parameter, then run again: existing labels are avoided, so only new ones are added.
5. Save the drawing once the user is happy.

## Lessons from first use
- First defaults (6" along a contour, 2" between labels, 1.5" minimum contour, 0.35" alignment band) were far too
  sparse. The user's own labels sat 0.2-2" from each other, on contour pieces about 1" long, and close to the
  alignment. The defaults now follow those.
- A label is never "on" its own contour: allow the contour's own segments whose arc-length range overlaps the label
  window, or a long straight segment through the label counts as a conflict.
- Users eyeball the last few. Let them hand-place those rather than chasing every spot.

## Dense contours (ditches, steep slopes)
- For a ditch or channel the user wants the `contour_label_ladder` skill instead: one contour per stop, reading in
  sequence down one slope and up the other. Filling every clear spot on dense contours gave far too many labels.

- Neighbouring contours are only ~1 ft apart, so every label box crosses them and nothing qualifies. Do what a finished
  go-by did: `allowOtherContours: true`, short label lines (`labelLineHalfFt: 0.5` = 1 ft), labels about every 20 ft
  along each slope (`spacingIn: 0.65`, `minGapIn: 0.25` at 1"=30'), and `ignoreLayers` for no-display alignments.
  Point labels, sample lines and other linework still block spots.

## API notes
- `SurfaceContourLabelGroup.Create(surfaceId, Point2dCollection labelLine, majorStyleId, minorStyleId, userStyleId)`
  creates one group; set `IsLabelLineVisible = false`. A 0.6 ft label line crosses only the target contour.
- `SurfaceContourLabelGroup.GetAvailableLabelGroupIds(surfaceId)` lists a surface's groups;
  `LabelLinePoints` gives each group's line.
- `TinSurface.GetTriangles(false)` returns only visible triangles.

## Script
```csharp
// contour-labels: add single contour labels here and there along a TIN surface's contours inside a layout
// viewport, only where the label touches nothing but its own contour (point labels and markers, multileaders,
// text, lines, alignments and their labels, sample lines, other contours, other new labels).
// Contours are traced from the surface's visible triangles at the style's minor interval. Each label is a
// contour label group with a short hidden label line across exactly one contour.
var surfName = Args.GetString("surface");
var styleName = Args.GetString("labelStyle");                 // used for major, minor and user contours
var spacingIn = Args.GetDouble("spacingIn", 3.0);             // min distance along one contour between its labels
var minGapIn = Args.GetDouble("minGapIn", 0.5);               // min distance between any two labels (centers)
var minContourIn = Args.GetDouble("minContourIn", 0.6);       // skip contour pieces shorter than this on the sheet
var clearIn = Args.GetDouble("clearanceIn", 0.02);
var dryRun = Args.GetBool("dryRun", true);
var allowOtherContours = Args.GetBool("allowOtherContours", false);   // dense contours (ditch slopes): labels may cross neighbouring contours
var ignoreLayers = new HashSet<string>(Args.GetStringList("ignoreLayers"), StringComparer.OrdinalIgnoreCase);   // e.g. a no-display alignment's layer

var vp = Get<Viewport>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("viewport"), 16)), 0));
if (Math.Abs(vp.TwistAngle) > 1e-6) throw new InvalidOperationException("Twisted viewports are not handled yet.");
double mpp = 1 / vp.CustomScale;
var vc = new Point2d(vp.ViewTarget.X + vp.ViewCenter.X, vp.ViewTarget.Y + vp.ViewCenter.Y);
double hw = vp.Width / 2 * mpp, hh = vp.Height / 2 * mpp;
bool InView(Point2d p, double m = 0) => Math.Abs(p.X - vc.X) <= hw - m && Math.Abs(p.Y - vc.Y) <= hh - m;
var frozen = new HashSet<ObjectId>(vp.GetFrozenLayers().Cast<ObjectId>());
var lt = Get<LayerTable>(Database.LayerTableId);
var layerVis = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
bool LayerVisible(string layer) {
  if (string.IsNullOrEmpty(layer)) return true;
  if (layerVis.TryGetValue(layer, out var v)) return v;
  bool vis = true; if (lt.Has(layer)) { var id = lt[layer]; var l = Get<LayerTableRecord>(id); vis = !l.IsFrozen && !l.IsOff && !frozen.Contains(id); }
  return layerVis[layer] = vis;
}

// ---- surface and label style ----
TinSurface surf = null;
foreach (ObjectId sid in CivilDoc.GetSurfaceIds()) { var s = Get<Autodesk.Civil.DatabaseServices.Surface>(sid) as TinSurface; if (s != null && s.Name == surfName) surf = s; }
if (surf == null) throw new ArgumentException($"TIN surface '{surfName}' not found.");
if (!LayerVisible(surf.Layer)) throw new InvalidOperationException($"Surface layer {surf.Layer} is not visible in this viewport.");
ObjectId styleId = ObjectId.Null;
foreach (ObjectId id in CivilDoc.Styles.LabelStyles.SurfaceLabelStyles.ContourLabelStyles) if (Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(id).Name == styleName) styleId = id;
if (styleId.IsNull) throw new ArgumentException($"Contour label style '{styleName}' not found.");
var ls = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(styleId);
double textHIn = 0.08;
foreach (ObjectId cid in ls.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text)) textHIn = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent>(cid).Text.Height.Value * 12;
var sst = Get<Autodesk.Civil.DatabaseServices.Styles.SurfaceStyle>(surf.StyleId);
double interval = sst.ContourStyle.MinorContourInterval;

// ---- segment index (model units) ----
double cell = 1.0 * mpp;
var grid = new Dictionary<(int, int), List<int>>();
var segA = new List<Point2d>(); var segB = new List<Point2d>(); var segOwner = new List<int>(); var segK = new List<int>(); var segTag = new List<string>(); string curTag = ""; int curK = -1;   // owner: -1 obstacle, else contour chain id
void AddSeg(Point2d a, Point2d b, int owner) {
  if (!InView(a, -10 * mpp) && !InView(b, -10 * mpp)) return;
  int i = segA.Count; segA.Add(a); segB.Add(b); segOwner.Add(owner); segK.Add(curK); segTag.Add(owner >= 0 ? "contour" : curTag);
  int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / cell), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / cell);
  int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / cell), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / cell);
  for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { if (!grid.TryGetValue((x, y), out var l)) grid[(x, y)] = l = new List<int>(); l.Add(i); }
}
void AddBox(Point2d mn, Point2d mx) { var a = mn; var b = new Point2d(mx.X, mn.Y); var c = mx; var d = new Point2d(mn.X, mx.Y); AddSeg(a, b, -1); AddSeg(b, c, -1); AddSeg(c, d, -1); AddSeg(d, a, -1); }
int obstacleEntities = 0;
void AddCurve(Autodesk.AutoCAD.DatabaseServices.Curve cv) {
  double len; try { len = cv.GetDistanceAtParameter(cv.EndParam); } catch { return; }
  int n = Math.Max(1, (int)Math.Ceiling(len / (0.05 * mpp)));
  if (cv is Polyline pl) { for (int i = 0; i < pl.NumberOfVertices - (pl.Closed ? 0 : 1); i++) { var a = pl.GetPoint2dAt(i); var b = pl.GetPoint2dAt((i + 1) % pl.NumberOfVertices); if (pl.GetBulgeAt(i) == 0) AddSeg(a, b, -1); else { var s0 = pl.GetDistanceAtParameter(i); var s1 = pl.GetDistanceAtParameter(i + 1); for (int k = 0; k < 8; k++) { var p0 = pl.GetPointAtDist(s0 + (s1 - s0) * k / 8); var p1 = pl.GetPointAtDist(s0 + (s1 - s0) * (k + 1) / 8); AddSeg(new Point2d(p0.X, p0.Y), new Point2d(p1.X, p1.Y), -1); } } } return; }
  Point3d prev = cv.StartPoint;
  for (int k = 1; k <= n; k++) { var p = cv.GetPointAtDist(Math.Min(len, len * k / n)); AddSeg(new Point2d(prev.X, prev.Y), new Point2d(p.X, p.Y), -1); prev = p; }
}
void AddEntity(Autodesk.AutoCAD.DatabaseServices.Entity e, int depth) {
  if (e is Autodesk.AutoCAD.DatabaseServices.Curve cv) { AddCurve(cv); return; }
  if (e is DBText || e is MText || e is Solid || e is Hatch || depth > 2) { try { var x = e.GeometricExtents; AddBox(new Point2d(x.MinPoint.X, x.MinPoint.Y), new Point2d(x.MaxPoint.X, x.MaxPoint.Y)); } catch { } return; }
  var parts = new DBObjectCollection();
  try { e.Explode(parts); } catch { try { var x = e.GeometricExtents; AddBox(new Point2d(x.MinPoint.X, x.MinPoint.Y), new Point2d(x.MaxPoint.X, x.MaxPoint.Y)); } catch { } return; }
  foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) { if (o is Autodesk.AutoCAD.DatabaseServices.Entity pe) AddEntity(pe, depth + 1); o.Dispose(); }
}

// ---- obstacles: model-space entities visible in the viewport (not points, not the surface) ----
var ms = Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Database));
var pointIds = new List<ObjectId>();
foreach (ObjectId id in ms) {
  var dxf = id.ObjectClass.DxfName;
  if (dxf == "AECC_COGO_POINT") { pointIds.Add(id); continue; }
  if (dxf == "AECC_TIN_SURFACE" || dxf == "VIEWPORT") continue;
  var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id);
  if (!e.Visible || !LayerVisible(e.Layer) || ignoreLayers.Contains(e.Layer)) continue;
  Extents3d ex; try { ex = e.GeometricExtents; } catch { continue; }
  if (ex.MaxPoint.X < vc.X - hw || ex.MinPoint.X > vc.X + hw || ex.MaxPoint.Y < vc.Y - hh || ex.MinPoint.Y > vc.Y + hh) continue;
  obstacleEntities++; curTag = dxf + " " + e.Layer;
  if (e is Alignment al) {
    // the alignment line plus a band for its station labels
    double band = Args.GetDouble("alignmentBandIn", 0.12) * mpp;
    for (double st = al.StartingStation; st < al.EndingStation; st += 0.05 * mpp) {
      curTag = "alignment line " + al.Name; double x0 = 0, y0 = 0, x1 = 0, y1 = 0; al.PointLocation(st, 0, ref x0, ref y0); al.PointLocation(Math.Min(al.EndingStation, st + 0.05 * mpp), 0, ref x1, ref y1);
      AddSeg(new Point2d(x0, y0), new Point2d(x1, y1), -1);
      curTag = "alignment station-label band " + al.Name; foreach (var off in new[] { -band, band }) { double a0 = 0, b0 = 0, a1 = 0, b1 = 0; al.PointLocation(st, off, ref a0, ref b0); al.PointLocation(Math.Min(al.EndingStation, st + 0.05 * mpp), off, ref a1, ref b1); AddSeg(new Point2d(a0, b0), new Point2d(a1, b1), -1); }
    }
    continue;
  }
  if (e is SampleLine sl) { var vs = sl.Vertices; for (int i = 1; i < vs.Count; i++) AddSeg(new Point2d(vs[i - 1].Location.X, vs[i - 1].Location.Y), new Point2d(vs[i].Location.X, vs[i].Location.Y), -1); continue; }
  if (dxf.StartsWith("AECC_") && !(e is Autodesk.AutoCAD.DatabaseServices.Curve)) { AddBox(new Point2d(ex.MinPoint.X, ex.MinPoint.Y), new Point2d(ex.MaxPoint.X, ex.MaxPoint.Y)); continue; }
  AddEntity(e, 0);
}

// ---- obstacles: COGO point markers and labels as shown (same geometry as the declutter script) ----
var groupList = new List<(bool ovL, ObjectId ls, bool ovP, ObjectId ps, HashSet<uint> nums)>();
foreach (ObjectId gid in CivilDoc.PointGroups) { var g = Get<PointGroup>(gid); groupList.Add((g.IsPointLabelStyleOverridden, g.PointLabelStyleId, g.IsPointStyleOverridden, g.PointStyleId, new HashSet<uint>(g.GetPointNumbers()))); }
var styleVis = new Dictionary<ObjectId, bool>();
bool LabelStyleVisible(ObjectId id) { if (id.IsNull) return false; if (styleVis.TryGetValue(id, out var v)) return v; var lay = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(id).Properties.Label.Layer.Value; return styleVis[id] = LayerVisible(lay); }
double pTextH = 0.075 * mpp, pGap = 0.005 * mpp, dog = 0.05 * mpp, markerR = 0.85;
var widthCache = new Dictionary<string, double>();
double TextW(double z) { var s = z.ToString("0.00", CultureInfo.InvariantCulture); if (widthCache.TryGetValue(s, out var w)) return w; using (var m = new MText()) { m.SetDatabaseDefaults(Database); m.TextHeight = pTextH; m.Contents = "{\\fTxt|b0|i1|c0|p2;" + s + "}"; w = m.ActualWidth + 0.006 * mpp; } return widthCache[s] = w; }
int pointsCounted = 0;
foreach (var id in pointIds) {
  var p = Get<CogoPoint>(id);
  var P = new Point2d(p.Location.X, p.Location.Y);
  if (!InView(P, -1 * mpp) || !LayerVisible(p.Layer)) continue;
  ObjectId lsId = p.LabelStyleId, psId = p.StyleId;
  foreach (var g in groupList) { if (g.nums.Contains(p.PointNumber)) { if (g.ovL) lsId = g.ls; if (g.ovP) psId = g.ps; if (g.ovL || g.ovP) break; } }
  curTag = "point marker " + p.PointNumber; if (!psId.IsNull) AddBox(new Point2d(P.X - markerR, P.Y - markerR), new Point2d(P.X + markerR, P.Y + markerR));
  if (!LabelStyleVisible(lsId)) continue;
  pointsCounted++; curTag = "point label " + p.PointNumber;
  double w = TextW(p.Elevation);
  if (p.IsLabelDragged) {
    var a = new Point2d(p.LabelLocation.X, p.LabelLocation.Y); bool right = a.X >= P.X;
    var el = new Point2d(right ? a.X - dog : a.X + dog, a.Y);
    AddSeg(P, el, -1); AddSeg(el, a, -1);
    if (right) AddBox(new Point2d(a.X + pGap, a.Y - pTextH / 2), new Point2d(a.X + pGap + w, a.Y + pTextH / 2));
    else AddBox(new Point2d(a.X - pGap - w, a.Y - pTextH / 2), new Point2d(a.X - pGap, a.Y + pTextH / 2));
  } else AddBox(new Point2d(P.X - w - 0.06 * mpp, P.Y - 0.06 * mpp - pTextH), new Point2d(P.X + w + 0.06 * mpp, P.Y + 0.06 * mpp + pTextH));
}

// ---- contours from visible triangles ----
string Key(Point3d a, Point3d b) => a.X < b.X || (a.X == b.X && a.Y < b.Y) ? $"{a.X:0.###},{a.Y:0.###}|{b.X:0.###},{b.Y:0.###}" : $"{b.X:0.###},{b.Y:0.###}|{a.X:0.###},{a.Y:0.###}";
Point2d Cut(Point3d a, Point3d b, double z) { if (a.X > b.X || (a.X == b.X && a.Y > b.Y)) { var t = a; a = b; b = t; } double f = (z - a.Z) / (b.Z - a.Z); return new Point2d(Math.Round(a.X + f * (b.X - a.X), 4), Math.Round(a.Y + f * (b.Y - a.Y), 4)); }
var pieces = new Dictionary<double, List<(Point2d, Point2d)>>();
foreach (var t in surf.GetTriangles(false)) {
  var v = new[] { t.Vertex1.Location, t.Vertex2.Location, t.Vertex3.Location };
  var c = new Point2d((v[0].X + v[1].X + v[2].X) / 3, (v[0].Y + v[1].Y + v[2].Y) / 3);
  if (!InView(c, -5 * mpp)) continue;
  double lo = v.Min(q => q.Z), hi = v.Max(q => q.Z);
  for (double z = Math.Ceiling(lo / interval) * interval; z <= hi; z += interval) {
    var cuts = new List<Point2d>();
    for (int i = 0; i < 3; i++) { var a = v[i]; var b = v[(i + 1) % 3]; if ((a.Z - z) * (b.Z - z) < 0 || (a.Z == z && b.Z != z)) { if (a.Z == z) cuts.Add(new Point2d(a.X, a.Y)); else cuts.Add(Cut(a, b, z)); } }
    cuts = cuts.Distinct().ToList();
    if (cuts.Count == 2) { if (!pieces.TryGetValue(z, out var l)) pieces[z] = l = new List<(Point2d, Point2d)>(); l.Add((cuts[0], cuts[1])); }
  }
}
// chain pieces of each elevation into polylines
var chains = new List<(double z, List<Point2d> pts)>();
string PK(Point2d p) => $"{p.X:0.###},{p.Y:0.###}";
foreach (var kv in pieces) {
  var adj = new Dictionary<string, List<int>>();
  for (int i = 0; i < kv.Value.Count; i++) foreach (var p in new[] { kv.Value[i].Item1, kv.Value[i].Item2 }) { if (!adj.TryGetValue(PK(p), out var l)) adj[PK(p)] = l = new List<int>(); l.Add(i); }
  var used = new bool[kv.Value.Count];
  for (int s = 0; s < kv.Value.Count; s++) {
    if (used[s]) continue; used[s] = true;
    var pts = new LinkedList<Point2d>(new[] { kv.Value[s].Item1, kv.Value[s].Item2 });
    foreach (var forward in new[] { true, false }) {
      while (true) {
        var end = forward ? pts.Last.Value : pts.First.Value;
        int next = adj[PK(end)].FirstOrDefault(i => !used[i], -1);
        if (next < 0) break; used[next] = true;
        var (a, b) = kv.Value[next]; var o = PK(a) == PK(end) ? b : a;
        if (forward) pts.AddLast(o); else pts.AddFirst(o);
      }
    }
    chains.Add((kv.Key, pts.ToList()));
  }
}
for (int ci = 0; ci < chains.Count; ci++) { var pts = chains[ci].pts; for (int i = 1; i < pts.Count; i++) { curK = i; AddSeg(pts[i - 1], pts[i], ci); } } curK = -1;

// ---- candidate test: oriented label box ----
double clear = clearIn * mpp;
double lblH = textHIn * mpp * 1.3 + 2 * clear;
bool SegHitsRect(Point2d a, Point2d b, Point2d c, Vector2d u, double halfL, double halfH) {
  var n = u.GetPerpendicularVector();
  double au = (a - c).DotProduct(u), av = (a - c).DotProduct(n), bu = (b - c).DotProduct(u), bv = (b - c).DotProduct(n);
  double t0 = 0, t1 = 1, du = bu - au, dv = bv - av;
  bool Clip(double p, double q) { if (Math.Abs(p) < 1e-12) return q >= 0; double r = q / p; if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; } else { if (r < t0) return false; if (r < t1) t1 = r; } return true; }
  return Clip(-du, au + halfL) && Clip(du, halfL - au) && Clip(-dv, av + halfH) && Clip(dv, halfH - av) && t0 <= t1;
}
List<int> Near(Point2d c, double r) {
  var res = new HashSet<int>();
  for (int x = (int)Math.Floor((c.X - r) / cell); x <= (int)Math.Floor((c.X + r) / cell); x++)
    for (int y = (int)Math.Floor((c.Y - r) / cell); y <= (int)Math.Floor((c.Y + r) / cell); y++)
      if (grid.TryGetValue((x, y), out var l)) foreach (var i in l) res.Add(i);
  return res.ToList();
}
var placed = new List<(Point2d c, Vector2d u, double halfL, double z, int chain)>();
// existing contour labels on this surface (the user's and earlier runs'): keep clear of them and count them for spacing
int existing = 0;
foreach (ObjectId gid in SurfaceContourLabelGroup.GetAvailableLabelGroupIds(surf.ObjectId)) {
  var lp = Get<SurfaceContourLabelGroup>(gid).LabelLinePoints;
  for (int q = 1; q < lp.Count; q++) {
    var a = lp[q - 1]; var b = lp[q];
    for (int ci = 0; ci < chains.Count; ci++) { var pts = chains[ci].pts; for (int k = 1; k < pts.Count; k++) {
      double Cr(Point2d o, Point2d pp, Point2d qq) => (pp.X - o.X) * (qq.Y - o.Y) - (pp.Y - o.Y) * (qq.X - o.X);
      double d1 = Cr(pts[k - 1], pts[k], a), d2 = Cr(pts[k - 1], pts[k], b), d3 = Cr(a, b, pts[k - 1]), d4 = Cr(a, b, pts[k]);
      if (!(d1 * d2 < 0 && d3 * d4 < 0)) continue;
      var c = a + (b - a) * (d1 / (d1 - d2)); var u = (pts[k] - pts[k - 1]).GetNormal(); var n = u.GetPerpendicularVector();
      string txt = chains[ci].z.ToString("0", CultureInfo.InvariantCulture);
      double tw; using (var m = new MText()) { m.SetDatabaseDefaults(Database); m.TextHeight = textHIn * mpp; m.Contents = txt; tw = m.ActualWidth; }
      double hl = tw / 2 + 0.04 * mpp, hh2 = textHIn * mpp * 0.65;
      var c1 = c - u * hl - n * hh2; var c2 = c + u * hl - n * hh2; var c3 = c + u * hl + n * hh2; var c4 = c - u * hl + n * hh2;
      curTag = "existing contour label " + gid.Handle; AddSeg(c1, c2, -1); AddSeg(c2, c3, -1); AddSeg(c3, c4, -1); AddSeg(c4, c1, -1);
      placed.Add((c, u, hl, chains[ci].z, ci)); existing++;
    } }
  }
}
int candidates = 0, rejectedCrowded = 0, rejectedBent = 0;
foreach (var ci in Enumerable.Range(0, chains.Count).OrderByDescending(i => Math.Abs(chains[i].z % 5) < 1e-6).ThenByDescending(i => chains[i].pts.Count)) {
  var (z, pts) = chains[ci];
  var cum = new List<double> { 0 }; for (int i = 1; i < pts.Count; i++) cum.Add(cum[i - 1] + pts[i].GetDistanceTo(pts[i - 1]));
  double total = cum[cum.Count - 1];
  if (total < minContourIn * mpp) continue;
  string txt = z.ToString("0", CultureInfo.InvariantCulture);
  double textW; using (var m = new MText()) { m.SetDatabaseDefaults(Database); m.TextHeight = textHIn * mpp; m.Contents = txt; textW = m.ActualWidth; }
  double halfL = textW / 2 + 0.04 * mpp + clear, halfH = lblH / 2;
  Point2d At(double s) { int i = cum.BinarySearch(s); if (i >= 0) return pts[i]; i = ~i; if (i <= 0) return pts[0]; if (i >= pts.Count) return pts[pts.Count - 1]; double f = (s - cum[i - 1]) / (cum[i] - cum[i - 1]); return pts[i - 1] + (pts[i] - pts[i - 1]) * f; }
  double lastS = double.NegativeInfinity;
  for (double s = halfL + 0.2 * mpp; s <= total - halfL - 0.2 * mpp; s += 0.1 * mpp) {
    if (s - lastS < spacingIn * mpp) continue;
    var c = At(s);
    if (!InView(c, 0.3 * mpp)) continue;
    var p0 = At(s - halfL); var p1 = At(s + halfL);
    if (p0.GetDistanceTo(p1) < 1e-6) continue;
    var u = (p1 - p0).GetNormal();
    candidates++;
    // the contour must run straight through the label
    bool bent = false;
    for (double q = s - halfL; q <= s + halfL; q += halfL / 4) { var pq = At(q); if (Math.Abs((pq - p0).DotProduct(u.GetPerpendicularVector())) > Args.GetDouble("bendIn", 0.04) * mpp) { bent = true; break; } }
    if (bent) { rejectedBent++; continue; }
    if (placed.Any(pl => pl.c.GetDistanceTo(c) < minGapIn * mpp)) continue;
    bool hit = false;
    foreach (var i in Near(c, halfL + halfH + cell)) {
      if (segOwner[i] == ci) {
        // its own contour: allowed inside the label window, not elsewhere (a hairpin through the label)
        int k = segK[i];
        if (cum[k] >= s - halfL - 0.1 * mpp && cum[k - 1] <= s + halfL + 0.1 * mpp) continue;
      }
      if (allowOtherContours && segOwner[i] >= 0 && segOwner[i] != ci) continue;
    }
    if (hit) { rejectedCrowded++; continue; }
    placed.Add((c, u, halfL, z, ci)); lastS = s;
  }
}

// ---- probe: why a given label line spot was (not) chosen ----
if (Args.Has("probe")) {
  var outp = new List<object>();
  foreach (var spec in Args.GetStringList("probe")) {
    var nums = spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    var a = new Point2d(nums[0], nums[1]); var b = new Point2d(nums[2], nums[3]);
    var crossings = new List<(int ci, int k, Point2d p)>();
    for (int ci = 0; ci < chains.Count; ci++) { var pts = chains[ci].pts; for (int k = 1; k < pts.Count; k++) {
      double Cr(Point2d o, Point2d p, Point2d q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
      double d1 = Cr(pts[k - 1], pts[k], a), d2 = Cr(pts[k - 1], pts[k], b), d3 = Cr(a, b, pts[k - 1]), d4 = Cr(a, b, pts[k]);
      if (d1 * d2 < 0 && d3 * d4 < 0) { double f = d1 / (d1 - d2); crossings.Add((ci, k, a + (b - a) * f)); } } }
    if (crossings.Count == 0) { outp.Add(new { spec, crosses = "no traced contour" }); continue; }
    var reasons = new List<string>();
    foreach (var (ci, k, p) in crossings) {
      var pts = chains[ci].pts;
      var u = (pts[k] - pts[k - 1]).GetNormal();
      string txt = chains[ci].z.ToString("0", CultureInfo.InvariantCulture);
      double textW; using (var m = new MText()) { m.SetDatabaseDefaults(Database); m.TextHeight = textHIn * mpp; m.Contents = txt; textW = m.ActualWidth; }
      double halfL = textW / 2 + 0.04 * mpp + clear, halfH = lblH / 2;
      // bend: distance of chain vertices within the window from the local tangent
      double maxDev = 0;
      foreach (var q in pts) { double along = (q - p).DotProduct(u); if (Math.Abs(along) <= halfL) maxDev = Math.Max(maxDev, Math.Abs((q - p).DotProduct(u.GetPerpendicularVector()))); }
      var tags = new HashSet<string>();
      foreach (var i in Near(p, halfL + halfH + cell)) {
        if (segOwner[i] == ci) { var mid = new Point2d((segA[i].X + segB[i].X) / 2, (segA[i].Y + segB[i].Y) / 2); if (Math.Abs((mid - p).DotProduct(u)) <= halfL + 0.05 * mpp) continue; }
        if (SegHitsRect(segA[i], segB[i], p, u, halfL, halfH)) tags.Add(segOwner[i] >= 0 ? $"contour {chains[segOwner[i]].z}" + (segOwner[i] == ci ? " (own, outside window)" : "") : segTag[i]);
      }
      double chainLen = 0; for (int q = 1; q < pts.Count; q++) chainLen += pts[q].GetDistanceTo(pts[q - 1]);
      var near = placed.Where(pl => pl.c.GetDistanceTo(p) < minGapIn * mpp).Select(pl => $"{pl.z}@{pl.c.GetDistanceTo(p) / mpp:0.00}in");
      reasons.Add($"z={chains[ci].z} chainLenIn={chainLen / mpp:0.0} bendIn={maxDev / mpp:0.000} hits=[{string.Join("; ", tags)}] closeToMine=[{string.Join(",", near)}]");
    }
    outp.Add(new { spec, crossings = crossings.Count, reasons });
  }
  return outp;
}

// ---- create ----
var created = new List<ObjectId>();
int failed = 0;
if (!dryRun) {
  foreach (var pl in placed.Skip(existing)) {
    var n = pl.u.GetPerpendicularVector() * Args.GetDouble("labelLineHalfFt", 0.3);
    var line = new Point2dCollection { pl.c - n, pl.c + n };
    try {
      var gid = SurfaceContourLabelGroup.Create(surf.ObjectId, line, styleId, styleId, styleId);
      var g = Get<SurfaceContourLabelGroup>(gid, OpenMode.ForWrite);
      g.IsLabelLineVisible = false;
      created.Add(gid);
    } catch { failed++; }
  }
  if (created.Count > 0) FocusOn(new Point3d(vc.X - hw, vc.Y - hh, 0), new Point3d(vc.X + hw, vc.Y + hh, 0));
}
return new {
  dryRun, surface = surf.Name, style = styleName, textHeightIn = Math.Round(textHIn, 3), interval, chains = chains.Count, obstacleEntities, pointLabels = pointsCounted, segments = segA.Count,
  existing, candidates, rejectedBent, rejectedCrowded, labels = placed.Count - existing, created = created.Count, failed,
  byElevation = placed.Skip(existing).GroupBy(p => p.z).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}"),
};
```
