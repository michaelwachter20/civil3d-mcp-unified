// Plain AutoCAD entities in model space: 2D/3D polylines, lines, DBText and MText.
// JSON-RPC methods (CommandDispatcher): createPolyline, create3dPolyline, createLineSegment, createText, createMText.
// Called by src/tools/domains/geometryDomain.ts (civil3d_geometry and its acad_create_* aliases).
// Every method is a CivilExecution.WriteAsync: command context, document lock, one transaction, one undo step, and
// the view follows the new entity. Layers are looked up with LookupUtils.GetLayerId (unknown layer ->
// CIVIL3D.OBJECT_NOT_FOUND listing the layers; no layer -> current layer). No file access, no reflection.
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plain AutoCAD geometry for civil3d_geometry: 2D and 3D polylines, lines, text and MText, added to model space as
/// writes (one undo step each, the view follows). Anything beyond these is done with civil3d_execute.
/// </summary>
public static class AcadCommands
{
  /// <summary>
  /// createPolyline: adds a 2D lightweight polyline to model space (changes the drawing). Reads <c>points</c>
  /// (required, array of at least two <c>{x, y}</c>), <c>closed</c> (optional int, 1 = closed) and <c>layer</c>
  /// (optional). Returns <c>handle</c>, <c>vertexCount</c>, <c>closed</c>, <c>layer</c>. Throws CIVIL3D.INVALID_INPUT
  /// for fewer than two points or a point missing x/y, CIVIL3D.OBJECT_NOT_FOUND for an unknown layer.
  /// </summary>
  public static Task<object?> CreatePolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var polyline = new Polyline();
      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject point)
        {
          continue;
        }

        var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        polyline.AddVertexAt(index, new Point2d(x, y), 0, 0, 0);
      }

      polyline.Closed = closed;

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        polyline.LayerId = layerId;
      }

      var polylineId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline>(transaction, polylineId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = created.NumberOfVertices,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
      };
    });
  }

  /// <summary>
  /// createText: adds single-line text (DBText) to model space (changes the drawing). Reads <c>text</c>, <c>x</c>,
  /// <c>y</c> (required), <c>z</c> (default 0), <c>height</c> (default 2.5), <c>rotation</c> (radians, default 0) and
  /// <c>layer</c> (optional). Returns the handle and the stored text, position, height, rotation and layer. Throws
  /// CIVIL3D.INVALID_INPUT for a missing required value, CIVIL3D.OBJECT_NOT_FOUND for an unknown layer.
  /// </summary>
  public static Task<object?> CreateTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 2.5d;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var dbText = new DBText
      {
        TextString = text,
        Position = new Point3d(x, y, z),
        Height = height,
        Rotation = rotation,
      };

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        dbText.LayerId = layerId;
      }

      var textId = modelSpace.AppendEntity(dbText);
      transaction.AddNewlyCreatedDBObject(dbText, true);

      var created = CivilObjectUtils.GetRequiredObject<DBText>(transaction, textId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.TextString,
        ["x"] = created.Position.X,
        ["y"] = created.Position.Y,
        ["z"] = created.Position.Z,
        ["height"] = created.Height,
        ["rotation"] = created.Rotation,
        ["layer"] = created.Layer,
      };
    });
  }

  /// <summary>
  /// create3dPolyline: adds a simple 3D polyline to model space (changes the drawing). Reads <c>points</c> (required,
  /// at least two <c>{x, y, z}</c>, z required), <c>closed</c> (optional int, 1 = closed) and <c>layer</c> (optional).
  /// Returns <c>handle</c>, <c>vertexCount</c>, <c>closed</c>, <c>layer</c>. Throws CIVIL3D.INVALID_INPUT for fewer
  /// than two (valid) points or a missing coordinate, CIVIL3D.OBJECT_NOT_FOUND for an unknown layer.
  /// </summary>
  public static Task<object?> Create3dPolylineAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two points.");
    }

    var closed = PluginRuntime.GetOptionalInt(parameters, "closed") == 1;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var pointCollection = new Point3dCollection();
      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject point)
        {
          continue;
        }

        var x = point["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = point["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        var z = point["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing z.");
        pointCollection.Add(new Point3d(x, y, z));
      }

      if (pointCollection.Count < 2)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "create3dPolyline requires at least two valid points.");
      }

      using var polyline3d = new Polyline3d(Poly3dType.SimplePoly, pointCollection, closed);

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        polyline3d.LayerId = layerId;
      }

      var polylineId = modelSpace.AppendEntity(polyline3d);
      transaction.AddNewlyCreatedDBObject(polyline3d, true);

      var created = CivilObjectUtils.GetRequiredObject<Polyline3d>(transaction, polylineId, OpenMode.ForRead);
      var vertexCount = 0;
      foreach (ObjectId vertexId in created)
      {
        _ = vertexId;
        vertexCount++;
      }

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["vertexCount"] = vertexCount,
        ["closed"] = created.Closed,
        ["layer"] = created.Layer,
      };
    });
  }

  /// <summary>
  /// createLineSegment: adds a Line to model space (changes the drawing). Reads <c>startX</c>, <c>startY</c>,
  /// <c>endX</c>, <c>endY</c> (required), <c>startZ</c>/<c>endZ</c> (default 0) and <c>layer</c> (optional). Returns
  /// the handle as <c>lineId</c>, both end points, <c>length</c> and <c>layer</c>. Throws CIVIL3D.INVALID_INPUT for a
  /// missing coordinate, CIVIL3D.OBJECT_NOT_FOUND for an unknown layer.
  /// </summary>
  public static Task<object?> CreateLineSegmentAsync(JsonObject? parameters)
  {
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startZ = PluginRuntime.GetOptionalDouble(parameters, "startZ") ?? 0d;
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");
    var endZ = PluginRuntime.GetOptionalDouble(parameters, "endZ") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var line = new Line(new Point3d(startX, startY, startZ), new Point3d(endX, endY, endZ));

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        line.LayerId = layerId;
      }

      var lineId = modelSpace.AppendEntity(line);
      transaction.AddNewlyCreatedDBObject(line, true);

      var created = CivilObjectUtils.GetRequiredObject<Line>(transaction, lineId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["lineId"] = CivilObjectUtils.GetHandle(created),
        ["startX"] = created.StartPoint.X,
        ["startY"] = created.StartPoint.Y,
        ["startZ"] = created.StartPoint.Z,
        ["endX"] = created.EndPoint.X,
        ["endY"] = created.EndPoint.Y,
        ["endZ"] = created.EndPoint.Z,
        ["length"] = created.Length,
        ["layer"] = created.Layer,
      };
    });
  }

  /// <summary>
  /// createMText: adds multiline text to model space (changes the drawing). Reads <c>text</c>, <c>x</c>, <c>y</c>
  /// (required), <c>z</c> (default 0), <c>width</c> (0 = no wrapping), <c>textHeight</c> (default 2.5),
  /// <c>rotation</c> (radians, default 0) and <c>layer</c> (optional). Returns the handle and the stored contents,
  /// location, height, rotation, width and layer. Throws CIVIL3D.INVALID_INPUT for a missing required value,
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown layer.
  /// </summary>
  public static Task<object?> CreateMTextAsync(JsonObject? parameters)
  {
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0d;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 0d;
    var textHeight = PluginRuntime.GetOptionalDouble(parameters, "textHeight") ?? 2.5d;
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0d;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      using var mtext = new MText
      {
        Contents = text,
        Location = new Point3d(x, y, z),
        TextHeight = textHeight,
        Rotation = rotation,
      };

      if (width > 0)
      {
        mtext.Width = width;
      }

      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        mtext.LayerId = layerId;
      }

      var mtextId = modelSpace.AppendEntity(mtext);
      transaction.AddNewlyCreatedDBObject(mtext, true);

      var created = CivilObjectUtils.GetRequiredObject<MText>(transaction, mtextId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(created),
        ["text"] = created.Contents,
        ["x"] = created.Location.X,
        ["y"] = created.Location.Y,
        ["z"] = created.Location.Z,
        ["textHeight"] = created.TextHeight,
        ["rotation"] = created.Rotation,
        ["width"] = created.Width,
        ["layer"] = created.Layer,
      };
    });
  }
}
