---
name: reset_declutter_run
category: workflows
description: Revert a declutter_point_labels run without UNDO - reset the labels it placed, put hand-dragged labels back at their recorded positions, and remove the points it added to the hide group.
requires_write: true
parameters:
  - name: moved
    type: string[]
    required: true
    description: movedNums from the run result
  - name: hidden
    type: string[]
    required: true
    description: hiddenNums from the run result
  - name: restore
    type: json
    required: false
    description: restore list from the run result (original positions of hand-dragged labels it moved)
  - name: hideGroup
    type: string
    required: true
    description: Point group the run added points to
---

## When to use
After a declutter run whose result the user rejects, when UNDO is no longer available. Pass the run result's
`movedNums` as `moved`, `hiddenNums` as `hidden`, and its `restore` list (original `LabelLocation` of each
hand-dragged label the run moved) as `restore`. Labels in `restore` go back to the user's own spot; all other
moved labels are reset to their style default.

Always keep a copy of `restore` from every run: once a hand-dragged label is reset, its original position is gone.
Restoring uses reset-then-set, because a new `LabelLocation` on a dragged label is applied relative to the old drag.

## Script
```csharp
// Revert a declutter run: reset the labels it moved and remove the points it added to the hide group. Labels only.
var moved = Args.GetStringList("moved").Select(uint.Parse).ToList();
var hiddenNums = new HashSet<string>(Args.GetStringList("hidden"));
int reset = 0, restored = 0, mismatched = 0;
var restore = Args.Raw("restore") as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
var restoreNums = new HashSet<uint>();
foreach (var node in restore) {
  var n = (uint)node["num"].GetValue<double>();
  restoreNums.Add(n);
  var pt = new Point3d(node["x"].GetValue<double>(), node["y"].GetValue<double>(), node["z"].GetValue<double>());
  var cpw = Get<CogoPoint>(CivilDoc.CogoPoints.GetPointByPointNumber(n), OpenMode.ForWrite);
  if (cpw.IsLabelDragged) cpw.ResetLabelLocation();   // a new location is applied relative to an existing drag
  cpw.LabelLocation = pt;                             // back to the user's own spot
  if (cpw.LabelLocation.DistanceTo(pt) > 0.01) mismatched++;
  restored++;
}
foreach (var n in moved.Where(n => !restoreNums.Contains(n))) {
  var id = CivilDoc.CogoPoints.GetPointByPointNumber(n);
  var p = Get<CogoPoint>(id);
  if (!p.IsLabelDragged) continue;
  Get<CogoPoint>(id, OpenMode.ForWrite).ResetLabelLocation();
  reset++;
}
var sf = CivilDoc.PointGroups.Cast<ObjectId>().Select(id => Get<PointGroup>(id)).First(g => g.Name == Args.GetString("hideGroup"));
var q = (StandardPointGroupQuery)sf.GetQuery();
var keep = (q.IncludeNumbers ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && !hiddenNums.Contains(s)).ToList();
q.IncludeNumbers = string.Join(",", keep);
var sfw = Get<PointGroup>(sf.ObjectId, OpenMode.ForWrite);
sfw.SetQuery(q);
sfw.Update();
return new { labelsReset = reset, handDraggedRestored = restored, restoreMismatches = mismatched, hideGroupNow = sfw.PointsCount, hideGroupInclude = q.IncludeNumbers };
```
