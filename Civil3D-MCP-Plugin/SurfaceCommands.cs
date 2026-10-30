using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind the civil3d_surface tool (src/tools/domains/surfaceDomain.ts): listing and inspecting
/// surfaces, sampling elevations, adding TIN data (points, breaklines, boundaries), contours, volumes, the stored
/// analysis ranges, and DEM import. Each method is registered by name in CommandDispatcher.cs.
///
/// Reads go through CivilExecution.ReadAsync and writes through WriteAsync, so they share the host queue and a write is
/// one undo step. Surfaces are found by name (case-insensitive) with CivilObjectUtils.FindSurfaceByName; a missing one
/// is CIVIL3D.OBJECT_NOT_FOUND.
///
/// The volume methods create a temporary TIN volume surface inside a read transaction. Reads never commit, so the
/// volume surface is discarded with the transaction and the drawing is left unchanged (scripts/live-host-smoke.mjs
/// checks that the surface list is the same before and after).
/// </summary>
public static class SurfaceCommands
{
  /// <summary>Lists every surface in the drawing: name, handle, type (TIN, Grid, TINVolume), and whether it is a data
  /// reference (with its source path).</summary>
  public static Task<object?> ListSurfacesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surfaces = civilDoc.GetSurfaceIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead))
        .Select(surface => new Dictionary<string, object?>
        {
          ["name"] = surface.Name,
          ["handle"] = CivilObjectUtils.GetHandle(surface),
          ["type"] = MapSurfaceType(surface),
          ["isReference"] = surface.IsReferenceObject,
          ["sourcePath"] = CivilObjectUtils.GetStringProperty(surface, "ReferencePath"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["surfaces"] = surfaces,
      };
    });
  }

  /// <summary>
  /// One surface's detail for <c>name</c>: style, layer, elevation and area statistics, triangle count (TIN only),
  /// bounding box and drawing units. <c>dependentAlignments</c> and <c>dependentCorridors</c> are always empty lists;
  /// they keep the response shape the TypeScript schema expects.
  /// </summary>
  public static Task<object?> GetSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      var terrainProperties = GetTerrainProperties(surface);
      var extents = surface.GeometricExtents;

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["type"] = MapSurfaceType(surface),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(surface.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = surface.Layer,
        ["statistics"] = new Dictionary<string, object?>
        {
          ["minimumElevation"] = generalProperties.MinimumElevation,
          ["maximumElevation"] = generalProperties.MaximumElevation,
          ["meanElevation"] = generalProperties.MeanElevation,
          ["area2d"] = terrainProperties?.SurfaceArea2D,
          ["area3d"] = terrainProperties?.SurfaceArea3D,
          ["numberOfPoints"] = generalProperties.NumberOfPoints,
          ["numberOfTriangles"] = GetTriangleCount(surface),
        },
        ["boundingBox"] = new Dictionary<string, object?>
        {
          ["minX"] = extents.MinPoint.X,
          ["minY"] = extents.MinPoint.Y,
          ["maxX"] = extents.MaxPoint.X,
          ["maxY"] = extents.MaxPoint.Y,
        },
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["isReference"] = surface.IsReferenceObject,
        ["dependentAlignments"] = new List<string>(),
        ["dependentCorridors"] = new List<string>(),
      };
    });
  }

  /// <summary>Elevation of surface <c>name</c> at (<c>x</c>, <c>y</c>). A point outside the surface is
  /// CIVIL3D.API_ERROR, not a null elevation.</summary>
  public static Task<object?> GetSurfaceElevationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var elevation = InvokeSurfaceElevation(surface, x, y);
      return new Dictionary<string, object?>
      {
        ["elevation"] = elevation,
        ["units"] = CivilObjectUtils.LinearUnits(database),
        ["surfaceName"] = surface.Name,
      };
    });
  }

  /// <summary>Elevations of surface <c>name</c> at each of <c>points</c> ([{x, y}]). Unlike
  /// SampleSurfaceElevationsAsync, one point off the surface fails the whole call.</summary>
  public static Task<object?> GetSurfaceElevationsAlongAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "getSurfaceElevationsAlong requires 'points'.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var samples = new List<Dictionary<string, object?>>();
      foreach (var point in pointsNode.OfType<JsonObject>())
      {
        var x = point["x"]!.GetValue<double>();
        var y = point["y"]!.GetValue<double>();
        samples.Add(new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["elevation"] = InvokeSurfaceElevation(surface, x, y),
        });
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  /// <summary>Elevation range, mean, 2D/3D area, point and triangle counts of surface <c>name</c>.
  /// <c>analysisType</c> is only echoed back.</summary>
  public static Task<object?> GetSurfaceStatisticsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var analysisType = PluginRuntime.GetOptionalString(parameters, "analysisType");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      var terrainProperties = GetTerrainProperties(surface);
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["analysisType"] = analysisType,
        ["minimumElevation"] = generalProperties.MinimumElevation,
        ["maximumElevation"] = generalProperties.MaximumElevation,
        ["meanElevation"] = generalProperties.MeanElevation,
        ["area2d"] = terrainProperties?.SurfaceArea2D,
        ["area3d"] = terrainProperties?.SurfaceArea3D,
        ["numberOfPoints"] = generalProperties.NumberOfPoints,
        ["numberOfTriangles"] = GetTriangleCount(surface),
      };
    });
  }

  /// <summary>Creates an empty TIN surface <c>name</c> with an optional <c>style</c> (default style when missing),
  /// <c>layer</c> and <c>description</c>. Data is added afterwards with the add* methods.</summary>
  public static Task<object?> CreateSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var surfaceId = CreateTinSurface(name, styleId);
      var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfaceId, OpenMode.ForWrite);
      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layerName))
      {
        surface.Layer = layerName;
      }

      var description = PluginRuntime.GetOptionalString(parameters, "description");
      if (!string.IsNullOrWhiteSpace(description))
      {
        surface.Description = description;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["created"] = true,
      };
    });
  }

  /// <summary>Erases surface <c>name</c>. Approval-gated on the TypeScript side; objects that depend on it are not
  /// checked here.</summary>
  public static Task<object?> DeleteSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      surface.Erase();
      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  /// <summary>Adds <c>points</c> ([{x, y, z}], z required) as TIN vertices to surface <c>name</c> and rebuilds it.
  /// Only TIN surfaces accept vertices.</summary>
  public static Task<object?> AddSurfacePointsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var points = ParseRequiredPoint3dArray(parameters, "points", "addSurfacePoints requires at least one point.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding TIN vertices is not supported for surface type '{surface.GetType().Name}'.");
      }
      tinSurface.AddVertices(points);

      SetSurfaceDescription(surface, description);
      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["pointsAdded"] = points.Count,
        ["description"] = surface.Description,
      };
    });
  }

  /// <summary>
  /// Adds one standard breakline through <c>points</c> (at least two, with z) to TIN surface <c>name</c> and rebuilds
  /// it. Only <c>breaklineType = "standard"</c> is supported; other types are refused before anything changes, so the
  /// caller isn't left with a half-applied edit.
  /// </summary>
  public static Task<object?> AddSurfaceBreaklineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var breaklineType = PluginRuntime.GetOptionalString(parameters, "breaklineType") ?? "standard";
    if (!string.Equals(breaklineType, "standard", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Breakline type '{breaklineType}' is not supported by this endpoint. No surface was modified; use breaklineType='standard'.");
    }
    var points = ParseRequiredPoint3dArray(parameters, "points", "addSurfaceBreakline requires at least two points.");
    if (points.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "addSurfaceBreakline requires at least two points.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (surface is not TinSurface tinSurface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding standard breaklines is not supported for surface type '{surface.GetType().Name}'.");
      }
      tinSurface.BreaklinesDefinition.AddStandardBreaklines(points, 1.0, 0.0, 0.0, 0.0);

      SetSurfaceDescription(surface, description);
      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["breaklineType"] = breaklineType,
        ["vertexCount"] = points.Count,
        ["description"] = surface.Description,
      };
    });
  }

  /// <summary>Adds a boundary polygon (<c>points</c>, at least three, x/y only) of type <c>boundaryType</c>
  /// (outer by default; show, hide, data clip) to surface <c>name</c> and rebuilds it.</summary>
  public static Task<object?> AddSurfaceBoundaryAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var boundaryType = PluginRuntime.GetOptionalString(parameters, "boundaryType") ?? "outer";
    var points = ParseRequiredPoint2dArray(parameters, "points", "addSurfaceBoundary requires at least three points.");
    if (points.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "addSurfaceBoundary requires at least three points.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      surface.BoundariesDefinition.AddBoundaries(points, 1.0, ResolveBoundaryType(boundaryType), true);

      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["boundaryType"] = boundaryType,
        ["vertexCount"] = points.Count,
      };
    });
  }

  /// <summary>
  /// Extracts contours of surface <c>name</c> as drawing objects at <c>minorInterval</c>, and again at
  /// <c>majorInterval</c> when it differs, returning their handles. It is a write (new objects in model space) although
  /// the surface itself is only read. Elevations on both intervals (every major contour, usually) are extracted twice.
  /// </summary>
  public static Task<object?> ExtractSurfaceContoursAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minorInterval = PluginRuntime.GetRequiredDouble(parameters, "minorInterval");
    var majorInterval = PluginRuntime.GetRequiredDouble(parameters, "majorInterval");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var extracted = ExtractContourEntities(surface, minorInterval, majorInterval);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["minorInterval"] = minorInterval,
        ["majorInterval"] = majorInterval,
        ["contourCount"] = extracted.Count,
        ["handles"] = extracted
          .Select(objectId => CivilObjectUtils.GetHandle(CivilObjectUtils.GetRequiredObject<Autodesk.AutoCAD.DatabaseServices.Entity>(transaction, objectId, OpenMode.ForRead)))
          .ToList(),
      };
    });
  }

  /// <summary>Cut, fill and net volume between <c>baseSurface</c> and <c>comparisonSurface</c>, from a temporary
  /// TIN volume surface (see the class remarks). Cut and fill areas aren't available from the API and come back null.</summary>
  public static Task<object?> ComputeSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);

      return new Dictionary<string, object?>
      {
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["cutArea"] = null,
        ["fillArea"] = null,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{CivilObjectUtils.LinearUnits(database)}^3",
          ["area"] = $"{CivilObjectUtils.LinearUnits(database)}^2",
        },
      };
    });
  }

  // ─── New analysis methods ───────────────────────────────────────────────

  /// <summary>
  /// Same volumes as ComputeSurfaceVolumeAsync, with the surface names and <c>method</c> in the answer. The method
  /// (tin_volume, average_end_area, prismoidal) is only echoed: the value always comes from a TIN volume surface.
  /// </summary>
  public static Task<object?> CalculateSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "tin_volume";

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);
      var units = CivilObjectUtils.LinearUnits(database);

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["cutVolume"] = volumeProperties.UnadjustedCutVolume,
        ["fillVolume"] = volumeProperties.UnadjustedFillVolume,
        ["netVolume"] = volumeProperties.UnadjustedNetVolume,
        ["cutArea"] = null,
        ["fillArea"] = null,
        ["method"] = method,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  /// <summary>The TIN volumes between two surfaces as a plain-text report plus the numbers; <c>format = "detailed"</c>
  /// adds the net balance and the cut/fill ratio.</summary>
  public static Task<object?> GetSurfaceVolumeReportAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var format = PluginRuntime.GetOptionalString(parameters, "format") ?? "summary";

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var volumeProperties = GetVolumeProperties(civilDoc, transaction, baseSurface, comparisonSurface);
      var units = CivilObjectUtils.LinearUnits(database);

      var cut = volumeProperties.UnadjustedCutVolume;
      var fill = volumeProperties.UnadjustedFillVolume;
      var net = volumeProperties.UnadjustedNetVolume;

      var lines = new List<string>
      {
        $"Surface Volume Report",
        $"====================",
        $"Base Surface:       {baseSurfaceName}",
        $"Comparison Surface: {comparisonSurfaceName}",
        $"",
        $"Cut Volume:  {cut:F3} {units}^3",
        $"Fill Volume: {fill:F3} {units}^3",
        $"Net Volume:  {net:F3} {units}^3",
        $"",
        "Cut and fill areas are not exposed by VolumeSurfaceProperties.",
      };

      if (format == "detailed")
      {
        lines.Add($"");
        lines.Add($"Net Balance: {(net >= 0 ? "Cut exceeds fill" : "Fill exceeds cut")} by {Math.Abs(net):F3} {units}^3");
        lines.Add($"Cut/Fill Ratio: {(fill > 0 ? (cut / fill).ToString("F3") : "N/A")}");
      }

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["format"] = format,
        ["report"] = string.Join("\n", lines),
        ["volumes"] = new Dictionary<string, object?>
        {
          ["cut"] = cut,
          ["fill"] = fill,
          ["net"] = net,
        },
        ["areas"] = new Dictionary<string, object?>
        {
          ["cut"] = null,
          ["fill"] = null,
        },
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  /// <summary>
  /// Cut and fill between two surfaces inside a <c>boundary</c> polygon (at least three points), estimated by sampling
  /// both surfaces on a grid of about 50 x 50 cells over the boundary's extents. The API has no volume-by-polygon
  /// call, so this is an approximation; cells where either surface has no elevation are skipped. Unlike the TIN
  /// volume methods it also returns cut and fill areas.
  /// </summary>
  public static Task<object?> CalculateSurfaceVolumeByRegionAsync(JsonObject? parameters)
  {
    var baseSurfaceName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");
    var boundary = ParseRequiredPoint2dArray(parameters, "boundary", "calculateSurfaceVolumeByRegion requires at least 3 boundary points.");
    if (boundary.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "calculateSurfaceVolumeByRegion requires at least 3 boundary points.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
      var comparisonSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
      var units = CivilObjectUtils.LinearUnits(database);

      // Sample elevations on a grid within the boundary and compute volume manually
      var minX = boundary.OfType<Point2d>().Min(p => p.X);
      var maxX = boundary.OfType<Point2d>().Max(p => p.X);
      var minY = boundary.OfType<Point2d>().Min(p => p.Y);
      var maxY = boundary.OfType<Point2d>().Max(p => p.Y);
      var gridSpacing = Math.Max((maxX - minX), (maxY - minY)) / 50.0;
      if (gridSpacing < 0.01) gridSpacing = 0.01;

      double cutVolume = 0;
      double fillVolume = 0;
      double cutArea = 0;
      double fillArea = 0;
      var cellArea = gridSpacing * gridSpacing;

      for (var x = minX + gridSpacing / 2; x < maxX; x += gridSpacing)
      {
        for (var y = minY + gridSpacing / 2; y < maxY; y += gridSpacing)
        {
          if (!IsPointInPolygon(x, y, boundary))
          {
            continue;
          }

          double baseZ;
          double compZ;
          try
          {
            baseZ = InvokeSurfaceElevation(baseSurface, x, y);
            compZ = InvokeSurfaceElevation(comparisonSurface, x, y);
          }
          catch
          {
            continue;
          }

          var diff = compZ - baseZ;
          if (diff > 0)
          {
            fillVolume += diff * cellArea;
            fillArea += cellArea;
          }
          else if (diff < 0)
          {
            cutVolume += Math.Abs(diff) * cellArea;
            cutArea += cellArea;
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["baseSurface"] = baseSurfaceName,
        ["comparisonSurface"] = comparisonSurfaceName,
        ["cutVolume"] = cutVolume,
        ["fillVolume"] = fillVolume,
        ["netVolume"] = fillVolume - cutVolume,
        ["cutArea"] = cutArea,
        ["fillArea"] = fillArea,
        ["regionBoundaryPointCount"] = boundary.Count,
        ["units"] = new Dictionary<string, object?>
        {
          ["volume"] = $"{units}^3",
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  /// <summary>
  /// The slope ranges stored in surface <c>name</c>'s Analysis tab, in percent, with their colors. It doesn't run a
  /// new analysis: <c>numRanges</c> and <c>ranges</c> are only echoed, and a surface without stored ranges is an error
  /// that tells the user to generate them in Surface Properties.
  /// </summary>
  public static Task<object?> AnalyzeSurfaceSlopeAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var requestedRanges = (PluginRuntime.GetParameter(parameters, "numRanges") as JsonNode)?.GetValue<int>();
    var numRanges = requestedRanges is > 0 ? requestedRanges.Value : 5;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var analysisData = surface.Analysis.GetSlopeData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored slope analysis. Generate slope ranges on the Surface Properties Analysis tab, then retry.");
      }

      var slopeBands = analysisData.Select((range, index) => new Dictionary<string, object?>
      {
        ["rangeIndex"] = index,
        ["minPercent"] = range.MinimumSlope * 100.0,
        ["maxPercent"] = range.MaximumSlope * 100.0,
        ["color"] = range.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "slope",
        ["numRanges"] = slopeBands.Count,
        ["requestedRanges"] = requestedRanges,
        ["requestedCustomRanges"] = rangesNode?.Count,
        ["slopeBands"] = slopeBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["slope"] = "percent",
        },
        ["note"] = "These are the exact slope ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  /// <summary>The elevation ranges stored in surface <c>name</c>'s Analysis tab plus its overall minimum and maximum;
  /// like the slope method, it reads what Civil 3D stored and doesn't compute new ranges.</summary>
  public static Task<object?> AnalyzeSurfaceElevationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var numRanges = (int?)((PluginRuntime.GetParameter(parameters, "numRanges") as JsonNode)?.GetValue<int>()) ?? 5;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      var units = CivilObjectUtils.LinearUnits(database);
      var analysisData = surface.Analysis.GetElevationData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored elevation analysis. Generate elevation ranges on the Surface Properties Analysis tab, then retry.");
      }

      var elevBands = analysisData.Select((range, index) => new Dictionary<string, object?>
      {
        ["rangeIndex"] = index,
        ["minElevation"] = range.MinimumElevation,
        ["maxElevation"] = range.MaximumElevation,
        ["color"] = range.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "elevation",
        ["numRanges"] = elevBands.Count,
        ["requestedRanges"] = numRanges,
        ["requestedCustomRanges"] = rangesNode?.Count,
        ["overallMin"] = generalProperties.MinimumElevation,
        ["overallMax"] = generalProperties.MaximumElevation,
        ["elevationBands"] = elevBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["elevation"] = units,
        },
        ["note"] = "These are the exact elevation ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  /// <summary>The direction (aspect) ranges stored in surface <c>name</c>'s Analysis tab, converted from radians to
  /// degrees. Reads stored ranges only.</summary>
  public static Task<object?> AnalyzeSurfaceDirectionsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var requestedRanges = (int?)((PluginRuntime.GetParameter(parameters, "numRanges") as JsonNode)?.GetValue<int>());

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var analysisData = surface.Analysis.GetDirectionData();
      if (analysisData.Length == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Surface '{name}' has no stored direction analysis. Generate direction ranges on the Surface Properties Analysis tab, then retry.");
      }

      var directionBands = analysisData.Select((band, index) => new Dictionary<string, object?>
      {
        ["sectorIndex"] = index,
        ["startAngle"] = band.MinimumDirection * 180.0 / Math.PI,
        ["endAngle"] = band.MaximumDirection * 180.0 / Math.PI,
        ["color"] = band.Scheme.ColorName,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["analysisType"] = "directions",
        ["numSectors"] = directionBands.Count,
        ["requestedSectors"] = requestedRanges,
        ["directionBands"] = directionBands,
        ["units"] = new Dictionary<string, object?>
        {
          ["angle"] = "degrees",
        },
        ["note"] = "These are the exact direction ranges stored by Civil 3D. The managed API does not report area or percent-of-surface for each range.",
      };
    });
  }

  /// <summary>
  /// Tries to add a watershed analysis to surface <c>name</c>. The managed API has no documented call for it, so this
  /// tries a few member names late-bound through CivilObjectUtils and reports <c>watershedsAdded = false</c> when none
  /// exists, rather than failing. <c>mergeAdjacentWatersheds</c> is only echoed.
  /// </summary>
  public static Task<object?> AddSurfaceWatershedsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var depthThreshold = PluginRuntime.GetParameter(parameters, "depthThreshold") is JsonNode dt ? dt.GetValue<double>() : 0.1;
    var mergeAdjacent = PluginRuntime.GetParameter(parameters, "mergeAdjacentWatersheds") is JsonNode ma && ma.GetValue<bool>();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);

      // Try to add watersheds via reflection
      var watershedsAdded = false;
      var watershedCount = 0;

      foreach (var methodName in new[] { "AddWatersheds", "CreateWatersheds", "ComputeWatersheds" })
      {
        var result = CivilObjectUtils.InvokeMethod(surface, methodName, depthThreshold);
        if (result != null)
        {
          watershedsAdded = true;
          watershedCount = result is int count ? count : 1;
          break;
        }
      }

      if (!watershedsAdded)
      {
        // Try accessing the Watersheds property
        var watershedsProperty = CivilObjectUtils.GetPropertyValue<object>(surface, "Watersheds");
        if (watershedsProperty != null)
        {
          CivilObjectUtils.InvokeMethod(watershedsProperty, "Add", depthThreshold);
          watershedsAdded = true;
        }
      }

      RebuildSurfaceIfAvailable(surface);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["depthThreshold"] = depthThreshold,
        ["mergeAdjacentWatersheds"] = mergeAdjacent,
        ["watershedsAdded"] = watershedsAdded,
        ["watershedCount"] = watershedCount,
        ["status"] = watershedsAdded ? "Watershed analysis added successfully" : "Watershed analysis may require manual configuration in Civil 3D",
      };
    });
  }

  /// <summary>
  /// Sets the minor and major contour intervals on surface <c>name</c>'s style. The change is to the style, so every
  /// surface that uses the same style changes too.
  /// </summary>
  public static Task<object?> SetSurfaceContourIntervalAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minorInterval = PluginRuntime.GetRequiredDouble(parameters, "minorInterval");
    var majorInterval = PluginRuntime.GetRequiredDouble(parameters, "majorInterval");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);

      var styleId = surface.StyleId;
      var style = CivilObjectUtils.GetRequiredObject<SurfaceStyle>(transaction, styleId, OpenMode.ForWrite);
      style.ContourStyle.MinorContourInterval = minorInterval;
      style.ContourStyle.MajorContourInterval = majorInterval;

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["minorInterval"] = minorInterval,
        ["majorInterval"] = majorInterval,
        ["applied"] = true,
        ["status"] = $"Contour intervals set: minor={minorInterval}, major={majorInterval}",
      };
    });
  }

  /// <summary>The statistics of GetSurfaceStatisticsAsync with horizontal, vertical and area units.</summary>
  public static Task<object?> GetSurfaceStatisticsDetailedAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var generalProperties = surface.GetGeneralProperties();
      var terrainProperties = GetTerrainProperties(surface);
      var units = CivilObjectUtils.LinearUnits(database);

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["minimumElevation"] = generalProperties.MinimumElevation,
        ["maximumElevation"] = generalProperties.MaximumElevation,
        ["meanElevation"] = generalProperties.MeanElevation,
        ["area2d"] = terrainProperties?.SurfaceArea2D,
        ["area3d"] = terrainProperties?.SurfaceArea3D,
        ["numberOfPoints"] = generalProperties.NumberOfPoints,
        ["numberOfTriangles"] = GetTriangleCount(surface),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = units,
          ["vertical"] = units,
          ["area"] = $"{units}^2",
        },
      };
    });
  }

  /// <summary>
  /// Samples surface <c>name</c> by <c>method</c>: <c>grid</c> (every <c>gridSpacing</c> over the surface extents,
  /// optionally only inside <c>boundary</c>), <c>points</c> (the given points), or <c>transect</c> (<c>numSamples</c>,
  /// default 50, evenly spaced from <c>startPoint</c> to <c>endPoint</c>, with the distance as <c>station</c>).
  /// Points off the surface are left out instead of failing the call, so a sample may have fewer points than asked.
  /// A small grid spacing over a large surface can return a very large answer.
  /// </summary>
  public static Task<object?> SampleSurfaceElevationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var method = PluginRuntime.GetRequiredString(parameters, "method");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var units = CivilObjectUtils.LinearUnits(database);
      var samples = new List<Dictionary<string, object?>>();

      if (method == "grid")
      {
        var gridSpacing = PluginRuntime.GetRequiredDouble(parameters, "gridSpacing");
        var extents = CivilObjectUtils.GetPropertyValue<Extents3d?>(surface, "GeometricExtents");
        if (extents == null)
        {
          throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to get extents for surface '{name}'.");
        }

        var minX = extents.Value.MinPoint.X;
        var maxX = extents.Value.MaxPoint.X;
        var minY = extents.Value.MinPoint.Y;
        var maxY = extents.Value.MaxPoint.Y;

        var boundaryNode = PluginRuntime.GetParameter(parameters, "boundary") as JsonArray;
        var boundaryPoints = boundaryNode != null
          ? boundaryNode.OfType<JsonObject>()
            .Select(p => new Point2d(p["x"]!.GetValue<double>(), p["y"]!.GetValue<double>()))
            .ToList()
          : (List<Point2d>?)null;

        for (var x = minX; x <= maxX; x += gridSpacing)
        {
          for (var y = minY; y <= maxY; y += gridSpacing)
          {
            if (boundaryPoints != null && !IsPointInPolygon(x, y, new Point2dCollection(boundaryPoints.ToArray())))
            {
              continue;
            }

            double elevation;
            try
            {
              elevation = InvokeSurfaceElevation(surface, x, y);
            }
            catch
            {
              continue;
            }

            samples.Add(new Dictionary<string, object?>
            {
              ["x"] = x,
              ["y"] = y,
              ["elevation"] = elevation,
            });
          }
        }
      }
      else if (method == "points")
      {
        var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=points requires 'points' array.");

        foreach (var pointNode in pointsNode.OfType<JsonObject>())
        {
          var x = pointNode["x"]!.GetValue<double>();
          var y = pointNode["y"]!.GetValue<double>();
          double elevation;
          try
          {
            elevation = InvokeSurfaceElevation(surface, x, y);
          }
          catch
          {
            continue;
          }

          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["elevation"] = elevation,
          });
        }
      }
      else if (method == "transect")
      {
        var startNode = PluginRuntime.GetParameter(parameters, "startPoint") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=transect requires 'startPoint'.");
        var endNode = PluginRuntime.GetParameter(parameters, "endPoint") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleSurfaceElevations with method=transect requires 'endPoint'.");
        var numSamples = (int?)((PluginRuntime.GetParameter(parameters, "numSamples") as JsonNode)?.GetValue<int>()) ?? 50;
        if (numSamples < 2) numSamples = 2;

        var x0 = startNode["x"]!.GetValue<double>();
        var y0 = startNode["y"]!.GetValue<double>();
        var x1 = endNode["x"]!.GetValue<double>();
        var y1 = endNode["y"]!.GetValue<double>();
        var totalLength = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

        for (var i = 0; i < numSamples; i++)
        {
          var t = (double)i / (numSamples - 1);
          var x = x0 + t * (x1 - x0);
          var y = y0 + t * (y1 - y0);
          double elevation;
          try
          {
            elevation = InvokeSurfaceElevation(surface, x, y);
          }
          catch
          {
            continue;
          }

          samples.Add(new Dictionary<string, object?>
          {
            ["x"] = x,
            ["y"] = y,
            ["station"] = t * totalLength,
            ["elevation"] = elevation,
          });
        }
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown sampling method '{method}'. Use 'grid', 'points', or 'transect'.");
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = name,
        ["method"] = method,
        ["sampleCount"] = samples.Count,
        ["samples"] = samples,
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = units,
          ["vertical"] = units,
        },
      };
    });
  }

  /// <summary>
  /// Creates TIN surface <c>name</c> from a DEM file (.dem, .tif, .tiff, .asc, .adf). The path goes through
  /// FileBoundary.ResolveImportPath, so it must be inside the import roots and not in the suite's shared folder. A
  /// <c>coordinateSystem</c> is refused up front because AddDEMFile can't assign one; if the import fails, the new
  /// surface is erased so no empty surface is left behind.
  /// </summary>
  public static Task<object?> CreateSurfaceFromDemAsync(JsonObject? parameters)
  {
    var filePath = FileBoundary.ResolveImportPath(
      PluginRuntime.GetRequiredString(parameters, "filePath"),
      ".dem", ".tif", ".tiff", ".asc", ".adf");
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var coordinateSystem = PluginRuntime.GetOptionalString(parameters, "coordinateSystem");

    if (!string.IsNullOrWhiteSpace(coordinateSystem))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "createSurfaceFromDem cannot assign a coordinate system through SurfaceDefinitionDEMFiles.AddDEMFile. " +
        "Assign the drawing coordinate system explicitly before importing the DEM.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);

      var surfaceId = CreateTinSurface(name, styleId);
      var surface = CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, surfaceId, OpenMode.ForWrite);
      try
      {
        surface.DEMFilesDefinition.AddDEMFile(filePath);
      }
      catch (Exception exception)
      {
        surface.Erase();
        throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Unable to import DEM file '{filePath}': {exception.Message}");
      }
      if (!string.IsNullOrWhiteSpace(layer)) surface.Layer = layer;
      if (!string.IsNullOrWhiteSpace(description)) surface.Description = description;
      surface.Rebuild();

      return new Dictionary<string, object?>
      {
        ["name"] = surface.Name,
        ["handle"] = CivilObjectUtils.GetHandle(surface),
        ["filePath"] = filePath,
        ["created"] = true,
        ["coordinateSystem"] = null,
      };
    });
  }

  // ─── Private helpers for new methods ────────────────────────────────────

  private static bool IsPointInPolygon(double x, double y, Point2dCollection polygon)
  {
    var count = polygon.Count;
    var inside = false;
    for (int i = 0, j = count - 1; i < count; j = i++)
    {
      var xi = polygon[i].X;
      var yi = polygon[i].Y;
      var xj = polygon[j].X;
      var yj = polygon[j].Y;
      if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
      {
        inside = !inside;
      }
    }

    return inside;
  }

  private static string MapSurfaceType(CivilSurface surface)
  {
    return surface switch
    {
      TinVolumeSurface => "TINVolume",
      GridSurface => "Grid",
      _ => "TIN",
    };
  }

  /// <summary>FindElevationAtXY with the API's exception turned into CIVIL3D.API_ERROR naming the surface and
  /// point; callers that sample many points catch it and skip the point.</summary>
  private static double InvokeSurfaceElevation(CivilSurface surface, double x, double y)
  {
    try
    {
      return surface.FindElevationAtXY(x, y);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not sample surface '{surface.Name}' at ({x}, {y}): {exception.Message}");
    }
  }

  private static TerrainSurfaceProperties? GetTerrainProperties(CivilSurface surface) => surface switch
  {
    TinSurface tinSurface => tinSurface.GetTerrainProperties(),
    GridSurface gridSurface => gridSurface.GetTerrainProperties(),
    _ => null,
  };

  private static int? GetTriangleCount(CivilSurface surface) =>
    surface is TinSurface tinSurface ? tinSurface.GetTinProperties().NumberOfTriangles : null;

  private static ObjectId CreateTinSurface(string name, ObjectId styleId) => TinSurface.Create(name, styleId);

  private static Point3dCollection ParseRequiredPoint3dArray(JsonObject? parameters, string name, string errorMessage)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, name) as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    var points = new Point3dCollection();
    foreach (var pointNode in pointsNode)
    {
      if (pointNode is not JsonObject point)
      {
        continue;
      }

      var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
      var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
      var z = point["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing z.");
      points.Add(new Point3d(x, y, z));
    }

    if (points.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    return points;
  }

  private static Point2dCollection ParseRequiredPoint2dArray(JsonObject? parameters, string name, string errorMessage)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, name) as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    var points = new Point2dCollection();
    foreach (var pointNode in pointsNode)
    {
      if (pointNode is not JsonObject point)
      {
        continue;
      }

      var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
      var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
      points.Add(new Point2d(x, y));
    }

    if (points.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", errorMessage);
    }

    return points;
  }

  private static SurfaceBoundaryType ResolveBoundaryType(string boundaryType) => boundaryType.Trim().ToLowerInvariant() switch
  {
    "show" => SurfaceBoundaryType.Show,
    "hide" => SurfaceBoundaryType.Hide,
    "outer" => SurfaceBoundaryType.Outer,
    "dataclip" or "data_clip" or "data-clip" => SurfaceBoundaryType.DataClip,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported surface boundary type '{boundaryType}'."),
  };

  private static void SetSurfaceDescription(CivilSurface surface, string? description)
  {
    if (!string.IsNullOrWhiteSpace(description))
    {
      surface.Description = description;
    }
  }

  private static void RebuildSurfaceIfAvailable(CivilSurface surface)
  {
    surface.Rebuild();
  }

  private static List<ObjectId> ExtractContourEntities(CivilSurface surface, double minorInterval, double majorInterval)
  {
    if (minorInterval <= 0 || majorInterval <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Contour intervals must be greater than zero.");
    }
    var minorContours = surface switch
    {
      TinSurface tinSurface => tinSurface.ExtractContours(minorInterval),
      GridSurface gridSurface => gridSurface.ExtractContours(minorInterval),
      _ => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Contour extraction is not supported for surface type '{surface.GetType().Name}'."),
    };
    var extracted = minorContours.Cast<ObjectId>().ToList();
    if (Math.Abs(majorInterval - minorInterval) > 1.0e-9)
    {
      var majorContours = surface switch
      {
        TinSurface tinSurface => tinSurface.ExtractContours(majorInterval),
        GridSurface gridSurface => gridSurface.ExtractContours(majorInterval),
        _ => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Contour extraction is not supported for surface type '{surface.GetType().Name}'."),
      };
      extracted.AddRange(majorContours.Cast<ObjectId>());
    }
    return extracted;
  }

  /// <summary>Volumes between two surfaces via a TIN volume surface created in the caller's transaction; in a read
  /// the transaction is never committed, so the volume surface doesn't stay in the drawing.</summary>
  private static VolumeSurfaceProperties GetVolumeProperties(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface)
  {
    var volumeSurface = CreateTinVolumeSurface(civilDoc, transaction, baseSurface, comparisonSurface);
    return volumeSurface.GetVolumeProperties();
  }

  private static TinVolumeSurface CreateTinVolumeSurface(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, CivilSurface baseSurface, CivilSurface comparisonSurface)
  {
    var name = $"{baseSurface.Name}_{comparisonSurface.Name}_Volume";
    var surfaceId = TinVolumeSurface.Create(name, baseSurface.ObjectId, comparisonSurface.ObjectId);
    return CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, surfaceId, OpenMode.ForRead);
  }
}
