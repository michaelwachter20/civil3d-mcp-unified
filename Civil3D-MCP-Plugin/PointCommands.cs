// COGO point handlers for civil3d_point (src/tools/domains/pointDomain.ts).
// JSON-RPC methods: listCogoPoints, getCogoPoint, listPointGroups, listPointFileFormats (reads, CivilExecution.ReadAsync:
// main thread, no undo step); createCogoPoints, importCogoPoints, importPointFile, deleteCogoPoints (writes,
// CivilExecution.WriteAsync: command context, document lock, one transaction, one undo step).
// Rules: points are often survey data; the AI must not change them unless the user asks. createCogoPoints and the
// typed-in importCogoPoints first ask the user in a Civil 3D window (PointCreationGate) and change nothing on "no";
// importPointFile is Civil 3D's own importer and asks nothing. The point file path goes through
// FileBoundary.ResolveImportPath, and its code-page copy (when one is needed) through FileBoundary's temporary-file
// helpers. Point numbers must be whole numbers of 1 or more (JsonRpcProtocol.PositiveIntegers).
// Point group create/update/delete/export/transform live in PointGroupCommands.
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
  /// <summary>
  /// listCogoPoints (read-only): the drawing's points sorted by number, optionally only those in point group
  /// <c>groupName</c>, paged with <c>offset</c> (default 0) and <c>limit</c> (default all). Returns totalCount,
  /// returnedCount, points (CivilObjectUtils.ToPointData) and units. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// group (names compared ignoring case).
  /// </summary>
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
        allowedNumbers = GetPointNumbersForGroup(civilDoc, transaction, groupName!);
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

  /// <summary>getCogoPoint (read-only): one point by its required <c>pointNumber</c>, as CivilObjectUtils.ToPointData.
  /// Throws CIVIL3D.OBJECT_NOT_FOUND when no point has that number.</summary>
  public static Task<object?> GetCogoPointAsync(JsonObject? parameters)
  {
    var pointNumber = (uint)PluginRuntime.GetRequiredInt(parameters, "pointNumber");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var point = FindPointByNumber(civilDoc, transaction, pointNumber, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
      return CivilObjectUtils.ToPointData(point);
    });
  }

  /// <summary>createCogoPoints (changes the drawing, one undo step): creates a point for each of the required
  /// <c>points</c> ({x, y, z, description?}); numbers come from the drawing's next point number unless the optional
  /// <c>startNumber</c> is given, then they run startNumber, startNumber + 1, ... Asks the user first
  /// (PointCreationGate). Returns created and pointNumbers. Throws CIVIL3D.INVALID_INPUT for an empty array or a point
  /// without x, y or z (nothing is committed then), CIVIL3D.USER_DECLINED / POINT_CREATION_OFF from the gate.</summary>
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
      + " (civil3d_point create).",
      PointCreationGate.PointListDetails(pointsNode.OfType<JsonObject>()
        .Select(p => $"E {p["x"]}, N {p["y"]}, elev {p["z"]}, {p["description"]?.ToString() ?? ""}").ToList()));

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
  /// Changes the drawing (one undo step) after asking the user (PointCreationGate). <c>format</c> and <c>data</c> are
  /// required. Returns imported, pointNumbers and renumbered. Throws CIVIL3D.API_ERROR for targetSurface,
  /// CIVIL3D.INVALID_INPUT for no points or a bad line (PointText.Parse), CIVIL3D.USER_DECLINED / POINT_CREATION_OFF.
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
      + " (civil3d_point import with data, not a file).",
      PointCreationGate.PointListDetails(points
        .Select(p => $"{(p.Number?.ToString() ?? "(next)")}: E {p.X}, N {p.Y}, elev {p.Z}, {p.Description}").ToList()));

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
  /// in (created when missing). No question is asked: this is the way points from a file are meant to come in
  /// (PointCreationGate).
  /// <para>
  /// A number already in use follows <c>ifNumberExists</c> (renumber, merge or overwrite). Without it the drawing's
  /// setting decides, except its default, Notify: that opens Civil 3D's Duplicate Point Number window, which blocks
  /// every request until someone answers it, so such a point takes the next free number and existing points stay as
  /// they are. A line Civil 3D can't read refuses the whole file, and nothing comes in.
  /// </para>
  /// Changes the drawing in one undo step; the drawing's duplicate-number setting is put back afterwards. Returns path,
  /// format, imported, pointsBefore/After, pointGroup, ifNumberExists and a note when characters were lost. Throws
  /// CIVIL3D.INVALID_INPUT for an unknown format, a bad ifNumberExists or a file Civil 3D can't read, and the
  /// FileBoundary errors (CIVIL3D.PATH_NOT_ALLOWED ...) for a path outside the import roots or with another extension.
  /// </summary>
  public static Task<object?> ImportPointFileAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveImportPath(PluginRuntime.GetRequiredString(parameters, "path"),
      ".txt", ".csv", ".pnt", ".xyz", ".nez", ".prn", ".asc", ".dat");
    var formatName = PluginRuntime.GetRequiredString(parameters, "format");
    var groupName = PluginRuntime.GetOptionalString(parameters, "pointGroup");
    var ifNumberExists = PluginRuntime.GetOptionalString(parameters, "ifNumberExists");

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
      var duplicateSetting = civilDoc.Settings.GetSettings<Autodesk.Civil.Settings.SettingsCmdCreatePoints>().PointIdentity.ResolveDuplicatePointNumbersOption;
      var drawingSetting = duplicateSetting.Value;
      var duplicates = ifNumberExists?.ToLowerInvariant() switch
      {
        "renumber" => Autodesk.Civil.PointNumbersExistType.Renumber,
        "merge" => Autodesk.Civil.PointNumbersExistType.Merge,
        "overwrite" => Autodesk.Civil.PointNumbersExistType.Overwrite,
        null => drawingSetting == Autodesk.Civil.PointNumbersExistType.Notify ? Autodesk.Civil.PointNumbersExistType.Renumber : drawingSetting,
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"ifNumberExists must be renumber, merge or overwrite, not '{ifNumberExists}'."),
      };

      var copy = AnsiCopy(path);
      uint imported;
      try
      {
        duplicateSetting.Value = duplicates;
        if (!string.IsNullOrWhiteSpace(groupName))
        {
          var groupId = civilDoc.PointGroups.Contains(groupName!) ? civilDoc.PointGroups[groupName!] : civilDoc.PointGroups.Add(groupName!);
          imported = CogoPointCollection.ImportPoints(copy?.Path ?? path, format, groupId);  // static: into the active drawing
        }
        else
        {
          imported = CogoPointCollection.ImportPoints(copy?.Path ?? path, format);
        }
      }
      catch (ArgumentException exception) when (exception.ParamName == "pointFileFullName")
      {
        var reason = exception.Message.Replace($" (Parameter '{exception.ParamName}')", string.Empty).Replace(copy?.Path ?? path, string.Empty).Trim();
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Civil 3D can't read {Path.GetFileName(path)} as {formatName}, so nothing was imported. {reason}");
      }
      finally
      {
        duplicateSetting.Value = drawingSetting;
        if (copy != null)
        {
          FileBoundary.DeleteTemporaryFile(copy.Path);
        }
      }

      return new Dictionary<string, object?>
      {
        ["path"] = path,
        ["format"] = formatName,
        ["imported"] = imported,
        ["pointsBefore"] = before,
        ["pointsAfter"] = civilDoc.CogoPoints.Count,
        ["pointGroup"] = groupName,
        ["ifNumberExists"] = duplicates.ToString().ToLowerInvariant(),
        ["note"] = copy?.Lost == true
          ? "The file is UTF-8 and Civil 3D reads the Windows code page, so characters that code page doesn't have came in as '?'."
          : null,
        ["importer"] = "Civil 3D (CogoPoints.ImportPoints)",
      };
    });
  }

  /// <summary>A temporary code-page copy of a point file; <c>Lost</c> is true when some characters became '?'.</summary>
  private sealed record PointFileCopy(string Path, bool Lost);

  /// <summary>
  /// Civil 3D's importer reads a point file in the Windows code page only: a UTF-8 file's "±" comes in as "Â±", and a
  /// byte order mark fails the first line. Such a file is written to a temporary copy in the code page for Civil 3D to
  /// import, still its own importer; null for a file that needs no copy. <c>Lost</c> says some characters had no
  /// equivalent there. The copy goes through FileBoundary.WriteTemporaryFile (the plugin's own temp folder) and the
  /// caller removes it with FileBoundary.DeleteTemporaryFile.
  /// </summary>
  private static PointFileCopy? AnsiCopy(string path)
  {
    var bytes = File.ReadAllBytes(path);
    if (bytes.All(value => value < 0x80))
    {
      return null;
    }

    string text;
    try
    {
      text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }
    catch (System.Text.DecoderFallbackException)
    {
      return null;  // Not UTF-8: already in a code page, as the importer wants it.
    }

    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    var ansi = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    var encoded = ansi.GetBytes(text);
    var copy = FileBoundary.WriteTemporaryFile(encoded, System.IO.Path.GetExtension(path));
    return new PointFileCopy(copy, ansi.GetString(encoded) != text);
  }

  /// <summary>listPointFileFormats (read-only, no parameters): the drawing's point file format names, for
  /// importPointFile's <c>format</c>. Returns { formats: [...] }.</summary>
  public static Task<object?> ListPointFileFormatsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?> { ["formats"] = FormatNames(PointFileFormatCollection.GetPointFileFormats(database)) });
  }

  /// <summary>The names of the formats in <paramref name="formats"/>, in the drawing's order.</summary>
  private static List<string> FormatNames(PointFileFormatCollection formats)
  {
    var names = new List<string>();
    for (var i = 0; i < formats.Count; i++)
    {
      names.Add(formats[i].Name);
    }

    return names;
  }

  /// <summary>deleteCogoPoints (changes the drawing, one undo step): erases the points with the given required
  /// <c>pointNumbers</c>; one missing number fails the whole call with CIVIL3D.OBJECT_NOT_FOUND, so nothing is erased
  /// (the write isn't committed). An empty or missing array, or an item that isn't a whole number of 1 or more
  /// (JsonRpcProtocol.PositiveIntegers), is CIVIL3D.INVALID_INPUT before anything is touched. No user window: the TS
  /// side requires an approval token for this delete. Returns deleted and pointNumbers.</summary>
  public static Task<object?> DeleteCogoPointsAsync(JsonObject? parameters)
  {
    var numbersNode = PluginRuntime.GetParameter(parameters, "pointNumbers") as JsonArray;
    if (numbersNode == null || numbersNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "deleteCogoPoints requires 'pointNumbers'.");
    }

    var pointNumbers = JsonRpcProtocol.PositiveIntegers(numbersNode, "pointNumbers").Distinct().ToList();

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

  /// <summary>listPointGroups (read-only, no parameters): every point group with name, description, pointCount and the
  /// include/exclude number patterns of a standard query (null for other query types).</summary>
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

  /// <summary>Opens every COGO point of the drawing for read, lazily.</summary>
  private static IEnumerable<CogoPoint> EnumeratePoints(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction)
  {
    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId objectId in civilDoc.CogoPoints)
    {
      yield return CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, objectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
    }
  }

  /// <summary>The point with <paramref name="pointNumber"/>, opened in <paramref name="openMode"/> (a linear scan of
  /// all points). Throws CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
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

  /// <summary>The point numbers in the group named <paramref name="groupName"/> (ignoring case); never null: an
  /// unknown group throws CIVIL3D.OBJECT_NOT_FOUND.</summary>
  private static HashSet<uint> GetPointNumbersForGroup(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction transaction, string groupName)
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
