// Intersection objects: list and inspect; creation is not in the managed API.
// JSON-RPC methods (CommandDispatcher): listIntersections, getIntersection, createIntersection. Called by
// src/tools/domains/intersectionDomain.ts (civil3d_intersection).
// Reads use CivilExecution.ReadAsync (no undo step). createIntersection checks its arguments and always throws
// CIVIL3D.API_ERROR without touching the drawing. No file access, no reflection.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Typed Civil 3D intersection queries. The managed API exposes
/// Intersection inspection, but it does not expose an Intersection.Create
/// factory; creation therefore returns an explicit capability error.
/// </summary>
public static class IntersectionCommands
{
  /// <summary>
  /// listIntersections: every intersection's name, handle, location and the alignments of its first two roads (as
  /// main and intersecting road). Read only. A <c>siteName</c> filter is refused with CIVIL3D.API_ERROR because the API
  /// doesn't give an intersection's site.
  /// </summary>
  public static Task<object?> ListIntersectionsAsync(JsonObject? parameters)
  {
    var siteNameFilter = PluginRuntime.GetOptionalString(parameters, "siteName");
    if (!string.IsNullOrWhiteSpace(siteNameFilter))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D intersections do not expose site membership through the managed API, so siteName cannot be used as an intersection filter.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var intersections = new List<Dictionary<string, object?>>();

      foreach (ObjectId id in civilDoc.GetIntersectionIds())
      {
        var intersection = CivilObjectUtils.GetRequiredObject<Intersection>(transaction, id, OpenMode.ForRead);
        var roads = intersection.IntersectionRoads.ToList();

        intersections.Add(new Dictionary<string, object?>
        {
          ["name"] = intersection.Name,
          ["handle"] = CivilObjectUtils.GetHandle(intersection),
          ["intersectionX"] = intersection.Location.X,
          ["intersectionY"] = intersection.Location.Y,
          ["mainRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineAlignmentId),
          ["intersectingRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineAlignmentId),
        });
      }

      return new Dictionary<string, object?> { ["intersections"] = intersections };
    });
  }

  /// <summary>
  /// createIntersection: requires <c>name</c>, <c>mainRoadAlignment</c>, <c>mainRoadProfile</c>,
  /// <c>intersectingRoadAlignment</c> and <c>intersectingRoadProfile</c> (CIVIL3D.INVALID_INPUT when missing), then
  /// always throws CIVIL3D.API_ERROR: there is no Intersection.Create in the API. Nothing is created.
  /// </summary>
  public static Task<object?> CreateIntersectionAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "name");
    _ = PluginRuntime.GetRequiredString(parameters, "mainRoadAlignment");
    _ = PluginRuntime.GetRequiredString(parameters, "mainRoadProfile");
    _ = PluginRuntime.GetRequiredString(parameters, "intersectingRoadAlignment");
    _ = PluginRuntime.GetRequiredString(parameters, "intersectingRoadProfile");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D does not expose an Intersection.Create method in the managed .NET API. No intersection or related corridor data was created; use the Civil 3D Create Intersection command for this operation.");
  }

  /// <summary>
  /// getIntersection: intersection <c>name</c> (required) with its location and both roads' alignments and profiles;
  /// with <c>includeCurbReturns</c> its regions' curb return alignments and profiles, with <c>includeCorridorInfo</c>
  /// its corridor. Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown name.
  /// </summary>
  public static Task<object?> GetIntersectionAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var includeCorridorInfo = PluginRuntime.GetOptionalBool(parameters, "includeCorridorInfo") ?? false;
    var includeCurbReturns = PluginRuntime.GetOptionalBool(parameters, "includeCurbReturns") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      foreach (ObjectId id in civilDoc.GetIntersectionIds())
      {
        var intersection = CivilObjectUtils.GetRequiredObject<Intersection>(transaction, id, OpenMode.ForRead);
        if (!string.Equals(intersection.Name, name, StringComparison.OrdinalIgnoreCase))
          continue;

        var roads = intersection.IntersectionRoads.ToList();
        var result = new Dictionary<string, object?>
        {
          ["name"] = intersection.Name,
          ["handle"] = CivilObjectUtils.GetHandle(intersection),
          ["intersectionX"] = intersection.Location.X,
          ["intersectionY"] = intersection.Location.Y,
          ["mainRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineAlignmentId),
          ["mainRoadProfile"] = GetObjectName(transaction, roads.ElementAtOrDefault(0)?.CenterlineProfileId),
          ["intersectingRoadAlignment"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineAlignmentId),
          ["intersectingRoadProfile"] = GetObjectName(transaction, roads.ElementAtOrDefault(1)?.CenterlineProfileId),
        };

        if (includeCurbReturns)
        {
          result["curbReturns"] = intersection.IntersectionRegions.Select(region =>
            new Dictionary<string, object?>
            {
              ["name"] = region.Name,
              ["angle"] = region.Angle,
              ["alignment"] = GetObjectName(transaction, region.CurbReturnAlignmentId),
              ["profile"] = GetObjectName(transaction, region.CurbReturnProfileId),
              ["inAlignment"] = GetObjectName(transaction, region.InAlignmentId),
              ["outAlignment"] = GetObjectName(transaction, region.OutAlignmentId),
            }).ToList();
        }

        if (includeCorridorInfo)
        {
          result["corridors"] = intersection.CorridorId.IsNull
            ? new List<string>()
            : new List<string> { GetObjectName(transaction, intersection.CorridorId) ?? intersection.CorridorId.Handle.ToString() };
        }

        return result;
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Intersection '{name}' was not found.");
    });
  }

  /// <summary>The name of the object behind <paramref name="id"/>, or null for a missing or null id.</summary>
  private static string? GetObjectName(Transaction transaction, ObjectId? id)
  {
    if (!id.HasValue || id.Value.IsNull)
      return null;

    return CivilObjectUtils.GetName(transaction.GetObject(id.Value, OpenMode.ForRead));
  }
}
