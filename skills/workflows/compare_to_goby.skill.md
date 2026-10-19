---
name: compare_to_goby
category: workflows
description: Compare the active drawing with a go-by (a finished example drawing) to find what still needs to be done - layouts, point groups, layers, surfaces, alignments, styles, and what is on chosen layers
requires_write: false
civil3d_versions: 2027
parameters:
  - name: goby
    type: string
    required: true
    description: Go-by drawing, by open-tab name ("Site-GoBy") or full .dwg path
  - name: layerPattern
    type: string
    required: false
    description: Optional regex; lists what is on matching layers in both drawings (e.g. "PERIMETER|LIMIT")
---

> Verified on Civil 3D 2027.

## When to use

The user gives you a drawing to work on plus a "go-by": a finished drawing of the same kind of job that
shows what the result should look like. Run this first, report the differences, and agree with the user
on what to do before changing anything.

## Rules

- The go-by is read only. `Reference(...)` rolls back anything done to it; never switch to it to edit it.
- All changes go to the active drawing (the one being worked on), each as its own approved, undoable step.
- Keep the work drawing active. Use `civil3d_drawing action=list_open` to see what is open and
  `action=switch` only if the user asks to work on another tab.
- Expect differences that are just the job's own data (dated survey point groups and their layers,
  point counts). Separate those from structural differences (missing layouts, surfaces, alignments,
  layers, point groups, styles) when you report.
- Ask the user where things are in the work drawing (have them select it, `Editor.SelectImplied()`)
  instead of guessing which objects match the go-by.

## Script (civil3d_query)

```csharp
var g = Reference(Args.GetString("goby"));
var layerPattern = Args.GetString("layerPattern", "");
Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> mainGet = id => Transaction.GetObject(id, OpenMode.ForRead);
Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> gGet = id => g.Get<Autodesk.AutoCAD.DatabaseServices.DBObject>(id);

List<string> Sym(ObjectId table, Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get) =>
  ((SymbolTable)get(table)).Cast<ObjectId>().Select(id => ((SymbolTableRecord)get(id)).Name).Where(n => !n.StartsWith("*")).ToList();
List<string> Layouts(Database db, Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get)
{
  var l = new List<(int, string)>();
  foreach (DBDictionaryEntry e in (DBDictionary)get(db.LayoutDictionaryId))
    if (e.Key != "Model") l.Add((((Layout)get(e.Value)).TabOrder, e.Key));
  return l.OrderBy(x => x.Item1).Select(x => x.Item2).ToList();
}
List<string> Groups(CivilDocument c, Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get) =>
  c.PointGroups.Cast<ObjectId>().Select(id => { var p = (PointGroup)get(id); return $"{p.Name} ({p.PointsCount})"; }).ToList();
List<string> Named(IEnumerable<ObjectId> ids, Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get) =>
  ids.Select(id => get(id) switch {
    Autodesk.Civil.DatabaseServices.Entity ce => ce.Name,
    Autodesk.Civil.DatabaseServices.Styles.StyleBase sb => sb.Name,
    var o => o.GetType().Name }).ToList();
object Diff(List<string> main, List<string> goBy, bool lists = false)
{
  string Key(string s) => System.Text.RegularExpressions.Regex.Replace(s, @" \(\d+\)$", "");
  var m = main.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var gb = goBy.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return new {
    main = main.Count, goBy = goBy.Count,
    onlyInGoBy = goBy.Where(x => !m.Contains(Key(x))).ToList(),
    onlyInMain = main.Where(x => !gb.Contains(Key(x))).ToList(),
    mainList = lists ? main : null, goByList = lists ? goBy : null };
}
Dictionary<string, Dictionary<string, int>> OnLayers(BlockTableRecord ms, Func<ObjectId, Autodesk.AutoCAD.DatabaseServices.DBObject> get)
{
  var r = new Dictionary<string, Dictionary<string, int>>();
  if (layerPattern.Length == 0) return r;
  foreach (ObjectId id in ms)
  {
    CancellationToken.ThrowIfCancellationRequested();
    var e = (Autodesk.AutoCAD.DatabaseServices.Entity)get(id);
    if (!System.Text.RegularExpressions.Regex.IsMatch(e.Layer, layerPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
    if (!r.TryGetValue(e.Layer, out var t)) r[e.Layer] = t = new Dictionary<string, int>();
    var n = id.ObjectClass.DxfName;
    t[n] = t.TryGetValue(n, out var c) ? c + 1 : 1;
  }
  return r;
}

return new {
  goBy = g.Name,
  layouts = Diff(Layouts(Database, mainGet), Layouts(g.Database, gGet), true),
  pointGroups = Diff(Groups(CivilDoc, mainGet), Groups(g.CivilDoc, gGet), true),
  surfaces = Diff(Named(CivilDoc.GetSurfaceIds().Cast<ObjectId>(), mainGet), Named(g.CivilDoc.GetSurfaceIds().Cast<ObjectId>(), gGet), true),
  alignments = Diff(Named(CivilDoc.GetAlignmentIds().Cast<ObjectId>(), mainGet), Named(g.CivilDoc.GetAlignmentIds().Cast<ObjectId>(), gGet), true),
  layers = Diff(Sym(Database.LayerTableId, mainGet), Sym(g.Database.LayerTableId, gGet)),
  blocks = Diff(Sym(Database.BlockTableId, mainGet), Sym(g.Database.BlockTableId, gGet)),
  textStyles = Diff(Sym(Database.TextStyleTableId, mainGet), Sym(g.Database.TextStyleTableId, gGet)),
  pointLabelStyles = Diff(Named(CivilDoc.Styles.LabelStyles.PointLabelStyles.LabelStyles.Cast<ObjectId>(), mainGet),
                          Named(g.CivilDoc.Styles.LabelStyles.PointLabelStyles.LabelStyles.Cast<ObjectId>(), gGet)),
  pointStyles = Diff(Named(CivilDoc.Styles.PointStyles.Cast<ObjectId>(), mainGet), Named(g.CivilDoc.Styles.PointStyles.Cast<ObjectId>(), gGet)),
  onLayersMain = OnLayers(ModelSpace, mainGet),
  onLayersGoBy = OnLayers(g.ModelSpace, gGet),
};
```

## Pitfalls

- `Reference(name)` matches open tabs by file name, with or without `.dwg`. A drawing that isn't open needs
  its full path; it is read in the background and closed when the query ends.
- Read-only queries can't start their own transactions; read the go-by only through `Reference(...).Get<T>(id)`.
- `CivilDocument.GetSiteIds()` on a background drawing returned the active drawing's count; don't rely on it.
- Point group names often carry dates or crew initials; compare the undated groups (display/surface/filter
  groups) for structure, and treat dated ones as the job's own data.
