---
name: convert_drawing_from_goby
category: workflows
description: Convert a drawing (often a save-as of an earlier deliverable) to match a finished go-by drawing's methodology - read-only side-by-side summaries of layouts, viewports, frozen layers, sheet text, surfaces, point groups, alignments, sample lines, section views and model-space annotation, then plan, ask, and convert step by step.
requires_write: false
civil3d_versions: 2027
parameters:
  - name: drawing
    type: string
    required: false
    description: The go-by to summarize - an open drawing's name, or a full .dwg path (found as below); empty = the active drawing
  - name: part
    type: string
    required: true
    description: layouts | layers | civil | model
---

> Verified on Civil 3D 2027.

## When to use
The user has a work drawing (frequently a save-as of a previous job) and a completed go-by of the same kind, and wants
the work drawing converted "the way the go-by does it" ("look at C-101 and do what we did there"). The go-by is read
only: `Reference(name or path)` reads it without opening it (changes are always rolled back). Edit only the work
drawing.

## Finding the go-by the user names
1. **An open drawing first** (the user has usually opened what matters): `civil3d_drawing` action=list_open; a drawing
   whose name matches (with or without .dwg, or the same name without "rev N", dates, "current") is the go-by: pass its
   name as `drawing`.
2. Otherwise **its latest version in the job's database**: `civil3d_project_notes` action=latest (file = the name; near =
   the user's words about its folder, e.g. "phase 2 area b"). Pass `series[0].latest.fullPath` as `drawing`.
3. Say which file you used and when it was modified. When the result has several series, or `folders` > 1 with folders
   that look like different areas (phases, areas) rather than dated deliveries of one drawing, ask which. When an
   open drawing isn't the newest (`newerOnDrive`), use it and mention the newer one once.
4. A path outside the allowed folders (CIVIL3D_FILE_ROOTS / CIVIL3D_IMPORT_ROOTS) can't be read: tell the user to add
   the job folder there.

## How to run
1. Summarize both drawings with this script, each `part` for each drawing (save the JSON to files and diff them):
   - `layouts`: per layout - viewports (scale, twist, model window, frozen layers), every text/attribute, entity
     counts by type and layer. Sheets often share one model window and differ only in frozen layers.
   - `layers`: layer states. `civil`: surfaces and their definition (point groups, boundaries, edits), point groups
     and queries/overrides, alignments with sample line groups and section views. `model`: model-space text,
     multileaders, dimensions and blocks with position and content.
2. Work out which layouts are the real deliverables (dated notes, filled title blocks) and which are untouched template.
3. Look closer at the go-by's feature-specific objects (extra alignments, sample line groups, section-view grids and
   their titles, plan callouts) and note exact placements relative to their anchors (view corners, arrow tips).
4. Write the conversion plan, list what can't be inferred, and ask. Typical questions: data that exists only in the
   go-by (imported design surfaces), layers or styles the work drawing lacks, station/spacing rules, what to delete.
5. Convert in small, saved steps (QSAVE after each), checking each with a read-only query.

## Gotchas
- Never read `SampleLineGroup.GetSectionSources()` (crashes on a read-opened group; blocked) - use `SectionSources`.
- A boundary or edit that was right for the earlier deliverable may be wrong for the new one (e.g. a hide boundary
  over the very feature the new drawing reports). Check surface definitions against the go-by.
- Copy styles only when asked; when a go-by style is just a variant (e.g. a section view style with the title
  hidden), recreate it with `CopyAsSibling` plus the one changed setting.

## Script
```csharp
// Read-only summary of a drawing (active, another open drawing by name, or a drawing on disk by full path) for
// comparison with a go-by.
// part=layouts|layers|civil|model
var which = Args.GetString("drawing", "");
var part = Args.GetString("part");
Database db; Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get; CivilDocument cd;
if (which == "") { db = Database; get = id => Get<Autodesk.AutoCAD.DatabaseServices.DBObject>(id); cd = CivilDoc; }
else { var r = Reference(which); db = r.Database; get = id => r.Get<Autodesk.AutoCAD.DatabaseServices.DBObject>(id); cd = r.CivilDoc; }
T G<T>(ObjectId id) where T : class => get(id) as T;
string Txt(Autodesk.AutoCAD.DatabaseServices.Entity e) => e is MText mt ? mt.Text : e is DBText dt ? dt.TextString : e is MLeader ml && ml.ContentType == ContentType.MTextContent ? ml.MText.Text : null;
string Flat(string s) => s == null ? null : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
var lt = G<LayerTable>(db.LayerTableId);
string LName(ObjectId id) => G<LayerTableRecord>(id).Name;

if (part == "layouts") {
  var res = new List<object>();
  foreach (DBDictionaryEntry e in G<DBDictionary>(db.LayoutDictionaryId)) {
    var lo = G<Layout>(e.Value); if (lo.ModelType) continue;
    var btr = G<BlockTableRecord>(lo.BlockTableRecordId);
    var vps = new List<object>(); var texts = new List<string>(); var counts = new Dictionary<string, int>();
    foreach (ObjectId id in btr) {
      var ent = G<Autodesk.AutoCAD.DatabaseServices.Entity>(id); if (ent == null) continue;
      var k = id.ObjectClass.DxfName + "|" + ent.Layer; counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
      if (ent is Viewport v && v.Number != 1) { double mpp = 1 / v.CustomScale; vps.Add(new { scale = Math.Round(mpp, 2), twistDeg = Math.Round(v.TwistAngle * 180 / Math.PI, 2), center = $"{v.ViewTarget.X + v.ViewCenter.X:0.0},{v.ViewTarget.Y + v.ViewCenter.Y:0.0}", size = $"{v.Width * mpp:0.0}x{v.Height * mpp:0.0}", frozen = v.GetFrozenLayers().Cast<ObjectId>().Select(LName).OrderBy(x => x).ToList() }); }
      var t = Txt(ent); if (t != null) texts.Add($"[{ent.Layer}] {Flat(t)}");
      if (ent is BlockReference br) foreach (ObjectId aid in br.AttributeCollection) { var a = G<AttributeReference>(aid); if (!string.IsNullOrWhiteSpace(a.TextString)) texts.Add($"[ATT {br.Name}.{a.Tag}] {Flat(a.TextString)}"); }
    }
    res.Add(new { lo.LayoutName, lo.TabOrder, viewports = vps, texts, counts = counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}") });
  }
  return res;
}
if (part == "layers") {
  var res = new List<string>();
  foreach (ObjectId id in lt) { var l = G<LayerTableRecord>(id); res.Add($"{l.Name}|c{l.Color.ColorIndex}|{(l.IsFrozen ? "F" : "")}{(l.IsOff ? "O" : "")}{(l.IsPlottable ? "" : "NP")}"); }
  return res.OrderBy(x => x);
}
if (part == "civil") {
  var surfaces = new List<object>();
  foreach (ObjectId sid in cd.GetSurfaceIds()) {
    var s = G<Autodesk.Civil.DatabaseServices.Surface>(sid);
    var ops = new List<string>();
    if (s is TinSurface ts) for (int i = 0; i < ts.Operations.Count; i++) {
      var op = ts.Operations[i];
      if (op is SurfaceOperationAddPointGroup pg) ops.Add("pointgroup " + G<PointGroup>(pg.PointGroupId)?.Name);
      else if (op is SurfaceOperationAddBoundary) ops.Add("boundary");
      else if (op is SurfaceOperationSwapEdge) { if (ops.Count == 0 || !ops[ops.Count - 1].StartsWith("swaps")) ops.Add("swaps 1"); else ops[ops.Count - 1] = "swaps " + (int.Parse(ops[ops.Count - 1].Substring(6)) + 1); }
      else ops.Add(op.GetType().Name.Replace("SurfaceOperation", ""));
    }
    surfaces.Add(new { s.Name, s.Layer, style = G<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(s.StyleId)?.Name, ops = string.Join("; ", ops) });
  }
  var pgs = new List<object>();
  foreach (ObjectId gid in cd.PointGroups) {
    var g = G<PointGroup>(gid);
    var q = g.GetQuery();
    string qs = q is StandardPointGroupQuery sq ? $"incNum={sq.IncludeNumbers} excNum={(sq.ExcludeNumbers ?? "").Length}ch incRaw={sq.IncludeRawDescriptions} excRaw={sq.ExcludeRawDescriptions} incFull={sq.IncludeFullDescriptions} excFull={sq.ExcludeFullDescriptions}" : q?.GetType().Name;
    pgs.Add(new { g.Name, n = g.PointsCount, ovPS = g.IsPointStyleOverridden ? (g.PointStyleId.IsNull ? "<none>" : G<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(g.PointStyleId)?.Name) : null, ovLS = g.IsPointLabelStyleOverridden ? (g.PointLabelStyleId.IsNull ? "<none>" : G<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(g.PointLabelStyleId)?.Name) : null, q = qs });
  }
  var als = new List<object>();
  foreach (ObjectId aid in cd.GetAlignmentIds()) {
    var al = G<Alignment>(aid);
    var slg = new List<object>();
    foreach (ObjectId gid in al.GetSampleLineGroupIds()) {
      var g = G<SampleLineGroup>(gid);
      var lines = new List<string>();
      foreach (ObjectId sid in g.GetSampleLineIds()) { var sl = G<SampleLine>(sid); var vs = sl.GetSectionViewIds().Cast<ObjectId>().Select(v => G<SectionView>(v)).Select(v => $"{v.Name}@{v.Location.X:0.0},{v.Location.Y:0.0}"); lines.Add($"{sl.Name} st={sl.Station:0.00} views=[{string.Join(", ", vs)}]"); }
      slg.Add(new { g.Name, lines });
    }
    als.Add(new { al.Name, al.Layer, start = al.StartingStation, end = Math.Round(al.EndingStation, 2), profiles = al.GetProfileIds().Count, sampleLineGroups = slg });
  }
  return new { surfaces, pointGroups = pgs, alignments = als };
}
if (part == "model") {
  // model-space annotation: text/mleaders/dims/blocks by layer with content; polylines etc. counted by layer
  var counts = new Dictionary<string, int>(); var annos = new List<string>();
  foreach (ObjectId id in G<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(db))) {
    var ent = G<Autodesk.AutoCAD.DatabaseServices.Entity>(id); if (ent == null) continue;
    var dxf = id.ObjectClass.DxfName;
    if (dxf == "AECC_COGO_POINT") continue;
    var k = dxf + "|" + ent.Layer; counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
    var t = Txt(ent);
    Extents3d ex; string at = ""; try { ex = ent.GeometricExtents; at = $"{(ex.MinPoint.X + ex.MaxPoint.X) / 2:0},{(ex.MinPoint.Y + ex.MaxPoint.Y) / 2:0}"; } catch { }
    if (t != null) annos.Add($"{dxf} [{ent.Layer}] @{at} {Flat(t)}");
    else if (ent is BlockReference br) annos.Add($"BLOCK {br.Name} [{ent.Layer}] @{at}");
    else if (ent is Dimension dm) annos.Add($"DIM [{ent.Layer}] @{at} {Math.Round(dm.Measurement, 2)} {dm.DimensionText}");
  }
  return new { counts = counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}"), annos = annos.OrderBy(a => a) };
}
throw new ArgumentException("part?");
```
