using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Shared helpers for the command classes: names and handles, typed late-bound property reads and method calls
/// (delegated to Civil3DCompatibility, the only file allowed to use reflection: tests/reflection_boundary.test.ts),
/// object-id collections, drawing units, and lookups by name (alignment, profile, surface, corridor) that throw
/// CIVIL3D.OBJECT_NOT_FOUND. Late-bound reads return null or default when a member doesn't exist on this release.
/// </summary>
public static class CivilObjectUtils
{
  public static string GetHandle(AcDbObject dbObject)
  {
    return dbObject.Handle.ToString();
  }

  public static string? GetName(object? value)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, "Name")?.ToString();
  }

  public static string? GetStringProperty(object? value, string propertyName)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, propertyName)?.ToString();
  }

  /// <summary>A property read late-bound; default(T) when the object is null or has no such property.</summary>
  public static T? GetPropertyValue<T>(object? value, string propertyName)
  {
    if (value == null)
    {
      return default;
    }

    return Civil3DCompatibility.GetPropertyValue<T>(value, propertyName);
  }

  /// <summary>A method call late-bound; null when the object is null or has no matching method.</summary>
  public static object? InvokeMethod(object? value, string methodName, params object?[] arguments)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.InvokeMethod(value, methodName, arguments);
  }

  /// <summary>The ObjectIds in an ObjectIdCollection or any enumerable (other items are skipped).</summary>
  public static IEnumerable<ObjectId> ToObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }
      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }

  public static T GetRequiredObject<T>(Transaction transaction, ObjectId objectId, OpenMode openMode) where T : AcDbObject
  {
    return (T)(transaction.GetObject(objectId, openMode) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Object {objectId} not found."));
  }

  /// <summary>The drawing's linear units as a word ("feet", "meters", or "other"; survey feet count as feet), as reported in every answer's units.</summary>
  public static string LinearUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "meters";
    if (database.Insunits == UnitsValue.Feet) return "feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "feet";
    return "other";
  }

  public static string AngularUnits(short aunits)
  {
    return aunits switch
    {
      0 => "degrees",
      1 => "degrees",
      2 => "grads",
      3 => "radians",
      _ => "degrees",
    };
  }

  /// <summary>The alignment named <paramref name="name"/> (case-insensitive), opened for read.</summary>
  public static Alignment FindAlignmentByName(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return alignment;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Alignment '{name}' was not found.");
  }

  public static Profile FindProfileByName(Alignment alignment, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in alignment.GetProfileIds())
    {
      var profile = GetRequiredObject<Profile>(transaction, objectId, openMode);
      if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return profile;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{name}' was not found on alignment '{alignment.Name}'.");
  }

  public static CivilSurface FindSurfaceByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.GetSurfaceIds())
    {
      var surface = GetRequiredObject<CivilSurface>(transaction, objectId, openMode);
      if (string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return surface;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Surface '{name}' was not found.");
  }

  public static Corridor FindCorridorByName(CivilDocument civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (ObjectId objectId in civilDoc.CorridorCollection)
    {
      var corridor = GetRequiredObject<Corridor>(transaction, objectId, openMode);
      if (string.Equals(corridor.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return corridor;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor '{name}' was not found.");
  }

  public static Database GetDatabase(object? civilDoc)
  {
    if (civilDoc != null)
    {
      var db = GetPropertyValue<Database>(civilDoc, "Database");
      if (db != null)
      {
        return db;
      }
    }

    return HostApplicationServices.WorkingDatabase
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "No active AutoCAD database is available.");
  }

  public static ObjectId GetModelSpaceBlockId(Database database, Transaction transaction)
  {
    var blockTable = GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    return blockTable[BlockTableRecord.ModelSpace];
  }

  public static double? GetDoubleProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToDouble(raw); } catch { return null; }
  }

  public static bool? GetBoolProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToBoolean(raw); } catch { return null; }
  }

  public static string VolumeUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "cubic meters";
    if (database.Insunits == UnitsValue.Feet) return "cubic feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "cubic feet";
    return "cubic units";
  }

  public static void TrySetName(AcDbObject obj, string name)
  {
    Civil3DCompatibility.TrySetProperty(obj, "Name", name);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    return Civil3DCompatibility.InvokeStaticMethod(type, methodName, arguments);
  }

  public static void TrySetLayer(AcDbObject obj, string layer, Database database, Transaction transaction)
  {
    try
    {
      var layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
      if (layerTable == null) return;
      if (!layerTable.Has(layer))
      {
        var lt = transaction.GetObject(database.LayerTableId, OpenMode.ForWrite) as LayerTable;
        var ltr = new LayerTableRecord { Name = layer };
        lt!.Add(ltr);
        transaction.AddNewlyCreatedDBObject(ltr, true);
      }
      Civil3DCompatibility.TrySetProperty(obj, "Layer", layer);
    }
    catch { /* ignore layer errors */ }
  }

  public static Dictionary<string, object?> ToPointData(CogoPoint point)
  {
    return new Dictionary<string, object?>
    {
      ["number"] = point.PointNumber,
      ["name"] = point.PointName,
      ["x"] = point.Location.X,
      ["y"] = point.Location.Y,
      ["z"] = point.Location.Z,
      ["rawDescription"] = point.RawDescription,
      ["fullDescription"] = point.FullDescription,
    };
  }
}
