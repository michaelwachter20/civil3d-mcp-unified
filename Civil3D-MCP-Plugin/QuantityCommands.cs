// Quantity takeoff on the open drawing: surface-to-surface volumes, gravity and pressure pipe lengths, parcel areas,
// alignment lengths, point counts per point group, and a CSV export of several of these.
// JSON-RPC methods (CommandDispatcher.cs): qtySurfaceVolume, qtyPipeNetworkLengths, qtyPressureNetworkLengths,
// qtyParcelAreas, qtyAlignmentLengths, qtyPointCountByGroup, qtyExportToCsv and qtyEarthworkSummary serve
// civil3d_quantity_takeoff (src/tools/domains/quantityTakeoffDomain.ts). qtyCorridorVolumes and qtyMaterialListGet are
// dispatched but no TS domain calls them yet; they read the QTO material lists of the sample-line groups on a
// corridor's alignments (CostEstimationCommands reuses CorridorMaterialLists for corridor pay items).
// Nothing here changes the drawing. Reads go through CivilExecution.ReadAsync (main thread, no undo step). The volume
// methods (and qtyExportToCsv with surface volumes) use MeasureAsync: a temporary TinVolumeSurface needs a document
// lock, the transaction is never committed, and the lock leaves one blank undo step. qtyExportToCsv writes its .csv
// only through FileBoundary, quoted by DesignMath.
// A value the API can't give throws (GetRequiredEngineeringDouble) rather than being reported as 0.
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Quantity takeoff commands — aggregate Civil 3D object data for cost estimation.
/// Uses typed Civil 3D APIs wherever the managed API exposes the required data.
/// </summary>
public static class QuantityCommands
{
  // -------------------------------------------------------------------------
  // qtyCorridorVolumes
  // -------------------------------------------------------------------------

  /// <summary>
  /// Corridor quantities from Civil 3D's own QTO: corridor material quantities live in the QTO material lists of the
  /// sample-line groups on the corridor's baseline alignments (Compute Materials), not on the corridor. For corridor
  /// <c>name</c> (required) it returns, per material list, the total cut, fill and usable volume at the last sample
  /// line (SampleLineGroup.GetTotalVolumeResultDataForMaterialList), in the drawing's cubic units. A list Civil 3D
  /// can't compute is reported with its <c>error</c>, never as zero; with no material list at all, <c>materialLists</c>
  /// is empty and <c>note</c> says to run Compute Materials. Read-only. CIVIL3D.INVALID_INPUT without <c>name</c>,
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor. No TS domain calls it yet.
  /// </summary>
  public static Task<object?> QtyCorridorVolumesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lists = CorridorMaterialLists(civilDoc, transaction, name, withVolumes: true);
      return new Dictionary<string, object?>
      {
        ["corridorName"] = name,
        ["materialLists"] = lists.Select(list => list.ToDictionary()).ToList(),
        ["units"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = lists.Count == 0
          ? NoMaterialListNote(name)
          : "Totals are Civil 3D's QTO results at the last sample line of each sample-line group, as the Total Volume table shows them.",
      };
    });
  }

  /// <summary>One QTO material list of a sample-line group on a corridor's alignment, with its totals when asked
  /// (null volumes and an <c>Error</c> when Civil 3D couldn't compute them).</summary>
  internal sealed record MaterialListTotal(
    string SampleLineGroup, string Alignment, string MaterialList, IReadOnlyList<string> Materials,
    double? CutVolume, double? FillVolume, double? UsableVolume, int SampleLineCount, string? Error)
  {
    /// <summary>The list as an answer row.</summary>
    public Dictionary<string, object?> ToDictionary() => new()
    {
      ["sampleLineGroup"] = SampleLineGroup,
      ["alignment"] = Alignment,
      ["materialList"] = MaterialList,
      ["materials"] = Materials,
      ["cutVolume"] = CutVolume,
      ["fillVolume"] = FillVolume,
      ["usableVolume"] = UsableVolume,
      ["sampleLineCount"] = SampleLineCount,
      ["error"] = Error,
    };
  }

  /// <summary>Why a corridor has no material quantities: no QTO material list on its alignments' sample-line groups.</summary>
  internal static string NoMaterialListNote(string corridorName) =>
    $"No QTO material list was found on the sample-line groups of corridor '{corridorName}''s baseline alignments. " +
    "Run Compute Materials in Civil 3D for a sample-line group on that alignment first.";

  /// <summary>
  /// The QTO material lists of every sample-line group on <paramref name="corridorName"/>'s baseline alignments, with
  /// their total volumes when <paramref name="withVolumes"/>. Typed API only, read-only; a list whose totals Civil 3D
  /// can't compute keeps its <c>Error</c>. CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor.
  /// </summary>
  internal static List<MaterialListTotal> CorridorMaterialLists(CivilDocument civilDoc, Transaction transaction, string corridorName, bool withVolumes)
  {
    var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
    var results = new List<MaterialListTotal>();
    var alignmentIds = corridor.Baselines.Select(baseline => baseline.AlignmentId).Where(id => !id.IsNull).Distinct().ToList();
    foreach (var alignmentId in alignmentIds)
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);
      foreach (ObjectId groupId in alignment.GetSampleLineGroupIds())
      {
        var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
        foreach (QTOMaterialList list in group.MaterialLists)
        {
          var listName = list.Name;
          var guid = list.Guid;
          IReadOnlyList<string> materials;
          try { materials = group.GetMaterialNamesInMapping(guid); }
          catch (Exception) { materials = Array.Empty<string>(); }

          double? cut = null, fill = null, usable = null;
          var sampleLines = 0;
          string? error = null;
          if (withVolumes)
          {
            try
            {
              var along = group.GetTotalVolumeResultDataForMaterialList(guid).GetResultsAlongSampleLines();
              sampleLines = along.Length;
              // Cumulative along the sample lines, so the last one holds the totals.
              if (along.Length > 0)
              {
                var last = along[^1].VolumeResult;
                (cut, fill, usable) = (last.CumulativeCutVolume, last.CumulativeFillVolume, last.CumulativeUsableVolume);
              }
            }
            catch (Exception exception) when (exception is not JsonRpcDispatchException)
            {
              error = $"Civil 3D couldn't compute this material list's volumes: {exception.Message}";
            }
          }

          results.Add(new MaterialListTotal(group.Name, alignment.Name, listName, materials, cut, fill, usable, sampleLines, error));
        }
      }
    }

    return results;
  }

  // -------------------------------------------------------------------------
  // qtySurfaceVolume
  // -------------------------------------------------------------------------

  /// <summary>
  /// Exact cut, fill and net volume between <c>baseSurface</c> and <c>comparisonSurface</c> (both required), from a
  /// temporary TinVolumeSurface. With <c>region</c> ([{x, y}, ...], at least 3 corners, closed automatically) the
  /// temporary surface gets that polygon as its outer boundary, so only the volume inside it is counted.
  /// <c>corridorName</c> is optional, echoed back and not used. Doesn't change the drawing (MeasureAsync: the
  /// temporary surface and its boundary are never committed). Returns the two names, <c>region</c> (the corner count
  /// or null), <c>cutVolume</c>, <c>fillVolume</c>, <c>netVolume</c> (unadjusted), <c>net2dArea</c> (always null),
  /// <c>units</c> and a <c>note</c>. Throws CIVIL3D.INVALID_INPUT for a missing name, a region that isn't at least
  /// three {x, y} numbers, or surfaces that are the same, empty or don't overlap, and CIVIL3D.OBJECT_NOT_FOUND for an
  /// unknown surface.
  /// </summary>
  public static Task<object?> QtySurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");
    var region = ParseRegion(PluginRuntime.GetParameter(parameters, "region") as JsonNode);

    return CivilExecution.MeasureAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var volumeProperties = MeasureVolume(civilDoc, transaction, baseSurfaceName, comparisonSurfaceName, region, "MCP_TMP_VOLUME");

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["corridorName"] = corridorName,
        ["region"] = region?.Count,
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["net2dArea"] = null,
        ["units"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = "Volumes are exact Civil 3D TinVolumeSurface results. The managed volume-properties API does not expose a net 2D area."
          + (region == null ? "" : " Only the part inside the region polygon is counted.")
          + (string.IsNullOrWhiteSpace(corridorName) ? "" : " corridorName is not used: this is the volume between the two surfaces."),
      };
    });
  }

  /// <summary>
  /// Cut and fill between two named surfaces from a temporary TinVolumeSurface, clipped to <paramref name="region"/>
  /// (an outer boundary, non-destructive) when given. Only valid inside MeasureAsync's uncommitted transaction.
  /// CIVIL3D.INVALID_INPUT for a same, empty or non-overlapping pair (checked before Create, which otherwise only says
  /// "Fail to create a surface"); CIVIL3D.OBJECT_NOT_FOUND for an unknown surface.
  /// </summary>
  private static VolumeSurfaceProperties MeasureVolume(CivilDocument civilDoc, Transaction transaction,
    string baseSurfaceName, string comparisonSurfaceName, List<(double X, double Y)>? region, string namePrefix)
  {
    var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
    var compSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
    CivilObjectUtils.RequireDifferentSurfaces(baseSurface, compSurface);
    var volumeSurfaceId = TinVolumeSurface.Create($"{namePrefix}_{Guid.NewGuid():N}", baseSurface.ObjectId, compSurface.ObjectId);
    var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeSurfaceId,
      region == null ? OpenMode.ForRead : OpenMode.ForWrite);
    if (region != null)
    {
      var corners = new Autodesk.AutoCAD.Geometry.Point2dCollection();
      foreach (var (x, y) in region) corners.Add(new Autodesk.AutoCAD.Geometry.Point2d(x, y));
      if (!corners[0].IsEqualTo(corners[corners.Count - 1])) corners.Add(corners[0]);
      volumeSurface.BoundariesDefinition.AddBoundaries(corners, 0.1, Autodesk.Civil.SurfaceBoundaryType.Outer, true);
      volumeSurface.Rebuild();
    }

    return volumeSurface.GetVolumeProperties();
  }

  /// <summary>The <c>region</c> polygon ([{x, y}, ...]) as corners, or null when not given. CIVIL3D.INVALID_INPUT for
  /// anything but an array of at least three objects with numeric x and y.</summary>
  private static List<(double X, double Y)>? ParseRegion(JsonNode? node)
  {
    if (node == null) return null;
    const string expected = "region must be an array of at least 3 corners like {\"x\": 1000.0, \"y\": 2000.0}.";
    if (node is not JsonArray array) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", expected);
    var corners = new List<(double X, double Y)>();
    foreach (var item in array)
    {
      if (item is not JsonObject corner
          || corner["x"] is not JsonValue xValue || !xValue.TryGetValue<double>(out var x)
          || corner["y"] is not JsonValue yValue || !yValue.TryGetValue<double>(out var y))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", expected);
      }

      corners.Add((x, y));
    }

    return corners.Count >= 3 ? corners : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", expected);
  }

  // -------------------------------------------------------------------------
  // qtyPipeNetworkLengths
  // -------------------------------------------------------------------------

  /// <summary>
  /// Total 3D pipe length of gravity network <c>name</c> (required), with a breakdown by size, material or both
  /// (<c>groupBySize</c>, <c>groupByMaterial</c>, both off by default; the key is "All" without grouping). Read-only.
  /// Returns <c>networkName</c>, <c>totalLength</c>, <c>pipeCount</c>, <c>breakdown</c> (key, length, count, longest
  /// first) and the drawing's linear <c>units</c>. Throws CIVIL3D.INVALID_INPUT without <c>name</c> and
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown network.
  /// </summary>
  public static Task<object?> QtyPipeNetworkLengthsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var groupBySize = PluginRuntime.GetOptionalBool(parameters, "groupBySize") ?? false;
    var groupByMaterial = PluginRuntime.GetOptionalBool(parameters, "groupByMaterial") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkObj = FindPipeNetworkByName(civilDoc, transaction, name)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");

      var totalLength = 0.0;
      var pipeCount = 0;
      var breakdown = new Dictionary<string, (double Length, int Count)>(StringComparer.OrdinalIgnoreCase);

      foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
      {
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pipeId, OpenMode.ForRead);
        var length = pipe.Length3D;
        var diameter = pipe.InnerDiameterOrWidth;
        var material = pipe.Material;

        totalLength += length;
        pipeCount++;

        // Build breakdown key
        var key = "All";
        if (groupBySize && groupByMaterial)
          key = $"{material} {PipeSize(diameter, database)}";
        else if (groupBySize)
          key = PipeSize(diameter, database);
        else if (groupByMaterial)
          key = material;

        if (breakdown.TryGetValue(key, out var existing))
          breakdown[key] = (existing.Length + length, existing.Count + 1);
        else
          breakdown[key] = (length, 1);
      }

      var breakdownList = breakdown
        .Select(kvp => new Dictionary<string, object?>
        {
          ["key"] = kvp.Key,
          ["length"] = kvp.Value.Length,
          ["count"] = kvp.Value.Count,
        })
        .OrderByDescending(d => d["length"])
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = name,
        ["totalLength"] = totalLength,
        ["pipeCount"] = pipeCount,
        ["breakdown"] = breakdownList,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyPressureNetworkLengths
  // -------------------------------------------------------------------------

  /// <summary>
  /// Total center-to-center length (3D, else 2D) of pressure network <c>name</c> (required), with the same optional
  /// <c>groupBySize</c> / <c>groupByMaterial</c> breakdown and result shape as qtyPipeNetworkLengths. Read-only.
  /// Throws CIVIL3D.INVALID_INPUT without <c>name</c>, CIVIL3D.OBJECT_NOT_FOUND for an unknown network, and
  /// CIVIL3D.API_ERROR when a pipe's length or diameter can't be read (the diameter is read even without grouping).
  /// </summary>
  public static Task<object?> QtyPressureNetworkLengthsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var groupBySize = PluginRuntime.GetOptionalBool(parameters, "groupBySize") ?? false;
    var groupByMaterial = PluginRuntime.GetOptionalBool(parameters, "groupByMaterial") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkObj = FindPressureNetworkByName(civilDoc, transaction, name)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure network '{name}' was not found.");

      var totalLength = 0.0;
      var pipeCount = 0;
      var breakdown = new Dictionary<string, (double Length, int Count)>(StringComparer.OrdinalIgnoreCase);

      foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes"))
      {
        var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
        var length = GetRequiredEngineeringDouble(pipe, $"pressure pipe '{CivilObjectUtils.GetName(pipe)}' length", "Length3DCenterToCenter", "Length2DCenterToCenter");
        var diameter = GetRequiredEngineeringDouble(pipe, $"pressure pipe '{CivilObjectUtils.GetName(pipe)}' diameter", "InnerDiameter", "OuterDiameter", "Diameter", "InnerDiameterOrWidth");
        var material = GetAnyString(pipe, "Material", "PipeFamily", "PartFamilyName", "PartDescription") ?? "Unknown";

        totalLength += length;
        pipeCount++;

        var key = "All";
        if (groupBySize && groupByMaterial)
          key = $"{material} {PipeSize(diameter, database)}";
        else if (groupBySize)
          key = PipeSize(diameter, database);
        else if (groupByMaterial)
          key = material;

        if (breakdown.TryGetValue(key, out var existing))
          breakdown[key] = (existing.Length + length, existing.Count + 1);
        else
          breakdown[key] = (length, 1);
      }

      var breakdownList = breakdown
        .Select(kvp => new Dictionary<string, object?>
        {
          ["key"] = kvp.Key,
          ["length"] = kvp.Value.Length,
          ["count"] = kvp.Value.Count,
        })
        .OrderByDescending(d => d["length"])
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = name,
        ["totalLength"] = totalLength,
        ["pipeCount"] = pipeCount,
        ["breakdown"] = breakdownList,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyParcelAreas
  // -------------------------------------------------------------------------

  /// <summary>
  /// Area and perimeter of the parcels in every site, or only in <c>siteName</c>, and only those in
  /// <c>parcelNames</c> (a string array) when given; both optional, matched case-insensitively. Read-only. Returns
  /// <c>parcels</c> (name, area, perimeter, siteName), <c>totalArea</c>, <c>parcelCount</c> and <c>units</c>
  /// (<c>area</c>: the drawing's square unit, e.g. "feet²"; <c>areaUnit</c>: sqft, sqm or "square drawing units";
  /// <c>length</c>: the linear unit of the perimeters), as listParcels gives them. Throws no error codes of its own.
  /// </summary>
  public static Task<object?> QtyParcelAreasAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var parcelNamesNode = PluginRuntime.GetParameter(parameters, "parcelNames") as JsonArray;
    var requestedParcelNames = parcelNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var parcelResults = new List<Dictionary<string, object?>>();
      var totalArea = 0.0;

      foreach (ObjectId siteId in civilDoc.GetSiteIds())
      {
        var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
        var currentSiteName = site.Name;

        if (!string.IsNullOrWhiteSpace(siteName) &&
            !currentSiteName.Equals(siteName, StringComparison.OrdinalIgnoreCase))
          continue;

        foreach (ObjectId parcelId in site.GetParcelIds())
        {
          if (parcelId == ObjectId.Null) continue;
          var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, parcelId, OpenMode.ForRead);
          var parcelName = parcel.Name;

          if (requestedParcelNames != null && !requestedParcelNames.Contains(parcelName)) continue;

          var area = parcel.Area;
          var perimeter = ParcelEditingCommands.GetParcelPerimeter(parcel);

          totalArea += area;
          parcelResults.Add(new Dictionary<string, object?>
          {
            ["name"] = parcelName,
            ["area"] = area,
            ["perimeter"] = perimeter,
            ["siteName"] = currentSiteName,
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["parcels"] = parcelResults,
        ["totalArea"] = totalArea,
        ["parcelCount"] = parcelResults.Count,
        // Areas are in the square of the linear unit, perimeters in the linear unit.
        ["units"] = new Dictionary<string, object?>
        {
          ["area"] = $"{CivilObjectUtils.LinearUnits(database)}²",
          ["areaUnit"] = DesignMath.NativeAreaUnit(CivilObjectUtils.LinearUnits(database)),
          ["length"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyAlignmentLengths
  // -------------------------------------------------------------------------

  /// <summary>
  /// Lengths of every alignment, or only those in <c>names</c> (optional string array). <c>startStation</c> and
  /// <c>endStation</c> (optional) are clamped to each alignment's own range, and the length reported is that station
  /// difference. Read-only. Returns <c>alignments</c> (name, length, totalLength, startStation, endStation),
  /// <c>totalLength</c> and <c>units</c>. Throws no error codes of its own.
  /// </summary>
  public static Task<object?> QtyAlignmentLengthsAsync(JsonObject? parameters)
  {
    var namesNode = PluginRuntime.GetParameter(parameters, "names") as JsonArray;
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    var requestedNames = namesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignmentResults = new List<Dictionary<string, object?>>();
      var totalLength = 0.0;

      foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);

        if (requestedNames != null && !requestedNames.Contains(alignment.Name)) continue;

        // Clamp station range
        var start = startStation.HasValue ? Math.Max(startStation.Value, alignment.StartingStation) : alignment.StartingStation;
        var end = endStation.HasValue ? Math.Min(endStation.Value, alignment.EndingStation) : alignment.EndingStation;
        var segmentLength = Math.Max(0, end - start);

        totalLength += segmentLength;
        alignmentResults.Add(new Dictionary<string, object?>
        {
          ["name"] = alignment.Name,
          ["length"] = segmentLength,
          ["totalLength"] = alignment.Length,
          ["startStation"] = start,
          ["endStation"] = end,
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignments"] = alignmentResults,
        ["totalLength"] = totalLength,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyPointCountByGroup
  // -------------------------------------------------------------------------

  /// <summary>
  /// Number of points in every point group, or only those in <c>groupNames</c> (optional string array). Read-only.
  /// Returns <c>groups</c> (name, count) and <c>totalPoints</c>, the drawing's COGO point count (not the sum of the
  /// groups, which overlap). Throws no error codes of its own.
  /// </summary>
  public static Task<object?> QtyPointCountByGroupAsync(JsonObject? parameters)
  {
    var groupNamesNode = PluginRuntime.GetParameter(parameters, "groupNames") as JsonArray;
    var requestedGroupNames = groupNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groupResults = new List<Dictionary<string, object?>>();
      var totalPoints = civilDoc.CogoPoints.Count;

      foreach (ObjectId groupId in civilDoc.PointGroups)
      {
        if (groupId == ObjectId.Null) continue;
        var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, groupId, OpenMode.ForRead);
        var groupName = group.Name;

        if (requestedGroupNames != null && !requestedGroupNames.Contains(groupName)) continue;

        var count = group.GetPointNumbers().Length;

        // Groups overlap, so adding them up counts a point once per group it is in.
        groupResults.Add(new Dictionary<string, object?>
        {
          ["name"] = groupName,
          ["count"] = count,
        });
      }

      return new Dictionary<string, object?>
      {
        ["groups"] = groupResults,
        ["totalPoints"] = totalPoints,
      };
    });
  }

  /// <summary>
  /// A pipe's size for a report. Civil 3D gives it in drawing units, so 0.45 m must read Ø450mm and 1.5 ft Ø18in; with
  /// no decimals in those units they were Ø0mm and Ø2mm.
  /// </summary>
  private static string PipeSize(double diameter, Database database)
  {
    var units = CivilObjectUtils.LinearUnits(database);
    return units.Contains("meter", StringComparison.OrdinalIgnoreCase)
      ? $"Ø{diameter * 1000:0.#}mm"
      : units.Contains("feet", StringComparison.OrdinalIgnoreCase) || units.Contains("foot", StringComparison.OrdinalIgnoreCase)
        ? $"Ø{diameter * 12:0.#}in"
        : $"Ø{diameter:0.###} {units}";
  }

  // -------------------------------------------------------------------------
  // qtyExportToCsv
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes a quantities .csv to <c>outputPath</c> (required) through FileBoundary.WriteAllTextAtomic (inside the
  /// export roots, no overwrite unless <c>overwrite</c>). Each section is its own header row and rows, then a blank
  /// line; every field is RFC 4180 quoted when needed and numbers use a dot (DesignMath.CsvLine). Sections:
  /// alignments (<c>includeAlignmentLengths</c>, or the older <c>includeAlignments</c>, on by default); surface volumes
  /// (<c>includeSurfaceVolumes</c> with <c>baseSurface</c> and <c>comparisonSurface</c>: cut, fill and net from a
  /// temporary TIN volume surface, as qtySurfaceVolume); surfaces (<c>includeSurfaces</c>, or
  /// <c>includeSurfaceVolumes</c> without the two surfaces: elevation range and point count of each surface);
  /// corridor volumes (<c>includeCorridorVolumes</c> with <c>corridorName</c>: the QTO material-list totals of
  /// qtyCorridorVolumes); <c>includePipeNetworks</c>, <c>includePressureNetworks</c>, <c>includeParcelAreas</c> and
  /// <c>includePointGroups</c> (off by default). <c>filterNames</c> (optional string array) keeps only objects,
  /// networks, parcels or groups with those names. Doesn't change the drawing (a run with surface volumes measures
  /// under MeasureAsync: never committed, one blank undo step). Returns the canonical <c>outputPath</c>,
  /// <c>rowsWritten</c>, <c>sectionsIncluded</c> and <c>notes</c> (what was asked for but couldn't be written).
  /// FileBoundary's codes pass through (CIVIL3D.PATH_NOT_ALLOWED, FILE_TYPE_NOT_ALLOWED, CONFLICT, FILE_IO_ERROR);
  /// CIVIL3D.API_ERROR when a pipe's length, diameter or slope can't be read; the surface and corridor lookups' own
  /// codes for unknown names.
  /// </summary>
  public static Task<object?> QtyExportToCsvAsync(JsonObject? parameters)
  {
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    // The tool's names first (includeAlignmentLengths, includeSurfaceVolumes); the plugin's own older names still work.
    var includeAlignments = PluginRuntime.GetOptionalBool(parameters, "includeAlignmentLengths")
      ?? PluginRuntime.GetOptionalBool(parameters, "includeAlignments") ?? true;
    var includeSurfaceVolumes = PluginRuntime.GetOptionalBool(parameters, "includeSurfaceVolumes") ?? false;
    var includeSurfaces = PluginRuntime.GetOptionalBool(parameters, "includeSurfaces") ?? false;
    var includeCorridorVolumes = PluginRuntime.GetOptionalBool(parameters, "includeCorridorVolumes") ?? false;
    var includePipeNetworks = PluginRuntime.GetOptionalBool(parameters, "includePipeNetworks") ?? false;
    var includePressureNetworks = PluginRuntime.GetOptionalBool(parameters, "includePressureNetworks") ?? false;
    var includeParcelAreas = PluginRuntime.GetOptionalBool(parameters, "includeParcelAreas") ?? false;
    var includePointGroups = PluginRuntime.GetOptionalBool(parameters, "includePointGroups") ?? false;
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");
    var baseSurfaceName = PluginRuntime.GetOptionalString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetOptionalString(parameters, "comparisonSurface");
    var filterNamesNode = PluginRuntime.GetParameter(parameters, "filterNames") as JsonArray;
    var filterNames = filterNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .Cast<string>()
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var measureVolumes = includeSurfaceVolumes && !string.IsNullOrWhiteSpace(baseSurfaceName) && !string.IsNullOrWhiteSpace(comparisonSurfaceName);
    // Without the two surfaces there is no volume to give: the per-surface summary is written instead, as before.
    includeSurfaces |= includeSurfaceVolumes && !measureVolumes;

    // A volume needs a temporary TIN volume surface, which needs the document lock (MeasureAsync); everything else is a
    // plain read.
    Func<Autodesk.AutoCAD.ApplicationServices.Document, CivilDocument, Database, Transaction, object?> export = (doc, civilDoc, database, transaction) =>
    {
      var sb = new StringBuilder();
      var rowsWritten = 0;
      var sectionsIncluded = new List<string>();
      var notes = new List<string>();
      var linearUnits = CivilObjectUtils.LinearUnits(database);
      void Row(params object?[] values) => sb.AppendLine(DesignMath.CsvLine(values));

      if (includeAlignments)
      {
        sectionsIncluded.Add("alignments");
        sb.AppendLine("Section,Name,Length,StartStation,EndStation,Units");
        foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);
          if (filterNames != null && !filterNames.Contains(alignment.Name)) continue;
          Row("Alignment", alignment.Name, Math.Round(alignment.Length, 3), Math.Round(alignment.StartingStation, 3), Math.Round(alignment.EndingStation, 3), linearUnits);
          rowsWritten++;
        }
        sb.AppendLine();
      }

      if (measureVolumes)
      {
        sectionsIncluded.Add("surfaceVolumes");
        sb.AppendLine("Section,BaseSurface,ComparisonSurface,Cut,Fill,Net,Units");
        var volumes = MeasureVolume(civilDoc, transaction, baseSurfaceName!, comparisonSurfaceName!, null, "MCP_TMP_QTY_EXPORT");
        Row("SurfaceVolume", baseSurfaceName, comparisonSurfaceName, Math.Round(volumes.UnadjustedCutVolume, 3),
          Math.Round(volumes.UnadjustedFillVolume, 3), Math.Round(volumes.UnadjustedNetVolume, 3), CivilObjectUtils.VolumeUnits(database));
        rowsWritten++;
        sb.AppendLine();
      }
      else if (includeSurfaceVolumes)
      {
        notes.Add("includeSurfaceVolumes needs baseSurface and comparisonSurface for volumes; each surface's elevation range and point count were written instead.");
      }

      if (includeSurfaces)
      {
        sectionsIncluded.Add("surfaces");
        sb.AppendLine("Section,Name,Type,MinElevation,MaxElevation,NumberOfPoints,Units");
        foreach (ObjectId surfId in civilDoc.GetSurfaceIds())
        {
          var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfId, OpenMode.ForRead);
          if (filterNames != null && !filterNames.Contains(surface.Name)) continue;
          // An empty surface throws when asked its properties.
          if (SurfaceCommands.IsEmpty(surface))
          {
            Row("Surface", surface.Name, surface.GetType().Name, null, null, 0, linearUnits);
          }
          else
          {
            var gp = surface.GetGeneralProperties();
            Row("Surface", surface.Name, surface.GetType().Name, Math.Round(gp.MinimumElevation, 3), Math.Round(gp.MaximumElevation, 3), gp.NumberOfPoints, linearUnits);
          }

          rowsWritten++;
        }
        sb.AppendLine();
      }

      if (includeCorridorVolumes)
      {
        if (string.IsNullOrWhiteSpace(corridorName))
        {
          notes.Add("includeCorridorVolumes needs corridorName; no corridor section was written.");
        }
        else
        {
          sectionsIncluded.Add("corridorVolumes");
          sb.AppendLine("Section,Corridor,SampleLineGroup,MaterialList,Cut,Fill,Usable,Units,Error");
          var lists = CorridorMaterialLists(civilDoc, transaction, corridorName!, withVolumes: true);
          if (lists.Count == 0) notes.Add(NoMaterialListNote(corridorName!));
          foreach (var list in lists)
          {
            Row("CorridorVolume", corridorName, list.SampleLineGroup, list.MaterialList, list.CutVolume, list.FillVolume, list.UsableVolume,
              CivilObjectUtils.VolumeUnits(database), list.Error);
            rowsWritten++;
          }
          sb.AppendLine();
        }
      }

      if (includePipeNetworks)
      {
        sectionsIncluded.Add("pipeNetworks");
        sb.AppendLine("Section,NetworkName,PipeName,Length,Diameter,Slope,Material,Units");
        foreach (var networkObj in EnumerateAllPipeNetworks(civilDoc, transaction))
        {
          var networkName = CivilObjectUtils.GetName(networkObj) ?? "unknown";
          if (filterNames != null && !filterNames.Contains(networkName)) continue;
          foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
          {
            var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
            var pipeName = CivilObjectUtils.GetName(pipe) ?? pipeId.Handle.ToString();
            var length = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' length", "Length3D", "Length2D", "Length");
            var diameter = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' diameter", "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
            var slope = GetRequiredEngineeringDouble(pipe, $"pipe '{pipeName}' slope", "Slope", "FlowSlope");
            var material = GetAnyString(pipe, "Material", "PartDescription", "PartFamilyName") ?? string.Empty;
            Row("PipeNetwork", networkName, pipeName, Math.Round(length, 3), Math.Round(diameter, 3), Math.Round(slope, 6), material, linearUnits);
            rowsWritten++;
          }
        }
        sb.AppendLine();
      }

      if (includePressureNetworks)
      {
        sectionsIncluded.Add("pressureNetworks");
        sb.AppendLine("Section,NetworkName,PipeName,Length,Diameter,Material,Units");
        foreach (ObjectId networkId in civilDoc.GetPressurePipeNetworkIds())
        {
          if (networkId == ObjectId.Null) continue;
          var networkObj = transaction.GetObject(networkId, OpenMode.ForRead);
          var networkName = CivilObjectUtils.GetName(networkObj) ?? "unknown";
          if (filterNames != null && !filterNames.Contains(networkName)) continue;
          foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes"))
          {
            var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
            var pipeName = CivilObjectUtils.GetName(pipe) ?? pipeId.Handle.ToString();
            // As in qtyPressureNetworkLengths: center-to-center length.
            var length = GetRequiredEngineeringDouble(pipe, $"pressure pipe '{pipeName}' length", "Length3DCenterToCenter", "Length2DCenterToCenter");
            var diameter = GetRequiredEngineeringDouble(pipe, $"pressure pipe '{pipeName}' diameter", "InnerDiameter", "OuterDiameter", "Diameter", "InnerDiameterOrWidth");
            var material = GetAnyString(pipe, "Material", "PipeFamily", "PartFamilyName", "PartDescription") ?? string.Empty;
            Row("PressureNetwork", networkName, pipeName, Math.Round(length, 3), Math.Round(diameter, 3), material, linearUnits);
            rowsWritten++;
          }
        }
        sb.AppendLine();
      }

      if (includeParcelAreas)
      {
        sectionsIncluded.Add("parcelAreas");
        // Area in the drawing's square unit, perimeter in its linear unit.
        sb.AppendLine("Section,SiteName,ParcelName,Area,AreaUnits,Perimeter,LengthUnits");
        foreach (ObjectId siteId in civilDoc.GetSiteIds())
        {
          var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
          var currentSiteName = site.Name;
          foreach (ObjectId parcelId in site.GetParcelIds())
          {
            if (parcelId == ObjectId.Null) continue;
            var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, parcelId, OpenMode.ForRead);
            var parcelName = parcel.Name;
            if (filterNames != null && !filterNames.Contains(parcelName)) continue;
            var perimeter = ParcelEditingCommands.GetParcelPerimeter(parcel);
            Row("Parcel", currentSiteName, parcelName, Math.Round(parcel.Area, 3), DesignMath.NativeAreaUnit(linearUnits),
              perimeter.HasValue ? Math.Round(perimeter.Value, 3) : null, linearUnits);
            rowsWritten++;
          }
        }
        sb.AppendLine();
      }

      if (includePointGroups)
      {
        sectionsIncluded.Add("pointGroups");
        sb.AppendLine("Section,GroupName,PointCount");
        foreach (ObjectId groupId in civilDoc.PointGroups)
        {
          if (groupId == ObjectId.Null) continue;
          var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, groupId, OpenMode.ForRead);
          var groupName = group.Name;
          if (filterNames != null && !filterNames.Contains(groupName)) continue;
          Row("PointGroup", groupName, group.GetPointNumbers().Length);
          rowsWritten++;
        }
        sb.AppendLine();
      }

      var canonicalOutputPath = FileBoundary.WriteAllTextAtomic(
        outputPath, sb.ToString(), Encoding.UTF8, overwrite, ".csv");

      return new Dictionary<string, object?>
      {
        ["outputPath"] = canonicalOutputPath,
        ["rowsWritten"] = rowsWritten,
        ["sectionsIncluded"] = sectionsIncluded,
        ["notes"] = notes,
      };
    };

    return measureVolumes ? CivilExecution.MeasureAsync(export) : CivilExecution.ReadAsync(export);
  }

  // -------------------------------------------------------------------------
  // qtyMaterialListGet
  // -------------------------------------------------------------------------

  /// <summary>
  /// The QTO material lists for corridor <c>corridorName</c> (required): QTO material lists belong to the sample-line
  /// groups on the corridor's baseline alignments, so this lists each group's lists and their material names
  /// (SampleLineGroup.MaterialLists / GetMaterialNamesInMapping), without volumes (qtyCorridorVolumes has those).
  /// Read-only. An empty <c>materialLists</c> comes with a <c>note</c>. CIVIL3D.INVALID_INPUT without
  /// <c>corridorName</c>, CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor. No TS domain calls it yet.
  /// </summary>
  public static Task<object?> QtyMaterialListGetAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lists = CorridorMaterialLists(civilDoc, transaction, corridorName, withVolumes: false);
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["materialLists"] = lists.Select(list => new Dictionary<string, object?>
        {
          ["sampleLineGroup"] = list.SampleLineGroup,
          ["alignment"] = list.Alignment,
          ["materialList"] = list.MaterialList,
          ["materials"] = list.Materials,
        }).ToList(),
        ["note"] = lists.Count == 0 ? NoMaterialListNote(corridorName) : null,
      };
    });
  }

  // -------------------------------------------------------------------------
  // qtyEarthworkSummary
  // -------------------------------------------------------------------------

  /// <summary>
  /// Whole-surface earthwork between <c>baseSurface</c> and <c>designSurface</c> (both required), from a temporary
  /// TinVolumeSurface as in qtySurfaceVolume (MeasureAsync, never committed, drawing unchanged). Giving
  /// <c>alignmentName</c>, <c>startStation</c> or <c>endStation</c> throws CIVIL3D.API_ERROR before the drawing is
  /// touched: station-by-station volumes need sample lines. Returns <c>cumulativeCut</c>, <c>cumulativeFill</c>,
  /// <c>netEarthwork</c>, <c>stationCount</c> 0, an empty <c>stations</c> list, <c>volumeUnits</c> and a <c>note</c>.
  /// Also throws CIVIL3D.INVALID_INPUT for a missing name or a same, empty or non-overlapping pair, and
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown surface.
  /// </summary>
  public static Task<object?> QtyEarthworkSummaryAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var designSurfaceName = PluginRuntime.GetRequiredString(parameters, "designSurface");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    if (startStation.HasValue || endStation.HasValue || !string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(parameters, "alignmentName")))
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Station-clipped earthwork requires sample lines/QTO. The managed TinVolumeSurface API computes whole-surface volume only; no station-volume approximation was used.");

    return CivilExecution.MeasureAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var designSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, designSurfaceName, OpenMode.ForRead);
      CivilObjectUtils.RequireDifferentSurfaces(baseSurface, designSurface);
      var volumeId = TinVolumeSurface.Create($"MCP_TMP_EARTHWORK_{Guid.NewGuid():N}", baseSurface.ObjectId, designSurface.ObjectId);
      var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeId, OpenMode.ForRead);
      var volumes = volumeSurface.GetVolumeProperties();

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["designSurface"] = designSurfaceName,
        ["cumulativeCut"] = volumes.UnadjustedCutVolume,
        ["cumulativeFill"] = volumes.UnadjustedFillVolume,
        ["netEarthwork"] = volumes.UnadjustedNetVolume,
        ["stationCount"] = 0,
        ["stations"] = Array.Empty<object>(),
        ["volumeUnits"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = "Exact whole-surface TinVolumeSurface quantities; station breakdown is not exposed by this API path.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  /// <summary>The gravity pipe network named <paramref name="name"/> (case-insensitive), opened for read, or null.</summary>
  private static AcDbObject? FindPipeNetworkByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase))
        return obj;
    }
    return null;
  }

  /// <summary>The pressure network named <paramref name="name"/> (case-insensitive), opened for read, or null.</summary>
  private static AcDbObject? FindPressureNetworkByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    foreach (ObjectId objectId in civilDoc.GetPressurePipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase))
        return obj;
    }
    return null;
  }

  /// <summary>Every gravity pipe network in the drawing, opened for read.</summary>
  private static IEnumerable<AcDbObject> EnumerateAllPipeNetworks(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      yield return transaction.GetObject(objectId, OpenMode.ForRead);
    }
  }

  /// <summary>
  /// Child object ids of <paramref name="owner"/>, trying each member name both as a method and as a property, for
  /// members named differently across releases. Null ids are skipped, and each id is given once even when a name works
  /// as both or two names reach the same list (a length would otherwise be counted twice).
  /// </summary>
  private static IEnumerable<ObjectId> GetChildObjectIds(AcDbObject owner, params string[] memberNames)
  {
    var seen = new HashSet<ObjectId>();
    foreach (var memberName in memberNames)
    {
      var methodResult = CivilObjectUtils.InvokeMethod(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(methodResult))
      {
        if (objectId != ObjectId.Null && seen.Add(objectId)) yield return objectId;
      }
      var propVal = CivilObjectUtils.GetPropertyValue<object>(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(propVal))
      {
        if (objectId != ObjectId.Null && seen.Add(objectId)) yield return objectId;
      }
    }
  }

  /// <summary>The first of several property names that has a value, for properties named differently across releases.</summary>
  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (v.HasValue) return v.Value;
    }
    return null;
  }

  /// <summary>Like GetAnyDouble, but throws CIVIL3D.API_ERROR naming <paramref name="valueDescription"/> when no
  /// property has a value, so a quantity is never silently 0.</summary>
  private static double GetRequiredEngineeringDouble(object? value, string valueDescription, params string[] propertyNames)
  {
    var result = GetAnyDouble(value, propertyNames);
    if (result.HasValue) return result.Value;
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Could not read {valueDescription} from the managed API. Zero was not substituted.");
  }

  /// <summary>The first of several string properties that is not blank, or null.</summary>
  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var v = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(v)) return v;
    }
    return null;
  }
}
