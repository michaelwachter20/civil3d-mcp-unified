using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_parcel_* editing tools.
///
/// Civil 3D API notes:
///   Parcel/site collection members vary by Civil 3D version, so this implementation
///   relies on reflection for site/parcel enumeration and creation fallbacks.
/// </summary>
public static class ParcelEditingCommands
{
  // -------------------------------------------------------------------------
  // listParcelSites
  // -------------------------------------------------------------------------

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
  /// Makes parcels from an entity or a list of corners. It adds parcel lines to the site (ParcelCom), and Civil 3D forms
  /// a parcel wherever they close a loop, so a line across an existing parcel splits it in two. The new parcels are the
  /// ones that weren't there before. All of it is one undo step, and lines that formed no parcel are taken out again.
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
          if (!database.TryGetObjectId(new Handle(Convert.ToInt64(sourceHandle, 16)), out var entityId) || entityId.IsNull)
          {
            throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Entity with handle '{sourceHandle}' was not found.");
          }

          sourceEntity = transaction.GetObject(entityId, OpenMode.ForRead).AcadObject;
        }
      }

      var segments = ParcelCom.FindSite(siteName).ParcelSegments;
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
      var corner = new Point2d(pair[0]?.GetValue<double>() ?? 0, pair[1]?.GetValue<double>() ?? 0);
      if (corners.Count == 0 || !corners[^1].IsEqualTo(corner)) corners.Add(corner);
    }

    if (corners.Count > 1 && corners[0].IsEqualTo(corners[^1])) corners.RemoveAt(corners.Count - 1);
    return corners;
  }

  // -------------------------------------------------------------------------
  // editParcel
  // -------------------------------------------------------------------------

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

  public static Task<object?> ReportParcelsAsync(JsonObject? parameters)
  {
    var siteName = PluginRuntime.GetRequiredString(parameters, "siteName");
    var parcelNamesNode = PluginRuntime.GetParameter(parameters, "parcelNames") as JsonArray;
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var includeCoordinates = PluginRuntime.GetOptionalBool(parameters, "includeCoordinates") ?? false;
    var units = PluginRuntime.GetOptionalString(parameters, "units") ?? "sqft";
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    var filterNames = parcelNamesNode?
      .Select(n => n?.GetValue<string>())
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var site = FindSiteByName(civilDoc, transaction, siteName);
      var rows = new List<Dictionary<string, object?>>();

      foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
      {
        var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, OpenMode.ForRead);
        if (filterNames != null && filterNames.Count > 0 && !filterNames.Contains(parcel.Name)) continue;

        var areaInUnits = ConvertArea(parcel.Area, units);

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

  private static Parcel FindParcelByName(Site site, Transaction transaction, string parcelName, OpenMode mode)
  {
    foreach (ObjectId pid in EnumerateParcelIds(site, transaction))
    {
      var parcel = CivilObjectUtils.GetRequiredObject<Parcel>(transaction, pid, mode);
      if (string.Equals(parcel.Name, parcelName, StringComparison.OrdinalIgnoreCase)) return parcel;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parcel '{parcelName}' was not found in site '{site.Name}'.");
  }

  /// <summary>A parcel's corners: its boundary is a curve, with one parameter per segment.</summary>
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

  private static void ApplyStyleToParcel(Parcel parcel, string styleName,
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction)
  {
    parcel.StyleId = LookupUtils.GetParcelStyleId(civilDoc, transaction, styleName);
  }

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

  private static object? GetNamedMember(object? value, string memberName)
  {
    if (value == null) return null;

    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static double? GetParcelPerimeter(Parcel parcel)
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

  private static double ConvertArea(double areaInDrawingUnits, string units)
  {
    return units.ToLowerInvariant() switch
    {
      "acres" => areaInDrawingUnits / 43560.0,
      "sqm" => areaInDrawingUnits * 0.092903,
      "ha" => areaInDrawingUnits * 0.092903 / 10000.0,
      _ => areaInDrawingUnits, // sqft or default
    };
  }

  private static string WriteCsv(
    string outputPath,
    List<Dictionary<string, object?>> rows,
    bool includeCoordinates,
    bool overwrite)
  {
    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Name,Area,AreaUnits,Perimeter,Style");
    foreach (var row in rows)
    {
      csv.AppendLine(string.Join(",",
        row.GetValueOrDefault("name"), row.GetValueOrDefault("area"),
        row.GetValueOrDefault("areaUnits"), row.GetValueOrDefault("perimeter"),
        row.GetValueOrDefault("style")));
    }

    return FileBoundary.WriteAllTextAtomic(
      outputPath, csv.ToString(), System.Text.Encoding.UTF8, overwrite, ".csv");
  }
}
