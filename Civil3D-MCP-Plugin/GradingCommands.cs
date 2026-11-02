using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Civil 3D Grading MCP tools.
///
/// Grading groups and gradings are out of reach: the .NET Grading class has no members, GradingGroup isn't public, a
/// Site has no way to them, and the COM interfaces have none either. The tools count what's in the drawing and say so,
/// because an empty list or "not found" would be false. Criteria and feature lines are ordinary objects.
/// </summary>
public static class GradingCommands
{
  private const string OutOfReach =
    "Civil 3D's API doesn't reach grading groups or gradings, in .NET or COM. Use the Grading Creation Tools. A group's surface is an ordinary surface, so the surface tools can read it and its volumes. Nothing was changed.";

  // -------------------------------------------------------------------------
  // listGradingGroups
  // -------------------------------------------------------------------------

  /// <summary>How many gradings are in model space; their groups can't be listed.</summary>
  public static Task<object?> ListGradingGroupsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var modelSpace = (BlockTableRecord)transaction.GetObject(CivilObjectUtils.GetModelSpaceBlockId(database, transaction), OpenMode.ForRead);
      var gradingClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Autodesk.Civil.DatabaseServices.Grading));
      var count = modelSpace.Cast<ObjectId>().Count(id => id.ObjectClass.IsDerivedFrom(gradingClass));

      return new Dictionary<string, object?>
      {
        ["groups"] = new List<object>(),
        ["gradingCount"] = count,
        ["note"] = "Civil 3D's API doesn't list grading groups, so this list is empty whether or not there are any: gradingCount is how many gradings the drawing has.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // getGradingGroup
  // -------------------------------------------------------------------------

  public static Task<object?> GetGradingGroupAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // createGradingGroup
  // -------------------------------------------------------------------------

  public static Task<object?> CreateGradingGroupAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // deleteGradingGroup
  // -------------------------------------------------------------------------

  public static Task<object?> DeleteGradingGroupAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // getGradingGroupVolume
  // -------------------------------------------------------------------------

  public static Task<object?> GetGradingGroupVolumeAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // createSurfaceFromGradingGroup
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSurfaceFromGradingGroupAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // listGradings
  // -------------------------------------------------------------------------

  public static Task<object?> ListGradingsAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // getGrading
  // -------------------------------------------------------------------------

  public static Task<object?> GetGradingAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // createGrading
  // -------------------------------------------------------------------------

  public static Task<object?> CreateGradingAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // deleteGrading
  // -------------------------------------------------------------------------

  public static Task<object?> DeleteGradingAsync(JsonObject? parameters) => throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", OutOfReach);

  // -------------------------------------------------------------------------
  // listGradingCriteria
  // -------------------------------------------------------------------------

  public static Task<object?> ListGradingCriteriaAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var criteriaList = new List<Dictionary<string, object?>>();

      foreach (ObjectId setId in civilDoc.Styles.GradingCriteriaSets)
      {
        var setObj = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Styles.GradingCriteriaSet>(transaction, setId, OpenMode.ForRead);
        var setName = CivilObjectUtils.GetName(setObj) ?? string.Empty;

        foreach (ObjectId criteriaId in setObj.GradingCriteriaIds())
        {
          var criteriaObj = transaction.GetObject(criteriaId, OpenMode.ForRead);
          criteriaList.Add(new Dictionary<string, object?>
          {
            ["setName"] = setName,
            ["name"] = CivilObjectUtils.GetName(criteriaObj),
            ["handle"] = CivilObjectUtils.GetHandle(criteriaObj),
            ["description"] = CivilObjectUtils.GetStringProperty(criteriaObj, "Description"),
          });
        }
      }

      return new Dictionary<string, object?> { ["criteriaList"] = criteriaList };
    });
  }

  // -------------------------------------------------------------------------
  // listFeatureLines
  // -------------------------------------------------------------------------

  public static Task<object?> ListFeatureLinesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var featureLines = EnumerateFeatureLines(database, transaction)
        .Select(featureLine => ToFeatureLineSummary(featureLine, transaction))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["featureLines"] = featureLines,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getFeatureLine
  // -------------------------------------------------------------------------

  public static Task<object?> GetFeatureLineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var featureLine = FindFeatureLineByName(civilDoc, transaction, name, database)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Feature line '{name}' not found.");

      return ToFeatureLineDetail(featureLine, database, transaction);
    });
  }

  // -------------------------------------------------------------------------
  // exportFeatureLineAsPolyline
  // -------------------------------------------------------------------------

  public static Task<object?> ExportFeatureLineAsPolylineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var targetLayer = PluginRuntime.GetOptionalString(parameters, "targetLayer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var featureLine = FindFeatureLineByName(civilDoc, transaction, name, database)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Feature line '{name}' not found.");

      var vertices = GetFeatureLineVertices(featureLine);
      if (vertices.Count < 2)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Feature line '{name}' does not expose enough vertices to export.");
      }

      var modelSpaceId = CivilObjectUtils.GetModelSpaceBlockId(database, transaction);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, modelSpaceId, OpenMode.ForWrite);
      var polylinePoints = new Point3dCollection();
      foreach (var vertex in vertices)
      {
        polylinePoints.Add(vertex);
      }

      using var polyline3d = new Polyline3d(Poly3dType.SimplePoly, polylinePoints, false);
      var polylineId = modelSpace.AppendEntity(polyline3d);
      transaction.AddNewlyCreatedDBObject(polyline3d, true);

      var resolvedLayer = targetLayer
        ?? CivilObjectUtils.GetStringProperty(featureLine, "Layer")
        ?? "0";
      CivilObjectUtils.TrySetLayer(polyline3d, resolvedLayer, database, transaction);

      var createdPolyline = CivilObjectUtils.GetRequiredObject<Polyline3d>(transaction, polylineId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["sourceFeatureLineName"] = CivilObjectUtils.GetName(featureLine) ?? name,
        ["sourceFeatureLineHandle"] = CivilObjectUtils.GetHandle(featureLine),
        ["polylineHandle"] = CivilObjectUtils.GetHandle(createdPolyline),
        ["targetLayer"] = resolvedLayer,
        ["vertexCount"] = vertices.Count,
        ["exported"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // createFeatureLine
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFeatureLineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;

    if (pointsNode == null || pointsNode.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createFeatureLine requires at least 2 points.");
    }

    var points = pointsNode.Select(node =>
    {
      if (node is not JsonObject pt)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each point must be a JSON object with x, y, z.");
      }
      var x = pt["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point missing x.");
      var y = pt["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point missing y.");
      var z = pt["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Feature-line point missing z; elevation 0 will not be assumed.");
      return new Point3d(x, y, z);
    }).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Get or find a site to add the feature line to
      var siteIds = civilDoc.GetSiteIds().Cast<ObjectId>();
      var firstSiteId = siteIds.FirstOrDefault();

      // Build a Point3dCollection for the feature line
      var pointCollection = new Point3dCollection();
      foreach (var pt in points)
      {
        pointCollection.Add(pt);
      }

      // Model space by name, not the current space: on a layout tab the line landed in paper space, where nothing finds it.
      var layerId = LookupUtils.GetLayerId(database, transaction, layer);
      var modelSpaceId = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead)[BlockTableRecord.ModelSpace];
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, modelSpaceId, OpenMode.ForWrite);
      using var sourcePolyline = new Polyline3d(Poly3dType.SimplePoly, pointCollection, false);
      var sourceId = modelSpace.AppendEntity(sourcePolyline);
      transaction.AddNewlyCreatedDBObject(sourcePolyline, true);
      var newObjectId = firstSiteId.IsNull
        ? Autodesk.Civil.DatabaseServices.FeatureLine.Create(name, sourceId)
        : Autodesk.Civil.DatabaseServices.FeatureLine.Create(name, sourceId, firstSiteId);
      sourcePolyline.Erase();
      var fl = transaction.GetObject(newObjectId, OpenMode.ForWrite);

      // Set name and layer
      if (!string.IsNullOrWhiteSpace(name))
      {
        CivilObjectUtils.TrySetName(fl, name);
      }
      ((Autodesk.AutoCAD.DatabaseServices.Entity)fl).LayerId = layerId;

      return new Dictionary<string, object?>
      {
        ["handle"] = CivilObjectUtils.GetHandle(fl),
        ["name"] = CivilObjectUtils.GetName(fl) ?? name,
        ["layer"] = layer,
        ["pointCount"] = points.Count,
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private static IEnumerable<DBObject> EnumerateFeatureLines(
    Database database,
    Transaction transaction)
  {
    var modelSpaceId = CivilObjectUtils.GetModelSpaceBlockId(database, transaction);
    var modelSpace = transaction.GetObject(modelSpaceId, OpenMode.ForRead) as BlockTableRecord;
    if (modelSpace == null)
    {
      yield break;
    }

    foreach (ObjectId id in modelSpace)
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead) as DBObject;
      if (obj?.GetType().FullName == "Autodesk.Civil.DatabaseServices.FeatureLine")
      {
        yield return obj;
      }
    }
  }

  private static DBObject? FindFeatureLineByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name,
    Database database)
  {
    // Feature lines are AutoCAD entities in model space — iterate BlockTableRecord
    var modelSpaceId = CivilObjectUtils.GetModelSpaceBlockId(database, transaction);
    var btr = transaction.GetObject(modelSpaceId, OpenMode.ForRead) as BlockTableRecord;
    if (btr == null) return null;

    var featureLineTypeName = "Autodesk.Civil.DatabaseServices.FeatureLine";
    foreach (ObjectId id in btr)
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead);
      if (obj.GetType().FullName == featureLineTypeName)
      {
        var objName = CivilObjectUtils.GetName(obj);
        if (string.Equals(objName, name, StringComparison.OrdinalIgnoreCase))
        {
          return obj;
        }
      }
    }
    return null;
  }

  private static Dictionary<string, object?> ToFeatureLineSummary(DBObject featureLine, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(featureLine),
      ["handle"] = CivilObjectUtils.GetHandle(featureLine),
      ["layer"] = CivilObjectUtils.GetStringProperty(featureLine, "Layer"),
      ["style"] = GetFeatureLineStyleName(featureLine, transaction),
    };
  }

  private static Dictionary<string, object?> ToFeatureLineDetail(
    DBObject featureLine,
    Database database,
    Transaction transaction)
  {
    var vertices = GetFeatureLineVertices(featureLine);
    var minElevation = vertices.Count > 0 ? vertices.Min(point => point.Z) : 0d;
    var maxElevation = vertices.Count > 0 ? vertices.Max(point => point.Z) : 0d;

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(featureLine) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(featureLine),
      ["layer"] = CivilObjectUtils.GetStringProperty(featureLine, "Layer") ?? string.Empty,
      ["style"] = GetFeatureLineStyleName(featureLine, transaction) ?? string.Empty,
      ["length"] = GetFeatureLineLength(featureLine),
      ["vertexCount"] = vertices.Count,
      ["vertices"] = vertices.Select(ToPointData).ToList(),
      ["minElevation"] = minElevation,
      ["maxElevation"] = maxElevation,
      ["units"] = CivilObjectUtils.LinearUnits(database),
    };
  }

  private static string? GetFeatureLineStyleName(DBObject featureLine, Transaction transaction)
  {
    try
    {
      var styleName = CivilObjectUtils.GetStringProperty(featureLine, "StyleName");
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        return styleName;
      }

      var styleIdObject = CivilObjectUtils.GetPropertyValue<object>(featureLine, "StyleId");
      if (styleIdObject is not ObjectId styleId || styleId == ObjectId.Null)
      {
        return null;
      }

      var style = transaction.GetObject(styleId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }
    catch
    {
      return null;
    }
  }

  private static double GetFeatureLineLength(DBObject featureLine)
  {
    foreach (var propertyName in new[] { "Length3D", "Length2D", "Length" })
    {
      var value = CivilObjectUtils.GetDoubleProperty(featureLine, propertyName);
      if (value.HasValue)
      {
        return value.Value;
      }
    }

    var vertices = GetFeatureLineVertices(featureLine);
    if (vertices.Count < 2)
    {
      return 0d;
    }

    var totalLength = 0d;
    for (var index = 1; index < vertices.Count; index++)
    {
      totalLength += vertices[index - 1].DistanceTo(vertices[index]);
    }

    return totalLength;
  }

  private static List<Point3d> GetFeatureLineVertices(DBObject featureLine)
  {
    if (featureLine is not Autodesk.Civil.DatabaseServices.FeatureLine typedFeatureLine)
      return new List<Point3d>();
    return DeduplicateSequentialPoints(
      typedFeatureLine.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints).Cast<Point3d>());
  }

  private static List<Point3d> DeduplicateSequentialPoints(IEnumerable<Point3d> points)
  {
    var result = new List<Point3d>();
    foreach (var point in points)
    {
      if (result.Count == 0 || !point.IsEqualTo(result[^1], new Tolerance(1e-8, 1e-8)))
      {
        result.Add(point);
      }
    }

    return result;
  }

  private static Dictionary<string, object?> ToPointData(Point3d point)
  {
    return new Dictionary<string, object?>
    {
      ["x"] = point.X,
      ["y"] = point.Y,
      ["z"] = point.Z,
    };
  }

}
