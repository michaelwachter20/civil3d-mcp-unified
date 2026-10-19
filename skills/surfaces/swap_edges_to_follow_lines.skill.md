---
name: swap_edges_to_follow_lines
category: surfaces
description: Make a TIN surface follow top-of-slope and toe lines (a clean ditch or basin) only by swapping triangle edges, without adding or deleting points, lines or breaklines
requires_write: true
civil3d_versions: 2027
parameters:
  - name: surface
    type: string
    required: true
    description: TIN surface name
  - name: chains
    type: json
    required: true
    description: Object of named lines, each an array of existing point numbers in order along the line, e.g. {"top": [..], "toe": [..]}
  - name: dryRun
    type: boolean
    required: false
    description: Report what crosses the lines without changing anything
---

> Verified on Civil 3D 2027.

## When to use

A surface built from a point group triangulates straight across a ditch, trench, basin or step, so contours and
sections cut across it. The survey has shots along the top of slope and the toe (often coded TOP / TOE in the
description). Swap edges until every point-to-point segment along those lines is a triangle edge; then each
triangle steps top, slope, toe and nothing crosses the feature.

## Rules

- Only swap edges. Never add or delete points, triangle lines, breaklines or boundaries, and never create
  point numbers: the surface stays defined by its point group plus swap edits. (Ask the user first if they want
  anything else.)
- Use points that are actually in the surface (its point group). Points on other surfaces don't count.
- Build each line from real shots only, ordered along the feature. Close a ring around an end only through
  shots that are there; don't bridge a stretch that has no shots on that side.
- If the surface's point group must change (points excluded or moved to another surface), change it first,
  then swap. Swaps made first can refer to triangles whose points are later removed; undo them and redo.
- One change per run, so `UNDO 1` reverts it, and end with the view on the area (`FocusOn`).

## Finding the lines

1. Read the surface's points (point group membership) with their descriptions, and group them by feature
   code (TOP, TOE, ...) and location. Plot or list them by station/easting to see each side.
2. For a ditch: one top line per side (joined around closed ends), one or two toe lines along the bottom
   (pairs of toe shots mean a flat bottom: use both lines).
3. Stop lines where a feature ends or passes into another one; ask the user how features meet if unsure.
4. Run with `dryRun: true` first to see how many edges cross each line.

## Script (civil3d_execute, or save with civil3d_script and run by name)

```csharp
// Swap TIN edges until each point-to-point line (by point number) is a triangle edge. Only re-connects
// existing triangles: no points, breaklines or boundaries are added. One line segment at a time; the
// triangles are re-read after every swap, and finished segments are never swapped again.
var surfName = Args.GetString("surface");
var dryRun = Args.GetBool("dryRun", false);
TinSurface surf = null;
foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { var t = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface; if (t != null && t.Name == surfName) surf = t; }
if (surf == null) throw new ArgumentException($"Surface '{surfName}' not found.");
if (!dryRun) surf.UpgradeOpen();
var byNum = new Dictionary<uint, Point2d>();
foreach (ObjectId id in CivilDoc.CogoPoints) { var p = (CogoPoint)Transaction.GetObject(id, OpenMode.ForRead); byNum[p.PointNumber] = new Point2d(p.Easting, p.Northing); }
var chains = Args.Get<Dictionary<string, uint[]>>("chains");
var segs = new List<(string line, uint na, uint nb, Point2d a, Point2d b)>();
foreach (var kv in chains)
  for (int i = 1; i < kv.Value.Length; i++)
  {
    if (!byNum.ContainsKey(kv.Value[i - 1]) || !byNum.ContainsKey(kv.Value[i])) throw new ArgumentException($"Point {kv.Value[i - 1]} or {kv.Value[i]} does not exist.");
    segs.Add((kv.Key, kv.Value[i - 1], kv.Value[i], byNum[kv.Value[i - 1]], byNum[kv.Value[i]]));
  }
double Cross(Point2d o, Point2d a, Point2d b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
bool Proper(Point2d a, Point2d b, Point2d c, Point2d d) { const double e = 1e-6; return Cross(a, b, c) * Cross(a, b, d) < -e && Cross(c, d, a) * Cross(c, d, b) < -e; }
(long, long) K(Point2d p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
((long, long), (long, long)) EK(Point2d a, Point2d b) { var ka = K(a); var kb = K(b); return ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka); }
bool Same(Point3d p, Point2d q) => Math.Abs(p.X - q.X) < 0.001 && Math.Abs(p.Y - q.Y) < 0.001;
Dictionary<((long, long), (long, long)), (Point2d a, Point2d b, List<Point2d> opp)> Edges()
{
  var edges = new Dictionary<((long, long), (long, long)), (Point2d a, Point2d b, List<Point2d> opp)>();
  foreach (var t in surf.GetTriangles(false))
  {
    var v = new[] { t.Vertex1.Location, t.Vertex2.Location, t.Vertex3.Location }.Select(p => new Point2d(p.X, p.Y)).ToArray();
    for (int i = 0; i < 3; i++)
    {
      var a = v[i]; var b = v[(i + 1) % 3]; var key = EK(a, b);
      if (!edges.TryGetValue(key, out var e)) edges[key] = e = (a, b, new List<Point2d>());
      e.opp.Add(v[(i + 2) % 3]);
    }
  }
  return edges;
}
var done = new HashSet<((long, long), (long, long))>();
int swaps = 0, alreadyEdges = 0;
var problems = new List<string>();
foreach (var s in segs)
{
  var target = EK(s.a, s.b);
  for (int iter = 0; ; iter++)
  {
    CancellationToken.ThrowIfCancellationRequested();
    var edges = Edges();
    if (edges.ContainsKey(target)) { if (iter == 0) alreadyEdges++; done.Add(target); break; }
    var crossing = edges.Where(kv => !done.Contains(kv.Key) && kv.Value.opp.Count == 2 && Proper(s.a, s.b, kv.Value.a, kv.Value.b)).Select(kv => kv.Value).ToList();
    if (crossing.Count == 0) { problems.Add($"{s.line} {s.na}-{s.nb}: not an edge but nothing crosses it (a surface point lies on the line)"); break; }
    if (dryRun) { problems.Add($"{s.line} {s.na}-{s.nb}: {crossing.Count} crossing edges"); break; }
    var convex = crossing.Where(e => Proper(e.a, e.b, e.opp[0], e.opp[1])).ToList();
    if (convex.Count == 0 || iter > 400) { problems.Add($"{s.line} {s.na}-{s.nb}: stuck ({crossing.Count} crossing edges, none swappable)"); break; }
    // Prefer swaps whose new edge no longer crosses this segment.
    var pick = convex.FirstOrDefault(e => !Proper(s.a, s.b, e.opp[0], e.opp[1]));
    if (pick.opp == null) pick = convex[iter % convex.Count];
    var found = surf.FindEdgeAtXY((pick.a.X + pick.b.X) / 2, (pick.a.Y + pick.b.Y) / 2);
    var ok = found != null && ((Same(found.Vertex1.Location, pick.a) && Same(found.Vertex2.Location, pick.b)) || (Same(found.Vertex1.Location, pick.b) && Same(found.Vertex2.Location, pick.a)));
    if (!ok) { problems.Add($"{s.line} {s.na}-{s.nb}: couldn't locate an edge to swap"); break; }
    surf.SwapEdge(found);
    swaps++;
  }
}
FocusOn(segs.SelectMany(s => new[] { new Point3d(s.a.X, s.a.Y, 0), new Point3d(s.b.X, s.b.Y, 0) }));
return new { surface = surfName, dryRun, segments = segs.Count, alreadyEdges, segmentsNowEdges = done.Count, swaps, problems };
```

## Verify

- `segmentsNowEdges` equals `segments` and `problems` is empty.
- Re-read the triangles with civil3d_query and confirm no triangle edge properly crosses any line segment.
- Look at it (contours, or a plot of the triangles) with the user before moving on.

## Pitfalls

- `TinSurface.SwapEdge` takes a `TinSurfaceEdge`, not a point: get it with `FindEdgeAtXY` at the edge midpoint
  and check its two vertices before swapping.
- Swapping every crossing edge in passes can flip the same edges back and forth. Finish one segment at a time,
  re-reading the triangles after each swap, and never swap a segment that is already done.
- An edge can only be swapped when its two triangles form a convex quadrilateral; other swaps make room.
- A surface point lying exactly on a line segment means that segment can't be an edge; split the line at it.
- The convex hull of the points is always triangulated, so thin triangles remain outside the outer top line;
  swapping can't remove them (that needs a boundary, which is a separate decision for the user).
