using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods for COGO points behind civil3d_point (src/tools/domains/pointDomain.ts): list (optionally by
/// group, with limit/offset), get by number, create, import from text, delete by number, and list point groups.
/// Point groups' create/update/delete/export/transform are in PointGroupCommands. COGO points are often survey
/// data, which the server instructions tell the AI not to change unless the user asks.
/// </summary>
public static class PointCommands
{
  public static Task<object?> ListCogoPointsAsync(JsonObject? parameters)
  {
    var groupName = PluginRuntime.GetOptionalString(parameters, "groupName");
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? int.MaxValue;
    var offset = PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      HashSet<uint>? allowedNumbers = null;
      if (!string.IsNullOrWhiteSpace(groupName))
      {
        allowedNumbers = TryGetPointNumbersForGroup(civilDoc, transaction, groupName!);
      }

      var allPoints = EnumeratePoints(civilDoc, transaction)
        .Where(p => allowedNumbers == null || allowedNumbers.Contains(p.PointNumber))
        .OrderBy(p => p.PointNumber)
        .ToList();

      var page = allPoints.Skip(offset).Take(limit).Select(CivilObjectUtils.ToPointData).ToList();

      return new Dictionary<string, object?>
      {
        ["totalCount"] = allPoints.Count,
        ["returnedCount"] = page.Count,
        ["points"] = page,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> GetCogoPointAsync(JsonObject? parameters)
  {
    var pointNumber = (uint)PluginRuntime.GetRequiredInt(parameters, "pointNumber");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      return CivilObjectUtils.ToPointData(point);
    });
  }

  /// <summary>Creates a point for each of <c>points</c> ({x, y, z, description}); numbers come from the drawing's next
  /// point number unless <c>startNumber</c> is given, then they run startNumber, startNumber + 1, ...</summary>
  public static async Task<object?> CreateCogoPointsAsync(JsonObject? parameters)
  {
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;
    if (pointsNode == null || pointsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createCogoPoints requires a non-empty 'points' array.");
    }

    var startNumber = PluginRuntime.GetOptionalInt(parameters, "startNumber");
    var first = pointsNode.OfType<JsonObject>().FirstOrDefault();
    await PointCreationGate.RequireAsync($"{pointsNode.Count} COGO point{(pointsNode.Count == 1 ? "" : "s")}"
      + (first != null ? $", the first at E {first["x"]}, N {first["y"]}, elevation {first["z"]}" : "")
      + " (civil3d_point create).");

    return await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdNumbers = new List<uint>();
      var collection = civilDoc.CogoPoints;

      for (var index = 0; index < pointsNode.Count; index++)
      {
        if (pointsNode[index] is not JsonObject pointNode)
        {
          continue;
        }

        var x = pointNode["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing x.");
        var y = pointNode["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing y.");
        var z = pointNode["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point is missing z.");
        var description = pointNode["description"]?.GetValue<string?>() ?? string.Empty;

        var objectId = collection.Add(new Point3d(x, y, z), description, true);
        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

        if (startNumber.HasValue)
        {
          point.PointNumber = (uint)(startNumber.Value + index);
        }

        createdNumbers.Add(point.PointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["created"] = createdNumbers.Count,
        ["pointNumbers"] = createdNumbers,
      };
    });
  }

  /// <summary>
  /// Creates points from <c>data</c>, text with one point per line in <c>format</c> pnezd, penz, xyzd or xyz
  /// (PointText.Parse: X is the easting, Y the northing). A point keeps its number from the file when that number is
  /// free in the drawing (<c>keepPointNumbers</c>, default true); one already taken, or a format without numbers, gets
  /// the drawing's next number, and the answer lists each one renumbered. <c>targetSurface</c> is refused (not
  /// implemented). The whole file is read before anything is created, so a bad line creates no points.
  /// </summary>
  public static async Task<object?> ImportCogoPointsAsync(JsonObject? parameters)
  {
    var format = PluginRuntime.GetRequiredString(parameters, "format");
    var data = PluginRuntime.GetRequiredString(parameters, "data");
    var targetSurface = PluginRuntime.GetOptionalString(parameters, "targetSurface");
    var keepPointNumbers = PluginRuntime.GetOptionalBool(parameters, "keepPointNumbers") ?? true;
    if (!string.IsNullOrWhiteSpace(targetSurface))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Importing COGO points directly into a target surface is not implemented. No points were imported.");
    }

    var points = PointText.Parse(data, format).ToList();
    if (points.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "No points in 'data'.");
    }

    await PointCreationGate.RequireAsync($"{points.Count} COGO point{(points.Count == 1 ? "" : "s")} typed in by the AI "
      + $"({format}), the first at E {points[0].X}, N {points[0].Y}, elevation {points[0].Z}"
      + " (civil3d_point import with data, not a file).");

    return await CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var createdNumbers = new List<uint>();
      var renumbered = new List<Dictionary<string, object?>>();
      var usedInFile = new HashSet<uint>();
      foreach (var parsed in points)
      {
        var objectId = civilDoc.CogoPoints.Add(new Point3d(parsed.X, parsed.Y, parsed.Z), parsed.Description, true);
        if (keepPointNumbers && parsed.Number is uint wanted)
        {
          // Free unless another point of the drawing (or an earlier line of this file) already has it.
          if (usedInFile.Add(wanted) && !civilDoc.CogoPoints.Contains(wanted))
          {
            civilDoc.CogoPoints.SetPointNumber(objectId, wanted);
          }
        }

        var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        createdNumbers.Add(point.PointNumber);
        if (keepPointNumbers && parsed.Number is uint asked && asked != point.PointNumber)
        {
          renumbered.Add(new Dictionary<string, object?> { ["inFile"] = asked, ["given"] = point.PointNumber });
        }
      }

      return new Dictionary<string, object?>
      {
        ["imported"] = createdNumbers.Count,
        ["pointNumbers"] = createdNumbers,
        ["renumbered"] = renumbered,
        ["targetSurface"] = null,
      };
    });
  }

  /// <summary>
  /// Imports a point file with Civil 3D's own importer (CogoPoints.ImportPoints, what Import Points does by hand):
  /// <c>path</c> (inside the import roots, FileBoundary), <c>format</c> by its name in the drawing's point file
  /// formats (listPointFileFormats: "PNEZD (comma delimited)"...), and optionally <c>pointGroup</c> to put the points
  /// in (created when missing). Civil 3D's settings decide point numbers and duplicates, as in its dialog. No
  /// question is asked: this is the way points from a file are meant to come in (PointCreationGate).
  /// </summary>
  public static Task<object?> ImportPointFileAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "path"),
      ".txt", ".csv", ".pnt", ".xyz", ".nez", ".prn", ".asc", ".dat");
    var formatName = PluginRuntime.GetRequiredString(parameters, "format");
    var groupName = PluginRuntime.GetOptionalString(parameters, "pointGroup");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var formats = PointFileFormatCollection.GetPointFileFormats(database);
      PointFileFormat format;
      try
      {
        format = formats[formatName];
      }
      catch (Exception)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"No point file format named '{formatName}' in this drawing. Its formats: {string.Join("; ", FormatNames(formats))}.");
      }

      var before = civilDoc.CogoPoints.Count;
      uint imported;
      if (!string.IsNullOrWhiteSpace(groupName))
      {
        var groupId = civilDoc.PointGroups.Contains(groupName!) ? civilDoc.PointGroups[groupName!] : civilDoc.PointGroups.Add(groupName!);
        imported = CogoPointCollection.ImportPoints(path, format, groupId);  // static: into the active drawing
      }
      else
      {
        imported = CogoPointCollection.ImportPoints(path, format);
      }

      return new Dictionary<string, object?>
      {
        ["path"] = path,
        ["format"] = formatName,
        ["imported"] = imported,
        ["pointsBefore"] = before,
        ["pointsAfter"] = civilDoc.CogoPoints.Count,
        ["pointGroup"] = groupName,
        ["importer"] = "Civil 3D (CogoPoints.ImportPoints)",
      };
    });
  }

  /// <summary>The drawing's point file format names, for importPointFile's <c>format</c>.</summary>
  public static Task<object?> ListPointFileFormatsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?> { ["formats"] = FormatNames(PointFileFormatCollection.GetPointFileFormats(database)) });
  }

  private static List<string> FormatNames(PointFileFormatCollection formats)
  {
    var names = new List<string>();
    for (var i = 0; i < formats.Count; i++)
    {
      names.Add(formats[i].Name);
    }

    return names;
  }

  /// <summary>Erases the points with the given <c>pointNumbers</c>; one missing number fails the whole call, so
  /// nothing is erased (the write isn't committed).</summary>
  public static Task<object?> DeleteCogoPointsAsync(JsonObject? parameters)
  {
    var numbersNode = PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray;
    if (numbersNode == null || numbersNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "deleteCogoPoints requires 'pointNumbers'.");
    }

    var pointNumbers = numbersNode.Select(node => (uint)(node?.GetValue<int>() ?? 0)).Where(n => n > 0).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var deleted = new List<uint>();
      foreach (var pointNumber in pointNumbers)
      {
        var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
        point.Erase();
        deleted.Add(pointNumber);
      }

      return new Dictionary<string, object?>
      {
        ["deleted"] = deleted.Count,
        ["pointNumbers"] = deleted,
      };
    });
  }

  public static Task<object?> ListPointGroupsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groups = new List<Dictionary<string, object?>>();
      foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.PointGroups)
      {
        var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        var query = group.GetQuery() as StandardPointGroupQuery;
        groups.Add(new Dictionary<string, object?>
        {
          ["name"] = group.Name,
          ["description"] = group.Description,
          ["pointCount"] = group.PointsCount,
          ["includePattern"] = query?.IncludeNumbers,
          ["excludePattern"] = query?.ExcludeNumbers,
        });
      }

      return new Dictionary<string, object?>
      {
        ["groups"] = groups,
      };
    });
  }

  private static IEnumerable<CogoPoint> EnumeratePoints(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      yield return CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
    }
  }

  private static CogoPoint FindPointByNumber(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction, uint pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode openMode)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      var point = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, openMode);
      if (point.PointNumber == pointNumber)
      {
        return point;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"COGO point '{pointNumber}' was not found.");
  }

  private static HashSet<uint>? TryGetPointNumbersForGroup(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction, string groupName)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.PointGroups)
    {
      var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      if (!string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      return new HashSet<uint>(group.GetPointNumbers());
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{groupName}' was not found.");
  }
}
