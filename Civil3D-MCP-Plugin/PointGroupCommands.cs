// Point group management and point export/transform for civil3d_point (src/tools/domains/pointDomain.ts).
// JSON-RPC methods: createPointGroup, updatePointGroup, deletePointGroup, transformCogoPoints (writes,
// CivilExecution.WriteAsync: command context, document lock, one transaction, one undo step) and exportCogoPoints
// (read, CivilExecution.ReadAsync: main thread, no undo step).
// exportCogoPoints returns the text in the answer (PointText.Format); it writes no file.
// Rules: transformCogoPoints MOVES COGO points, which are often survey data; the AI may only use it when the user
// asks (server instructions). The TS side requires approval; both it and this handler refuse to move every point
// unless allPoints: true is sent, and a scale or rotation needs its base point (baseX/baseY). Point numbers must be
// whole numbers of 1 or more (JsonRpcProtocol.PositiveIntegers). Listing is in PointCommands.
// Uses the typed PointGroup / StandardPointGroupQuery API; no reflection, no file access.
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Civil 3D Point Group management and point export/transform.
///
/// Uses the documented PointGroup and StandardPointGroupQuery managed APIs.
/// </summary>
public static class PointGroupCommands
{
  // -------------------------------------------------------------------------
  // createPointGroup
  // -------------------------------------------------------------------------

  /// <summary>
  /// createPointGroup (changes the drawing, one undo step): adds a point group named <c>name</c> (required) with an
  /// optional <c>description</c> and a standard query from the optional <c>includeNumbers</c>,
  /// <c>excludeNumbers</c> (number ranges such as "1-100,205") and <c>includeDescriptions</c> (raw description
  /// patterns such as "EP*"). Returns name, handle and created. The name is not checked here first: a name already in
  /// use fails inside Civil 3D's PointGroups.Add.
  /// </summary>
  public static Task<object?> CreatePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var includeNumbers = PluginRuntime.GetOptionalString(parameters, "includeNumbers");
    var excludeNumbers = PluginRuntime.GetOptionalString(parameters, "excludeNumbers");
    var includeDescriptions = PluginRuntime.GetOptionalString(parameters, "includeDescriptions");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var newGroupId = civilDoc.PointGroups.Add(name);
      var newGroup = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, newGroupId, OpenMode.ForWrite);
      newGroup.Description = description;
      ApplyStandardQuery(newGroup, includeNumbers, excludeNumbers, includeDescriptions);

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["handle"] = CivilObjectUtils.GetHandle(newGroup),
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // updatePointGroup
  // -------------------------------------------------------------------------

  /// <summary>
  /// updatePointGroup (changes the drawing, one undo step): finds the group <c>name</c> (required, ignoring case) and
  /// sets whichever of <c>description</c>, <c>includeNumbers</c>, <c>excludeNumbers</c>, <c>includeDescriptions</c> are
  /// given; the others are kept. Returns name and updated. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown group and
  /// CIVIL3D.CONFLICT when the group uses a custom query (its criteria would be lost).
  /// </summary>
  public static Task<object?> UpdatePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var includeNumbers = PluginRuntime.GetOptionalString(parameters, "includeNumbers");
    var excludeNumbers = PluginRuntime.GetOptionalString(parameters, "excludeNumbers");
    var includeDescriptions = PluginRuntime.GetOptionalString(parameters, "includeDescriptions");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindPointGroupByName(civilDoc, transaction, name, OpenMode.ForWrite);

      if (description != null) group.Description = description;
      ApplyStandardQuery(group, includeNumbers, excludeNumbers, includeDescriptions);

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["updated"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // deletePointGroup
  // -------------------------------------------------------------------------

  /// <summary>deletePointGroup (changes the drawing, one undo step): erases the group <c>name</c> (required); its
  /// points stay in the drawing. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown group.</summary>
  public static Task<object?> DeletePointGroupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindPointGroupByName(civilDoc, transaction, name, OpenMode.ForWrite);
      group.Erase();

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // exportCogoPoints
  // -------------------------------------------------------------------------

  /// <summary>
  /// exportCogoPoints (read-only): the points as text, one line each in <c>format</c> (pnezd default, penz, xyzd, xyz,
  /// csv; PointText.Format) joined by <c>delimiter</c> (default ","). Optional filters: <c>groupName</c> and
  /// <c>pointNumbers</c>; both given means points in both. Returns format, exportedCount, data (the text) and units.
  /// Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown group and CIVIL3D.INVALID_INPUT for an unknown format or a point
  /// number that isn't a whole number of 1 or more (JsonRpcProtocol.PositiveIntegers).
  /// </summary>
  public static Task<object?> ExportCogoPointsAsync(JsonObject? parameters)
  {
    var format = PluginRuntime.GetOptionalString(parameters, "format") ?? "pnezd";
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");
    var pointNumbers = JsonRpcProtocol.PositiveIntegers(PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray, "pointNumbers");
    var delimiter = PluginRuntime.GetOptionalString(parameters, "delimiter") ?? ",";

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? allowedNumbers = null;

      if (!string.IsNullOrWhiteSpace(groupName))
      {
        var group = FindPointGroupByName(civilDoc, transaction, groupName!, OpenMode.ForRead);
        allowedNumbers = new HashSet<uint>(group.GetPointNumbers());
      }

      if (pointNumbers.Count > 0)
      {
        var explicit_ = new HashSet<uint>(pointNumbers);
        allowedNumbers = allowedNumbers == null ? explicit_ : new HashSet<uint>(allowedNumbers.Intersect(explicit_));
      }

      var sb = new StringBuilder();
      var exportedCount = 0;

      foreach (ObjectId objectId in civilDoc.CogoPoints)
      {
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, OpenMode.ForRead);
        if (allowedNumbers != null && !allowedNumbers.Contains(point.PointNumber)) continue;

        // Columns in the order importCogoPoints reads them back (PointText): pnezd is northing then easting.
        var line = PointText.Format(format, point.PointNumber, point.PointName, point.Location.X, point.Location.Y,
          point.Location.Z, point.RawDescription, delimiter);

        sb.AppendLine(line);
        exportedCount++;
      }

      return new Dictionary<string, object?>
      {
        ["format"] = format,
        ["exportedCount"] = exportedCount,
        ["data"] = sb.ToString(),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // -------------------------------------------------------------------------
  // transformCogoPoints
  // -------------------------------------------------------------------------

  /// <summary>
  /// transformCogoPoints (MOVES points; changes the drawing, one undo step): for the points in <c>pointNumbers</c>,
  /// else in point group <c>groupName</c>, else, only when <c>allPoints</c> is true, EVERY point of the drawing: scales
  /// X and Y by <c>scaleFactor</c> (default 1, must be above 0) and rotates by <c>rotateRadians</c> (default 0), both
  /// about the base point <c>baseX</c>/<c>baseY</c>, then adds <c>translateX/Y/Z</c> (default 0). Elevations are only
  /// translated. The base point is required whenever there is a scale or rotation (0,0 is only used when sent as
  /// such: about the origin a state-plane job flies miles away). Returns transformedCount, notFound (listed numbers
  /// the drawing doesn't have) and the values used. Throws CIVIL3D.INVALID_INPUT with no selection and no allPoints,
  /// for a missing base point, a scale of 0 or less, or a bad point number; CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// group; CIVIL3D.API_ERROR when Civil 3D won't move a point (the whole write is then not committed).
  /// </summary>
  public static Task<object?> TransformCogoPointsAsync(JsonObject? parameters)
  {
    var pointNumbers = JsonRpcProtocol.PositiveIntegers(PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray, "pointNumbers");
    var translateX = PluginRuntime.GetOptionalDouble(parameters, "translateX") ?? 0;
    var translateY = PluginRuntime.GetOptionalDouble(parameters, "translateY") ?? 0;
    var translateZ = PluginRuntime.GetOptionalDouble(parameters, "translateZ") ?? 0;
    var rotateRadians = PluginRuntime.GetOptionalDouble(parameters, "rotateRadians") ?? 0;
    var scaleFactor = PluginRuntime.GetOptionalDouble(parameters, "scaleFactor") ?? 1.0;
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");
    var allPoints = PluginRuntime.GetOptionalBool(parameters, "allPoints") ?? false;
    var baseX = PluginRuntime.GetOptionalDouble(parameters, "baseX");
    var baseY = PluginRuntime.GetOptionalDouble(parameters, "baseY");

    // The selection is checked here too, not only in Node: a call that names no points must not move all the survey data.
    if (pointNumbers.Count == 0 && string.IsNullOrWhiteSpace(groupName) && !allPoints)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Name the points to move (pointNumbers or groupName), or send allPoints: true to move every point in the drawing. Nothing was moved.");
    }

    if (!(scaleFactor > 0) || double.IsInfinity(scaleFactor))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"scaleFactor must be a number above 0, not {scaleFactor}. Nothing was moved.");
    }

    if ((scaleFactor != 1.0 || rotateRadians != 0) && (!baseX.HasValue || !baseY.HasValue))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "A scale or rotation needs the point it turns about: send baseX and baseY (drawing coordinates; 0 and 0 only if the origin is really meant). Nothing was moved.");
    }

    var pivotX = baseX ?? 0;
    var pivotY = baseY ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? targetNumbers = null;

      if (pointNumbers.Count > 0)
      {
        targetNumbers = new HashSet<uint>(pointNumbers);
      }
      else if (!string.IsNullOrWhiteSpace(groupName))
      {
        var group = FindPointGroupByName(civilDoc, transaction, groupName!, OpenMode.ForRead);
        targetNumbers = new HashSet<uint>(group.GetPointNumbers());
      }

      var transformedCount = 0;
      var moved = new HashSet<uint>();
      var sinRot = Math.Sin(rotateRadians);
      var cosRot = Math.Cos(rotateRadians);

      foreach (ObjectId objectId in civilDoc.CogoPoints)
      {
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, OpenMode.ForRead);
        if (targetNumbers != null && !targetNumbers.Contains(point.PointNumber)) continue;

        var writablePoint = transaction.GetObject(objectId, OpenMode.ForWrite) as CogoPoint;
        if (writablePoint == null) continue;

        // Scale and rotate about the base point, then translate.
        var dx = (writablePoint.Location.X - pivotX) * scaleFactor;
        var dy = (writablePoint.Location.Y - pivotY) * scaleFactor;
        var x = pivotX + dx * cosRot - dy * sinRot + translateX;
        var y = pivotY + dx * sinRot + dy * cosRot + translateY;
        var z = writablePoint.Location.Z + translateZ;

        SetPointCoordinate(writablePoint, x, y, z);
        moved.Add(writablePoint.PointNumber);
        transformedCount++;
      }

      return new Dictionary<string, object?>
      {
        ["transformedCount"] = transformedCount,
        ["notFound"] = pointNumbers.Where(number => !moved.Contains(number)).Distinct().ToList(),
        ["translateX"] = translateX,
        ["translateY"] = translateY,
        ["translateZ"] = translateZ,
        ["rotateRadians"] = rotateRadians,
        ["scaleFactor"] = scaleFactor,
        ["baseX"] = baseX,
        ["baseY"] = baseY,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  /// <summary>The point group named <paramref name="name"/> (ignoring case), opened in <paramref name="mode"/>.
  /// Throws CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static PointGroup FindPointGroupByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name,
    OpenMode mode)
  {
    foreach (ObjectId objectId in civilDoc.PointGroups)
    {
      var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, mode);
      if (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return group;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{name}' was not found.");
  }

  /// <summary>Sets the given include/exclude criteria on the group's standard query (creating one when the group has
  /// none) and updates the group's point list; does nothing when all three are null. Throws CIVIL3D.CONFLICT for a
  /// group with a custom query, so its criteria are never discarded.</summary>
  private static void ApplyStandardQuery(
    PointGroup group,
    string? includeNumbers,
    string? excludeNumbers,
    string? includeDescriptions)
  {
    if (includeNumbers == null && excludeNumbers == null && includeDescriptions == null)
    {
      return;
    }

    var existingQuery = group.GetQuery();
    if (existingQuery is not null && existingQuery is not StandardPointGroupQuery)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Point group '{group.Name}' uses a custom query. Standard filters were not applied because doing so would discard its existing QueryString criteria.");
    }

    var query = existingQuery as StandardPointGroupQuery ?? new StandardPointGroupQuery();
    if (includeNumbers != null) query.IncludeNumbers = includeNumbers;
    if (excludeNumbers != null) query.ExcludeNumbers = excludeNumbers;
    if (includeDescriptions != null) query.IncludeRawDescriptions = includeDescriptions;
    group.SetQuery(query);
    group.Update();
  }

  /// <summary>Moves a point (open for write) to the given easting, northing and elevation; a refusal by Civil 3D
  /// becomes CIVIL3D.API_ERROR naming the point.</summary>
  private static void SetPointCoordinate(CogoPoint point, double x, double y, double z)
  {
    try
    {
      point.Easting = x;
      point.Northing = y;
      point.Elevation = z;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"COGO point {point.PointNumber} could not be moved: {exception.Message}");
    }
  }
}
