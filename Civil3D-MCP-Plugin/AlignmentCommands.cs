// Alignment reads, station/point conversion, create and delete.
// JSON-RPC methods (CommandDispatcher): listAlignments, getAlignment, alignmentStationToPoint, alignmentSampleStations,
// alignmentPointToStation, createAlignment, deleteAlignment. Called by src/tools/domains/alignmentDomain.ts
// (civil3d_alignment; its report action samples stations through these in TypeScript).
// Reads use CivilExecution.ReadAsync (no undo step); createAlignment and deleteAlignment use WriteAsync (one undo
// step each; delete is approval-gated in TypeScript). RequireStation and StationOffsetOf are shared with
// AlignmentEditCommands and others. Late-bound reads go through CivilObjectUtils/Civil3DCompatibility; no file access.
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind civil3d_alignment's read, create and delete actions (src/tools/domains/alignmentDomain.ts):
/// list, get with entities and dependents, station to point and back, sampling stations, create from points, delete.
/// Geometry edits (tangents, spirals, station equations, offsets, widenings) are in AlignmentEditCommands.
/// </summary>
public static class AlignmentCommands
{
  /// <summary>How far past either end a station may be and still count as on the alignment (drawing units).</summary>
  private const double StationTolerance = 0.001;

  /// <summary>
  /// listAlignments: every alignment in the drawing as a summary (name, handle, type, length, start/end station, site,
  /// profile count, isReference) under <c>alignments</c>. Read only; takes no parameters.
  /// </summary>
  public static Task<object?> ListAlignmentsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignments = new List<Dictionary<string, object?>>();
      foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
        alignments.Add(ToAlignmentSummary(alignment));
      }

      return new Dictionary<string, object?>
      {
        ["alignments"] = alignments,
      };
    });
  }

  /// <summary>
  /// getAlignment: one alignment in detail. Reads <c>name</c> (required). Returns name, handle, type, style, layer,
  /// length, stations, its entities (index, type line/arc/spiral, stations, length) sorted by start station, the
  /// profiles and corridors that depend on it, and isReference. Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown name.
  /// </summary>
  public static Task<object?> GetAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var entities = new List<Dictionary<string, object?>>();
      var entityCollection = CivilObjectUtils.GetPropertyValue<object>(alignment, "Entities");
      if (entityCollection is System.Collections.IEnumerable enumerable)
      {
        var index = 0;
        foreach (var entity in enumerable)
        {
          entities.Add(new Dictionary<string, object?>
          {
            ["index"] = index++,
            ["type"] = MapAlignmentEntityType(CivilObjectUtils.GetStringProperty(entity, "EntityType") ?? entity?.GetType().Name),
            ["startStation"] = CivilObjectUtils.GetPropertyValue<double?>(entity, "StartStation") ?? 0,
            ["endStation"] = CivilObjectUtils.GetPropertyValue<double?>(entity, "EndStation") ?? 0,
            ["length"] = CivilObjectUtils.EntityLength(entity),
          });
        }
      }

      // Civil 3D keeps entities in the order they were made, so a curve added between two tangents comes last. Read
      // down the road instead; index stays the entity's own, the number delete_entity takes.
      entities = entities.OrderBy(entity => (double)entity["startStation"]!).ToList();

      var dependentProfiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead).Name)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(alignment.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = alignment.Layer,
        ["length"] = alignment.Length,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["dependentProfiles"] = dependentProfiles,
        ["dependentCorridors"] = CorridorsUsing(civilDoc, transaction, alignment.ObjectId),
        ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
      };
    });
  }

  /// <summary>
  /// alignmentStationToPoint: the x/y at <c>station</c> and <c>offset</c> (default 0, right positive) on alignment
  /// <c>name</c> (both required except offset), with the drawing's linear units. Read only. CIVIL3D.OBJECT_NOT_FOUND for
  /// an unknown alignment, CIVIL3D.INVALID_INPUT for a station off the alignment.
  /// </summary>
  public static Task<object?> StationToPointAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      RequireStation(alignment, station);
      double x = 0;
      double y = 0;
      alignment.PointLocation(station, offset, ref x, ref y);

      return new Dictionary<string, object?>
      {
        ["x"] = x,
        ["y"] = y,
        ["station"] = station,
        ["offset"] = offset,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  /// <summary>
  /// alignmentSampleStations: x/y for each of <c>stations</c> (required, 1 to 200 numbers) at one <c>offset</c>
  /// (default 0) on alignment <c>name</c> (required), as <c>samples</c>. Read only. CIVIL3D.INVALID_INPUT for an empty,
  /// too long or non-numeric list or any station off the alignment (the whole call fails), CIVIL3D.OBJECT_NOT_FOUND
  /// for an unknown alignment.
  /// </summary>
  public static Task<object?> SampleStationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;
    if (stationsNode == null || stationsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations requires at least one station.");
    }
    if (stationsNode.Count > 200)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations accepts at most 200 stations.");
    }

    var stations = stationsNode.Select(node =>
      node?.GetValue<double>()
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every station must be numeric."))
      .ToArray();

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var units = CivilObjectUtils.LinearUnits(database);
      var samples = new List<Dictionary<string, object?>>(stations.Length);

      foreach (var station in stations)
      {
        RequireStation(alignment, station);
        double x = 0;
        double y = 0;
        alignment.PointLocation(station, offset, ref x, ref y);
        samples.Add(new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["station"] = station,
          ["offset"] = offset,
          ["units"] = units,
        });
      }

      return new Dictionary<string, object?> { ["samples"] = samples };
    });
  }

  /// <summary>
  /// alignmentPointToStation: the station and signed offset of point <c>x</c>, <c>y</c> on alignment <c>name</c> (all
  /// required), plus <c>distanceFromAlignment</c> and units. Read only. CIVIL3D.INVALID_INPUT for a point beyond the
  /// alignment's ends, CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.
  /// </summary>
  public static Task<object?> PointToStationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var (station, offset) = StationOffsetOf(alignment, x, y);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  /// <summary>
  /// Creates alignment <c>name</c> through <c>points</c> (at least two): a temporary polyline is drawn and converted
  /// with curves added between tangents, and the polyline is erased. Optional <c>style</c>, <c>layer</c>,
  /// <c>labelSet</c> and <c>site</c> fall back to the drawing's defaults (LookupUtils). A write (one undo step).
  /// Returns name, handle, <c>created</c> and a note when curves were added. Throws CIVIL3D.INVALID_INPUT for fewer
  /// than two points, a point that isn't an object or lacks a numeric x/y (naming the point; StationMath.ReadVertices)
  /// or <c>type</c> = offset (use alignmentOffsetCreate), CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// style, layer, label set or site.
  /// </summary>
  public static Task<object?> CreateAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    if (string.Equals(PluginRuntime.GetOptionalString(parameters, "type"), "offset", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "An offset alignment is made from its parent: use action=offset_create.");
    }

    // Read and checked before the host queue: every point becomes a vertex, so its index is the vertex index.
    var vertices = StationMath.ReadVertices(PluginRuntime.GetParameter(parameters, "points") as JsonArray, "createAlignment");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var polyline = new Polyline();
      for (var index = 0; index < vertices.Count; index++)
      {
        polyline.AddVertexAt(index, new Point2d(vertices[index].X, vertices[index].Y), 0, 0, 0);
      }

      var polylineId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      var polylineOptions = new PolylineOptions
      {
        AddCurvesBetweenTangents = true,
        EraseExistingEntities = true,
        PlineId = polylineId,
      };

      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "site"));

      var alignmentId = Alignment.Create(civilDoc, polylineOptions, name, siteId, layerId, styleId, labelSetId);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["created"] = true,
        ["note"] = vertices.Count > 2
          ? "Curves were added between the tangents, at the radius in the drawing's alignment settings, so the alignment passes near the interior points, not through them."
          : null,
      };
    });
  }

  /// <summary>
  /// Erases alignment <c>name</c> and the profiles on it (the answer names them); approval-gated on the TypeScript
  /// side. A corridor built on it is refused with CIVIL3D.CONFLICT: Civil 3D would erase the alignment and leave the
  /// corridor broken. A write (one undo step). CIVIL3D.OBJECT_NOT_FOUND for an unknown name.
  /// </summary>
  public static Task<object?> DeleteAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var corridors = CorridorsUsing(civilDoc, transaction, alignment.ObjectId);
      if (corridors.Count > 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Alignment '{alignment.Name}' is used by corridor {string.Join(", ", corridors)}. Delete or change the corridor first.");
      }

      var profiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead).Name)
        .ToList();
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      writeAlignment.Erase();

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
        ["profilesDeleted"] = profiles,
      };
    });
  }

  /// <summary>The corridors with a baseline on this alignment.</summary>
  private static List<string> CorridorsUsing(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, ObjectId alignmentId)
  {
    var names = new List<string>();
    foreach (ObjectId corridorId in civilDoc.CorridorCollection)
    {
      var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
      if (corridor.Baselines.Any(baseline => baseline.AlignmentId == alignmentId))
      {
        names.Add(corridor.Name);
      }
    }

    return names;
  }

  /// <summary>Refuses a station off the alignment, which Civil 3D reports only as "PointNotOnEntity".</summary>
  internal static void RequireStation(Alignment alignment, double station)
  {
    if (station < alignment.StartingStation - StationTolerance || station > alignment.EndingStation + StationTolerance)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Station {station} is off alignment '{alignment.Name}', which runs from {alignment.StartingStation} to {alignment.EndingStation}.");
    }
  }

  /// <summary>The station and offset of a point, or a plain refusal for one beyond the alignment's ends.</summary>
  internal static (double Station, double Offset) StationOffsetOf(Alignment alignment, double x, double y)
  {
    double station = 0;
    double offset = 0;
    try
    {
      alignment.StationOffset(x, y, ref station, ref offset);
    }
    catch (Exception exception) when (exception.GetType().Name == "PointNotOnEntityException")
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Point ({x}, {y}) isn't alongside alignment '{alignment.Name}': it is beyond the alignment's ends.");
    }

    return (station, offset);
  }

  /// <summary>The short form of an alignment used by listAlignments.</summary>
  private static Dictionary<string, object?> ToAlignmentSummary(Alignment alignment)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = alignment.Name,
      ["handle"] = CivilObjectUtils.GetHandle(alignment),
      ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
      ["length"] = alignment.Length,
      ["startStation"] = alignment.StartingStation,
      ["endStation"] = alignment.EndingStation,
      ["site"] = CivilObjectUtils.GetStringProperty(alignment, "SiteName"),
      ["profileCount"] = alignment.GetProfileIds().Count,
      ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
    };
  }

  /// <summary>Civil 3D's alignment type text as the tool's word: offset, rail, curb_return or centerline (default).</summary>
  private static string MapAlignmentType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("offset"))
    {
      return "offset";
    }

    if (text.Contains("rail"))
    {
      return "rail";
    }

    if (text.Contains("curb"))
    {
      return "curb_return";
    }

    return "centerline";
  }

  /// <summary>An alignment entity's type text as spiral, arc (curves) or line (default).</summary>
  private static string MapAlignmentEntityType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("spiral"))
    {
      return "spiral";
    }

    if (text.Contains("curve") || text.Contains("arc"))
    {
      return "arc";
    }

    return "line";
  }
}
