// Shared helpers used by nearly every *Commands.cs handler: handles and names, late-bound property reads and method
// calls, object-id collections, drawing units, model extents, and find-by-name for alignments, profiles, surfaces and
// corridors. Serves no JSON-RPC method itself. All helpers run inside the caller's CivilExecution.ReadAsync/WriteAsync
// lambda and use the transaction it was given; they never open their own. Reflection is delegated to
// Civil3DCompatibility (the only file allowed to use it, tests/reflection_boundary.test.ts). Only TrySetLayer and
// TrySetName change anything, and only inside the caller's write.
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
  /// <summary>Civil 3D answers a volume between a surface and itself, with an empty one, or with one somewhere else
  /// entirely, with "Fail to create a surface". Refuses those three cases first with CIVIL3D.INVALID_INPUT and a
  /// plain reason.</summary>
  public static void RequireDifferentSurfaces(Autodesk.Civil.DatabaseServices.Surface baseSurface, Autodesk.Civil.DatabaseServices.Surface comparisonSurface)
  {
    if (baseSurface.ObjectId == comparisonSurface.ObjectId)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A volume needs two different surfaces: these are the same one.");
    }

    foreach (var surface in new[] { baseSurface, comparisonSurface })
    {
      if (SurfaceCommands.IsEmpty(surface))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Surface '{surface.Name}' has no points yet, so a volume can't be worked out with it.");
      }
    }

    var first = baseSurface.GeometricExtents;
    var second = comparisonSurface.GeometricExtents;
    if (first.MaxPoint.X < second.MinPoint.X || second.MaxPoint.X < first.MinPoint.X
      || first.MaxPoint.Y < second.MinPoint.Y || second.MaxPoint.Y < first.MinPoint.Y)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Surfaces '{baseSurface.Name}' and '{comparisonSurface.Name}' don't overlap, so there's no volume between them. Check that both are in the same coordinates.");
    }
  }

  /// <summary>The object's handle as hex text, the id every answer uses for an object.</summary>
  public static string GetHandle(AcDbObject dbObject)
  {
    return dbObject.Handle.ToString();
  }

  /// <summary>
  /// Whether an object is the kind Civil 3D keeps at the origin whatever the drawing's coordinates: a pipe's connectors.
  /// Counted in extents they pull a view from state plane coordinates back to 0,0, so nothing shows.
  /// </summary>
  public static bool IsHiddenAtOrigin(ObjectId id) => id.ObjectClass.Name.Contains("Connector", StringComparison.Ordinal);

  /// <summary>Model space's extents from its objects, or null when it's empty. Database.Extmin isn't current during a write.</summary>
  public static Extents3d? ModelExtents(Database database, Transaction transaction)
  {
    Extents3d? all = null;
    var modelSpace = GetRequiredObject<BlockTableRecord>(transaction, SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
    foreach (ObjectId id in modelSpace)
    {
      if (!IsHiddenAtOrigin(id) && transaction.GetObject(id, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Entity { Bounds: { } bounds })
      {
        var grown = all ?? bounds;
        grown.AddExtents(bounds);
        all = grown;
      }
    }

    return all;
  }

  /// <summary>
  /// An alignment entity's length. Not every kind of entity has a Length to read, and 0 made QC call sound tangents
  /// zero-length, so the stations it spans answer when it doesn't.
  /// </summary>
  public static double EntityLength(object? entity)
  {
    var length = GetPropertyValue<double?>(entity, "Length") ?? 0;
    return length > 0
      ? length
      : (GetPropertyValue<double?>(entity, "EndStation") ?? 0) - (GetPropertyValue<double?>(entity, "StartStation") ?? 0);
  }

  /// <summary>The object's <c>Name</c> read late-bound, or null when it has none.</summary>
  public static string? GetName(object? value)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, "Name")?.ToString();
  }

  /// <summary>A property read late-bound and turned into text; null when the object is null or has no such property.</summary>
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

  /// <summary>
  /// Opens <paramref name="objectId"/> in the caller's transaction and casts it to <typeparamref name="T"/>. Throws
  /// CIVIL3D.OBJECT_NOT_FOUND when GetObject returns null; a wrong type is an InvalidCastException.
  /// </summary>
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

  /// <summary>The AUNITS value as a word: 2 grads, 3 radians, everything else (including surveyor's units) degrees.</summary>
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

  /// <summary>The alignment named <paramref name="name"/> (case-insensitive), opened for read. CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
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

  /// <summary>The profile named <paramref name="name"/> (case-insensitive) on <paramref name="alignment"/>. CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
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

  /// <summary>The surface named <paramref name="name"/> (case-insensitive), any surface type. CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
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

  /// <summary>The corridor named <paramref name="name"/> (case-insensitive). CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
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

  /// <summary>
  /// The Database behind a CivilDocument (read late-bound), else the working database. CIVIL3D.API_ERROR when there
  /// is neither.
  /// </summary>
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

  /// <summary>The ObjectId of model space's block table record.</summary>
  public static ObjectId GetModelSpaceBlockId(Database database, Transaction transaction)
  {
    var blockTable = GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    return blockTable[BlockTableRecord.ModelSpace];
  }

  /// <summary>A property read late-bound and converted to double; null when missing or not convertible.</summary>
  public static double? GetDoubleProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToDouble(raw); } catch { return null; }
  }

  /// <summary>A property read late-bound and converted to bool; null when missing or not convertible.</summary>
  public static bool? GetBoolProperty(object? value, string propertyName)
  {
    if (value == null) return null;
    var raw = Civil3DCompatibility.GetPropertyValue(value, propertyName);
    if (raw == null) return null;
    try { return Convert.ToBoolean(raw); } catch { return null; }
  }

  /// <summary>The drawing's volume units as words ("cubic feet", "cubic meters", or "cubic units"), matching <see cref="LinearUnits"/>.</summary>
  public static string VolumeUnits(Database database)
  {
    if (database.Insunits == UnitsValue.Meters) return "cubic meters";
    if (database.Insunits == UnitsValue.Feet) return "cubic feet";
    if (database.Insunits.ToString().Contains("Survey", StringComparison.OrdinalIgnoreCase)) return "cubic feet";
    return "cubic units";
  }

  /// <summary>Sets <c>Name</c> late-bound when the object has a writable one; otherwise does nothing.</summary>
  public static void TrySetName(AcDbObject obj, string name)
  {
    Civil3DCompatibility.TrySetProperty(obj, "Name", name);
  }

  /// <summary>A static method call late-bound (Civil3DCompatibility); null when there is no matching method.</summary>
  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    return Civil3DCompatibility.InvokeStaticMethod(type, methodName, arguments);
  }

  /// <summary>
  /// Puts <paramref name="obj"/> on <paramref name="layer"/>, creating the layer (default properties) when the drawing
  /// doesn't have it. Use only inside a write. Every error is swallowed: the object simply keeps its layer.
  /// </summary>
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

  /// <summary>A COGO point as the answer shape: number, name, x/y/z, raw and full description.</summary>
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
