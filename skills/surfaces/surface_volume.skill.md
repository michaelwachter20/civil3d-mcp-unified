---
name: surface_volume
category: surfaces
description: Compute cut/fill volumes between two TIN surfaces
requires_write: true
parameters:
  - name: baseSurface
    type: string
    required: true
    description: Name of the base (existing ground) surface
  - name: comparisonSurface
    type: string
    required: true
    description: Name of the comparison (proposed) surface
---

## Code Template

```csharp
TinSurface baseSurf = null, compSurf = null;

foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var s = Transaction.GetObject(id, OpenMode.ForRead);
    if (s is TinSurface tin)
    {
        if (tin.Name.Equals(Args.GetString("baseSurface"), StringComparison.OrdinalIgnoreCase))
            baseSurf = tin;
        if (tin.Name.Equals(Args.GetString("comparisonSurface"), StringComparison.OrdinalIgnoreCase))
            compSurf = tin;
    }
}

if (baseSurf == null) return new { error = "Base surface not found" };
if (compSurf == null) return new { error = "Comparison surface not found" };

// Exact volumes come from a TIN volume surface. Civil 3D makes one only under a document lock, so this needs
// civil3d_execute; the inner transaction is aborted, so the drawing keeps nothing.
double cut, fill, net;
using (var temporary = Database.TransactionManager.StartTransaction())
{
    var volumeId = TinVolumeSurface.Create($"TMP_VOLUME_{Guid.NewGuid():N}", baseSurf.ObjectId, compSurf.ObjectId);
    var volumeProps = ((TinVolumeSurface)temporary.GetObject(volumeId, OpenMode.ForRead)).GetVolumeProperties();
    cut = volumeProps.UnadjustedCutVolume;
    fill = volumeProps.UnadjustedFillVolume;
    net = volumeProps.UnadjustedNetVolume;
    temporary.Abort();
}

return new {
    baseSurface = baseSurf.Name,
    comparisonSurface = compSurf.Name,
    cutVolume = cut,
    fillVolume = fill,
    netVolume = net
};
```

## Usage Notes
- Both surfaces must be TIN surfaces
- civil3d_surface action=compute_volume gives the same numbers without a script; use it unless this needs adapting
- Net volume positive = more fill than cut
- Surfaces must overlap for meaningful results
- Units depend on drawing settings (typically m³ or ft³)
