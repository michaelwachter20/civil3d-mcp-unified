// Catchments and catchment groups: list, inspect, edit hydrology properties, copy to another group, flow path and
// boundary geometry. JSON-RPC methods (CommandDispatcher): listCatchmentGroups, getCatchmentGroup, listCatchments,
// getCatchmentProperties, setCatchmentProperties, copyCatchmentToGroup, getCatchmentFlowPath, getCatchmentBoundary.
// Called by src/tools/domains/hydrologyDomain.ts (civil3d_hydrology catchment actions).
// Reads use CivilExecution.ReadAsync; setCatchmentProperties and copyCatchmentToGroup use WriteAsync (one undo step).
// Finding groups and their catchments, and properties that differ between releases, go late-bound through
// Civil3DCompatibility. Properties Civil 3D can't answer come back as null rather than failing. No file access.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Backend commands for Civil 3D catchment management.
/// Uses the verified Autodesk.Civil.DatabaseServices.Catchment class from AeccDbMgd.dll.
/// CatchmentGroup collection access uses reflection for forward-compatibility with 2023+ API.
/// </summary>
public static class CatchmentCommands
{
  // ──────────────────────────────────────────────
  //  List Catchment Groups
  // ──────────────────────────────────────────────
  /// <summary>
  /// listCatchmentGroups: every catchment group with handle and the names of its catchments, plus
  /// <c>totalGroups</c>. Read only; no parameters.
  /// </summary>
  public static Task<object?> ListCatchmentGroupsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groupIds = GetCatchmentGroupIds(civilDoc, database);
      var groups = new List<Dictionary<string, object?>>();

      foreach (var groupId in groupIds)
      {
        var groupObj = transaction.GetObject(groupId, OpenMode.ForRead);
        if (groupObj == null) continue;

        var name = CivilObjectUtils.GetName(groupObj) ?? "(unnamed)";
        var handle = CivilObjectUtils.GetHandle(groupObj);

        // Enumerate catchments in this group
        var catchmentIds = GetCatchmentIdsInGroup(groupObj);
        var catchmentNames = new List<string>();
        foreach (var cId in catchmentIds)
        {
          var cObj = transaction.GetObject(cId, OpenMode.ForRead);
          var cName = CivilObjectUtils.GetName(cObj);
          if (cName != null) catchmentNames.Add(cName);
        }

        groups.Add(new Dictionary<string, object?>
        {
          ["name"] = name,
          ["handle"] = handle,
          ["catchmentCount"] = catchmentNames.Count,
          ["catchmentNames"] = catchmentNames,
        });
      }

      return new Dictionary<string, object?>
      {
        ["groups"] = groups,
        ["totalGroups"] = groups.Count,
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Get Catchment Group (detailed)
  // ──────────────────────────────────────────────
  /// <summary>
  /// getCatchmentGroup: group <c>groupName</c> (required) with a summary of each catchment (area, perimeter, runoff
  /// coefficient, curve number, Tc, reference surface). Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown group.
  /// </summary>
  public static Task<object?> GetCatchmentGroupAsync(JsonObject? parameters)
  {
    var groupName = PluginRuntime.GetRequiredString(parameters, "groupName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groupId = FindCatchmentGroupByName(civilDoc, database, transaction, groupName);
      var groupObj = transaction.GetObject(groupId, OpenMode.ForRead);
      var catchmentIds = GetCatchmentIdsInGroup(groupObj);

      var catchments = new List<Dictionary<string, object?>>();
      foreach (var cId in catchmentIds)
      {
        var catchment = CivilObjectUtils.GetRequiredObject<Catchment>(transaction, cId, OpenMode.ForRead);
        catchments.Add(ToCatchmentSummary(catchment));
      }

      return new Dictionary<string, object?>
      {
        ["groupName"] = CivilObjectUtils.GetName(groupObj),
        ["handle"] = CivilObjectUtils.GetHandle(groupObj!),
        ["catchments"] = catchments,
        ["catchmentCount"] = catchments.Count,
      };
    });
  }

  // ──────────────────────────────────────────────
  //  List All Catchments (across all groups)
  // ──────────────────────────────────────────────
  /// <summary>listCatchments: a summary of every catchment in every group, each with its <c>groupName</c>. Read only; no parameters.</summary>
  public static Task<object?> ListCatchmentsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groupIds = GetCatchmentGroupIds(civilDoc, database);
      var allCatchments = new List<Dictionary<string, object?>>();

      foreach (var groupId in groupIds)
      {
        var groupObj = transaction.GetObject(groupId, OpenMode.ForRead);
        var groupName = CivilObjectUtils.GetName(groupObj) ?? "(unnamed)";
        var catchmentIds = GetCatchmentIdsInGroup(groupObj);

        foreach (var cId in catchmentIds)
        {
          var catchment = CivilObjectUtils.GetRequiredObject<Catchment>(transaction, cId, OpenMode.ForRead);
          var summary = ToCatchmentSummary(catchment);
          summary["groupName"] = groupName;
          allCatchments.Add(summary);
        }
      }

      return new Dictionary<string, object?>
      {
        ["catchments"] = allCatchments,
        ["totalCatchments"] = allCatchments.Count,
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Get Catchment Properties (detailed)
  // ──────────────────────────────────────────────
  /// <summary>
  /// getCatchmentProperties: the full detail of catchment <c>catchmentName</c> (required): summary plus description,
  /// Manning's n, impervious areas, soil group, discharge and most distant points, containing group and more. Read
  /// only. CIVIL3D.OBJECT_NOT_FOUND for an unknown catchment.
  /// </summary>
  public static Task<object?> GetCatchmentPropertiesAsync(JsonObject? parameters)
  {
    var catchmentName = PluginRuntime.GetRequiredString(parameters, "catchmentName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var catchment = FindCatchmentByName(civilDoc, database, transaction, catchmentName, OpenMode.ForRead);
      return ToCatchmentDetail(catchment, transaction);
    });
  }

  // ──────────────────────────────────────────────
  //  Set Catchment Properties (edit)
  // ──────────────────────────────────────────────
  /// <summary>
  /// setCatchmentProperties: changes catchment <c>catchmentName</c> (required). Optional <c>runoffCoefficient</c>,
  /// <c>manningsCoefficient</c>, <c>curveNumber</c> (rounded to an integer), <c>description</c>, <c>newName</c>; at
  /// least one is needed. Changes the drawing. Returns the list of changes and the updated summary. Manning's n and
  /// curve number are set late-bound and listed as changed even if this release has no such property.
  /// CIVIL3D.INVALID_INPUT when nothing is given, CIVIL3D.OBJECT_NOT_FOUND for an unknown catchment.
  /// </summary>
  public static Task<object?> SetCatchmentPropertiesAsync(JsonObject? parameters)
  {
    var catchmentName = PluginRuntime.GetRequiredString(parameters, "catchmentName");

    // Optional editable properties
    var runoffCoefficient = PluginRuntime.GetOptionalDouble(parameters, "runoffCoefficient");
    var manningsCoefficient = PluginRuntime.GetOptionalDouble(parameters, "manningsCoefficient");
    var curveNumber = PluginRuntime.GetOptionalDouble(parameters, "curveNumber");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var newName = PluginRuntime.GetOptionalString(parameters, "newName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var catchment = FindCatchmentByName(civilDoc, database, transaction, catchmentName, OpenMode.ForWrite);
      var changes = new List<string>();

      if (runoffCoefficient.HasValue)
      {
        catchment.RunoffCoefficient = runoffCoefficient.Value;
        changes.Add($"RunoffCoefficient={runoffCoefficient.Value}");
      }

      if (manningsCoefficient.HasValue)
      {
        TrySetDeprecatedProperty(catchment, manningsCoefficient.Value, "ManningsCoefficient");
        changes.Add($"ManningsCoefficient={manningsCoefficient.Value}");
      }

      if (curveNumber.HasValue)
      {
        TrySetDeprecatedProperty(catchment, (int)Math.Round(curveNumber.Value), "CompositeCurveNumber", "CurveNumber");
        changes.Add($"CurveNumber={curveNumber.Value}");
      }

      if (!string.IsNullOrWhiteSpace(description))
      {
        catchment.Description = description;
        changes.Add($"Description={description}");
      }

      if (!string.IsNullOrWhiteSpace(newName))
      {
        catchment.Name = newName;
        changes.Add($"Name={newName}");
      }

      if (changes.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "setCatchmentProperties requires at least one property to change (runoffCoefficient, manningsCoefficient, curveNumber, description, newName).");
      }

      return new Dictionary<string, object?>
      {
        ["catchmentName"] = catchment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(catchment),
        ["changes"] = changes,
        ["updatedProperties"] = ToCatchmentSummary(catchment),
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Copy Catchment To Group
  // ──────────────────────────────────────────────
  /// <summary>
  /// copyCatchmentToGroup: copies catchment <c>catchmentName</c> into group <c>targetGroupName</c> (both required) with
  /// Catchment.CreateCopy. Changes the drawing. Returns the new catchment's name and handle. CIVIL3D.OBJECT_NOT_FOUND
  /// for an unknown catchment or group.
  /// </summary>
  public static Task<object?> CopyCatchmentToGroupAsync(JsonObject? parameters)
  {
    var catchmentName = PluginRuntime.GetRequiredString(parameters, "catchmentName");
    var targetGroupName = PluginRuntime.GetRequiredString(parameters, "targetGroupName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var catchment = FindCatchmentByName(civilDoc, database, transaction, catchmentName, OpenMode.ForRead);
      var targetGroupId = FindCatchmentGroupByName(civilDoc, database, transaction, targetGroupName);

      // Catchment.CreateCopy is a static method: Catchment.CreateCopy(ObjectId catchmentId, ObjectId groupId)
      var newCatchmentId = Catchment.CreateCopy(catchment.ObjectId, targetGroupId);
      var newCatchment = CivilObjectUtils.GetRequiredObject<Catchment>(transaction, newCatchmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["sourceCatchment"] = catchmentName,
        ["targetGroup"] = targetGroupName,
        ["newCatchmentName"] = newCatchment.Name,
        ["newCatchmentHandle"] = CivilObjectUtils.GetHandle(newCatchment),
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Get Catchment Flow Path
  // ──────────────────────────────────────────────
  /// <summary>
  /// getCatchmentFlowPath: the flow path of catchment <c>catchmentName</c> (required): length, 3D points and segments
  /// (type, length, slope, travel time when computed). A catchment without a flow path answers
  /// <c>hasFlowPath</c> false instead of failing. Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown catchment.
  /// </summary>
  public static Task<object?> GetCatchmentFlowPathAsync(JsonObject? parameters)
  {
    var catchmentName = PluginRuntime.GetRequiredString(parameters, "catchmentName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var catchment = FindCatchmentByName(civilDoc, database, transaction, catchmentName, OpenMode.ForRead);

      List<Dictionary<string, double>> points;
      List<Dictionary<string, object?>> segments;
      double length;
      try
      {
        var flowPath = catchment.FlowPath;
        length = catchment.FlowPathLength;
        points = flowPath.GetPath().Cast<Point3d>()
          .Select(point => new Dictionary<string, double> { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z })
          .ToList();
        segments = flowPath.GetFlowSegments()
          .Select(segment => new Dictionary<string, object?>
          {
            ["index"] = segment.Index,
            ["type"] = segment.GetType().Name,
            ["length"] = segment.Length,
            ["slope"] = segment.Slope,
            ["travelTime"] = segment.IsTravelTimeCalculated ? segment.TravelTime : null,
          })
          .ToList();
      }
      catch (Autodesk.Civil.CivilException)
      {
        // A catchment with no flow path hands back an object that throws when it's read.
        return new Dictionary<string, object?>
        {
          ["catchmentName"] = catchmentName,
          ["hasFlowPath"] = false,
          ["pointCount"] = 0,
          ["points"] = new List<object>(),
          ["segments"] = new List<object>(),
        };
      }

      return new Dictionary<string, object?>
      {
        ["catchmentName"] = catchmentName,
        ["hasFlowPath"] = points.Count > 0,
        ["length"] = length,
        ["pointCount"] = points.Count,
        ["points"] = points,
        ["segments"] = segments,
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Get Catchment Boundary
  // ──────────────────────────────────────────────
  /// <summary>
  /// getCatchmentBoundary: the 2D and 3D boundary vertices of catchment <c>catchmentName</c> (required); a boundary
  /// Civil 3D can't give comes back empty. Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown catchment.
  /// </summary>
  public static Task<object?> GetCatchmentBoundaryAsync(JsonObject? parameters)
  {
    var catchmentName = PluginRuntime.GetRequiredString(parameters, "catchmentName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var catchment = FindCatchmentByName(civilDoc, database, transaction, catchmentName, OpenMode.ForRead);

      var points2d = new List<Dictionary<string, double>>();
      try
      {
        var boundary2d = catchment.BoundaryPolyline2d;
        if (boundary2d != null)
        {
          foreach (Point2d pt in boundary2d)
          {
            points2d.Add(new Dictionary<string, double>
            {
              ["x"] = pt.X,
              ["y"] = pt.Y,
            });
          }
        }
      }
      catch
      {
        // Boundary extraction failed
      }

      var points3d = new List<Dictionary<string, double>>();
      try
      {
        var boundary3d = catchment.BoundaryPolyline3d;
        if (boundary3d != null)
        {
          foreach (Point3d pt in boundary3d)
          {
            points3d.Add(new Dictionary<string, double>
            {
              ["x"] = pt.X,
              ["y"] = pt.Y,
              ["z"] = pt.Z,
            });
          }
        }
      }
      catch
      {
        // 3D boundary extraction failed
      }

      return new Dictionary<string, object?>
      {
        ["catchmentName"] = catchmentName,
        ["boundary2dPointCount"] = points2d.Count,
        ["boundary2dPoints"] = points2d,
        ["boundary3dPointCount"] = points3d.Count,
        ["boundary3dPoints"] = points3d,
      };
    });
  }

  // ──────────────────────────────────────────────
  //  Helpers
  // ──────────────────────────────────────────────

  /// <summary>
  /// Gets catchment group ObjectIds from CivilDocument.GetCatchmentGroups() via reflection
  /// for forward-compatibility. Falls back to Database extension method.
  /// </summary>
  private static List<ObjectId> GetCatchmentGroupIds(object civilDoc, Database database)
  {
    var ids = new List<ObjectId>();
    if (Civil3DCompatibility.TryInvokeMethod(civilDoc, "GetCatchmentGroups", out var result))
    {
      foreach (var id in CivilObjectUtils.ToObjectIds(result))
      {
        ids.Add(id);
      }
    }

    if (ids.Count > 0) return ids;

    if (Civil3DCompatibility.TryInvokeLoadedStaticMethod(
      "GetCivilCatchmentGroups", typeof(Database), out result, database))
    {
      foreach (var id in CivilObjectUtils.ToObjectIds(result))
      {
        ids.Add(id);
      }
    }

    return ids;
  }

  /// <summary>
  /// Gets catchment ObjectIds from a group object. Group typically has a method or
  /// property to enumerate its child catchment IDs.
  /// </summary>
  private static List<ObjectId> GetCatchmentIdsInGroup(AcDbObject? groupObj)
  {
    if (groupObj == null) return new List<ObjectId>();

    var ids = new List<ObjectId>();

    if (Civil3DCompatibility.TryInvokeMethod(groupObj, "GetAllCatchmentIds", out var result))
    {
      foreach (var id in CivilObjectUtils.ToObjectIds(result))
      {
        ids.Add(id);
      }
    }

    if (ids.Count > 0) return ids;

    result = Civil3DCompatibility.GetPropertyValue(groupObj, "CatchmentIds");
    if (result != null)
    {
      foreach (var id in CivilObjectUtils.ToObjectIds(result))
      {
        ids.Add(id);
      }
    }

    return ids;
  }

  /// <summary>The catchment group named <paramref name="name"/> (case-insensitive). CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static ObjectId FindCatchmentGroupByName(object civilDoc, Database database, Transaction transaction, string name)
  {
    var groupIds = GetCatchmentGroupIds(civilDoc, database);
    foreach (var gId in groupIds)
    {
      var groupObj = transaction.GetObject(gId, OpenMode.ForRead);
      var gName = CivilObjectUtils.GetName(groupObj);
      if (string.Equals(gName, name, StringComparison.OrdinalIgnoreCase))
      {
        return gId;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Catchment group '{name}' was not found.");
  }

  /// <summary>
  /// The catchment named <paramref name="name"/> (case-insensitive) in any group, opened with
  /// <paramref name="openMode"/>. CIVIL3D.OBJECT_NOT_FOUND when there is none.
  /// </summary>
  private static Catchment FindCatchmentByName(object civilDoc, Database database, Transaction transaction, string name, OpenMode openMode)
  {
    var groupIds = GetCatchmentGroupIds(civilDoc, database);
    foreach (var gId in groupIds)
    {
      var groupObj = transaction.GetObject(gId, OpenMode.ForRead);
      var catchmentIds = GetCatchmentIdsInGroup(groupObj);

      foreach (var cId in catchmentIds)
      {
        var catchment = CivilObjectUtils.GetRequiredObject<Catchment>(transaction, cId, openMode);
        if (string.Equals(catchment.Name, name, StringComparison.OrdinalIgnoreCase))
        {
          return catchment;
        }
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Catchment '{name}' was not found in any group.");
  }

  /// <summary>A catchment's short answer shape; each value is null when Civil 3D can't give it.</summary>
  private static Dictionary<string, object?> ToCatchmentSummary(Catchment catchment)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = catchment.Name,
      ["handle"] = CivilObjectUtils.GetHandle(catchment),
      ["area2d"] = SafeGet(() => catchment.Area2d),
      ["perimeter2d"] = SafeGet(() => catchment.Perimeter2d),
      ["runoffCoefficient"] = SafeGet(() => catchment.RunoffCoefficient),
      ["curveNumber"] = SafeGetDouble(catchment, "CompositeCurveNumber", "CurveNumber"),
      ["timeOfConcentration"] = SafeGet(() => catchment.TimeOfConcentration),
      ["referenceSurfaceName"] = SafeGetStr(() => catchment.ReferenceSurfaceName),
    };
  }

  /// <summary>The summary plus every detail property; release-dependent ones are read late-bound under either name.</summary>
  private static Dictionary<string, object?> ToCatchmentDetail(Catchment catchment, Transaction transaction)
  {
    var detail = ToCatchmentSummary(catchment);

    // Additional detailed properties
    detail["description"] = SafeGetStr(() => catchment.Description);
    detail["manningsCoefficient"] = SafeGetDouble(catchment, "ManningsCoefficient");
    detail["imperviousArea"] = SafeGet(() => catchment.ImperviousArea);
    detail["unconnectedImperviousArea"] = SafeGetDouble(catchment, "UnconnectedImperviousArea");
    detail["hydrologicalSoilGroup"] = SafeGetEnumText(catchment, "HydrologicalSoilGroup");
    detail["hydrologicallyMostDistantLength"] = SafeGetDouble(catchment, "FlowPathLength", "HydrologicallyMostDistantLength");
    detail["antecedentWetness"] = SafeGetEnumText(catchment, "AntecedentWetness");
    detail["exclusionary"] = SafeGet(() => catchment.Exclusionary);
    detail["referencePipeNetworkName"] = SafeGetStr(() => catchment.ReferencePipeNetworkName);
    detail["referencePipeNetworkStructureName"] = SafeGetStringProperty(catchment, "ReferenceDischargeObjectName", "ReferencePipeNetworkStructureName");
    detail["timeOfConcentrationCalculationMethod"] = SafeGetEnumText(catchment, "TimeOfConcentrationCalculationMethod");

    // Discharge point
    try
    {
      var dp = catchment.DischargePoint;
      detail["dischargePoint"] = new Dictionary<string, double>
      {
        ["x"] = dp.X,
        ["y"] = dp.Y,
        ["z"] = dp.Z,
      };
    }
    catch
    {
      detail["dischargePoint"] = null;
    }

    // Hydrologically most distant point
    try
    {
      var hmdp = catchment.HydrologicallyMostDistantPoint;
      detail["hydrologicallyMostDistantPoint"] = new Dictionary<string, double>
      {
        ["x"] = hmdp.X,
        ["y"] = hmdp.Y,
        ["z"] = hmdp.Z,
      };
    }
    catch
    {
      detail["hydrologicallyMostDistantPoint"] = null;
    }

    // Containing group name
    try
    {
      var groupId = catchment.ContainingGroupId;
      if (!groupId.IsNull)
      {
        var groupObj = transaction.GetObject(groupId, OpenMode.ForRead);
        detail["containingGroupName"] = CivilObjectUtils.GetName(groupObj);
      }
    }
    catch
    {
      detail["containingGroupName"] = null;
    }

    return detail;
  }

  private static double? SafeGet(Func<double> getter)
  {
    try { return getter(); }
    catch { return null; }
  }

  private static bool? SafeGet(Func<bool> getter)
  {
    try { return getter(); }
    catch { return null; }
  }

  private static string? SafeGetStr(Func<string?> getter)
  {
    try { return getter(); }
    catch { return null; }
  }

  /// <summary>The first of <paramref name="propertyNames"/> that reads as a number (late-bound), else null.</summary>
  private static double? SafeGetDouble(object target, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var value = CivilObjectUtils.GetDoubleProperty(target, propertyName);
      if (value.HasValue)
        return value.Value;
    }

    return null;
  }

  private static string? SafeGetStringProperty(object target, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var value = CivilObjectUtils.GetStringProperty(target, propertyName);
      if (!string.IsNullOrWhiteSpace(value))
        return value;
    }

    return null;
  }

  private static string? SafeGetEnumText(object target, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var value = Civil3DCompatibility.GetPropertyValue(target, propertyName);
      if (value != null) return value.ToString();
    }

    return null;
  }

  /// <summary>Sets the first of <paramref name="propertyNames"/> this release can write (late-bound); silently does nothing if none.</summary>
  private static void TrySetDeprecatedProperty(object target, object value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      if (Civil3DCompatibility.TrySetProperty(target, propertyName, value)) return;
    }
  }
}
