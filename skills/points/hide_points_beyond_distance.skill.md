---
name: hide_points_beyond_distance
category: points
description: Hide every COGO point farther than a distance from an alignment's centerline (distance to the line, ends included) by adding the point numbers to a hide point group's include list (compact ranges, existing entries kept). Points are not changed.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: alignment
    type: string
    required: true
    description: Alignment whose centerline (start to end) is measured to
  - name: maxDist
    type: number
    required: true
    description: Points farther than this are hidden
  - name: hideGroup
    type: string
    required: true
    description: Point group whose style overrides are none (hides marker and label)
  - name: dryRun
    type: boolean
    required: false
    description: Count only (default true)
---

> Verified on Civil 3D 2027.

## When to use
A plan of one feature should only show the survey around it (e.g. points within 75 ft of a ditch centerline), the
way a go-by that only contains nearby data looks. The include list is written as ranges of consecutive numbers.
The measure uses the straight line between the alignment's start and end, which suits single-line alignments.

## Script
```csharp
// Add every point farther than maxDist from an alignment's centerline (distance to the line, ends included) to a
// hide point group's include list, keeping what is already there. Numbers are written as compact ranges.
var dryRun = Args.GetBool("dryRun", true);
double maxD = Args.GetDouble("maxDist");
Alignment al = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == Args.GetString("alignment")) al = a; }
double ax = 0, ay = 0, bx = 0, by = 0; al.PointLocation(al.StartingStation, 0, ref ax, ref ay); al.PointLocation(al.EndingStation, 0, ref bx, ref by);
var A = new Point2d(ax, ay); var B = new Point2d(bx, by); var AB = B - A;
double Dist(Point3d p) { var q = new Point2d(p.X, p.Y); double t = Math.Max(0, Math.Min(1, (q - A).DotProduct(AB) / AB.DotProduct(AB))); return q.GetDistanceTo(A + AB * t); }
var far = new List<uint>(); int near = 0;
foreach (ObjectId id in CivilDoc.CogoPoints) { var p = Get<CogoPoint>(id); if (Dist(p.Location) > maxD) far.Add(p.PointNumber); else near++; }
var g = Get<PointGroup>(CivilDoc.PointGroups[Args.GetString("hideGroup")]);
var q = (StandardPointGroupQuery)g.GetQuery();
var existing = new HashSet<string>((q.IncludeNumbers ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
// expand existing plain numbers so they merge into ranges; keep any existing range tokens as they are
var nums = new SortedSet<uint>(far);
var keepTokens = new List<string>();
foreach (var t in existing) { if (uint.TryParse(t, out var n)) nums.Add(n); else keepTokens.Add(t); }
var tokens = new List<string>(); uint? s0 = null, prev = null;
foreach (var n in nums) { if (s0 == null) { s0 = prev = n; continue; } if (n == prev + 1) { prev = n; continue; } tokens.Add(s0 == prev ? $"{s0}" : $"{s0}-{prev}"); s0 = prev = n; }
if (s0 != null) tokens.Add(s0 == prev ? $"{s0}" : $"{s0}-{prev}");
tokens.AddRange(keepTokens);
var include = string.Join(",", tokens);
if (!dryRun) {
  var gw = Get<PointGroup>(g.ObjectId, OpenMode.ForWrite);
  q.IncludeNumbers = include; gw.SetQuery(q); gw.Update();
}
return new { dryRun, far = far.Count, near, groupPointsBefore = g.PointsCount, tokens = tokens.Count, includeLength = include.Length, groupPointsAfter = dryRun ? -1L : (long)Get<PointGroup>(g.ObjectId).PointsCount };
```
