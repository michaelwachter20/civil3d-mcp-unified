---
name: section_views_from_template
category: sections
description: Create the remaining section views of a sample line group by copying a view the user made (style, layer, offset and elevation ranges) and placing each one with its origin on the arrow tip of an anchor multileader already drawn in the sheet windows - sheet by sheet, top to bottom, in station order. Suggested saved script name section-views-from-template.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: templateView
    type: string
    required: true
    description: Handle of the section view the user made
  - name: sampleLineGroup
    type: string
    required: true
    description: Handle of the sample line group
  - name: anchorText
    type: string
    required: true
    description: Text the anchor multileaders contain; each one's arrow tip is a view origin (offset 0, elevation 0)
  - name: skipLines
    type: string[]
    required: false
    description: Sample line names to leave out (e.g. one outside the area)
  - name: columnToleranceFt
    type: number
    required: false
    description: Anchors within this x distance count as one sheet column (default 50)
  - name: dryRun
    type: boolean
    required: false
    description: Plan only, change nothing (default true)
---

> Verified on Civil 3D 2027.

## When to use
Section sheets are set up in advance: each layout viewport frames a model-space window that already holds the
annotation for several section views (notes, dimensions, corner ticks), and the user made the first view by hand
to show the settings. Make the rest so each lands exactly where the sheet expects it.

## How to read the setup (read-only, small steps)
1. Sample line groups, sample lines (name, station) and their section views (name, style, layer, location).
2. The template view's offset/elevation ranges (`IsOffsetRangeAutomatic`, `OffsetLeft/Right`,
   `IsElevationRangeAutomatic`, `ElevationMin/Max`) and bands (`Bands.GetBottomBandItems()/GetTopBandItems()`).
3. Section sheets: each viewport's model window (`ViewTarget + ViewCenter` +/- size / `CustomScale`). Sheets for
   different materials often frame the same window and differ only in frozen layers, so one set of views serves all.
4. Entities inside each window: find the multileader whose arrow tip is the template view's `Location`. Its text is
   the anchor; there is usually one per planned view. Spacing between views is often not uniform, so use the anchors,
   not a fixed pitch.
5. Sample line widths (`Alignment.StationOffset` on the vertices) to confirm the template's offset range fits all.

## Hard rules / gotchas
- Never call `SampleLineGroup.GetSectionSources()` on a group opened for read: it updates the group and crashes Civil 3D
  (`eNotOpenForWrite`). The plugin blocks the raw call; use the `SectionSources(groupId)` helper in write mode.
- `Section.LeftOffset` etc. throw "not supported by current Section" for surface sections - read names/styles/layers only.
- Sampling: a new sample line group samples nothing. Turn sources on with the `SetSectionSources(groupId, names, styleId)`
  helper (write mode). Through the API, sample lines that existed before their sources were sampled never get section data
  (unsample/resample does not fix it; the dialog does); the helper recreates such lines and their views by default.
  Simplest: sample first, then create the sample lines. Section point counts are only valid in a later run.
- `SectionView.Create(name, sampleLineId, location)` makes a view with automatic ranges and the default style; set
  `StyleId`, `Layer`, and turn off automatic ranges before setting them.
- The script checks the existing template view sits on its anchor, which proves the order (station <-> anchor) before
  anything is created.

## Script
```csharp
// Create section views for a sample line group, matching a template view the user made: same style, layer,
// offset and elevation ranges. Each view's origin (offset 0, elevation 0) goes on the arrow tip of an anchor
// multileader (e.g. a "CENTERLINE" note pointing at each view's origin) already drawn in the sheet windows, filled sheet by sheet
// (sheet windows left to right), top to bottom, in station order. Views that already exist are skipped.
var template = Get<SectionView>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("templateView"), 16)), 0));
var group = Get<SampleLineGroup>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("sampleLineGroup"), 16)), 0));
var anchorText = Args.GetString("anchorText");   // words the anchor multileader's text must contain, e.g. "CENTERLINE"
var skip = new HashSet<string>(Args.GetStringList("skipLines"));
var dryRun = Args.GetBool("dryRun", true);
string Flat(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim().ToUpperInvariant();

// anchors: arrow tips of the matching multileaders, ordered by column (x) then top to bottom
var anchors = new List<Point3d>();
var ms = Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Database));
foreach (ObjectId id in ms) {
  if (id.ObjectClass.DxfName != "MULTILEADER") continue;
  var ml = Get<MLeader>(id);
  if (ml.ContentType != ContentType.MTextContent || !Flat(ml.MText.Text).Contains(Flat(anchorText))) continue;
  foreach (int li in ml.GetLeaderIndexes()) foreach (int ll in ml.GetLeaderLineIndexes(li)) anchors.Add(ml.GetFirstVertex(ll));
}
double colTol = Args.GetDouble("columnToleranceFt", 50);
var ordered = anchors.GroupBy(a => Math.Round(a.X / colTol)).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(a => a.Y)).ToList();

var lines = group.GetSampleLineIds().Cast<ObjectId>().Select(id => Get<SampleLine>(id)).Where(sl => !skip.Contains(sl.Name)).OrderBy(sl => sl.Station).ToList();
var plan = new List<(SampleLine sl, Point3d at, bool exists)>();
for (int i = 0; i < lines.Count; i++) {
  if (i >= ordered.Count) throw new InvalidOperationException($"Only {ordered.Count} anchors for {lines.Count} sample lines.");
  plan.Add((lines[i], ordered[i], lines[i].GetSectionViewIds().Count > 0));
}
// a view that already exists must already sit on its anchor (sanity check of the order)
foreach (var p in plan.Where(p => p.exists)) {
  var v = Get<SectionView>(p.sl.GetSectionViewIds()[0]);
  if (v.Location.DistanceTo(new Point3d(p.at.X, p.at.Y, v.Location.Z)) > 0.5) throw new InvalidOperationException($"Existing view {v.Name} is not on anchor {p.at.X:0.0},{p.at.Y:0.0}; check the order.");
}
var created = new List<string>();
if (!dryRun) {
  foreach (var p in plan.Where(p => !p.exists)) {
    var vid = SectionView.Create(p.sl.Name, p.sl.ObjectId, new Point3d(p.at.X, p.at.Y, 0));
    var v = Get<SectionView>(vid, OpenMode.ForWrite);
    v.StyleId = template.StyleId;
    v.Layer = template.Layer;
    v.IsOffsetRangeAutomatic = false; v.OffsetLeft = template.OffsetLeft; v.OffsetRight = template.OffsetRight;
    v.IsElevationRangeAutomatic = false; v.ElevationMin = template.ElevationMin; v.ElevationMax = template.ElevationMax;
    created.Add($"{v.Name} at {v.Location.X:0.00},{v.Location.Y:0.00}");
  }
  var all = plan.Select(p => p.at).ToList();
  FocusOn(new Point3d(all.Min(a => a.X) - 150, all.Min(a => a.Y) - 15, 0), new Point3d(all.Max(a => a.X) + 150, all.Max(a => a.Y) + 30, 0));
}
return new { dryRun, anchors = ordered.Count, template = template.Name, plan = plan.Select(p => $"{p.sl.Name} -> {p.at.X:0.00},{p.at.Y:0.00}{(p.exists ? " (exists)" : "")}"), created };
```
