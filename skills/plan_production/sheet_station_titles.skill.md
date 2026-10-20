---
name: sheet_station_titles
category: plan_production
description: Set each sheet's station-range subtitle to what its plan viewport actually shows (nearest foot), chained page to page, smaller station first - with a read-only plan that flags gaps, overlaps and odd pages for the user before anything changes.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: fromTab
    type: integer
    required: true
    description: First layout (tab order) to update
  - name: toTab
    type: integer
    required: true
    description: Last layout (tab order) to update
  - name: alignments
    type: json
    required: false
    description: '[{"name": "...", "prefix": "EQ"}, ...] alignments to read, in listing order, with an optional station prefix'
  - name: viewportLayer
    type: string
    required: false
    description: Layer of the plan viewport (default the largest viewport on each layout)
  - name: titles
    type: json
    required: false
    description: Apply mode - {layout name: reviewed subtitle text}
---
> Verified on Civil 3D 2027.

## When to use
A plan set has one layout per sheet with a station-range subtitle (e.g. "12+00 - 20+00") and a plan viewport along
an alignment. The user wants each subtitle to show what the viewport really shows, to the nearest foot, with each
page starting where the previous one ends. Suggested saved script name sheet-station-titles.

## Rules
- Station range = where the alignment enters and leaves the plan viewport (twisted viewports handled in DCS),
  rounded to the nearest foot; smaller station on the left, larger on the right.
- Chain the pages: a page starts at the previous page's (rounded) end.
- Stop and ask the user about anything the plan flags, before changing anything:
  - GAP: a stretch no viewport shows between two pages. Proposed: the page starts where its own viewport starts.
  - OVERLAP: the viewport starts before the previous page's end. Proposed: start at the previous page's end.
  - A page showing two alignments (e.g. the end of one and the start of the next), the alignment changing between
    pages, an alignment entering a viewport twice, or a subtitle that can't be found.
- Keep a station prefix the titles use (e.g. "EQ" stations on one alignment) via the `alignments` prefix.
- Change only the station text of the subtitle (formatting codes, position, layer stay). Pages outside the range the
  user named are not touched. Save after applying.

## How to run
1. Plan (civil3d_query, read-only): `fromTab`, `toTab`, optional `viewportLayer` (else the largest viewport on each
   layout), `alignments` `[{"name": "...", "prefix": "EQ"}, {"name": "..."}]` in the order they should be listed
   (default: all alignments, no prefix), optional `minRunLength` (ft, default 10: shorter clips at a viewport corner
   are ignored and reported).
2. Show the user old -> proposed for every page and every flag; check one page by hand against the user's example.
3. Apply (civil3d_execute) with `titles` = {layout name: reviewed text}. One undo step. QSAVE.

## Notes
- The subtitle is found as the one MText on the layout whose text starts with a station range
  (`[prefix]12+34 - [prefix]56+78`); a layout with none or several is reported, never guessed.
- Stations are the alignment's raw stations; if an alignment has station equations, check the titles use the same.
- Rerunning the plan after applying should give `same = true` on every page (handy as a check).

## Script
```csharp
// sheet-station-titles: station range in each sheet's subtitle = what the plan viewport actually shows, chained
// page to page (start = previous page's end). Plan mode (no "titles" arg, read-only): proposes a title per layout
// and flags gaps, overlaps, reversed titles and pages that need a look. Apply mode ("titles": {layout: text}, the
// reviewed list): replaces only the station text of each subtitle, keeping its formatting codes.
var dict = Get<DBDictionary>(Database.LayoutDictionaryId);
int from = Args.Has("fromTab") ? Args.GetInt("fromTab") : 1, to = Args.Has("toTab") ? Args.GetInt("toTab") : 9999;
var vpLayer = Args.Has("viewportLayer") ? Args.GetString("viewportLayer") : null;
var rx = new System.Text.RegularExpressions.Regex(@"^\s*[A-Z]*\d+\+\d\d(\.\d+)?\s*-\s*[A-Z]*\d+\+\d\d");
var layouts = new List<Layout>();
foreach (DBDictionaryEntry en in dict) { var l = Get<Layout>(en.Value); if (!l.ModelType && l.TabOrder >= from && l.TabOrder <= to) layouts.Add(l); }
layouts = layouts.OrderBy(l => l.TabOrder).ToList();
MText Subtitle(Layout l, List<string> notes) {
  var subs = new List<MText>();
  foreach (ObjectId id in Get<BlockTableRecord>(l.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is MText m && rx.IsMatch(m.Text)) subs.Add(m);
  if (subs.Count != 1) { notes.Add($"{subs.Count} station subtitles found"); return null; }
  return subs[0];
}

if (Args.Has("titles")) {   // apply
  var map = Args.Get<Dictionary<string, string>>("titles"); var rows = new List<string>();
  foreach (var kv in map) {
    var notes = new List<string>(); var mt = Subtitle(Get<Layout>(dict.GetAt(kv.Key)), notes);
    if (mt == null) throw new InvalidOperationException($"{kv.Key}: {notes[0]} - nothing changed");
    var c = mt.Contents; int at = c.IndexOf(mt.Text, StringComparison.Ordinal);
    if (at < 0 || c.LastIndexOf(mt.Text, StringComparison.Ordinal) != at) throw new InvalidOperationException($"{kv.Key}: subtitle text not found once in its contents - nothing changed");
    Get<MText>(mt.ObjectId, OpenMode.ForWrite).Contents = c.Substring(0, at) + kv.Value + c.Substring(at + mt.Text.Length);
    rows.Add($"{kv.Key}: '{mt.Text}' -> '{kv.Value}'");
  }
  return new { changed = rows.Count, rows };
}

// plan: alignments in display order with an optional station prefix (e.g. "EQ")
var alSpec = Args.Has("alignments") ? Args.Get<List<Dictionary<string, string>>>("alignments") : null;
var als = new List<(Alignment a, string prefix)>();
foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (alSpec == null) als.Add((a, "")); }
if (alSpec != null) foreach (var s in alSpec) { Alignment hit = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == s["name"]) hit = a; } if (hit == null) throw new ArgumentException($"Alignment '{s["name"]}' not found"); als.Add((hit, s.TryGetValue("prefix", out var p) ? p : "")); }
double minRun = Args.Has("minRunLength") ? Args.GetDouble("minRunLength") : 10, tol = 1;
string Sta(double s) { var r = Math.Round(s, MidpointRounding.AwayFromZero); return $"{Math.Floor(r / 100):0}+{r % 100:00}"; }
var pages = new List<object>(); (int al, double end)? prev = null;
foreach (var l in layouts) {
  var notes = new List<string>(); var mt = Subtitle(l, notes);
  Viewport vp = null; double best = 0;
  foreach (ObjectId id in Get<BlockTableRecord>(l.BlockTableRecordId)) if (Get<Autodesk.AutoCAD.DatabaseServices.Entity>(id) is Viewport v && v.Number != 1 && (vpLayer == null ? true : v.Layer == vpLayer) && v.Width * v.Height > best) { vp = v; best = v.Width * v.Height; }
  if (mt == null || vp == null) { pages.Add(new { layout = l.LayoutName, old = mt?.Text, proposed = (string)null, notes = notes.Append(vp == null ? "no plan viewport" : "").Where(n => n != "") }); continue; }
  var toDcs = (Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) * Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) * Matrix3d.PlaneToWorld(vp.ViewDirection)).Inverse();
  double hw = vp.Width / vp.CustomScale / 2, hh = vp.Height / vp.CustomScale / 2; var c0 = vp.ViewCenter;
  bool Inside(Alignment a, double s) { double x = 0, y = 0; a.PointLocation(s, 0, ref x, ref y); var p = new Point3d(x, y, 0).TransformBy(toDcs); return Math.Abs(p.X - c0.X) <= hw && Math.Abs(p.Y - c0.Y) <= hh; }
  var runs = new List<(int al, double s0, double s1)>();
  for (int ai = 0; ai < als.Count; ai++) { var a = als[ai].a;
    double Edge(double sOut, double sIn) { for (int k = 0; k < 30; k++) { var mid = (sOut + sIn) / 2; if (Inside(a, mid)) sIn = mid; else sOut = mid; } return sIn; }
    bool inRun = false; double st0 = 0, last = a.StartingStation;
    for (double s = a.StartingStation; ; s = Math.Min(s + 1, a.EndingStation)) {
      bool ins = Inside(a, s);
      if (ins && !inRun) { st0 = s == a.StartingStation ? s : Edge(last, s); inRun = true; }
      if (!ins && inRun) { runs.Add((ai, st0, Edge(s, last))); inRun = false; }
      last = s; if (s >= a.EndingStation) break;
    }
    if (inRun) runs.Add((ai, st0, a.EndingStation)); }
  foreach (var r in runs.Where(r => r.s1 - r.s0 < minRun)) notes.Add($"ignored {r.s1 - r.s0:0.0} ft of {als[r.al].a.Name} at the viewport edge");
  runs = runs.Where(r => r.s1 - r.s0 >= minRun).OrderBy(r => r.al).ThenBy(r => r.s0).ToList();
  if (runs.Count == 0) { pages.Add(new { layout = l.LayoutName, old = mt.Text, proposed = (string)null, notes = notes.Append("no alignment in the viewport") }); prev = null; continue; }
  if (runs.Count(r => r.al == runs[0].al) > 1) notes.Add("alignment enters the viewport more than once - check");
  var first = runs[0]; double start = first.s0;
  if (prev.HasValue && prev.Value.al == first.al) {
    if (first.s0 > prev.Value.end + tol) notes.Add($"GAP: previous page ends {Sta(prev.Value.end)}, this viewport starts {Sta(first.s0)} (kept own start - ask)");
    else { if (first.s0 < prev.Value.end - tol) notes.Add($"OVERLAP: viewport starts {Sta(first.s0)}, previous page ends {Sta(prev.Value.end)} (used previous end - ask)"); start = prev.Value.end; }
  } else if (prev.HasValue) notes.Add("alignment changes from the previous page - check");
  var parts = runs.Select((r, i) => $"{als[r.al].prefix}{Sta(i == 0 ? start : r.s0)} - {als[r.al].prefix}{Sta(r.s1)}");
  var m0 = System.Text.RegularExpressions.Regex.Matches(mt.Text, @"\d+\+\d\d(\.\d+)?");
  if (m0.Count >= 2 && double.Parse(m0[0].Value.Replace("+", ""), CultureInfo.InvariantCulture) > double.Parse(m0[1].Value.Replace("+", ""), CultureInfo.InvariantCulture)) notes.Add("old title was larger-to-smaller (fixed)");
  var proposed = string.Join(", ", parts);
  pages.Add(new { layout = l.LayoutName, old = mt.Text, proposed, same = proposed == mt.Text, runs = runs.Select(r => $"{als[r.al].a.Name} {r.s0:0.00}-{r.s1:0.00}"), notes });
  prev = (runs.Last().al, Math.Round(runs.Last().s1, MidpointRounding.AwayFromZero));   // chain on the rounded end so the next start matches the text
}
return pages;
```
