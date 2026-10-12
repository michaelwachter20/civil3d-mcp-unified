---
name: declutter_point_labels
category: workflows
description: De-overlap COGO point labels on a layout sheet - drag every label to a clear spot, hide repeat/older shots in a hide point group, never touch the points themselves. Saved script name declutter-labels (undo helper: declutter-labels-undo).
requires_write: true
parameters:
  - name: primary
    type: string
    required: false
    description: Display group placed first (default CLSM Display)
  - name: secondary
    type: string[]
    required: false
    description: Display groups placed second (default PFP FFP Display, BASE Display)
  - name: hideGroup
    type: string
    required: false
    description: Point group that hides marker and label (default Secondary Filter)
  - name: maxOffsetIn
    type: number
    required: false
    description: Normal max leader reach in paper inches (default 0.30)
  - name: lastOfTypeMaxIn
    type: number
    required: false
    description: Reach for the last stubborn labels in paper inches (default 0.5)
  - name: clearanceIn
    type: number
    required: false
    description: Minimum clear space between labels in paper inches (default 0.02)
  - name: dryRun
    type: boolean
    required: false
    description: Plan only, change nothing (default true)
---

## When to use
The user has COGO point labels overlapping on a plotted sheet (layout viewport) and wants them dragged apart,
with dense spots thinned by adding points to a point group whose style override is <none> (e.g. "Secondary Filter").
Proven on a real Civil 3D 2027 levee drawing (1"=30', viewport twisted -60 deg, ~500 visible labels): ~0.5 s, one undo step.

## Hard rules (from the user)
- Never explode a point (not even in memory), never move a point, never edit point data. Only label positions
  and the hide group's include list change.
- Primary type first, then secondary types. Hand-dragged labels have priority to stay but may move if they overlap.
- Thinning keeps the newest survey and keeps each type represented nearby.

## How to run
1. User opens the layout and activates the viewport (double-click inside it).
2. Run with dryRun: true (civil3d_query) and report the counts. Then run dryRun: false with civil3d_execute,
   or save it with civil3d_script (mode write) so the user can run MCPRUN declutter-labels.
3. Before a real run, record which labels are already dragged and their LabelLocation (the result's estore
   list covers hand-dragged labels the run moved) so the run can be reverted after UNDO history is gone
   (see eset_declutter_run).

## Civil 3D gotchas this script handles
- Setting CogoPoint.LabelLocation on an ALREADY-DRAGGED label is applied relative to the old drag. Call
  ResetLabelLocation() first, then set it, then verify (writesOffTarget must be 0).
- Undragged labels are rotated by their label style; dragged ones are horizontal on the sheet. Drag every label
  you place so its geometry is known.
- Dragged label geometry (measured): 0.083" tall x (text width + 0.006"), centered vertically on the leader end,
  0.025" beyond it on the side away from the point. Leader = point -> elbow -> 0.05" horizontal landing.
- Ignore labels whose label layer is frozen, off or frozen in the viewport (they block good spots otherwise).
- Points stacked within a marker width: a leader leaving one unavoidably crosses the other's marker; allow it.
- CogoPoint.Bounds excludes dragged labels, and Explode returns nothing - don't rely on either.

## Script
```csharp
// declutter-labels: de-overlap COGO point labels in the active layout viewport.
// LABELS ONLY: never moves, explodes, or edits a point. It sets CogoPoint.LabelLocation (resetting an
// already-dragged label first) and adds point numbers to the hide group's include list.
//
// Pass 1  primary group (CLSM) placed first, newest survey first (date read from layer "NODE yyyy mm dd XXX").
// Pass 2  secondary groups (FFP, BASE) placed around them.
// Pass 3  hand-dragged labels move only if they overlap something (never hidden here).
// Pass 4  stuck labels retry once space has been freed.
// Every label that isn't hand-dragged gets dragged: default labels are rotated by their style, dragged ones
// are horizontal on the sheet, so their geometry is known.
// Hiding (hide group): repeat shots (same type within repeatShotIn), then older shots when the same type stays
// visible within representRadiusIn, and as a last resort when no spot within lastOfTypeMaxIn avoids text and
// leaders. Leaders may graze a point marker but never cross label text or another leader.
// Geometry measured on a 1"=30' sheet: dragged label = 0.083" tall x (text width + 0.006"), vertically centered
// on the leader end, 0.025" beyond it on the side away from the point; leader = point -> elbow -> 0.05" landing.
var primary = Args.GetString("primary", "CLSM Display");
var secondaryGroups = Args.GetStringList("secondary");
if (secondaryGroups.Count == 0) secondaryGroups = new List<string> { "PFP FFP Display", "BASE Display" };
var hideGroupName = Args.GetString("hideGroup", "Secondary Filter");
var maxOffsetIn = Args.GetDouble("maxOffsetIn", 0.30);
var dryRun = Args.GetBool("dryRun", true);

if (Database.TileMode) throw new InvalidOperationException("Switch to a layout and activate the viewport (double-click inside it) first.");
var vp = Get<Viewport>(Editor.CurrentViewportObjectId);
if (vp.Number < 2) throw new InvalidOperationException("Activate a model-space viewport on the layout (double-click inside it) first.");
double mpp = 1.0 / vp.CustomScale;                       // model units per paper inch
double fontH = Args.GetDouble("textHeightIn", 0.075) * mpp;          // label text height
// Measured on the sheet (user-drawn box around a dragged label): the label occupies 0.083" x (text width + 0.006"),
// vertically centered on the leader end, 0.025" beyond it on the side away from the point.
double textH = Args.GetDouble("labelBoxHeightIn", 0.083) * mpp;
double widthExtra = Args.GetDouble("labelExtraWidthIn", 0.006) * mpp;
double offX = 0.05 * mpp, offY = 0.05 * mpp;              // label style X/Y offsets
double markerR = Args.GetDouble("markerSizeFt", 0.85);
double maxOffset = maxOffsetIn * mpp;
double gap = Args.GetDouble("leaderGapIn", 0.025) * mpp;
double pad = Args.GetDouble("clearanceIn", 0.02) * mpp;           // minimum clear space between labels
// Leaders run point -> elbow -> leader end; the horizontal landing into the label is ~0.05" (measured from user tracing)
double dog = Args.GetDouble("leaderLandingIn", 0.05) * mpp;
double representRadius = Args.GetDouble("representRadiusIn", 0.5) * mpp;   // a type must stay visible within this distance
double dupRadius = Args.GetDouble("repeatShotIn", 0.1) * mpp;              // same-type shots closer than this are repeats

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
  groupList.Add((g.Name, g.IsPointLabelStyleOverridden, g.PointLabelStyleId, new HashSet<uint>(g.GetPointNumbers())));
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
int skippedInvisible = 0;

var L = new List<Lbl>();
foreach (ObjectId id in CivilDoc.CogoPoints) {
  var p = Get<CogoPoint>(id);
  var d3 = p.Location.TransformBy(wcsToDcs);
  var d = new Point2d(d3.X, d3.Y);
  if (Math.Abs(d.X - vc.X) > hw || Math.Abs(d.Y - vc.Y) > hh) continue;
  string grp = null; ObjectId ls = p.LabelStyleId;
  foreach (var gr in groupList) if (gr.ovL && gr.nums.Contains(p.PointNumber)) { grp = gr.name; ls = gr.ls; break; }
  if (ls.IsNull) continue;
  if (LabelInvisible(ls)) { skippedInvisible++; continue; }
  var m = System.Text.RegularExpressions.Regex.Match(p.Layer, @"(\d{4}) (\d{2}) (\d{2})");
  var lbl = new Lbl {
    Id = id, Num = p.PointNumber, Type = grp ?? "(own)", P = d, W = TextW(p.Elevation), Z = p.Location.Z,
    Date = m.Success ? new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : DateTime.MinValue,
    Fixed = p.IsLabelDragged, Active = true,
  };
  if (p.IsLabelDragged) { var a3 = p.LabelLocation.TransformBy(wcsToDcs); lbl.SetDragged(new Point2d(a3.X, a3.Y), textH, gap, dog); }
  else lbl.SetDefault(offX, offY, textH);
  L.Add(lbl);
}

// Two labels can only interact if their points are within this distance (both may be offset and long)
double reach = 2 * (maxOffset + gap + L.Select(l => l.W).DefaultIfEmpty(0).Max() + markerR + pad) + 1;
// Labels that touch anything (other than their own marker) before we change anything

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
  return hits;
}

// ---- audit mode ----
if (Args.Has("audit")) {
  var want = new HashSet<uint>(Args.GetStringList("audit").Select(uint.Parse));
  return L.Where(l => want.Contains(l.Num)).Select(l => new {
    l.Num, l.Type, l.Fixed, l.Active,
    box = $"x {(l.Box.X0 - l.P.X) / mpp:0.000}..{(l.Box.X1 - l.P.X) / mpp:0.000}, y {(l.Box.Y0 - l.P.Y) / mpp:0.000}..{(l.Box.Y1 - l.P.Y) / mpp:0.000}",
    hits = Conflicts(l, l.Box, l.Leader, o => o.Active).Where(h => h != l).Select(h => $"{h.Num} {h.Type}"),
    others = L.Where(o => o != l && o.Active && Math.Abs(o.P.X - l.P.X) < 0.5 * mpp && Math.Abs(o.P.Y - l.P.Y) < 0.5 * mpp && BoxBox(l.Box, o.Box)).Select(o => o.Num),
  }).ToList();
}

var cands = new List<Vector2d>();
for (double r = 0.08 * mpp; r <= maxOffset + 1e-9; r += 0.02 * mpp)
  for (int k = 0; k < 24; k++) { var ang = k * Math.PI / 12; cands.Add(new Vector2d(r * Math.Cos(ang), r * Math.Sin(ang))); }
cands = cands.OrderBy(c => c.Length).ThenBy(c => Math.Abs(c.Y)).ToList();

var extCands = new List<Vector2d>();
for (double r = maxOffset + 0.02 * mpp; r <= Args.GetDouble("lastOfTypeMaxIn", 0.5) * mpp + 1e-9; r += 0.02 * mpp)
  for (int k = 0; k < 24; k++) { var ang = k * Math.PI / 12; extCands.Add(new Vector2d(r * Math.Cos(ang), r * Math.Sin(ang))); }
extCands = extCands.OrderBy(c => c.Length).ThenBy(c => Math.Abs(c.Y)).ToList();

var overlappingBefore = L.Count(l => Conflicts(l, l.Box, l.Leader, o => true).Any(h => h != l));
var hidden = new List<Lbl>(); var unresolved = new List<Lbl>(); var forcedOver = new HashSet<Lbl>();
var hiddenAsLastResort = new List<uint>();
int movedP1 = 0, movedP2 = 0, keptP1 = 0, keptP2 = 0, hiddenAsRepeat = 0, movedExtended = 0;

bool TryFree(Lbl me, List<Vector2d> candidates, Func<Lbl, bool> present) {
  foreach (var c in candidates) {
    var a = me.P + c;
    var (box, leader) = Lbl.DraggedGeom(me.P, a, me.W, textH, gap, dog);
    if (Conflicts(me, box, leader, present).Count == 0) { me.SetDragged(a, textH, gap, dog); me.Moved = true; return true; }
  }
  return false;
}

// Every label that isn't hand-dragged gets dragged, so its geometry is known (undragged labels are
// rotated by their style). The first spot tried is right next to the point, about where the default sits.
var nearDefault = new Vector2d(offX - gap, offY);

// Try to settle one label (label position only); returns "kept" / "moved" / "hidden" / "unresolved".
string Settle(Lbl me, Func<Lbl, bool> present, bool allowHide = true) {
  if (TryFree(me, new List<Vector2d> { nearDefault }, present)) return "kept";
  // Repeat shot: a newer same-type label is shown right here, so hide this older one instead of moving it
  if (allowHide && L.Any(o => o != me && o.Type == me.Type && o.Active && present(o) && o.P.GetDistanceTo(me.P) <= dupRadius)) { me.Active = false; hidden.Add(me); hiddenAsRepeat++; return "hidden"; }
  if (TryFree(me, cands, present)) return "moved";
  // Hide only if its type stays represented here: a same-type label within representRadius remains shown
  if (allowHide && L.Any(o => o != me && o.Type == me.Type && o.Active && present(o) && o.P.GetDistanceTo(me.P) <= representRadius)) { me.Active = false; hidden.Add(me); return "hidden"; }
  // Last label of its type here: allow a longer move (up to lastOfTypeMaxIn) before giving up
  if (TryFree(me, extCands, present)) { movedExtended++; return "moved"; }
  // Last resort: a spot whose only conflicts are point markers (no text on text, no leader through text,
  // no crossing leaders). Fewest marker touches wins; shorter spots win ties.
  bool Severe(Bx box, Sg[] leader) => L.Any(o => o != me && o.Active && present(o)
      && Math.Abs(o.P.X - me.P.X) < reach && Math.Abs(o.P.Y - me.P.Y) < reach
      && (BoxBox(box, o.Box)
          || (o.Leader != null && o.Leader.Any(s => SegBox(s.A, s.B, box)))
          || (leader != null && (leader.Any(s => SegBox(s.A, s.B, o.Box)) || (o.Leader != null && leader.Any(s => o.Leader.Any(q => SegSeg(s.A, s.B, q.A, q.B))))))));
  int bestCount = int.MaxValue; Vector2d? best = null;
  foreach (var c in new[] { nearDefault }.Concat(cands).Concat(extCands)) {
    var g = Lbl.DraggedGeom(me.P, me.P + c, me.W, textH, gap, dog);
    if (Severe(g.box, g.leader)) continue;
    var hits = Conflicts(me, g.box, g.leader, present);
    if (hits.Contains(me)) continue;
    if (hits.Count < bestCount) { bestCount = hits.Count; best = c; }
  }
  if (best is not Vector2d bc) { me.Active = false; hidden.Add(me); hiddenAsLastResort.Add(me.Num); return "hidden"; }   // better hidden than overlapping
  me.SetDragged(me.P + bc, textH, gap, dog); me.Moved = true;
  unresolved.Add(me); return "unresolved";
}

// ---- Pass 1: primary type. Undragged secondary labels will all be re-placed, so they don't block. ----
var p1 = L.Where(l => l.Type == primary && !l.Fixed).ToList();
var p1Done = new HashSet<Lbl>();
foreach (var me in p1.OrderByDescending(l => l.Date).ThenByDescending(l => L.Count(o => o.P.GetDistanceTo(l.P) < 2 * textH))) {
  var r = Settle(me, present: o => o.Fixed || p1Done.Contains(o));
  if (r == "moved") movedP1++; else if (r == "kept") keptP1++;
  p1Done.Add(me);
}

// ---- Pass 2: secondary types (every one not hand-dragged), placed around the primary labels ----
var p2 = L.Where(l => secondaryGroups.Contains(l.Type) && !l.Fixed && l.Active).ToList();
var p2Done = new HashSet<Lbl>();
foreach (var me in p2.OrderByDescending(l => l.Date).ThenByDescending(l => L.Count(o => o.P.GetDistanceTo(l.P) < 2 * textH))) {
  var r = Settle(me, present: o => o.Fixed || (o.Type == primary && o.Active) || p2Done.Contains(o));
  if (r == "moved") movedP2++; else if (r == "kept") keptP2++;
  p2Done.Add(me);
}
// ---- Pass 3: hand-dragged labels move only if they overlap something (never hidden) ----
var originalAnchors = new Dictionary<Lbl, Point3d>();
var p3 = L.Where(l => l.Fixed && l.Active && Conflicts(l, l.Box, l.Leader, o => o.Active).Any(h => h != l)).ToList();
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
  if (hidden.Count > 0) {
    var hg = Get<PointGroup>(hideGroup.ObjectId, OpenMode.ForWrite);
    var q = hg.GetQuery() as StandardPointGroupQuery ?? new StandardPointGroupQuery();
    q.IncludeNumbers = string.Join(",", new[] { q.IncludeNumbers }.Where(s => !string.IsNullOrWhiteSpace(s)).Concat(hidden.Select(h => h.Num.ToString())));
    hg.SetQuery(q);
    hg.Update();
  }
}

string Short(string t) => t.Replace(" Display", "").Replace("PFP ", "");
return new {
  dryRun, scale = $"1\" = {mpp:0.##}'", labelsInView = L.Count, skippedInvisible, overlappingBefore,
  overlappingAmongHandDragged = L.Count(l => l.Fixed && Conflicts(l, l.Box, l.Leader, o => true).Any(h => h != l && h.Fixed)),
  pass1 = new { type = primary, considered = p1.Count, kept = keptP1, moved = movedP1, hidden = hidden.Count(h => h.Type == primary), unresolved = unresolved.Count(u => u.Type == primary) },
  pass2 = new { considered = p2.Count, kept = keptP2, moved = movedP2, hidden = hidden.Count(h => h.Type != primary), unresolved = unresolved.Count(u => u.Type != primary) },
  hiddenByType = hidden.GroupBy(h => Short(h.Type)).Select(g => new { type = g.Key, count = g.Count() }),
  unresolved = unresolved.Select(u => u.Num),
  handDragged = L.Where(l => l.Fixed).Select(l => l.Num), movedNums = L.Where(l => l.Active && l.Moved).Select(l => l.Num), hiddenNums = hidden.Select(h => h.Num),
  writesOffTarget,
  hiddenAsLastResort, hiddenAsRepeat, movedExtended, handDraggedMoved = movedP3, fixedOnRetry,
  restore = originalAnchors.Select(kv => new { num = kv.Key.Num, x = kv.Value.X, y = kv.Value.Y, z = kv.Value.Z }), textOnTextRemaining = L.Count(l => l.Active && L.Any(o => o != l && o.Active && BoxBox(l.Box, o.Box))),
  textOnTextPairs = L.Where(l => l.Active).SelectMany(l => L.Where(o => o.Active && o.Num > l.Num && BoxBox(l.Box, o.Box)).Select(o => new { a = l.Num, aType = Short(l.Type), aFixed = l.Fixed, b = o.Num, bType = Short(o.Type), bFixed = o.Fixed })),
  stillOverlappingCount = stillOverlapping.Count, stillOverlapping = stillOverlapping.Take(40),
};

// ---- types ----
record struct Bx(double X0, double Y0, double X1, double Y1);
record struct Sg(Point2d A, Point2d B);
class Lbl {
  public ObjectId Id; public uint Num; public string Type; public Point2d P; public double W, Z; public DateTime Date;
  public bool Fixed, Active, Moved;
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
