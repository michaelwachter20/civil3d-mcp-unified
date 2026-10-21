---
name: points_by_segment_report
category: points
description: Report what a set of point groups contains (e.g. one field day per crew) - shot types from the description, which segment (station range on an alignment) each falls in, and the station range per type and segment widened to the nearest +25 ft, with offsets. Read only. Suggested saved script name points-by-segment.
requires_write: false
civil3d_versions: 2027
parameters:
  - name: alignment
    type: string
    required: true
    description: Alignment the segment stations refer to
  - name: groups
    type: string[]
    required: true
    description: Point groups to report (e.g. the day's crew groups)
  - name: segments
    type: json
    required: true
    description: '[{"name": "SEG-A", "from": "6375", "to": "6975"}, ...] station ranges (from <= station < to)'
---

> Verified on Civil 3D 2027.

## When to use
After a field day is imported, the user wants to know what was surveyed: what kind of shots, in which segment, and
over which stations. Segments are project-specific (a table or the drawing's boundary lines and labels); keep the
project's segment table in project notes or its files folder and pass the ranges in.

## Notes
- Shot type = the description without its last token (the area code), e.g. "GRID/FILL/A3" -> type GRID/FILL, code A3.
  Adjust if the project's description order differs.
- Station ranges are widened outward to whole +25 ft (floor of the first, ceiling of the last), e.g. 75+25 to 79+50.
- Offsets are reported as left (negative) / right (positive) of the alignment.
- Present the day combined (all groups together, not split by crew unless asked), per segment and type, and call out shots whose area code differs from the segment they
  are in (usually a crew working a few feet over a boundary) - never change the codes, only report them.

## Script
```csharp
// Read-only: for the points of the given point groups, the shot type (description without the area code), the
// segment they fall in (station ranges on an alignment) and the station range per type and segment, widened to
// whole +25 ft (floor/ceiling).
Alignment al = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == Args.GetString("alignment")) al = a; }
var segs = Args.Get<List<Dictionary<string, string>>>("segments").Select(s => (name: s["name"], from: double.Parse(s["from"], CultureInfo.InvariantCulture), to: double.Parse(s["to"], CultureInfo.InvariantCulture))).ToList();
string Sta(double s) => $"{Math.Floor(s / 100):0}+{s - Math.Floor(s / 100) * 100:00}";
double Down(double s) => Math.Floor(s / 25) * 25; double Up(double s) => Math.Ceiling(s / 25) * 25;
var rows = new List<(string group, string seg, string type, string code, double st, double off)>();
foreach (var gname in Args.GetStringList("groups")) {
  foreach (var n in Get<PointGroup>(CivilDoc.PointGroups[gname]).GetPointNumbers()) {
    var p = Get<CogoPoint>(CivilDoc.CogoPoints.GetPointByPointNumber(n));
    double st = 0, off = 0; al.StationOffset(p.Easting, p.Northing, ref st, ref off);
    var parts = (p.RawDescription ?? "").Split('/');
    var code = parts.Length > 1 ? parts.Last() : "";
    var type = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : parts[0];
    var seg = segs.Where(s => st >= s.from && st < s.to).Select(s => s.name).FirstOrDefault() ?? "(no segment)";
    rows.Add((gname, seg, type, code, st, off));
  }
}
var detail = rows.GroupBy(r => (r.group, r.seg, r.type, r.code)).OrderBy(g => g.Key.group).ThenBy(g => g.Min(r => r.st)).ThenBy(g => g.Key.type)
  .Select(g => $"{g.Key.group} | {g.Key.seg} | {g.Key.type} (code {g.Key.code}) | {g.Count()} pts | sta {Sta(Down(g.Min(r => r.st)))} to {Sta(Up(g.Max(r => r.st)))} | off {g.Min(r => r.off):0} to {g.Max(r => r.off):0}").ToList();
var bySeg = rows.GroupBy(r => (r.group, r.seg)).OrderBy(g => g.Key.group).ThenBy(g => g.Min(r => r.st))
  .Select(g => $"{g.Key.group} | {g.Key.seg} | {g.Count()} pts | sta {Sta(Down(g.Min(r => r.st)))} to {Sta(Up(g.Max(r => r.st)))} | types: {string.Join(", ", g.GroupBy(r => r.type).OrderByDescending(t => t.Count()).Select(t => $"{t.Key} {t.Count()}"))}").ToList();
return new { total = rows.Count, bySeg, detail };
```
