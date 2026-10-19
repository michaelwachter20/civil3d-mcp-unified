---
name: crossing_feature_alignment
category: alignments
description: Make a siteless straight alignment along a feature that crosses the project centerline (e.g. a ditch) between two survey points, find the exact crossing (stations on both), and add a project-centerline sample line that runs exactly along the feature (end -> crossing -> start) for a longitudinal section view.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: startPoint
    type: integer
    required: true
    description: Point number at the feature's start (e.g. the middle shot at one end)
  - name: endPoint
    type: integer
    required: true
    description: Point number at the feature's other end
  - name: mainAlignment
    type: string
    required: true
    description: Project centerline alignment name
  - name: mainSampleLineGroup
    type: string
    required: true
    description: Handle of the project centerline's sample line group (gets the along-feature sample line)
  - name: name
    type: string
    required: true
    description: New alignment name
  - name: layer
    type: string
    required: true
    description: Layer for the alignment (created with layerColor if missing - ask first)
  - name: layerColor
    type: integer
    required: false
    description: ACI color for a new layer (default 7)
  - name: alignmentStyle
    type: string
    required: true
    description: Alignment style (e.g. a no-display style)
  - name: labelSet
    type: string
    required: true
    description: Alignment label set (e.g. an empty label set)
  - name: dryRun
    type: boolean
    required: false
    description: Report start/end/crossing and offsets only (default true)
---

> Verified on Civil 3D 2027.

## When to use
A feature (farm ditch, canal, crossing pipe) runs roughly perpendicular to the project centerline and needs its own
cross sections plus a section along it. The project centerline's sample lines are perpendicular to the centerline,
so a sample line at the crossing station runs along the feature: give it the feature's end, crossing and start as
vertices and its section view becomes the "along the feature" section.

## Notes
- The crossing is found by bisection on the feature line where the project-centerline offset changes sign; the result
  gives both stations (project and feature) and the N/E for plan callouts.
- Put the sample line on the same layer/style as the other sample lines afterwards if the group default differs.
- If the feature is longer than one sheet window, split the along-feature section into several views by offset range.
- Sample line groups: turn sampling on before creating sample lines (see `SetSectionSources`).

## Script
```csharp
// Farm ditch alignment (siteless single line between two survey points) plus a project-centerline sample line
// that runs exactly along it (end -> crossing -> start), like the go-by.
var dryRun = Args.GetBool("dryRun", true);
var startP = Get<CogoPoint>(CivilDoc.CogoPoints.GetPointByPointNumber((uint)Args.GetInt("startPoint"))).Location;
var endP = Get<CogoPoint>(CivilDoc.CogoPoints.GetPointByPointNumber((uint)Args.GetInt("endPoint"))).Location;
Alignment main = null; foreach (ObjectId aid in CivilDoc.GetAlignmentIds()) { var a = Get<Alignment>(aid); if (a.Name == Args.GetString("mainAlignment")) main = a; }
var group = Get<SampleLineGroup>(Database.GetObjectId(false, new Handle(Convert.ToInt64(Args.GetString("mainSampleLineGroup"), 16)), 0));
var a2 = new Point2d(startP.X, startP.Y); var b2 = new Point2d(endP.X, endP.Y);
double Off(Point2d p) { double st = 0, off = 0; main.StationOffset(p.X, p.Y, ref st, ref off); return off; }
// crossing: bisection on the ditch line where the project-centerline offset is zero
double lo = 0, hi = 1; if (Math.Sign(Off(a2)) == Math.Sign(Off(b2))) throw new InvalidOperationException("Ditch line does not cross the project centerline.");
for (int i = 0; i < 60; i++) { double m = (lo + hi) / 2; if (Math.Sign(Off(a2 + (b2 - a2) * m)) == Math.Sign(Off(a2))) lo = m; else hi = m; }
var cross = a2 + (b2 - a2) * lo;
double cst = 0, coff = 0; main.StationOffset(cross.X, cross.Y, ref cst, ref coff);
double ditchSta = cross.GetDistanceTo(a2);
string StaText(double s) => $"{Math.Floor(s / 100):0}+{s - Math.Floor(s / 100) * 100:00.00}";
var info = new { start = $"{a2.X:0.000},{a2.Y:0.000}", end = $"{b2.X:0.000},{b2.Y:0.000}", length = Math.Round(a2.GetDistanceTo(b2), 2),
  crossing = $"{cross.X:0.000},{cross.Y:0.000}", crossingProjectStation = StaText(cst), crossingDitchStation = StaText(ditchSta),
  startOffset = Math.Round(Off(a2), 2), endOffset = Math.Round(Off(b2), 2) };
if (dryRun) return info;

var lt = Get<LayerTable>(Database.LayerTableId, OpenMode.ForWrite);
var layerName = Args.GetString("layer");
ObjectId layerId;
if (lt.Has(layerName)) layerId = lt[layerName];
else { var ltr = new LayerTableRecord { Name = layerName, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)Args.GetInt("layerColor", 7)) }; layerId = lt.Add(ltr); Transaction.AddNewlyCreatedDBObject(ltr, true); }
ObjectId styleId = ObjectId.Null; foreach (ObjectId id in CivilDoc.Styles.AlignmentStyles) if (Get<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(id).Name == Args.GetString("alignmentStyle")) styleId = id;
ObjectId labelSetId = ObjectId.Null; foreach (ObjectId id in CivilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles) { var n = Get<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(id).Name; if (n == Args.GetString("labelSet")) labelSetId = id; }
if (styleId.IsNull || labelSetId.IsNull) throw new ArgumentException("Alignment style or label set not found.");
var alId = Alignment.Create(CivilDoc, Args.GetString("name"), ObjectId.Null, layerId, styleId, labelSetId);
var al = Get<Alignment>(alId, OpenMode.ForWrite);
al.Entities.AddFixedLine(new Point3d(a2.X, a2.Y, 0), new Point3d(b2.X, b2.Y, 0));
// sample line on the project centerline along the ditch: end -> crossing -> start
var pts = new Point2dCollection { b2, cross, a2 };
var slId = SampleLine.Create(StaText(cst), group.ObjectId, pts);
var sl = Get<SampleLine>(slId, OpenMode.ForWrite);
FocusOn(new Point3d(Math.Min(a2.X, b2.X) - 60, Math.Min(a2.Y, b2.Y) - 30, 0), new Point3d(Math.Max(a2.X, b2.X) + 60, Math.Max(a2.Y, b2.Y) + 30, 0));
return new { info, alignment = al.Name, alignmentLength = Math.Round(al.Length, 2), sampleLine = sl.Name, sampleLineStation = Math.Round(sl.Station, 2), sl.Layer, slStyle = Get<Autodesk.Civil.DatabaseServices.Styles.StyleBase>(sl.StyleId).Name };
```
