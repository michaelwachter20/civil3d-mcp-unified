// Geometry edits on existing horizontal alignments, plus offset alignments and widenings.
// JSON-RPC methods (CommandDispatcher): alignmentAddTangent, alignmentAddCurve, alignmentAddSpiral,
// alignmentDeleteEntity, alignmentSetStationEquation, alignmentGetStationOffset, alignmentOffsetCreate,
// alignmentWidenTransition. Called by src/tools/domains/alignmentDomain.ts (civil3d_alignment edit actions).
// Every method except alignmentGetStationOffset (CivilExecution.ReadAsync) is a CivilExecution.WriteAsync: one
// transaction, one undo step. Entity indexes are the alignment's own entity order (creation order), the same index
// getAlignment reports. No file access, no reflection.
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D horizontal alignments:
/// add_tangent, add_curve, add_spiral, delete_entity,
/// set_station_equation, get_station_offset,
/// offset_create, widen_transition.
/// </summary>
public static class AlignmentEditCommands
{
  // ─── alignmentAddTangent ──────────────────────────────────────────────────

  /// <summary>
  /// alignmentAddTangent: adds a fixed line from (<c>startX</c>, <c>startY</c>) to (<c>endX</c>, <c>endY</c>) to
  /// alignment <c>alignmentName</c> (all required). Changes the drawing. Returns the new entity's index and the
  /// coordinates. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment; Civil 3D API failures surface as internal errors.
  /// </summary>
  public static Task<object?> AddTangentAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      entities.AddFixedLine(new Point3d(startX, startY, 0), new Point3d(endX, endY, 0));

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "add_tangent",
        ["entityIndex"] = entities.Count - 1,
        ["startX"] = startX,
        ["startY"] = startY,
        ["endX"] = endX,
        ["endY"] = endY,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentAddCurve ────────────────────────────────────────────────────

  /// <summary>
  /// alignmentAddCurve: not implemented. Reads <c>alignmentName</c>, <c>passThroughX</c>, <c>passThroughY</c> and
  /// <c>radius</c> (required, so a missing one is CIVIL3D.INVALID_INPUT) and then always throws CIVIL3D.API_ERROR: the
  /// schema gives no curve direction, so nothing is created and the drawing is never opened.
  /// </summary>
  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var passThroughX = PluginRuntime.GetRequiredDouble(parameters, "passThroughX");
    var passThroughY = PluginRuntime.GetRequiredDouble(parameters, "passThroughY");
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add the requested radius-{radius} curve at ({passThroughX}, {passThroughY}): Civil 3D requires an explicit clockwise direction or additional geometry. " +
      "The current tool schema does not provide enough information, so no curve was created.");
  }

  // ─── alignmentAddSpiral ───────────────────────────────────────────────────

  /// <summary>
  /// A spiral-curve-spiral between two tangents of alignment <c>alignmentName</c>, the road layout's usual transition:
  /// <c>fromEntityIndex</c> and <c>toEntityIndex</c> are the tangents (the index alignment get shows), <c>radius</c> the
  /// curve, <c>length</c> the spiral in and <c>lengthOut</c> the spiral out (default the same). Two tangents with a
  /// curve between them are refused by Civil 3D: delete_entity the curve first. <c>spiralType</c> is clothoid
  /// (default), cubic or biquadratic. A write (one undo step); returns the new entity's index and the alignment's new
  /// length. CIVIL3D.INVALID_INPUT for an unknown spiral type or an index out of range, CIVIL3D.OBJECT_NOT_FOUND for
  /// an unknown alignment.
  /// </summary>
  public static Task<object?> AddSpiralAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var spiralName = PluginRuntime.GetOptionalString(parameters, "spiralType") ?? "clothoid";
    var fromIndex = (int)PluginRuntime.GetRequiredDouble(parameters, "fromEntityIndex");
    var toIndex = (int)PluginRuntime.GetRequiredDouble(parameters, "toEntityIndex");
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");
    var lengthOut = PluginRuntime.GetOptionalDouble(parameters, "lengthOut") ?? length;
    var spiralType = spiralName.ToLowerInvariant() switch
    {
      "clothoid" => Autodesk.Civil.SpiralType.Clothoid,
      "cubic" => Autodesk.Civil.SpiralType.CubicParabola,
      "biquadratic" => Autodesk.Civil.SpiralType.BiQuadratic,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"spiralType must be clothoid, cubic or biquadratic, not '{spiralName}'."),
    };

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, found.ObjectId, OpenMode.ForWrite);
      var entities = alignment.Entities.Cast<AlignmentEntity>().ToList();
      foreach (var index in new[] { fromIndex, toIndex })
      {
        if (index < 0 || index >= entities.Count)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Entity index {index} is out of range: alignment '{alignment.Name}' has {entities.Count} entities.");
        }
      }

      var spiral = alignment.Entities.AddFreeSCS(entities[fromIndex].EntityId, entities[toIndex].EntityId, length, lengthOut, Autodesk.Civil.DatabaseServices.SpiralParamType.Length, radius, false, spiralType);
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["spiralType"] = spiralName,
        ["radius"] = radius,
        ["lengthIn"] = length,
        ["lengthOut"] = lengthOut,
        ["entityIndex"] = alignment.Entities.Cast<AlignmentEntity>().ToList().FindIndex(entity => entity.EntityId == spiral.EntityId),
        ["newLength"] = alignment.Length,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentDeleteEntity ────────────────────────────────────────────────

  /// <summary>
  /// alignmentDeleteEntity: removes entity <c>entityIndex</c> (creation-order index from getAlignment) from alignment
  /// <c>alignmentName</c> (both required). Changes the drawing. CIVIL3D.INVALID_INPUT for an index out of range,
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.
  /// </summary>
  public static Task<object?> DeleteEntityAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      var count = entities.Count;
      if (entityIndex < 0 || entityIndex >= count)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Entity index {entityIndex} is out of range (alignment has {count} entities).");
      }

      entities.RemoveAt(entityIndex);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "delete_entity",
        ["deletedEntityIndex"] = entityIndex,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentSetStationEquation ─────────────────────────────────────────

  /// <summary>
  /// alignmentSetStationEquation: adds a station equation at <c>rawStation</c> (back) whose ahead station is
  /// <c>nominalStation</c> on alignment <c>alignmentName</c> (all required). Increasing when ahead >= back, else
  /// decreasing. Changes the drawing. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.
  /// </summary>
  public static Task<object?> SetStationEquationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var rawStation = PluginRuntime.GetRequiredDouble(parameters, "rawStation");
    var nominalStation = PluginRuntime.GetRequiredDouble(parameters, "nominalStation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var equationType = nominalStation >= rawStation
        ? StationEquationType.Increasing
        : StationEquationType.Decreasing;
      writeAlignment.StationEquations.Add(rawStation, nominalStation, equationType);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["rawStation"] = rawStation,
        ["nominalStation"] = nominalStation,
        ["equationType"] = equationType.ToString(),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentGetStationOffset ────────────────────────────────────────────

  /// <summary>
  /// alignmentGetStationOffset: the station and signed offset of point <c>x</c>, <c>y</c> on alignment
  /// <c>alignmentName</c> (all required); same answer as alignmentPointToStation. Read only. CIVIL3D.INVALID_INPUT
  /// for a point beyond the alignment's ends, CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.
  /// </summary>
  public static Task<object?> GetStationOffsetAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var (station, offset) = AlignmentCommands.StationOffsetOf(alignment, x, y);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // ─── alignmentOffsetCreate ────────────────────────────────────────────────

  /// <summary>
  /// alignmentOffsetCreate: creates offset alignment <c>offsetName</c> at <c>offset</c> (negative left, positive right)
  /// from alignment <c>alignmentName</c> (all required), with optional <c>style</c> and <c>layer</c>. Changes the
  /// drawing. Returns the names, offset and handle. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment, style or layer.
  /// </summary>
  public static Task<object?> OffsetCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var offsetName = PluginRuntime.GetRequiredString(parameters, "offsetName");
    var offset = PluginRuntime.GetRequiredDouble(parameters, "offset");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));

      // A negative offset is left of the parent, a positive one right.
      var offsetAlignmentId = Alignment.CreateOffsetAlignment(offsetName, baseAlignment.ObjectId, offset, styleId);
      var offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, offsetAlignmentId, OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(layerName))
      {
        offsetAlignment.LayerId = layerId;
      }

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = offsetAlignment.Name,
        ["offset"] = offset,
        ["handle"] = CivilObjectUtils.GetHandle(offsetAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentWidenTransition ─────────────────────────────────────────────

  /// <summary>
  /// alignmentWidenTransition: creates an offset alignment on <c>side</c> (left or anything else = right) of
  /// <c>alignmentName</c> at <c>startOffset</c> and adds a widening to <c>endOffset</c> between <c>startStation</c> and
  /// <c>endStation</c> (all required). <c>offsetName</c> defaults to "&lt;alignment&gt;_Widen_&lt;side&gt;"; the default
  /// alignment style is used. Changes the drawing. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.
  /// </summary>
  public static Task<object?> WidenTransitionAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var startOffset = PluginRuntime.GetRequiredDouble(parameters, "startOffset");
    var endOffset = PluginRuntime.GetRequiredDouble(parameters, "endOffset");
    var offsetName = PluginRuntime.GetOptionalString(parameters, "offsetName")
      ?? $"{alignmentName}_Widen_{side}";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var baseAlignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      // An offset alignment at the start offset, widened to the end offset between the two stations. Left of the
      // parent is a negative offset; Civil 3D's widening takes the distance, on the side the alignment already is.
      var signedOffset = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(startOffset) : Math.Abs(startOffset);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, null);
      var offsetAlignmentId = Alignment.CreateOffsetAlignment(offsetName, baseAlignment.ObjectId, signedOffset, styleId);
      CivilObjectUtils.GetRequiredObject<Alignment>(transaction, offsetAlignmentId, OpenMode.ForWrite)
        .OffsetAlignmentInfo.AddWidening(startStation, endStation, Math.Abs(endOffset));

      var resultAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, offsetAlignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = baseAlignment.Name,
        ["offsetName"] = resultAlignment.Name,
        ["side"] = side,
        ["startStation"] = startStation,
        ["endStation"] = endStation,
        ["startOffset"] = startOffset,
        ["endOffset"] = endOffset,
        ["handle"] = CivilObjectUtils.GetHandle(resultAlignment),
        ["success"] = true,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────
}
