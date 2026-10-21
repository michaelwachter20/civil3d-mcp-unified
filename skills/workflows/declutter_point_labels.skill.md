---
name: declutter_point_labels
category: workflows
description: De-overlap COGO point labels on a layout sheet - drag every label to a clear spot close to its point, each display group on its own side, close shots stacked in sheet order, repeat/older shots hidden in a hide point group, hand placements kept; never touches the points themselves. Suggested saved script name declutter-labels (undo helper declutter-labels-undo).
requires_write: true
parameters:
  - name: primary
    type: string
    required: true
    description: Display point group whose labels are placed first (the material or type that matters most on this sheet)
  - name: secondary
    type: string[]
    required: false
    description: Other display point groups, placed after the primary group
  - name: hideGroup
    type: string
    required: true
    description: Point group whose style override hides marker and label; thinned points are added to its include list
  - name: sideByGroup
    type: json
    required: false
    description: Preferred label side per display group, e.g. {"FINISH Display":"right-up","EXISTING Display":"left-down"}
  - name: protectedCodes
    type: string[]
    required: false
    description: Description codes (first token) that are never hidden (default TOP, TOE, PERI)
  - name: keepLabels
    type: string[]
    required: false
    description: Point numbers whose hand-placed labels never move
  - name: redoAiPlacement
    type: boolean
    required: false
    description: Re-place every dragged label not in keepLabels (use when iterating on a previous run)
  - name: unhide
    type: string[]
    required: false
    description: Point numbers a previous run hid; taken out of the hide group and considered again
  - name: labelBoxHeightIn
    type: number
    required: false
    description: Label text box height on the sheet in paper inches (default 0.075)
  - name: clearanceIn
    type: number
    required: false
    description: Minimum clear space between labels in paper inches (default 0 - labels may touch, never overlap)
  - name: flipMaxIn
    type: number
    required: false
    description: How far a crowded label may go in any direction before longer reaches are tried (default 0.25)
  - name: annotationObstacles
    type: boolean
    required: false
    description: Treat other annotation on the sheet (Civil labels such as station/offset labels, MLeaders, MText, text) as fixed obstacles, from exact in-memory Explode geometry (default true)
  - name: dryRun
    type: boolean
    required: false
    description: Plan only, change nothing (default true)
---

## When to use
The user has COGO point labels overlapping on a plotted sheet (layout viewport) and wants them dragged apart,
with dense spots thinned by adding points to a point group whose style override is `<none>`.
Proven on live Civil 3D 2027 drawings (twisted and untwisted layout viewports, ~500-1000 visible labels): a second
or two per run, one undo step.

## Hard rules
- Never explode a point (not even in memory), never move a point, never edit point data. Only label positions
  and the hide group's include list change.
- Labels of protected codes (default TOP/TOE/PERI) are never hidden.
- Labels the user placed by hand stay where they are (`keepLabels`); never "improve" them.
- Leaders never cross each other or label text; text never overlaps text.
- Other annotation already on the sheet is a fixed obstacle (v4): point labels never sit on, or run a leader through,
  station/offset or other Civil labels, MLeaders (their text, leader line and landing), MText or text - and no other
  label's leader may run through a point label.

## Exact geometry (v4)
- Other annotation is measured with in-memory `Entity.Explode` - it returns copies and never changes the drawing.
  A Civil label explodes to a block reference (explode again: border lines, MText, a Leader); an MLeader explodes to
  MText, a landing line, the leader polyline from the arrowhead, and the arrow solid.
- Civil 3D labels explode only in the write (command) context: in a read-only run they return nothing (MLeaders work in both). Run through `civil3d_execute`; the script stops if a label explodes empty.
- Exploded geometry follows the ACTIVE viewport's twist and annotation scale (labels are view-oriented and
  annotative), which is why the script runs in the activated viewport of the sheet being cleaned.
- Points cannot be exploded (`CogoPoint.Explode` returns nothing), so point labels keep their calibrated box.
- Check leader LINES against boxes, not only text against text: a leader running through a label is a clash.

## Placement rules (learned from a user's hand corrections)
- Each display group has a side (`sideByGroup`), e.g. one group right-up and another left-down, so two surveys
  of the same spot read apart. The first spot is 0.10" sideways and just above (or below) the point, with an
  obtuse bend into a 0.05" landing.
- Shots of one group close together (`stackRadiusIn`) stack in neat rows that follow the points' own top-to-bottom
  order on the sheet, even when the points differ by 0.001". The order applies only among labels going the same
  way (both up or both down).
- Crowded spots: closeness beats the side. A stuck label first tries 0.01" nudges around its first spot, then the
  next stack rows, then any of the four directions within `flipMaxIn` - own side cheapest, other side or other
  direction next, both together last, and toward the outside of its cluster rather than across it. A label well
  above or below its point (0.06"+) may sit nearly straight over it (leader end 0.03" out).
- Only after that does it reach further (0.3-0.75") or hide an older repeat shot.

## How to run
1. Ask the user for their point group names: the display groups (`primary`, `secondary`), the hide group, and which
   side each group's labels go. `civil3d_query` can list the point groups and their style overrides.
2. Open the layout and activate the viewport (double-click inside it).
3. Dry run (`civil3d_execute` with `dryRun: true` - it changes nothing; Civil 3D labels only explode in the write context, so a read-only `civil3d_query` run would miss them) and report the counts (`unresolved`, `crossingLeaders`,
   `stackOrderViolations`, `textOnTextRemaining`).
4. Apply as a preview (`civil3d_execute`) without saving, then select the remaining problem points for the user
   (`Editor.SetImpliedSelection` from a read-only `civil3d_query`: a selection set inside a write's command
   context is cleared when the command ends).
5. Iterate: the user drags a few labels where they belong. Find them with `diff: true` (labels whose position
   differs from the plan of a run with the same args), read the offsets in sheet inches, turn them into a rule,
   add those points to `keepLabels`, and rerun with `redoAiPlacement: true` and `unhide` = the previous run's
   `hiddenNums`.
6. When the user approves, save the drawing and save the script with `civil3d_script` (mode write, the user's
   group names as defaults) so they can run `MCPRUN`.
7. Keep each run's `movedNums`, `hiddenNums` and `restore` list: `restore` holds the original `LabelLocation`
   of every hand-dragged label the run moved, so it can be reverted after UNDO history is gone
   (see `reset_declutter_run`).

Debug arguments: `audit: ["*"]` lists every label that conflicts as it sits now (and crossing leaders);
`audit: [nums]` explains what each listed label touches; `explain: [nums]` shows the planned offsets.

## Adapting to other drawings
- Calibrate against the user's own hand placements, not guesses: run `audit: ["*"]` with `keepLabels` set to
  labels the user placed and confirmed clean, and adjust `labelBoxHeightIn` / `clearanceIn` / `markerSizeFt`
  until none of them is flagged. For an elevation-only label with 0.075" text this gave 0.075" and 0 clearance;
  a box of 0.083" with 0.005" clearance flagged clean placements and pushed labels far away.
- Read the label style (text height, dragged-state display) from the display group's label style override.
- The survey date is read from layer names containing "yyyy mm dd". Without one, all points rank equally.

## Civil 3D gotchas this script handles
- Setting `CogoPoint.LabelLocation` on an ALREADY-DRAGGED label is applied relative to the old drag. Call
  `ResetLabelLocation()` first, then set it, then verify (`writesOffTarget` must be 0).
- Undragged labels are rotated by their label style; dragged ones are horizontal on the sheet. Drag every label
  you place so its geometry is known.
- Leader = point -> elbow -> 0.05" horizontal landing; text starts right at the leader end.
- Ignore points whose own layer, or whose label layer, is frozen, off or frozen in the viewport.
- Points stacked within a marker width: a leader leaving one unavoidably crosses the other's marker; allow it.
- `CogoPoint.Bounds` excludes dragged labels, and `CogoPoint.Explode` returns nothing - don't rely on either for
  points. For every other annotation, in-memory Explode gives the exact drawn geometry (see Exact geometry).

## Script
```csharp
// declutter-labels: de-overlap COGO point labels in the active layout viewport.
// LABELS ONLY: never moves, explodes, or edits a point. It sets CogoPoint.LabelLocation (resetting an
// already-dragged label first) and adds point numbers to the hide group's include list.
//
// Pass 1  primary display group placed first, newest survey first (survey date read from the point's layer name
// Pass 2  secondary display groups placed around them.
// Pass 3  hand-dragged labels move only if they overlap something (never hidden here).
// Pass 4  stuck labels retry once space has been freed.
// Every label that isn't hand-dragged gets dragged: default labels are rotated by their style, dragged ones
// are horizontal on the sheet, so their geometry is known.
// Hiding (hide group): repeat shots (same type within repeatShotIn), then older shots when the same type stays
// visible within representRadiusIn, and as a last resort when no spot within lastOfTypeMaxIn avoids text and
// leaders. Leaders may graze a point marker but never cross label text or another leader.
// Geometry measured on a 1"=30' sheet: dragged label = 0.083" tall x (text width + 0.006"), vertically centered
// on the leader end, 0.025" beyond it on the side away from the point; leader = point -> elbow -> 0.05" landing.
// v2: shots whose description code is protected (default TOP, TOE, PERI) are never hidden; protected shots close
// together on the sheet (stackRadiusIn) are stacked by elevation, highest label on top; "repeat shot" compares the
// description code too (a TOP is never a repeat of a plain shot); points whose own layer is frozen/off/VP-frozen
// are invisible and ignored.
// v3 (from hand corrections): sideByGroup puts each group on its own side (e.g. one group right-up, another left-down); stacks
// follow the points' sheet order (only among labels going the same way); stuck labels first try fine 0.01" nudges,
// then (protected/stacked) any quadrant within ~0.25", cheapest first: own side < other side or direction < both,
// and toward the outside of the cluster. A label well above/below its point may sit nearly straight over it.
// Label box calibrated to 0.075" with no clearance (labels may touch, never overlap). keepLabels pins hand placements.
// v4: other annotation on the sheet (Civil labels, MLeaders, MText, text) is a fixed obstacle measured with in-memory
// Explode in the active viewport (exact text/border boxes and leader lines); annotationObstacles=false turns it off.
// Debug: audit=["*"] lists labels that conflict as they sit now; explain=[nums] shows planned offsets;
// diff=true lists labels whose position differs from the plan (hand edits since a run with the same args).
var primary = Args.GetString("primary");                // display group whose labels are placed first
var redoAiPlacement = Args.GetBool("redoAiPlacement", false);
// Point numbers a previous run added to the hide group; they are taken out first and considered again.
var unhide = new HashSet<uint>(Args.GetStringList("unhide").Select(uint.Parse));
var keepLabels = new HashSet<uint>(Args.GetStringList("keepLabels").Select(uint.Parse));
var protectedCodes = new HashSet<string>(Args.Has("protectedCodes") ? Args.GetStringList("protectedCodes") : new List<string> { "TOP", "TOE", "PERI" }, StringComparer.OrdinalIgnoreCase);
var secondaryGroups = Args.GetStringList("secondary");
var hideGroupName = Args.GetString("hideGroup");        // group whose style override hides marker + label
var maxOffsetIn = Args.GetDouble("maxOffsetIn", 0.30);
var dryRun = Args.GetBool("dryRun", true);

if (Database.TileMode) throw new InvalidOperationException("Switch to a layout and activate the viewport (double-click inside it) first.");
var vp = Get<Viewport>(Editor.CurrentViewportObjectId);
if (vp.Number < 2) throw new InvalidOperationException("Activate a model-space viewport on the layout (double-click inside it) first.");
double mpp = 1.0 / vp.CustomScale;                       // model units per paper inch
double fontH = Args.GetDouble("textHeightIn", 0.075) * mpp;          // label text height
// Measured on the sheet (user-drawn box around a dragged label): the label occupies 0.083" x (text width + 0.006"),
// vertically centered on the leader end, 0.025" beyond it on the side away from the point.
double textH = Args.GetDouble("labelBoxHeightIn", 0.075) * mpp;   // calibrated: every hand placement by the user clears at 0.075" / 0 clearance
double widthExtra = Args.GetDouble("labelExtraWidthIn", 0.006) * mpp;
double offX = 0.05 * mpp, offY = 0.05 * mpp;              // label style X/Y offsets
double markerR = Args.GetDouble("markerSizeFt", 0.85);
double maxOffset = maxOffsetIn * mpp;
double gap = Args.GetDouble("leaderGapIn", 0.005) * mpp;   // user's trace: text starts right at the landing end
double pad = Args.GetDouble("clearanceIn", 0.0) * mpp;          // minimum clear space between labels (user's stacks: ~0.001-0.012")
// Leaders run point -> elbow -> leader end; the horizontal landing into the label is ~0.05" (measured from user tracing)
double dog = Args.GetDouble("leaderLandingIn", 0.05) * mpp;
double representRadius = Args.GetDouble("representRadiusIn", 0.5) * mpp;   // a type must stay visible within this distance
double dupRadius = Args.GetDouble("repeatShotIn", 0.1) * mpp;              // same-type shots closer than this are repeats
double stackRadius = Args.GetDouble("stackRadiusIn", 0.12) * mpp;          // protected shots this close are stacked by elevation

var v = Editor.GetCurrentView();
var dcsToWcs = Matrix3d.Rotation(-v.ViewTwist, v.ViewDirection, v.Target) * Matrix3d.Displacement(v.Target - Point3d.Origin) * Matrix3d.PlaneToWorld(v.ViewDirection);
var wcsToDcs = dcsToWcs.Inverse();
var vc = v.CenterPoint; double hw = v.Width / 2, hh = v.Height / 2;

var widthCache = new Dictionary<string, double>();
double TextW(double z) {
  var s = z.ToString("0.00", CultureInfo.InvariantCulture);
  if (widthCache.TryGetValue(s, out var w)) return w;
  using (var m = new MText()) { m.SetDatabaseDefaults(Database); m.TextHeight = fontH; m.Contents = "{\\fTxt|b0|i1|c0|p2;" + s + "}"; w = m.ActualWidth + widthExtra; }
  return widthCache[s] = w;
}

// ---- collect visible labels ----
var groupList = new List<(string name, bool ovL, ObjectId ls, HashSet<uint> nums)>();
PointGroup hideGroup = null;
foreach (ObjectId gid in CivilDoc.PointGroups) {
  var g = Get<PointGroup>(gid);
  if (g.Name == hideGroupName) hideGroup = g;
  var nums = new HashSet<uint>(g.GetPointNumbers());
  if (g.Name == hideGroupName) nums.ExceptWith(unhide);   // shots a previous run hid are considered again
  groupList.Add((g.Name, g.IsPointLabelStyleOverridden, g.PointLabelStyleId, nums));
}
if (hideGroup == null) throw new ArgumentException($"Point group '{hideGroupName}' not found.");

// Labels whose layer is frozen, off, or frozen in this viewport are invisible on the sheet: ignore them entirely
var frozenInVp = new HashSet<ObjectId>(vp.GetFrozenLayers().Cast<ObjectId>());
var layerTable = Get<LayerTable>(Database.LayerTableId);
var invisibleStyleCache = new Dictionary<ObjectId, bool>();
bool LabelInvisible(ObjectId labelStyleId) {
  if (invisibleStyleCache.TryGetValue(labelStyleId, out var hiddenStyle)) return hiddenStyle;
  var layer = Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(labelStyleId).Properties.Label.Layer.Value;
  bool invisible = false;
  if (!string.IsNullOrEmpty(layer) && layerTable.Has(layer)) {
    var lid = layerTable[layer]; var lr = Get<LayerTableRecord>(lid);
    invisible = lr.IsFrozen || lr.IsOff || frozenInVp.Contains(lid);
  }
  return invisibleStyleCache[labelStyleId] = invisible;
}
int skippedInvisible = 0, skippedPointLayer = 0;
// A point on a layer that is frozen, off, or frozen in this viewport is invisible on the sheet (marker and label)
var pointLayerCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
bool PointLayerInvisible(string layer) {
  if (pointLayerCache.TryGetValue(layer, out var hid)) return hid;
  bool invisible = false;
  if (layerTable.Has(layer)) { var lid = layerTable[layer]; var lr = Get<LayerTableRecord>(lid); invisible = lr.IsFrozen || lr.IsOff || frozenInVp.Contains(lid); }
  return pointLayerCache[layer] = invisible;
}

var L = new List<Lbl>();
foreach (ObjectId id in CivilDoc.CogoPoints) {
  var p = Get<CogoPoint>(id);
  var d3 = p.Location.TransformBy(wcsToDcs);
  var d = new Point2d(d3.X, d3.Y);
  if (Math.Abs(d.X - vc.X) > hw || Math.Abs(d.Y - vc.Y) > hh) continue;
  if (PointLayerInvisible(p.Layer)) { skippedPointLayer++; continue; }
  string grp = null; ObjectId ls = p.LabelStyleId;
  foreach (var gr in groupList) if (gr.ovL && gr.nums.Contains(p.PointNumber)) { grp = gr.name; ls = gr.ls; break; }
  if (ls.IsNull) continue;
  if (LabelInvisible(ls)) { skippedInvisible++; continue; }
  var m = System.Text.RegularExpressions.Regex.Match(p.Layer, @"(\d{4}) (\d{2}) (\d{2})");
  var lbl = new Lbl {
    Id = id, Num = p.PointNumber, Type = grp ?? "(own)", P = d, W = TextW(p.Elevation), Z = p.Location.Z,
    Date = m.Success ? new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : DateTime.MinValue,
    // Dragged labels are the user's (kept) unless redoAiPlacement is set: then only labels in keepLabels stay put.
    Fixed = p.IsLabelDragged && (!redoAiPlacement || keepLabels.Contains(p.PointNumber)), Active = true,
  };
  // Description code: first token when it is a feature code (e.g. TOP/XYZ -> TOP), otherwise empty.
  var firstToken = (p.RawDescription ?? "").Split('/')[0].Trim().ToUpperInvariant();
  lbl.Code = new[] { "TOP", "TOE", "PERI", "GRID" }.Contains(firstToken) ? firstToken : "";
  lbl.Protected = protectedCodes.Contains(lbl.Code);
  if (p.IsLabelDragged) { var a3 = p.LabelLocation.TransformBy(wcsToDcs); lbl.SetDragged(new Point2d(a3.X, a3.Y), textH, gap, dog); }
  else lbl.SetDefault(offX, offY, textH);
  L.Add(lbl);
}

// ---- fixed annotation obstacles (v4): other things drawn on the sheet that labels must not touch ----
// Exact geometry from in-memory Explode (returns copies; the drawing is not changed). Exploded geometry follows the
// ACTIVE viewport's twist and annotation scale, which is why this script works in the active viewport.
//   Civil labels (station/offset etc.): box around their text + border, plus their leader lines
//   MLeaders: text box + leader polyline + landing line;  MText / text: extents
var obBoxes = new List<Bx>(); var obSegs = new List<Sg>(); int obstaclesSkipped = 0; bool probed = false;
if (Args.GetBool("annotationObstacles", true)) {
  Point2d D2(Point3d p) { var q = p.TransformBy(wcsToDcs); return new Point2d(q.X, q.Y); }
  bool NearSheet(Point2d q) => Math.Abs(q.X - vc.X) <= hw + maxOffset * 3 && Math.Abs(q.Y - vc.Y) <= hh + maxOffset * 3;
  void Pieces(Autodesk.AutoCAD.DatabaseServices.Entity e, List<Point2d> boxPts, List<Sg> segs, int depth) {
    var parts = new DBObjectCollection(); try { e.Explode(parts); } catch { return; }
    foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) {
      if (o is BlockReference br && depth < 3) Pieces(br, boxPts, segs, depth + 1);
      else if (o is Leader ld) { for (int i = 0; i + 1 < ld.NumVertices; i++) segs.Add(new Sg(D2(ld.VertexAt(i)), D2(ld.VertexAt(i + 1)))); }
      else if (o is Autodesk.AutoCAD.DatabaseServices.Entity pe) { try { var ex = pe.GeometricExtents; boxPts.Add(D2(ex.MinPoint)); boxPts.Add(D2(ex.MaxPoint)); boxPts.Add(D2(new Point3d(ex.MinPoint.X, ex.MaxPoint.Y, 0))); boxPts.Add(D2(new Point3d(ex.MaxPoint.X, ex.MinPoint.Y, 0))); } catch { } }
      o.Dispose(); } }
  foreach (ObjectId id in ModelSpace) {
    var dxf = id.ObjectClass.DxfName; if (dxf == "AECC_COGO_POINT") continue;
    if (!probed && Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is Autodesk.Civil.DatabaseServices.Label pl0) { probed = true; var pp = new DBObjectCollection(); try { pl0.Explode(pp); } catch { } bool empty = pp.Count == 0; foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in pp) o.Dispose();
      if (empty) throw new InvalidOperationException("Civil 3D labels explode to nothing in a read-only run - run with civil3d_execute (dryRun:true changes nothing), or pass annotationObstacles:false."); }
    var e = Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id);
    bool civilLabel = e is Autodesk.Civil.DatabaseServices.Label; if (!civilLabel && !(e is MLeader) && !(e is MText) && !(e is DBText)) continue;
    if (PointLayerInvisible(e.Layer)) continue;
    try {
      var boxPts = new List<Point2d>(); var segs = new List<Sg>();
      if (e is MLeader ml) { var parts = new DBObjectCollection(); ml.Explode(parts);
        foreach (Autodesk.AutoCAD.DatabaseServices.DBObject o in parts) { if (o is MText mt) { var ex = mt.GeometricExtents; boxPts.Add(D2(ex.MinPoint)); boxPts.Add(D2(ex.MaxPoint)); }
          else if (o is Polyline pl) { for (int i = 0; i + 1 < pl.NumberOfVertices; i++) segs.Add(new Sg(D2(pl.GetPoint3dAt(i)), D2(pl.GetPoint3dAt(i + 1)))); }
          else if (o is Line ln) segs.Add(new Sg(D2(ln.StartPoint), D2(ln.EndPoint))); o.Dispose(); } }
      else if (civilLabel) Pieces(e, boxPts, segs, 0);
      else { var ex = e.GeometricExtents; boxPts.Add(D2(ex.MinPoint)); boxPts.Add(D2(ex.MaxPoint)); }
      if (boxPts.Count > 0) { var b = new Bx(boxPts.Min(p => p.X), boxPts.Min(p => p.Y), boxPts.Max(p => p.X), boxPts.Max(p => p.Y)); if (NearSheet(new Point2d((b.X0 + b.X1) / 2, (b.Y0 + b.Y1) / 2))) obBoxes.Add(b); }
      foreach (var s in segs) if (NearSheet(s.A) || NearSheet(s.B)) obSegs.Add(s);
    } catch { obstaclesSkipped++; } } }
var Obstacle = new Lbl { Num = 0, Type = "(annotation)" };
bool HitsObstacle(Bx box, Sg[] leader) {
  foreach (var ob in obBoxes) { if (ob.X0 > box.X1 + reach || ob.X1 < box.X0 - reach) continue; if (BoxBox(box, ob)) return true; if (leader != null && leader.Any(s => SegBox(s.A, s.B, ob))) return true; }
  foreach (var s in obSegs) if (SegBox(s.A, s.B, box)) return true;
  return false; }
// Two labels can only interact if their points are within this distance (both may be offset and long)
double reach = 2 * (maxOffset + gap + L.Select(l => l.W).DefaultIfEmpty(0).Max() + markerR + pad) + 1;
// Labels that touch anything (other than their own marker) before we change anything

// ---- elevation stacks: protected shots close together on the sheet (transitively) form one stack ----
var stackOf = new Dictionary<Lbl, List<Lbl>>();
// Stacks: shots of the same display group close together on the sheet (any code), so their labels line up by elevation
foreach (var a in L)
{
  foreach (var b in L.Where(l => l != a && l.Type == a.Type && Math.Abs(l.P.X - a.P.X) <= stackRadius && Math.Abs(l.P.Y - a.P.Y) <= stackRadius && l.P.GetDistanceTo(a.P) <= stackRadius))
  {
    var sa = stackOf.TryGetValue(a, out var x) ? x : null; var sb = stackOf.TryGetValue(b, out var y) ? y : null;
    if (sa == null && sb == null) { var s = new List<Lbl> { a, b }; stackOf[a] = s; stackOf[b] = s; }
    else if (sa != null && sb == null) { sa.Add(b); stackOf[b] = sa; }
    else if (sa == null && sb != null) { sb.Add(a); stackOf[a] = sb; }
    else if (sa != sb) { foreach (var m in sb) { sa.Add(m); stackOf[m] = sa; } }
  }
}
var placed = new HashSet<Lbl>(L.Where(l => l.Fixed));
// Stack order follows the points' own top-to-bottom order on the sheet (so leaders never cross); elevation only
// breaks ties when two points are level on the sheet. (User's correction: an upper point's label stays above even
// when its elevation is lower.)
double levelTol = 0.0005 * mpp;   // strictly the points' order: even a 0.001" difference decides (user's correction)
int Above(Lbl a, Lbl b) => a.P.Y > b.P.Y + levelTol ? 1 : a.P.Y < b.P.Y - levelTol ? -1 : Math.Sign(a.Z - b.Z);
// The order applies to labels going the same vertical way (both above or both below their points); when one goes up
// and the other down they split apart, and crossing leaders are caught by the conflict check (user's correction).
bool OrderOk(Lbl me, Bx box) {
  if (!stackOf.TryGetValue(me, out var mates)) return true;
  bool meUp = (box.Y0 + box.Y1) / 2 >= me.P.Y;
  foreach (var o in mates) {
    if (o == me || !o.Active || !placed.Contains(o)) continue;
    if (((o.Box.Y0 + o.Box.Y1) / 2 >= o.P.Y) != meUp) continue;
    var rel = Above(o, me);
    if (rel > 0 && box.Y1 > o.Box.Y0) return false;
    if (rel < 0 && box.Y0 < o.Box.Y1) return false;
  }
  return true;
}
// Emit labels in the given order, but each stack together, highest elevation first.
IEnumerable<Lbl> StackOrder(IEnumerable<Lbl> ordered, HashSet<Lbl> inPass) {
  var emitted = new HashSet<Lbl>();
  foreach (var l in ordered) {
    if (emitted.Contains(l)) continue;
    if (stackOf.TryGetValue(l, out var mates))
      // Fill rows outward from the point: an upward stack starts with its lowest shot, a downward one with its highest,
      // so the highest elevation always ends up on top.
      // An upward stack starts with its lowest point, a downward stack with its highest (ties: elevation)
      foreach (var m in mates.Where(m => inPass.Contains(m) && !emitted.Contains(m)).OrderBy(m => SideOf(m).sy > 0 ? m.P.Y : -m.P.Y).ThenBy(m => SideOf(m).sy > 0 ? m.Z : -m.Z)) { emitted.Add(m); yield return m; }
    else { emitted.Add(l); yield return l; }
  }
}
string Kind(Lbl l) => l.Type + "/" + l.Code;

// ---- geometry ----
bool BoxBox(Bx a, Bx b) => a.X0 < b.X1 + pad && a.X1 > b.X0 - pad && a.Y0 < b.Y1 + pad && a.Y1 > b.Y0 - pad;
bool SegSeg(Point2d a, Point2d b, Point2d c, Point2d d) {
  double Cross(Point2d o, Point2d p, Point2d q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
  double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
  return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
}
bool SegBox(Point2d a, Point2d b, Bx r) {
  var e = new Bx(r.X0 - pad, r.Y0 - pad, r.X1 + pad, r.Y1 + pad);
  bool Inside(Point2d p) => p.X > e.X0 && p.X < e.X1 && p.Y > e.Y0 && p.Y < e.Y1;
  if (Inside(a) || Inside(b)) return true;
  var c1 = new Point2d(e.X0, e.Y0); var c2 = new Point2d(e.X1, e.Y0); var c3 = new Point2d(e.X1, e.Y1); var c4 = new Point2d(e.X0, e.Y1);
  return SegSeg(a, b, c1, c2) || SegSeg(a, b, c2, c3) || SegSeg(a, b, c3, c4) || SegSeg(a, b, c4, c1);
}
Bx MarkerBox(Lbl l) => new Bx(l.P.X - markerR, l.P.Y - markerR, l.P.X + markerR, l.P.Y + markerR);

// Conflicts of label `me` with candidate geometry (box, optional leader) against every other present label.
// `present` decides which other labels currently occupy their label geometry; markers of active points always count.
List<Lbl> Conflicts(Lbl me, Bx box, Sg[] leader, Func<Lbl, bool> present) {
  var hits = new List<Lbl>();
  if (BoxBox(box, MarkerBox(me))) hits.Add(me);
  foreach (var o in L) {
    if (o == me || !o.Active) continue;
    if (Math.Abs(o.P.X - me.P.X) > reach || Math.Abs(o.P.Y - me.P.Y) > reach) continue;   // too far to interact
    // A leader leaving a point stacked under another point's marker unavoidably crosses that marker; ignore it
    bool stacked = o.P.GetDistanceTo(me.P) < 2 * markerR;
    bool hit = BoxBox(box, MarkerBox(o)) || (!stacked && leader != null && leader.Any(s => SegBox(s.A, s.B, MarkerBox(o))));
    if (!hit && present(o)) {
      hit = BoxBox(box, o.Box)
         || (o.Leader != null && o.Leader.Any(s => SegBox(s.A, s.B, box)))
         || (leader != null && (leader.Any(s => SegBox(s.A, s.B, o.Box))
             || (o.Leader != null && leader.Any(s => o.Leader.Any(t => SegSeg(s.A, s.B, t.A, t.B))))));
    }
    if (hit) hits.Add(o);
  }
  if (HitsObstacle(box, leader)) hits.Add(Obstacle);   // v4: text, leaders, other labels already on the sheet
  return hits;
}

// ---- audit mode ----
if (Args.Has("audit")) {
  var auditList = Args.GetStringList("audit");
  if (auditList.Contains("*")) {
    // Every shown label as it sits now: which ones touch text, a leader, or a marker
    var bad = L.Where(l => l.Active && Conflicts(l, l.Box, l.Leader, o => o.Active).Any(h => h != l)).Select(l => l.Num).ToList();
    var cross = L.Where(a => a.Active && a.Leader != null).SelectMany(a => L.Where(b => b.Active && b.Leader != null && b.Num > a.Num && a.Leader.Any(s => b.Leader.Any(t => SegSeg(s.A, s.B, t.A, t.B)))).Select(b => $"{a.Num}x{b.Num}")).ToList();
    return new { problems = bad, crossing = cross };
  }
  var want = new HashSet<uint>(auditList.Select(uint.Parse));
  return L.Where(l => want.Contains(l.Num)).Select(l => new {
    l.Num, l.Type, l.Fixed, l.Active,
    box = $"x {(l.Box.X0 - l.P.X) / mpp:0.000}..{(l.Box.X1 - l.P.X) / mpp:0.000}, y {(l.Box.Y0 - l.P.Y) / mpp:0.000}..{(l.Box.Y1 - l.P.Y) / mpp:0.000}",
    hits = Conflicts(l, l.Box, l.Leader, o => o.Active).Where(h => h != l).Select(h => $"{h.Num} {h.Type}"),
    others = L.Where(o => o != l && o.Active && Math.Abs(o.P.X - l.P.X) < 0.5 * mpp && Math.Abs(o.P.Y - l.P.Y) < 0.5 * mpp && BoxBox(l.Box, o.Box)).Select(o => o.Num),
  }).ToList();
}

// Candidate leader ends on a neat grid, on the label's side only: rows one label height + clearance apart,
// columns every 0.05". The leader bend must sit at least minElbowIn outward of the point, so the leader leaves the
// point heading away from it and meets the landing at an obtuse angle (never doubling back).
// Placement slots measured from the user's example (1"=30' sheet): the label starts (leader end) anchorOffsetIn
// sideways from the point, the bend sits a landing-length (0.05") before it, so the leader leaves the point heading
// outward and meets the landing at an obtuse angle. Rows: the first just above (going up) or just below (going down)
// the point, then one row further each time; columns step out by colStep. A slot is (column, row) and is turned into
// an offset per label by its group's side: e.g. {"FINISH Display":"right-up","EXISTING Display":"left-down"}.
double anchorDx = Args.GetDouble("anchorOffsetIn", 0.10) * mpp;
double firstUp = Args.GetDouble("firstRowUpIn", 0.02) * mpp, firstDown = Args.GetDouble("firstRowDownIn", 0.035) * mpp;
double rowStep = Args.GetDouble("rowStepIn", 0.09) * mpp;
double colStep = 0.05 * mpp;
List<Vector2d> Slots(double minR, double maxR) {
  var list = new List<Vector2d>();   // X = column index, Y = row index
  for (int col = 0; anchorDx + col * colStep <= maxR + 1e-9; col++)
    for (int row = 0; firstUp + row * rowStep <= maxR + 1e-9; row++) {
      double r = Math.Sqrt(Math.Pow(anchorDx + col * colStep, 2) + Math.Pow(firstUp + row * rowStep, 2));
      if (r > minR + 1e-9 && r <= maxR + 1e-9) list.Add(new Vector2d(col, row));
    }
  return list.OrderBy(s => s.X * 4 + s.Y).ToList();   // fill a few rows in a column before stepping out
}
var cands = Slots(0, maxOffset);
var extCands = Slots(maxOffset, Args.GetDouble("lastOfTypeMaxIn", 0.5) * mpp);
var protCands = Slots(Args.GetDouble("lastOfTypeMaxIn", 0.5) * mpp, Args.GetDouble("protectedMaxIn", 0.75) * mpp);
var sideByGroup = Args.Has("sideByGroup") ? Args.Get<Dictionary<string, string>>("sideByGroup") : new Dictionary<string, string>();
// (sx, sy): horizontal side (+1 right, -1 left, 0 either) and vertical direction (+1 up, -1 down, 0 either)
(int sx, int sy) SideOf(Lbl l) {
  if (!sideByGroup.TryGetValue(l.Type, out var s)) return (0, 0);
  s = s.ToLowerInvariant();
  return (s.Contains("left") ? -1 : s.Contains("right") ? 1 : 0, s.Contains("down") ? -1 : s.Contains("up") ? 1 : 0);
}
Vector2d Offset(int sx, int sy, Vector2d slot) => new Vector2d(sx * (anchorDx + slot.X * colStep), sy > 0 ? firstUp + slot.Y * rowStep : -(firstDown + slot.Y * rowStep));
IEnumerable<Vector2d> ForSide(Lbl l, IEnumerable<Vector2d> slotsIn, bool allowOtherDirection = false) {
  var (sx, sy) = SideOf(l);
  // Keep labels close to their point vertically: stacked labels fill rows in the same column (one on top of the
  // other); single labels stay on the first row and step sideways before going up or down.
  var slots = stackOf.ContainsKey(l) ? slotsIn.OrderBy(s => s.X).ThenBy(s => s.Y) : slotsIn.OrderBy(s => s.Y).ThenBy(s => s.X);
  var xs = sx == 0 ? new[] { 1, -1 } : new[] { sx };
  var ys = sy == 0 || allowOtherDirection ? (sy < 0 ? new[] { -1, 1 } : new[] { 1, -1 }) : new[] { sy };
  foreach (var slot in slots) foreach (var y in ys) foreach (var x in xs) yield return Offset(x, y, slot);
}

// Fine nudges around the preferred first spot (user's corrections in tight clusters: a label nudged 0.01-0.03" off
// the neat grid, or flipped to the other vertical direction / other side, beats one sent far away). Offsets are
// ordered by distance from the preferred spot for that direction; flips cost a little extra. The leader end stays
// at least minAnchorIn out so the leader still leaves the point heading outward (obtuse bend).
double nudgeStep = 0.01 * mpp, minAnchor = Args.GetDouble("minAnchorIn", 0.07) * mpp;
double shortAnchor = Args.GetDouble("shortAnchorIn", 0.03) * mpp, tallRise = Args.GetDouble("tallRiseIn", 0.06) * mpp;
// In a crowded spot (user's corrections): any of the four quadrants is fine if it keeps the label close; a label sent
// 0.3-0.5" away is worse than one on the "wrong" side. Cost = distance from that quadrant's preferred spot + a
// penalty for the other side / other direction (both together cost more), and a little more when a mate's point
// lies that way (so labels go to the outside of their cluster rather than across it).
double nudgeMax = Args.GetDouble("nudgeMaxIn", 0.07) * mpp, flipMax = Args.GetDouble("flipMaxIn", 0.25) * mpp;
double sidePen = Args.GetDouble("sideFlipCostIn", 0.06) * mpp, dirPen = Args.GetDouble("dirFlipCostIn", 0.06) * mpp, bothPen = Args.GetDouble("bothFlipCostIn", 0.14) * mpp;
double insidePen = Args.GetDouble("insideCostIn", 0.04) * mpp;
List<Vector2d> Nudges(Lbl l, bool flips) {
  var (sx, sy) = SideOf(l);
  var list = new List<(Vector2d o, double cost)>();
  var mates = stackOf.TryGetValue(l, out var st) ? st.Where(m => m != l && m.Active).ToList() : new List<Lbl>();
  double lvl = Args.GetDouble("flipLevelIn", 0.005) * mpp;
  bool Outermost(int dx, int dy) => !mates.Any(m => dx != 0 ? (m.P.X - l.P.X) * dx > lvl : (m.P.Y - l.P.Y) * dy > lvl);
  foreach (var x in sx == 0 || flips ? new[] { sx == 0 ? 1 : sx, sx == 0 ? -1 : -sx } : new[] { sx })
    foreach (var y in sy == 0 || flips ? new[] { sy == 0 ? 1 : sy, sy == 0 ? -1 : -sy } : new[] { sy }) {
      double prefY = y > 0 ? firstUp : -firstDown;
      bool fx = sx != 0 && x != sx, fy = sy != 0 && y != sy;
      double penalty = fx && fy ? bothPen : fx ? sidePen : fy ? dirPen : 0;
      if (fx && !Outermost(x, 0)) penalty += insidePen;
      if (fy && !Outermost(0, y)) penalty += insidePen;
      double maxD = flips ? flipMax : nudgeMax;
      for (double ax = shortAnchor; ax <= anchorDx + maxD + 1e-9; ax += nudgeStep)
        for (double ay = 0; ay <= Math.Abs(prefY) + maxD + 1e-9; ay += nudgeStep) {
          var o = new Vector2d(x * ax, y * ay);
          // a label well above/below its point may sit almost straight over it (user: leader end 0.03" out, 0.06" down)
          if (ax < minAnchor - 1e-9 && ay < tallRise - 1e-9) continue;
          double dist = Math.Sqrt(Math.Pow(ax - anchorDx, 2) + Math.Pow(y * ay - prefY, 2));
          if (dist > maxD + 1e-9) continue;
          list.Add((o, dist + penalty));
        }
    }
  return list.OrderBy(t => t.cost).Select(t => t.o).ToList();
}
bool TryOffsets(Lbl me, IEnumerable<Vector2d> offsets, Func<Lbl, bool> present) {
  foreach (var c in offsets) {
    var a = me.P + c;
    var (box, leader) = Lbl.DraggedGeom(me.P, a, me.W, textH, gap, dog);
    if (OrderOk(me, box) && Conflicts(me, box, leader, present).Count == 0) { me.SetDragged(a, textH, gap, dog); me.Moved = true; return true; }
  }
  return false;
}

var overlappingBefore =L.Count(l => Conflicts(l, l.Box, l.Leader, o => true).Any(h => h != l));
var hidden = new List<Lbl>(); var unresolved = new List<Lbl>(); var forcedOver = new HashSet<Lbl>();
var hiddenAsLastResort = new List<uint>();
int movedP1 = 0, movedP2 = 0, keptP1 = 0, keptP2 = 0, hiddenAsRepeat = 0, movedExtended = 0;

int movedOtherDirection = 0, nudged = 0, flipped = 0;
bool TryFreeAny(Lbl me, List<Vector2d> slots, Func<Lbl, bool> present) {
  foreach (var c in ForSide(me, slots, allowOtherDirection: true)) {
    var a = me.P + c;
    var (box, leader) = Lbl.DraggedGeom(me.P, a, me.W, textH, gap, dog);
    if (OrderOk(me, box) && Conflicts(me, box, leader, present).Count == 0) { me.SetDragged(a, textH, gap, dog); me.Moved = true; return true; }
  }
  return false;
}
bool TryFree(Lbl me, List<Vector2d> candidates, Func<Lbl, bool> present) {
  foreach (var c in ForSide(me, candidates)) {
    var a = me.P + c;
    var (box, leader) = Lbl.DraggedGeom(me.P, a, me.W, textH, gap, dog);
    if (OrderOk(me, box) && Conflicts(me, box, leader, present).Count == 0) { me.SetDragged(a, textH, gap, dog); me.Moved = true; return true; }
  }
  return false;
}

// Every label that isn't hand-dragged gets dragged, so its geometry is known (undragged labels are
// rotated by their style). The first spot tried is right next to the point, about where the default sits.
var nearDefault = new Vector2d(0, 0);   // slot: first column, first row on the label's side

// Try to settle one label (label position only); returns "kept" / "moved" / "hidden" / "unresolved".
string Settle(Lbl me, Func<Lbl, bool> present, bool allowHide = true) {
  var r = SettleCore(me, present, allowHide && !me.Protected);
  if (r != "hidden") placed.Add(me);
  return r;
}
string SettleCore(Lbl me, Func<Lbl, bool> present, bool allowHide) {
  if (TryFree(me, new List<Vector2d> { nearDefault }, present)) return "kept";
  // Repeat shot: a newer label of the same group and description code is shown right here, so hide this older one
  if (allowHide && L.Any(o => o != me && Kind(o) == Kind(me) && o.Active && present(o) && o.P.GetDistanceTo(me.P) <= dupRadius)) { me.Active = false; hidden.Add(me); hiddenAsRepeat++; return "hidden"; }
  // A small nudge off the grid first; in a cluster (stacked or protected) a flip to the other direction/side next
  if (TryOffsets(me, Nudges(me, flips: false), present)) { nudged++; return "moved"; }
  // Stacked: the next rows of the stack on its own side come before a flip
  if (stackOf.ContainsKey(me) && TryFree(me, cands.Where(s => s.X == 0 && s.Y <= 2).ToList(), present)) return "moved";
  if ((me.Protected || stackOf.ContainsKey(me)) && TryOffsets(me, Nudges(me, flips: true), present)) { flipped++; return "moved"; }
  if (TryFree(me, cands, present)) return "moved";
  // Hide only if its kind stays represented here: a label of the same group and code within representRadius remains shown
  if (allowHide && L.Any(o => o != me && Kind(o) == Kind(me) && o.Active && present(o) && o.P.GetDistanceTo(me.P) <= representRadius)) { me.Active = false; hidden.Add(me); return "hidden"; }
  // Last label of its kind here: allow a longer move (up to lastOfTypeMaxIn) before giving up
  if (TryFree(me, extCands, present)) { movedExtended++; return "moved"; }
  // Protected shots must stay visible: reach further (up to protectedMaxIn) before settling for a compromise
  if (me.Protected && TryFree(me, protCands, present)) { movedExtended++; return "moved"; }
  // Still nothing: allow the other vertical direction (same side) before settling for a compromise
  if (me.Protected && TryFreeAny(me, cands.Concat(extCands).Concat(protCands).ToList(), present)) { movedOtherDirection++; return "moved"; }
  // Last resort: a spot whose only conflicts are point markers (no text on text, no leader through text,
  // no crossing leaders). Fewest marker touches wins; shorter spots win ties.
  bool Severe(Bx box, Sg[] leader) => HitsObstacle(box, leader) || L.Any(o => o != me && o.Active && present(o)
      && Math.Abs(o.P.X - me.P.X) < reach && Math.Abs(o.P.Y - me.P.Y) < reach
      && (BoxBox(box, o.Box)
          || (o.Leader != null && o.Leader.Any(s => SegBox(s.A, s.B, box)))
          || (leader != null && (leader.Any(s => SegBox(s.A, s.B, o.Box)) || (o.Leader != null && leader.Any(s => o.Leader.Any(q => SegSeg(s.A, s.B, q.A, q.B))))))));
  int bestCount = int.MaxValue; Vector2d? best = null;
  foreach (var c in ForSide(me, new[] { nearDefault }.Concat(cands).Concat(extCands).Concat(me.Protected ? protCands : new List<Vector2d>()), allowOtherDirection: true)) {
    var g = Lbl.DraggedGeom(me.P, me.P + c, me.W, textH, gap, dog);
    if (!OrderOk(me, g.box) || Severe(g.box, g.leader)) continue;
    var hits = Conflicts(me, g.box, g.leader, present);
    if (hits.Contains(me)) continue;
    if (hits.Count < bestCount) { bestCount = hits.Count; best = c; }
  }
  if (best == null && me.Protected) {
    // Protected shots are never hidden: take the spot with the fewest conflicts that keeps the elevation order
    foreach (var c in ForSide(me, new[] { nearDefault }.Concat(cands).Concat(extCands).Concat(protCands), allowOtherDirection: true)) {
      var g = Lbl.DraggedGeom(me.P, me.P + c, me.W, textH, gap, dog);
      if (!OrderOk(me, g.box)) continue;
      var n = Conflicts(me, g.box, g.leader, present).Count;
      if (n < bestCount) { bestCount = n; best = c; }
    }
    if (best == null) best = ForSide(me, new[] { nearDefault }).First();
  }
  if (best is not Vector2d bc) { me.Active = false; hidden.Add(me); hiddenAsLastResort.Add(me.Num); return "hidden"; }   // better hidden than overlapping
  me.SetDragged(me.P + bc, textH, gap, dog); me.Moved = true;
  unresolved.Add(me); return "unresolved";
}

// ---- Pass 1: primary type. Undragged secondary labels will all be re-placed, so they don't block. ----
var p1 = L.Where(l => l.Type == primary && !l.Fixed).ToList();
var p1Done = new HashSet<Lbl>();
foreach (var me in StackOrder(p1.OrderByDescending(l => l.Protected).ThenByDescending(l => l.Date).ThenByDescending(l => L.Count(o => o.P.GetDistanceTo(l.P) < 2 * textH)), new HashSet<Lbl>(p1))) {
  var r = Settle(me, present: o => o.Fixed || p1Done.Contains(o));
  if (r == "moved") movedP1++; else if (r == "kept") keptP1++;
  p1Done.Add(me);
}

// ---- Pass 2: secondary types (every one not hand-dragged), placed around the primary labels ----
var p2 = L.Where(l => secondaryGroups.Contains(l.Type) && !l.Fixed && l.Active).ToList();
var p2Done = new HashSet<Lbl>();
foreach (var me in StackOrder(p2.OrderByDescending(l => l.Protected).ThenByDescending(l => l.Date).ThenByDescending(l => L.Count(o => o.P.GetDistanceTo(l.P) < 2 * textH)), new HashSet<Lbl>(p2))) {
  var r = Settle(me, present: o => o.Fixed || (o.Type == primary && o.Active) || p2Done.Contains(o));
  if (r == "moved") movedP2++; else if (r == "kept") keptP2++;
  p2Done.Add(me);
}
// ---- Pass 3: hand-dragged labels move only if they overlap something (never hidden) ----
var originalAnchors = new Dictionary<Lbl, Point3d>();
var p3 = L.Where(l => l.Fixed && l.Active && !keepLabels.Contains(l.Num) && Conflicts(l, l.Box, l.Leader, o => o.Active).Any(h => h != l)).ToList();
int movedP3 = 0;
foreach (var me in p3.OrderByDescending(l => l.Date)) {
  originalAnchors[me] = Get<CogoPoint>(me.Id).LabelLocation;
  me.Fixed = false;
  unresolved.Remove(me);
  Settle(me, present: o => o.Active, allowHide: false);
  movedP3++;
}

// ---- Pass 4: retry labels that were stuck, now that hand-dragged labels may have made room ----
int fixedOnRetry = 0;
foreach (var me in unresolved.Where(u => !p3.Contains(u)).ToList()) {
  unresolved.Remove(me);
  var r = Settle(me, present: o => o.Active);
  if (r != "unresolved") fixedOnRetry++;
}

// Final audit: any remaining conflicts among everything shown
var stillOverlapping = L.Where(l => l.Active && Conflicts(l, l.Box, l.Leader, o => true).Any(h => h != l)).Select(l => l.Num).ToList();
var stacks = stackOf.Values.Distinct().ToList();
var stackOrderViolations = stacks.SelectMany(s => s.Where(a => a.Active).SelectMany(a => s.Where(b => b.Active && Above(a, b) > 0 && a.Box.Y0 < b.Box.Y1 && ((a.Box.Y0 + a.Box.Y1) / 2 >= a.P.Y) == ((b.Box.Y0 + b.Box.Y1) / 2 >= b.P.Y)).Select(b => $"{a.Num} label not above {b.Num}"))).ToList();
var crossingLeaders = L.Where(a => a.Active && a.Leader != null).SelectMany(a => L.Where(b => b.Active && b.Leader != null && b.Num > a.Num && a.Leader.Any(s => b.Leader.Any(t => SegSeg(s.A, s.B, t.A, t.B)))).Select(b => $"{a.Num}x{b.Num}")).ToList();

int writesOffTarget = 0;
if (!dryRun) {
  foreach (var l in L.Where(l => l.Active && l.Moved)) {
    var w = new Point3d(l.Anchor.X, l.Anchor.Y, 0).TransformBy(dcsToWcs);
    var cp = Get<CogoPoint>(l.Id, OpenMode.ForWrite);
    var wasDragged = cp.IsLabelDragged;
    // Civil 3D applies a new LabelLocation relative to an existing drag, so reset the label first
    if (wasDragged) cp.ResetLabelLocation();
    cp.LabelLocation = new Point3d(w.X, w.Y, l.Z);   // label only; point untouched
    if (new Point2d(cp.LabelLocation.X, cp.LabelLocation.Y).GetDistanceTo(new Point2d(w.X, w.Y)) > 0.01) writesOffTarget++;
  }
  if (hidden.Count > 0 || unhide.Count > 0) {
    var hg = Get<PointGroup>(hideGroup.ObjectId, OpenMode.ForWrite);
    var q = hg.GetQuery() as StandardPointGroupQuery ?? new StandardPointGroupQuery();
    var keepNums = (q.IncludeNumbers ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
      .Where(s => !(uint.TryParse(s, out var n) && unhide.Contains(n)));   // only plain numbers listed in unhide come out
    q.IncludeNumbers = string.Join(",", keepNums.Concat(hidden.Select(h => h.Num.ToString())).Distinct());
    hg.SetQuery(q);
    hg.Update();
  }
}

string Short(string t) => t.Replace(" Display", "");
if (Args.Has("diff")) {
  // Labels whose position on the sheet now differs from this plan (= hand edits since the last run with these args)
  return L.Where(l => l.Moved || l.Active).Select(l => {
    var cp = Get<CogoPoint>(l.Id); if (!cp.IsLabelDragged) return null;
    var a3 = cp.LabelLocation.TransformBy(wcsToDcs);
    double d = new Point2d(a3.X, a3.Y).GetDistanceTo(l.Anchor) / mpp;
    if (d < 0.005 || !l.Moved) return null;
    return new { l.Num, l.Type, l.Code, z = Math.Round(l.Z, 2), planX = Math.Round((l.Anchor.X - l.P.X) / mpp, 3), planY = Math.Round((l.Anchor.Y - l.P.Y) / mpp, 3),
      nowX = Math.Round((a3.X - l.P.X) / mpp, 3), nowY = Math.Round((a3.Y - l.P.Y) / mpp, 3), wasUnresolved = unresolved.Contains(l),
      stack = stackOf.TryGetValue(l, out var st) ? string.Join(",", st.Select(s => s.Num)) : "" };
  }).Where(x => x != null).ToList();
}
if (Args.Has("explain")) {
  var ex = new HashSet<uint>(Args.GetStringList("explain").Select(uint.Parse));
  return L.Where(l => ex.Contains(l.Num)).Select(l => new { l.Num, l.Active, unresolved = unresolved.Contains(l), dx = Math.Round((l.Anchor.X - l.P.X) / mpp, 3), dy = Math.Round((l.Anchor.Y - l.P.Y) / mpp, 3), stack = stackOf.TryGetValue(l, out var st) ? string.Join(",", st.Select(s => s.Num)) : "" }).ToList();
}
return new {
  dryRun, scale = $"1\" = {mpp:0.##}'", labelsInView = L.Count, annotationObstacles = obBoxes.Count, obstacleLeaderSegments = obSegs.Count, obstaclesSkipped, labelsOnObstacles = L.Count(l => l.Active && HitsObstacle(l.Box, l.Leader)), skippedInvisible, skippedPointLayer, overlappingBefore,
  protectedShown = L.Count(l => l.Protected && l.Active), protectedHidden = L.Count(l => l.Protected && !l.Active),
  stacks = stacks.Count, stackedLabels = stacks.Sum(s => s.Count), stackOrderViolations, crossingLeaders,
  protectedUnresolved = unresolved.Where(u => u.Protected).Select(u => u.Num),
  overlappingAmongHandDragged = L.Count(l => l.Fixed && Conflicts(l, l.Box, l.Leader, o => true).Any(h => h != l && h.Fixed)),
  pass1 = new { type = primary, considered = p1.Count, kept = keptP1, moved = movedP1, hidden = hidden.Count(h => h.Type == primary), unresolved = unresolved.Count(u => u.Type == primary) },
  pass2 = new { considered = p2.Count, kept = keptP2, moved = movedP2, hidden = hidden.Count(h => h.Type != primary), unresolved = unresolved.Count(u => u.Type != primary) },
  hiddenByType = hidden.GroupBy(h => Short(h.Type)).Select(g => new { type = g.Key, count = g.Count() }),
  unresolved = unresolved.Select(u => u.Num),
  handDragged = L.Where(l => l.Fixed).Select(l => l.Num), movedNums = L.Where(l => l.Active && l.Moved).Select(l => l.Num), hiddenNums = hidden.Select(h => h.Num),
  writesOffTarget,
  hiddenAsLastResort, hiddenAsRepeat, movedExtended, nudged, flipped, handDraggedMoved = movedP3, fixedOnRetry,
  restore = originalAnchors.Select(kv => new { num = kv.Key.Num, x = kv.Value.X, y = kv.Value.Y, z = kv.Value.Z }), textOnTextRemaining = L.Count(l => l.Active && L.Any(o => o != l && o.Active && BoxBox(l.Box, o.Box))),
  textOnTextPairs = L.Where(l => l.Active).SelectMany(l => L.Where(o => o.Active && o.Num > l.Num && BoxBox(l.Box, o.Box)).Select(o => new { a = l.Num, aType = Short(l.Type), aFixed = l.Fixed, b = o.Num, bType = Short(o.Type), bFixed = o.Fixed })),
  stillOverlappingCount = stillOverlapping.Count, stillOverlapping = stillOverlapping.Take(40),
};

// ---- types ----
record struct Bx(double X0, double Y0, double X1, double Y1);
record struct Sg(Point2d A, Point2d B);
class Lbl {
  public ObjectId Id; public uint Num; public string Type; public Point2d P; public double W, Z; public DateTime Date;
  public bool Fixed, Active, Moved, Protected; public string Code = "";
  public Point2d Anchor; public Bx Box; public Sg[] Leader;
  public static Bx DefaultBox(Point2d p, double w, double offX, double offY, double h) => new Bx(p.X + offX, p.Y + offY - h / 2, p.X + offX + w, p.Y + offY + h / 2);
  // Leader from the point center to an elbow, then a horizontal landing of length `dog` into the leader end.
  public static (Bx box, Sg[] leader) DraggedGeom(Point2d p, Point2d a, double w, double h, double gap, double dog) {
    bool right = a.X >= p.X;
    var box = right ? new Bx(a.X + gap, a.Y - h / 2, a.X + gap + w, a.Y + h / 2) : new Bx(a.X - gap - w, a.Y - h / 2, a.X - gap, a.Y + h / 2);
    var elbow = new Point2d(right ? a.X - dog : a.X + dog, a.Y);
    return (box, new[] { new Sg(p, elbow), new Sg(elbow, a) });
  }
  public void SetDefault(double offX, double offY, double h) { Box = DefaultBox(P, W, offX, offY, h); Leader = null; Anchor = P; }
  public void SetDragged(Point2d a, double h, double gap, double dog) { var g = DraggedGeom(P, a, W, h, gap, dog); Box = g.box; Leader = g.leader; Anchor = a; }
}
```
