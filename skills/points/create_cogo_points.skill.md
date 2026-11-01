---
name: create_cogo_points
category: points
description: Create COGO points from coordinates with optional descriptions
requires_write: true
parameters:
  - name: points
    type: array
    required: true
    description: Array of {easting, northing, elevation, description}
---

## Code Template

```csharp
var created = new List<object>();

// Civil 3D asks the user in a window before these are made.
foreach (var p in Args.Raw("points").AsArray())
{
    var id = CivilDoc.CogoPoints.Add(new Point3d(p["easting"].GetValue<double>(), p["northing"].GetValue<double>(), p["elevation"].GetValue<double>()), false);
    var point = Transaction.GetObject(id, OpenMode.ForWrite) as CogoPoint;
    point.RawDescription = p["description"]?.GetValue<string>() ?? "";
    created.Add(new { pointNumber = point.PointNumber, easting = point.Easting, northing = point.Northing });
}

return new { success = true, created };
```

## Usage Notes
- Set second parameter of Add() to false to auto-assign point numbers
- RawDescription triggers description key matching
- Points are added to the default _All Points group automatically
