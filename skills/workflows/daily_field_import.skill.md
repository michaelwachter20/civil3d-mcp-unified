---
name: daily_field_import
category: workflows
description: Import a day's checked field files into a running "all points" drawing the way it is done by hand with Import Points - a layer per crew copied from the previous day's layer, a point group per crew at the top of the list, duplicate point numbers offset by a fixed step (+10,000,000, +20,000,000 ... per point) - then update the material point groups and report what each crew did. Suggested saved script names import-day, update-point-groups, points-by-segment.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: date
    type: string
    required: true
    description: The day as used in layer/group names (e.g. "2026 10 19"), taken from the day folder's name, not file dates
  - name: files
    type: json
    required: true
    description: '[{"path": "...topo.csv", "crew": "ABC"}, ...] - the checked file per crew and its crew code'
  - name: layerPrefix
    type: string
    required: true
    description: Layer name prefix before the date (the drawing's existing day layers show it)
  - name: templateLayer
    type: string
    required: true
    description: The previous day's layer, copied for the new day layers
  - name: pointStyle
    type: string
    required: true
    description: Point style the import uses (the Create Points default)
  - name: labelStyle
    type: string
    required: true
    description: Point label style the import uses
  - name: offsetStep
    type: integer
    required: false
    description: Duplicate point number offset step (default 10000000)
  - name: dryRun
    type: boolean
    required: false
    description: Plan only (default true)
---

> Verified on Civil 3D 2027.

## When to use
A project keeps one drawing with every survey point ("all points"), updated after each field day from the crews'
checked files. Learn the drawing's conventions from earlier days before the first run: layer names and properties
per day and crew, point group names and queries (include-numbers), styles, where new groups go in the list, and how
earlier duplicates were numbered (prefixes like 1..., 2..., 3... on bumped numbers). Keep the project's own folder
layout, crew codes and group list in project notes, not here.

## Hard rules
- Field files are **read only**: never edit, rename, move, re-save or delete anything in the field folders and never
  write files there. If a file looks wrong, report it.
- Never change point data: northing, easting, elevation and raw description go in exactly as in the file. The only
  change is the point number of a duplicate, by the offset rule.
- Use only the files the user says are checked (e.g. the "topo" export), PNEZD comma delimited unless told otherwise.
- Dry run first and show it (layers, groups, counts, which numbers get offset and by how much); the drawing is live.

## How to run
1. List the day folder (crew subfolders) and read the checked files; confirm format and crew codes with the user.
2. `import-day` dry run, then run. It refuses when a target point group already exists (the day was imported).
   Duplicates: each number gets the smallest of +step, +2*step, ... that is free, which matches the Duplicate Point
   Number dialog with "Add an offset" applied to all and repeated until the import goes through.
3. Verify every point against its file line (coordinates, elevation, raw and full description, layer, styles), and
   that the groups hold exactly the imported numbers and sit at the top of the list. Save (QSAVE).
4. `update-point-groups` for the material groups (same as right-click > Update; they re-run their own filters - never
   add points to them by hand). A group count that drops is usually a stale count from earlier, unrelated edits:
   check its query before worrying. Save again.
5. Report what each crew did with the `points_by_segment_report` skill: shot types, segment, station range to the
   nearest +25 ft, offsets, and anything that crosses a segment boundary.

## API notes
- `CogoPoints.Add(location, true)` then set `PointNumber`, `RawDescription`, `Layer`, `StyleId`, `LabelStyleId`;
  setting the raw description this way does not run description keys (full description = raw).
- New layer as a copy: `(LayerTableRecord)template.Clone()`, rename, add to the layer table.
- Point group display order: `CivilDoc.PointGroups.DrawOrder` is settable (index 0 = top of the list).
- `CogoPoints.Contains(number)` is fast enough to check every candidate number on drawings with 200k points.

## Script
```csharp
// import-day: import a day's checked field files (PNEZD, comma delimited, no header) into the running all-points
// drawing the way it is done by hand with Import Points:
//  - each crew's points on layer "<layerPrefix><date> <crew>", created as a copy of a template layer (the
//    previous day's) when missing; point and label style as given (the Create Points defaults)
//  - a point group "<date> <crew>" holding exactly the imported point numbers, put at the TOP of the point group list
//  - duplicate point numbers: offset by step, 2*step, 3*step ... (the smallest that is free), per point, like the
//    Duplicate Point Number dialog with "Add an offset" applied to all
//  - point data is never changed: northing, easting, elevation and raw description exactly as in the file
//  - the files are only read; nothing is ever written to the field folders
// Refuses to run when a target point group already exists (that day/crew was imported already).
var dryRun = Args.GetBool("dryRun", true);
var date = Args.GetString("date");                  // "2026 10 19" (from the folder name)
var prefix = Args.GetString("layerPrefix");          // e.g. "SURVEY " - layer = prefix + date + " " + crew
uint step = (uint)Args.GetInt("offsetStep", 10000000);
var lt = Get<LayerTable>(Database.LayerTableId);
var template = Args.GetString("templateLayer");
if (!lt.Has(template)) throw new ArgumentException($"Template layer '{template}' not found.");
ObjectId StyleByName(Autodesk.Civil.DatabaseServices.Styles.StyleCollectionBase coll, string name) { foreach (ObjectId id in coll) if (Get<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(id).Name == name) return id; throw new ArgumentException($"Style '{name}' not found."); }
var pointStyle = StyleByName(CivilDoc.Styles.PointStyles, Args.GetString("pointStyle"));
var labelStyle = StyleByName(CivilDoc.Styles.LabelStyles.PointLabelStyles.LabelStyles, Args.GetString("labelStyle"));

var taken = new HashSet<uint>();   // numbers assigned in this run
bool Free(uint n) => !taken.Contains(n) && !CivilDoc.CogoPoints.Contains(n);
var plans = new List<(string crew, string file, string layer, string group, List<(uint orig, uint num, double n, double e, double z, string d)> pts)>();
foreach (var spec in Args.Get<List<Dictionary<string, string>>>("files")) {
  var crew = spec["crew"]; var file = spec["path"];
  var group = $"{date} {crew}"; var layer = $"{prefix}{date} {crew}";
  if (CivilDoc.PointGroups.Contains(group)) throw new InvalidOperationException($"Point group '{group}' already exists - was this day imported already?");
  var pts = new List<(uint, uint, double, double, double, string)>();
  foreach (var raw in System.IO.File.ReadAllLines(file)) {
    if (raw.Trim().Length == 0) continue;
    var f = raw.Split(',');
    if (f.Length < 4) throw new FormatException($"{System.IO.Path.GetFileName(file)}: bad line '{raw}'");
    uint orig = uint.Parse(f[0].Trim(), CultureInfo.InvariantCulture);
    double n = double.Parse(f[1], CultureInfo.InvariantCulture), e = double.Parse(f[2], CultureInfo.InvariantCulture), z = double.Parse(f[3], CultureInfo.InvariantCulture);
    var d = f.Length > 4 ? string.Join(",", f.Skip(4)) : "";
    uint num = orig; uint k = 0;
    while (!Free(num)) { k++; num = orig + k * step; }
    taken.Add(num);
    pts.Add((orig, num, n, e, z, d));
  }
  plans.Add((crew, System.IO.Path.GetFileName(file), layer, group, pts));
}
string Ranges(IEnumerable<uint> nums) {
  var t = new List<string>(); uint? s0 = null, prev = null;
  foreach (var x in nums.OrderBy(v => v)) { if (s0 == null) { s0 = prev = x; continue; } if (x == prev + 1) { prev = x; continue; } t.Add(s0 == prev ? $"{s0}" : $"{s0}-{prev}"); s0 = prev = x; }
  if (s0 != null) t.Add(s0 == prev ? $"{s0}" : $"{s0}-{prev}");
  return string.Join(",", t);
}
var summary = plans.Select(p => new {
  p.file, p.crew, p.layer, layerExists = lt.Has(p.layer), p.group, points = p.pts.Count,
  kept = p.pts.Count(x => x.orig == x.num),
  offsets = p.pts.Where(x => x.orig != x.num).GroupBy(x => (x.num - x.orig) / step).Select(g => $"+{g.Key * step}: {g.Count()}"),
  include = Ranges(p.pts.Select(x => x.num)),
}).ToList();
if (dryRun) return new { dryRun, date, summary };

var made = new List<ObjectId>();
string lastLayer = null;
foreach (var p in plans) {
  // layer: copy of the template layer
  if (!lt.Has(p.layer)) {
    var tl = Get<LayerTableRecord>(lt[template]);
    var nl = (LayerTableRecord)tl.Clone(); nl.Name = p.layer;
    var ltw = Get<LayerTable>(Database.LayerTableId, OpenMode.ForWrite); ltw.Add(nl); Transaction.AddNewlyCreatedDBObject(nl, true);
  }
  foreach (var x in p.pts) {
    var id = CivilDoc.CogoPoints.Add(new Point3d(x.e, x.n, x.z), true);
    var cp = Get<CogoPoint>(id, OpenMode.ForWrite);
    cp.PointNumber = x.num;
    cp.RawDescription = x.d;
    cp.Layer = p.layer; cp.StyleId = pointStyle; cp.LabelStyleId = labelStyle;
    made.Add(id);
  }
  // point group with exactly these numbers, at the top of the list
  var gid = CivilDoc.PointGroups.Add(p.group);
  var g = Get<PointGroup>(gid, OpenMode.ForWrite);
  var q = new StandardPointGroupQuery { IncludeNumbers = Ranges(p.pts.Select(x => x.num)) };
  g.SetQuery(q); g.Update();
  var order = CivilDoc.PointGroups.DrawOrder;
  var newOrder = new ObjectIdCollection { gid };
  foreach (ObjectId o in order) if (o != gid) newOrder.Add(o);
  CivilDoc.PointGroups.DrawOrder = newOrder;
  lastLayer = p.layer;
}
if (lastLayer != null) Database.Clayer = lt[lastLayer];
FocusOn(made);
return new { dryRun, created = made.Count, summary, currentLayer = lastLayer };
```
