// Site and parcel handlers for civil3d_parcel (src/tools/domains/parcelDomain.ts).
// JSON-RPC methods: listParcelSites, listParcels, getParcel, reportParcels (reads, CivilExecution.ReadAsync: main
// thread, no undo step); editParcel (CivilExecution.WriteAsync: command context, document lock, one transaction, one
// undo step); createParcel (CivilExecution.WithDocumentLockAsync plus a COM undo mark: the .NET API can't create a
// parcel, so parcel lines are added to the site through COM (LandCom) and the result is one undo step);
// adjustParcelLotLine (always refused: there is no API for it).
// reportParcels' optional CSV goes through FileBoundary.WriteAllTextAtomic. Late-bound reads go through
// CivilObjectUtils / Civil3DCompatibility.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_parcel_* editing tools.
///
/// Civil 3D API notes:
///   Sites and parcels are read through the typed API (GetSiteIds, Site.GetParcelIds); a few late-bound member
///   names are still tried as a fallback when listing a site's parcels, and the perimeter and boundary are read by
///   property name. Creating a parcel needs COM (LandCom), since .NET has no call for it.
/// </summary>
public static class ParcelEditingCommands
{
  // -------------------------------------------------------------------------
  // listParcelSites
  // -------------------------------------------------------------------------

  /// <summary>listParcelSites (read-only, no parameters): every site with name, handle and parcelCount.</summary>
  public static Task<object?> ListParcelSitesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var rows = new List<Dictionary<string, object?>>();
      foreach (ObjectId sid in EnumerateSiteIds(civilDoc))
      {
        var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, sid, OpenMode.ForRead);
        rows.Add(new Dictionary<string, object?>
        {
          ["name"] = site.Name,
          ["handle"] = CivilObjectUtils.GetHandle(site),
          ["parcelCount"] = EnumerateParcelIds(site, transaction).Count(),
        });
      }

      return new Dictionary<string, object?>
      {
        ["sites"] = rows,
      };
    });
  }

  // -------------------------------------------------------------------------
  // listParcels
  // -------------------------------------------------------------------------

  /// <summary>listParcels (read-only): the parcels of site <c>siteName</c> (required, ignoring case) with name,
  /// handle, number, area, perimeter and style, plus the drawing's area and length units. Throws
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown site.</summary>
  public static Task<object?> ListParcelsAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcels = new List<Dictionary<string, object?>>();

      foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
      {
        var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, OpenMode.ForRead);
        parcels.Add(new Dictionary<string, object?>
        {
          ["name"] = parcel.Name,
          ["handle"] = CivilObjectUtils.GetHandle(parcel),
          ["number"] = GetParcelNumber(parcel),
          ["area"] = parcel.Area,
          ["perimeter"] = GetParcelPerimeter(parcel),
          ["style"] = CivilObjectUtils.GetStringProperty(parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
        });
      }

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["parcels"] = parcels,
        ["units"] = new Dictionary<string, object?>
        {
          ["area"] = $"{CivilObjectUtils.LinearUnits(database)}²",
          ["length"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // getParcel
  // -------------------------------------------------------------------------

  /// <summary>getParcel (read-only): one parcel by <c>siteName</c> and <c>parcelName</c> (both required, ignoring
  /// case), with handle, number, area, perimeter and style. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown site or
  /// parcel.</summary>
  public static Task<object?> GetParcelAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["name"] = parcel.Name,
        ["handle"] = CivilObjectUtils.GetHandle(parcel),
        ["number"] = GetParcelNumber(parcel),
        ["area"] = parcel.Area,
        ["perimeter"] = GetParcelPerimeter(parcel),
        ["style"] = CivilObjectUtils.GetStringProperty(parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
      };
    });
  }

  // -------------------------------------------------------------------------
  // createParcel
  // -------------------------------------------------------------------------

  /// <summary>
  /// Makes parcels from an entity or a list of corners. It adds parcel lines to the site (LandCom), and Civil 3D forms
  /// a parcel wherever they close a loop, so a line across an existing parcel splits it in two. The new parcels are the
  /// ones that weren't there before. All of it is one undo step, and lines that formed no parcel are taken out again.
  /// <para>
  /// createParcel (changes the drawing). Reads <c>siteName</c> (required), then either <c>sourceHandle</c> (a closed
  /// entity's hex handle) or <c>points</c> ([[x, y], ...], at least 3 corners), and optionally <c>name</c> (used only
  /// when exactly one parcel forms), <c>style</c> and <c>areaLabelStyle</c>. Returns siteName, created, parcelCount
  /// and parcels (name, handle, area, perimeter). Throws CIVIL3D.INVALID_INPUT with neither source or a sourceHandle
  /// that isn't hexadecimal,
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown site, handle or style, and CIVIL3D.API_ERROR when the lines close no
  /// area; on any failure the added lines are deleted again.
  /// </para>
  /// </summary>
  public static Task<object?> CreateParcelAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelName = PluginRuntime.GetOptionalString(parameters, "name");
    var sourceHandle = PluginRuntime.GetOptionalString(parameters, "sourceHandle");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var areaLabelStyle = PluginRuntime.GetOptionalString(parameters, "areaLabelStyle");
    var corners = CornersOf(PluginRuntime.GetParameter(parameters, "points") as JsonArray);

    if (string.IsNullOrWhiteSpace(sourceHandle) && corners.Count < 3)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "createParcel requires either a sourceHandle (entity handle) or a points array with ≥3 vertices.");
    }

    // A handle is hexadecimal ("2A3F"); anything else is the caller's mistake, not an internal error.
    long sourceHandleValue = 0;
    if (!string.IsNullOrWhiteSpace(sourceHandle)
        && !long.TryParse(sourceHandle.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out sourceHandleValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"sourceHandle '{sourceHandle}' isn't an entity handle: send the hexadecimal handle of a closed entity, e.g. \"2A3F\".");
    }

    return CivilExecution.WithDocumentLockAsync<object?>((doc, civilDoc) =>
    {
      var database = doc.Database;
      HashSet<ObjectId> before;
      object? sourceEntity = null;
      using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
      {
        before = EnumerateParcelIds(FindSiteByName(civilDoc, transaction, siteName), transaction).ToHashSet();
        if (!string.IsNullOrWhiteSpace(sourceHandle))
        {
          if (!database.TryGetObjectId(new Handle(sourceHandleValue), out var entityId) || entityId.IsNull)
          {
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Entity with handle '{sourceHandle}' was not found.");
          }

          sourceEntity = transaction.GetObject(entityId, OpenMode.ForRead).AcadObject;
        }
      }

      // The read transaction above is closed before COM runs: COM opens the objects itself and fails on ones a
      // transaction still holds open (CivilExecution.WithDocumentLockAsync).
      var segments = LandCom.FindSite(siteName).ParcelSegments;
      var added = new List<dynamic>();
      dynamic document = Autodesk.AutoCAD.ApplicationServices.DocumentExtension.GetAcadDocument(doc);
      document.StartUndoMark();
      try
      {
        if (sourceEntity != null)
        {
          added.Add(segments.AddFromEntity((dynamic)sourceEntity, false));
        }
        else
        {
          for (var index = 0; index < corners.Count; index++)
          {
            var (from, to) = (corners[index], corners[(index + 1) % corners.Count]);
            added.Add(segments.AddLine(from.X, from.Y, to.X, to.Y));
          }
        }

        using var transaction = database.TransactionManager.StartTransaction();
        var site = FindSiteByName(civilDoc, transaction, siteName);
        var created = EnumerateParcelIds(site, transaction).Where(id => !before.Contains(id)).ToList();
        if (created.Count == 0)
        {
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            "Parcel was not created: these lines close no area. Check that the boundary is closed and forms a valid area.");
        }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var id in created)
        {
          var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, id, OpenMode.ForWrite);
          if (!string.IsNullOrWhiteSpace(style)) ApplyStyleToParcel(parcel, style, civilDoc, transaction);
          if (!string.IsNullOrWhiteSpace(areaLabelStyle)) ApplyLabelStyleToParcel(parcel, areaLabelStyle, civilDoc, transaction);
          if (created.Count == 1 && !string.IsNullOrWhiteSpace(parcelName)) CivilObjectUtils.TrySetName(parcel, parcelName);
          rows.Add(new Dictionary<string, object?>
          {
            ["name"] = parcel.Name,
            ["handle"] = CivilObjectUtils.GetHandle(parcel),
            ["area"] = parcel.Area,
            ["perimeter"] = GetParcelPerimeter(parcel),
          });
        }

        transaction.Commit();
        return new Dictionary<string, object?>
        {
          ["siteName"] = siteName,
          ["created"] = true,
          ["parcelCount"] = rows.Count,
          ["parcels"] = rows,
        };
      }
      catch
      {
        for (var index = added.Count - 1; index >= 0; index--)
        {
          added[index].Delete();
        }

        throw;
      }
      finally
      {
        document.EndUndoMark();
      }
    });
  }

  /// <summary>Corners from [x, y] pairs, without repeats: a list that ends where it began has no extra closing corner.</summary>
  private static List<Point2d> CornersOf(JsonArray? pointsNode)
  {
    var corners = new List<Point2d>();
    foreach (var node in pointsNode ?? [])
    {
      if (node is not JsonArray pair || pair.Count < 2) continue;
      if (pair[0] is not JsonValue xValue || !xValue.TryGetValue<double>(out var x)
          || pair[1] is not JsonValue yValue || !yValue.TryGetValue<double>(out var y))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"points: each corner must be [x, y] numbers, got {pair.ToJsonString()}.");
      }

      var corner = new Point2d(x, y);
      if (corners.Count == 0 || !corners[^1].IsEqualTo(corner)) corners.Add(corner);
    }

    if (corners.Count > 1 && corners[0].IsEqualTo(corners[^1])) corners.RemoveAt(corners.Count - 1);
    return corners;
  }

  // -------------------------------------------------------------------------
  // editParcel
  // -------------------------------------------------------------------------

  /// <summary>editParcel (changes the drawing, one undo step): finds <c>parcelName</c> in <c>siteName</c> (both
  /// required) and sets whichever of <c>newName</c>, <c>style</c>, <c>areaLabelStyle</c>, <c>description</c> are given.
  /// Returns siteName, name, handle, area and updated. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown site, parcel or
  /// style. A rename Civil 3D refuses is skipped silently (CivilObjectUtils.TrySetName).</summary>
  public static Task<object?> EditParcelAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");
    var newName = PluginRuntime.GetOptionalString(parameters, "newName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var areaLabelStyle = PluginRuntime.GetOptionalString(parameters, "areaLabelStyle");
    var description = PluginRuntime.GetOptionalString(parameters, "description");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var parcel = FindParcelByName(site, transaction, parcelName, OpenMode.ForWrite);

      if (!string.IsNullOrWhiteSpace(newName)) CivilObjectUtils.TrySetName(parcel, newName);
      if (!string.IsNullOrWhiteSpace(style)) ApplyStyleToParcel(parcel, style, civilDoc, transaction);
      if (!string.IsNullOrWhiteSpace(areaLabelStyle)) ApplyLabelStyleToParcel(parcel, areaLabelStyle, civilDoc, transaction);
      if (!string.IsNullOrWhiteSpace(description))
      {
        parcel.Description = description;
      }

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["name"] = parcel.Name,
        ["handle"] = CivilObjectUtils.GetHandle(parcel),
        ["area"] = parcel.Area,
        ["updated"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // adjustParcelLotLine
  // -------------------------------------------------------------------------

  /// <summary>
  /// Neither Civil 3D's .NET API nor COM can slide a lot line to an area: it called methods that don't exist and said
  /// "attempted" with nothing changed. The arguments stay so a caller learns this rather than a schema error.
  /// adjustParcelLotLine: reads <c>parcelName</c> (required; CIVIL3D.INVALID_INPUT without it) and always throws
  /// CIVIL3D.API_ERROR pointing the user to Parcel Layout Tools. Never touches the drawing.
  /// </summary>
  public static Task<object?> AdjustParcelLotLineAsync(JsonObject? parameters)
  {
    var parcelName = PluginRuntime.GetRequiredString(parameters, "parcelName");
    throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
      $"Nothing was changed: Civil 3D has no API for sliding a lot line, so '{parcelName}' can't be sized from here. " +
      "Use Parcel Layout Tools (Slide Line) in Civil 3D.");
  }

  // -------------------------------------------------------------------------
  // reportParcels
  // -------------------------------------------------------------------------

  /// <summary>
  /// reportParcels (read-only on the drawing): the parcels of <c>siteName</c> (required), optionally only those in
  /// <c>parcelNames</c>, with area in <c>units</c> (sqft, acres, sqm, ha, converted from the drawing's own square
  /// units by DesignMath.ConvertArea; left out, the drawing's own: sqft in feet, sqm in meters), perimeter, style and,
  /// with <c>includeCoordinates</c>, the boundary vertices. With <c>outputPath</c> it also writes a .csv (name, area,
  /// units, perimeter, style, plus the vertices as "x y;x y;..." with <c>includeCoordinates</c>; RFC 4180 quoting)
  /// through FileBoundary, refusing to replace a file unless <c>overwrite</c> is true. Returns siteName, parcelCount,
  /// parcels and the written outputPath. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown site, CIVIL3D.INVALID_INPUT for
  /// an unknown <c>units</c> or a conversion in a drawing in neither feet nor meters, and FileBoundary's errors for
  /// the path.
  /// </summary>
  public static Task<object?> ReportParcelsAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelNamesNode = PluginRuntime.GetParameter(parameters, "parcelNames") as JsonArray;
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var includeCoordinates = PluginRuntime.GetOptionalBool(parameters, "includeCoordinates") ?? false;
    var requestedUnits = PluginRuntime.GetOptionalString(parameters, "units");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    var filterNames = parcelNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var rows = new List<Dictionary<string, object?>>();
      var linearUnits = CivilObjectUtils.LinearUnits(database);
      var units = requestedUnits ?? DesignMath.NativeAreaUnit(linearUnits);

      foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
      {
        var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, OpenMode.ForRead);
        if (filterNames != null && filterNames.Count > 0 && !filterNames.Contains(parcel.Name)) continue;

        var areaInUnits = requestedUnits == null ? parcel.Area : DesignMath.ConvertArea(parcel.Area, linearUnits, units);

        var row = new Dictionary<string, object?>
        {
          ["name"] = parcel.Name,
          ["handle"] = CivilObjectUtils.GetHandle(parcel),
          ["area"] = areaInUnits,
          ["areaUnits"] = units,
          ["perimeter"] = GetParcelPerimeter(parcel),
          ["style"] = CivilObjectUtils.GetStringProperty(
            parcel.StyleId.IsNull ? null : transaction.GetObject(parcel.StyleId, OpenMode.ForRead), "Name"),
        };

        if (includeCoordinates)
        {
          row["vertices"] = ParcelVertices(parcel)
            .Select(point => new Dictionary<string, object?> { ["x"] = point.X, ["y"] = point.Y })
            .ToList();
        }

        rows.Add(row);
      }

      if (!string.IsNullOrWhiteSpace(outputPath))
      {
        outputPath = WriteCsv(outputPath, rows, includeCoordinates, overwrite);
      }

      return new Dictionary<string, object?>
      {
        ["siteName"] = siteName,
        ["parcelCount"] = rows.Count,
        ["parcels"] = rows,
        ["outputPath"] = outputPath,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>The site named <paramref name="siteName"/> (ignoring case), open for read; CIVIL3D.OBJECT_NOT_FOUND
  /// otherwise.</summary>
  private static Site FindSiteByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction, string siteName)
  {
    foreach (ObjectId sid in EnumerateSiteIds(civilDoc))
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, sid, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase)) return site;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Site '{siteName}' was not found.");
  }

  /// <summary>The parcel named <paramref name="parcelName"/> (ignoring case) in the site, opened in
  /// <paramref name="mode"/>; CIVIL3D.OBJECT_NOT_FOUND otherwise.</summary>
  private static Parcel FindParcelByName(Site site, Transaction transaction, string parcelName, OpenMode mode)
  {
    foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
    {
      var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, mode);
      if (string.Equals(parcel.Name, parcelName, StringComparison.OrdinalIgnoreCase)) return parcel;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parcel '{parcelName}' was not found in site '{site.Name}'.");
  }

  /// <summary>A parcel's corners: its boundary is a curve, with one parameter per segment. Read late-bound from
  /// BaseCurve; an empty list when there is none. Arc segments give only their end points.</summary>
  private static List<Point2d> ParcelVertices(object parcel)
  {
    var points = new List<Point2d>();
    if (CivilObjectUtils.GetPropertyValue<object>(parcel, "BaseCurve") is Curve boundary)
    {
      for (var parameter = boundary.StartParam; parameter <= boundary.EndParam + 1.0e-9; parameter += 1)
      {
        var point = boundary.GetPointAtParameter(parameter);
        points.Add(new Point2d(point.X, point.Y));
      }
    }

    return points;
  }

  /// <summary>Sets the parcel's style by name (LookupUtils; CIVIL3D.OBJECT_NOT_FOUND for an unknown style).</summary>
  private static void ApplyStyleToParcel(Parcel parcel, string styleName,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    parcel.StyleId = LookupUtils.GetParcelStyleId(civilDoc, transaction, styleName);
  }

  /// <summary>Sets the parcel's area label style by name (LookupUtils; CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// style).</summary>
  private static void ApplyLabelStyleToParcel(Parcel parcel, string labelStyleName,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    parcel.AreaSelectionLabelStyleId = LookupUtils.GetParcelAreaLabelStyleId(civilDoc, transaction, labelStyleName);
  }

  private static IEnumerable<ObjectId> EnumerateSiteIds(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      if (objectId != ObjectId.Null)
        yield return objectId;
    }
  }

  /// <summary>The site's parcel ids from Site.GetParcelIds, plus any more found through the late-bound members
  /// Parcels, ParcelCollection or ParcelIds, without duplicates.</summary>
  private static IEnumerable<ObjectId> EnumerateParcelIds(Site site, Transaction transaction)
  {
    var documentedIds = new HashSet<ObjectId>();
    foreach (ObjectId objectId in site.GetParcelIds())
    {
      if (objectId != ObjectId.Null && documentedIds.Add(objectId))
        yield return objectId;
    }

    // Compatibility fallbacks are retained for older host variations, but
    // de-duplicate anything already returned by the documented API.
    foreach (var memberName in new[] { "Parcels", "ParcelCollection", "ParcelIds" })
    {
      var value = GetNamedMember(site, memberName) ?? CivilObjectUtils.InvokeMethod(site, memberName);

      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null && documentedIds.Add(objectId))
          yield return objectId;
      }

      if (value is System.Collections.IEnumerable enumerable)
      {
        foreach (var item in enumerable)
        {
          if (item is Parcel parcel)
          {
            if (documentedIds.Add(parcel.ObjectId)) yield return parcel.ObjectId;
          }
          else if (item is Autodesk.AutoCAD.DatabaseServices.DBObject dbObject)
          {
            if (documentedIds.Add(dbObject.ObjectId)) yield return dbObject.ObjectId;
          }
        }
      }
    }
  }

  /// <summary>A property or field read by name through Civil3DCompatibility; null when there is neither.</summary>
  private static object? GetNamedMember(object? value, string memberName)
  {
    if (value == null) return null;

    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  /// <summary>The parcel's Perimeter property when it has one, else the length of the polygon through its corners
  /// (straight segments), or null with fewer than two corners.</summary>
  internal static double? GetParcelPerimeter(Parcel parcel)
  {
    var perimeter = CivilObjectUtils.GetDoubleProperty(parcel, "Perimeter");
    if (perimeter.HasValue)
      return perimeter.Value;

    var points = ParcelVertices(parcel);

    if (points.Count < 2)
      return null;

    double length = 0;
    for (var index = 1; index < points.Count; index++)
    {
      length += points[index - 1].GetDistanceTo(points[index]);
    }

    if (points[0] != points[^1])
      length += points[^1].GetDistanceTo(points[0]);

    return length;
  }

  private static int GetParcelNumber(Parcel parcel)
  {
    return parcel.Number;
  }

  /// <summary>Writes the report rows as UTF-8 CSV through FileBoundary.WriteAllTextAtomic (.csv only, export roots,
  /// no overwrite unless asked) and returns the full path written. Every field is RFC 4180 quoted when it needs it and
  /// numbers use a dot (DesignMath.CsvLine); with <paramref name="includeCoordinates"/> a Vertices column holds the
  /// boundary as "x y;x y;...".</summary>
  private static string WriteCsv(
    string outputPath,
    List<Dictionary<string, object?>> rows,
    bool includeCoordinates,
    bool overwrite)
  {
    var csv = new System.Text.StringBuilder();
    csv.AppendLine(includeCoordinates ? "Name,Area,AreaUnits,Perimeter,Style,Vertices" : "Name,Area,AreaUnits,Perimeter,Style");
    foreach (var row in rows)
    {
      var fields = new List<object?>
      {
        row.GetValueOrDefault("name"), row.GetValueOrDefault("area"),
        row.GetValueOrDefault("areaUnits"), row.GetValueOrDefault("perimeter"),
        row.GetValueOrDefault("style"),
      };
      if (includeCoordinates)
      {
        var vertices = row.GetValueOrDefault("vertices") as IEnumerable<Dictionary<string, object?>> ?? [];
        fields.Add(DesignMath.VertexList(vertices.Select(vertex => ((double)vertex["x"]!, (double)vertex["y"]!))));
      }

      csv.AppendLine(DesignMath.CsvLine(fields));
    }

    return FileBoundary.WriteAllTextAtomic(
      outputPath, csv.ToString(), System.Text.Encoding.UTF8, overwrite, ".csv");
  }
}
