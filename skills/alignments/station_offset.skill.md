---
name: station_offset
category: alignments
description: Convert between station+offset and X,Y coordinates along an alignment
requires_write: false
parameters:
  - name: alignmentName
    type: string
    required: true
  - name: station
    type: double
    required: false
    description: Station value (for station-to-point conversion)
  - name: offset
    type: double
    required: false
    description: Offset from centerline (for station-to-point)
  - name: x
    type: double
    required: false
    description: X coordinate (for point-to-station conversion)
  - name: y
    type: double
    required: false
    description: Y coordinate (for point-to-station conversion)
---

## Code Template — Station to Point

```csharp
Alignment alignment = null;
foreach (ObjectId id in CivilDoc.GetAlignmentIds())
{
    var a = Transaction.GetObject(id, OpenMode.ForRead) as Alignment;
    if (a != null && a.Name.Equals(Args.GetString("alignmentName"), StringComparison.OrdinalIgnoreCase))
    { alignment = a; break; }
}
if (alignment == null) return new { error = "Alignment not found" };

double station = Args.GetDouble("station");
double offset = Args.GetDouble("offset", 0.0);
double x = 0, y = 0;
alignment.PointLocation(station, offset, ref x, ref y);

return new { alignmentName = alignment.Name, station, offset, x, y };
```

## Code Template — Point to Station

```csharp
Alignment alignment = null;
foreach (ObjectId id in CivilDoc.GetAlignmentIds())
{
    var a = Transaction.GetObject(id, OpenMode.ForRead) as Alignment;
    if (a != null && a.Name.Equals(Args.GetString("alignmentName"), StringComparison.OrdinalIgnoreCase))
    { alignment = a; break; }
}
if (alignment == null) return new { error = "Alignment not found" };

double x = Args.GetDouble("x"), y = Args.GetDouble("y");
double station = 0, offset = 0;
alignment.StationOffset(x, y, ref station, ref offset);

return new { alignmentName = alignment.Name, x, y, station, offset };
```
