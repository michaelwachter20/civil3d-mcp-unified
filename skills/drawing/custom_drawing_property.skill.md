---
name: custom_drawing_property
category: drawing
description: Read, set or add a custom drawing property (DWGPROPS > Custom, e.g. a segment title used by title-block fields), then get the fields that use it to show the new value.
requires_write: true
civil3d_versions: 2027
parameters:
  - name: name
    type: string
    required: true
    description: Custom property name
  - name: value
    type: string
    required: true
    description: New value (the property is added if missing)
---

> Verified on Civil 3D 2027.

## Notes
- Read: `Database.SummaryInfo` (Title, Subject, Author, ... and `CustomProperties` enumerator).
- Write: `new DatabaseSummaryInfoBuilder(Database.SummaryInfo)`, set `CustomPropertyTable[name]`, assign
  `Database.SummaryInfo = builder.ToDatabaseSummaryInfo()`.
- Title blocks reference it with fields like `\AcVar CustomDP.<name>` inside block definitions (search block table
  records for objects with `HasFields`). `Field.Evaluate()` from the API does not refresh the cached text; a save
  (FIELDEVAL includes save) does, and REGENALL refreshes the display.
- After a save-as, check this property: it often still names the previous job.

## Script
```csharp
// Set (or add) a custom drawing property (DWGPROPS > Custom).
var b = new DatabaseSummaryInfoBuilder(Database.SummaryInfo);
var key = Args.GetString("name"); var before = b.CustomPropertyTable.Contains(key) ? b.CustomPropertyTable[key]?.ToString() : null;
b.CustomPropertyTable[key] = Args.GetString("value");
Database.SummaryInfo = b.ToDatabaseSummaryInfo();
var after = new Dictionary<string, string>(); var e = Database.SummaryInfo.CustomProperties; while (e.MoveNext()) after[e.Key.ToString()] = e.Value?.ToString();
return new { before, after };
```
