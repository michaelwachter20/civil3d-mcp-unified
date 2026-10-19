---
name: contour_label_ladder
category: surfaces
description: Label a ditch or channel's contours as a ladder - one label per stop along the channel, stepping one contour down the first side slope and back up the other (e.g. 8 7 6 5 4 3 2 3 4 5 6 7 8), with short hidden label lines so each crosses only its own contour. Suggested saved script name contour-ladder.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: surface
    type: string
    required: true
    description: TIN surface whose contours are labeled
  - name: alignment
    type: string
    required: true
    description: Channel centerline alignment (stations along the channel)
  - name: labelStyle
    type: string
    required: true
    description: Contour label style used for major, minor and user contours
  - name: stepFt
    type: number
    required: false
    description: Distance between stops along the channel (default 18)
  - name: startStation
    type: number
    required: false
    description: First stop station (default 18)
  - name: endMarginFt
    type: number
    required: false
    description: Stop this far before the alignment end (default 18)
  - name: reachFt
    type: number
    required: false
    description: How far out from the centerline to search for the contour crossing (default 35)
  - name: interval
    type: number
    required: false
    description: Contour interval (default 1)
  - name: labelLineFt
    type: number
    required: false
    description: Hidden label line length (default 1)
  - name: sides
    type: string[]
    required: false
    description: Slope order, e.g. ["left","right"] - down the first, up the second
  - name: dryRun
    type: boolean
    required: false
    description: Plan only (default true)
---

> Verified on Civil 3D 2027.

## When to use
A narrow channel with steep side slopes (farm ditch, swale, canal): contours are about 1 ft apart, so labelling
"wherever there is room" either finds nothing or floods the slopes (93 labels on a 255 ft ditch was far too many).
The user's standard - matching a finished go-by - is a ladder that reads in sequence along the channel.

## How it works
1. Starting at `startStation` on the first side, the first label is the highest whole contour on that slope.
2. Every `stepFt`, the next stop labels the next lower contour where it crosses the line across the slope
   (found by sampling surface elevations outward from the centerline).
3. When the next lower contour no longer crosses that side (the bottom), it switches to the other side and climbs,
   one contour per stop, until no higher contour crosses or the end margin is reached.
4. Each label is a contour label group with a hidden label line `labelLineFt` long, across the slope.

Run a dry run and read the `sequence` back to the user (e.g. "8 7 6 5 4 3 2 3 4 5 6 7 8") before creating.
Remove earlier contour labels of the surface first if they are being replaced.

## Script
```csharp
// contour-ladder: contour labels down one side slope of a channel and up the other, one contour per stop, so they
// read in sequence (e.g. 8 7 6 5 4 3 ... 3 4 5 6 7 8), the way a finished go-by labels a ditch. Stops are every
// `stepFt` along the channel alignment; at each stop the label goes where the current contour crosses the line
// across the slope on the current side. Short hidden label lines (labelLineFt) cross exactly one contour.
var dryRun = Args.GetBool("dryRun", true);
TinSurface surf = null; foreach (ObjectId sid in CivilDoc.GetSurfaceIds()) { var s = Get<Autodesk.Civil.DatabaseServices.Surface>(sid) as TinSurface; if (s != null && s.Name == Args.GetString("surface")) surf = s; }
Alignment al = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == Args.GetString("alignment")) al = a; }
ObjectId styleId = ObjectId.Null; foreach (ObjectId id in CivilDoc.Styles.LabelStyles.SurfaceLabelStyles.ContourLabelStyles) if (Get<Autodesk.Civil.DatabaseServices.Styles.LabelStyle>(id).Name == Args.GetString("labelStyle")) styleId = id;
double step = Args.GetDouble("stepFt", 18), start = Args.GetDouble("startStation", 18), reach = Args.GetDouble("reachFt", 35), interval = Args.GetDouble("interval", 1);
double half = Args.GetDouble("labelLineFt", 1) / 2;
var sides = Args.GetStringList("sides"); if (sides.Count == 0) sides = new List<string> { "left", "right" };
double? Z(double st, double off) { double x = 0, y = 0; al.PointLocation(st, off, ref x, ref y); try { return surf.FindElevationAtXY(x, y); } catch { return null; } }
// crossing of contour z on the line from the centerline (off 0) out to `reach` on a side: returns offset
double? Cross(double st, int sign, double z) {
  double? prev = null; double prevOff = 0;
  for (double o = 0; o <= reach + 1e-9; o += 0.1) { var e = Z(st, sign * o); if (e == null) { prev = null; continue; } if (prev != null && (prev.Value - z) * (e.Value - z) <= 0 && prev != e) { double f = (z - prev.Value) / (e.Value - prev.Value); return sign * (prevOff + f * 0.1); } prev = e; prevOff = o; }
  return null;
}
double? MaxZ(double st, int sign) { double? m = null; for (double o = 0; o <= reach; o += 0.5) { var e = Z(st, sign * o); if (e != null && (m == null || e > m)) m = e; } return m; }
double? MinZ(double st) { double? m = null; foreach (var sg in new[] { -1, 1 }) for (double o = 0; o <= reach; o += 0.5) { var e = Z(st, sg * o); if (e != null && (m == null || e < m)) m = e; } return m; }
var plan = new List<(double st, double off, double z, string side)>();
double stn = start; int sideIdx = 0; int sign = sides[0] == "left" ? -1 : 1;
// first contour: the highest whole contour that crosses the first stop's slope
var top = MaxZ(stn, sign); if (top == null) throw new InvalidOperationException("No surface at the start station.");
double zc = Math.Floor(top.Value / interval) * interval; if (zc >= top.Value) zc -= interval;
int dir = -1;   // going down the first slope, then up the second
while (stn <= al.EndingStation - Args.GetDouble("endMarginFt", 18)) {
  var off = Cross(stn, sign, zc);
  if (off == null) {
    if (dir < 0) {
      // reached the bottom on this side: switch to the other side and climb from the lowest contour there
      if (sideIdx + 1 >= sides.Count) break;
      sideIdx++; sign = sides[sideIdx] == "left" ? -1 : 1; dir = 1;
      var lo = MinZ(stn); if (lo == null) break;
      zc = Math.Ceiling(lo.Value / interval) * interval; if (zc <= lo.Value) zc += interval;
      continue;
    }
    break;   // ran out of contours going up
  }
  plan.Add((stn, off.Value, zc, sides[sideIdx]));
  zc += dir * interval; stn += step;
}
var created = 0;
if (!dryRun) {
  foreach (var p in plan) {
    double x0 = 0, y0 = 0, x1 = 0, y1 = 0; al.PointLocation(p.st, p.off - half, ref x0, ref y0); al.PointLocation(p.st, p.off + half, ref x1, ref y1);
    var gid = SurfaceContourLabelGroup.Create(surf.ObjectId, new Point2dCollection { new Point2d(x0, y0), new Point2d(x1, y1) }, styleId, styleId, styleId);
    Get<SurfaceContourLabelGroup>(gid, OpenMode.ForWrite).IsLabelLineVisible = false; created++;
  }
  var e = al.GeometricExtents; FocusOn(new Point3d(e.MinPoint.X - 60, e.MinPoint.Y - 20, 0), new Point3d(e.MaxPoint.X + 60, e.MaxPoint.Y + 20, 0));
}
return new { dryRun, labels = plan.Count, created, sequence = string.Join(" ", plan.Select(p => p.z.ToString("0"))), plan = plan.Select(p => $"sta {p.st:0} {p.side} off {p.off:0.0} z {p.z:0}") };
```
