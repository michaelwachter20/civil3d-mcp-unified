// Corridor region and target editing on one baseline at a time.
// JSON-RPC methods (CommandDispatcher): getCorridorTargetMappings, setCorridorTargetMappings, addCorridorRegion,
// deleteCorridorRegion. Called by src/tools/domains/corridorDomain.ts (civil3d_corridor).
// getCorridorTargetMappings is a CivilExecution.ReadAsync. The three edits are CivilExecution.WriteAsync (one undo
// step) and rebuild the corridor synchronously inside that write, unlike rebuildCorridor's background job, so a large
// corridor holds the host queue until the rebuild ends. Baselines and regions are addressed by index. No file access.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_corridor_editing tools:
///   getCorridorTargetMappings / setCorridorTargetMappings /
///   addCorridorRegion / deleteCorridorRegion.
///
/// Civil 3D API notes:
///   Corridor.Baselines  → Baseline collection
///   Baseline.BaselineRegions → BaselineRegion collection
///   BaselineRegion.AppliedAssemblyId, StartStation, EndStation
///   Target mappings are on SubassemblyTargetInfo objects attached to each region.
/// </summary>
public static class CorridorEditingCommands
{
  // -------------------------------------------------------------------------
  // getCorridorTargetMappings
  // -------------------------------------------------------------------------

  /// <summary>
  /// getCorridorTargetMappings: for corridor <c>corridorName</c> (required), baseline <c>baselineIndex</c> (default 0)
  /// and optionally one <c>regionIndex</c>, each region's stations and its targets (parameter and logical name,
  /// subassembly, target type, target object names). Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor,
  /// CIVIL3D.INVALID_INPUT for a baseline index out of range; a region index out of range just returns no regions.
  /// </summary>
  public static Task<object?> GetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);

      var results = new List<Dictionary<string, object?>>();

      var regions = baseline.BaselineRegions;
      for (var ri = 0; ri < regions.Count; ri++)
      {
        if (regionIndex.HasValue && ri != regionIndex.Value) continue;
        var region = regions[ri];
        var targets = ReadTargetMappings(region, transaction);
        results.Add(new Dictionary<string, object?>
        {
          ["regionIndex"] = ri,
          ["regionName"] = region.Name,
          ["startStation"] = region.StartStation,
          ["endStation"] = region.EndStation,
          ["targets"] = targets,
        });
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regions"] = results,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setCorridorTargetMappings
  // -------------------------------------------------------------------------

  /// <summary>
  /// setCorridorTargetMappings: on corridor <c>corridorName</c> (required), baseline <c>baselineIndex</c> and region
  /// <c>regionIndex</c> (both default 0), points each target named in <c>targets</c> (required array of
  /// <c>{parameterName, targetType surface|alignment|profile, targetName}</c>; incomplete entries are skipped) at that
  /// object, then rebuilds the corridor. Changes the drawing. Returns how many targets were set. Throws
  /// CIVIL3D.INVALID_INPUT for a missing targets array, an index out of range or a target name the region doesn't
  /// have (message lists its targets), CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor or target object (or type).
  /// </summary>
  public static Task<object?> SetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex") ?? 0;
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var targetsNode = PluginRuntime.GetParameter(parameters, "targets") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "targets array is required.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Region index {regionIndex} is out of range. Corridor '{corridorName}' baseline {baselineIndex} has {baseline.BaselineRegions.Count} region(s).");

      var region = baseline.BaselineRegions[regionIndex];
      var appliedCount = 0;
      var targetInfos = region.GetTargets();

      foreach (var targetNode in targetsNode)
      {
        if (targetNode is not JsonObject t) continue;
        var paramName = t["parameterName"]?.GetValue<string>();
        var targetType = t["targetType"]?.GetValue<string>();
        var targetName = t["targetName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(paramName) || string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetName)) continue;

        // Find the Civil 3D object to use as target
        var targetId = ResolveTargetObjectId(civilDoc, transaction, targetType!, targetName!);
        if (targetId == null)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Target object '{targetName}' of type '{targetType}' was not found.");

        var matches = targetInfos.Where(info => string.Equals(info.DisplayName, paramName, StringComparison.OrdinalIgnoreCase)
          || string.Equals(info.LogicalName, paramName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
          var known = string.Join(", ", targetInfos.Select(info => info.DisplayName).Distinct());
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Region '{region.Name}' has no target named '{paramName}'. " + (known.Length > 0 ? $"Its targets: {known}." : "Its subassemblies take no targets."));
        }

        foreach (var info in matches)
        {
          info.TargetIds = new ObjectIdCollection { targetId.Value };
          appliedCount++;
        }
      }

      region.SetTargets(targetInfos);

      // Rebuild corridor to apply changes
      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = regionIndex,
        ["targetsApplied"] = appliedCount,
        ["message"] = $"Applied {appliedCount} target mapping(s) and rebuilt corridor '{corridorName}'.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // addCorridorRegion
  // -------------------------------------------------------------------------

  /// <summary>
  /// addCorridorRegion: adds region "Region n" with assembly <c>assemblyName</c> from <c>startStation</c> to
  /// <c>endStation</c> (all required, with <c>corridorName</c>) on baseline <c>baselineIndex</c> (default 0), sets
  /// <c>frequency</c> along tangents (default 10), <c>frequencyAtCurves</c> and <c>frequencyAtKneePoints</c> (applied
  /// as the profile-curve frequency; both default to frequency), then rebuilds the corridor. Changes the drawing.
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor or assembly, CIVIL3D.INVALID_INPUT for a bad baseline index.
  /// </summary>
  public static Task<object?> AddCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var frequency = PluginRuntime.GetOptionalDouble(parameters, "frequency") ?? 10.0;
    var frequencyAtCurves = PluginRuntime.GetOptionalDouble(parameters, "frequencyAtCurves") ?? frequency;
    var frequencyAtKneePoints = PluginRuntime.GetOptionalDouble(parameters, "frequencyAtKneePoints") ?? frequency;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      // Find the assembly
      ObjectId assemblyId = ObjectId.Null;
      foreach (ObjectId aid in civilDoc.AssemblyCollection)
      {
        var asm = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, aid, OpenMode.ForRead);
        if (string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        {
          assemblyId = aid;
          break;
        }
      }
      if (assemblyId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{assemblyName}' was not found.");

      // Add region via baseline API
      var newRegionIndex = baseline.BaselineRegions.Count;
      var added = baseline.BaselineRegions.Add($"Region {newRegionIndex + 1}", assemblyId, startStation, endStation);
      added.AppliedAssemblySetting.FrequencyAlongTangents = frequency;
      added.AppliedAssemblySetting.FrequencyAlongCurves = frequencyAtCurves;
      added.AppliedAssemblySetting.FrequencyAlongProfileCurves = frequencyAtKneePoints;

      // Rebuild corridor
      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = newRegionIndex,
        ["assemblyName"] = assemblyName,
        ["startStation"] = startStation,
        ["endStation"] = endStation,
        ["frequency"] = frequency,
        ["message"] = $"Region added to corridor '{corridorName}' at stations {startStation:F3}–{endStation:F3}.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // deleteCorridorRegion
  // -------------------------------------------------------------------------

  /// <summary>
  /// deleteCorridorRegion: removes region <c>regionIndex</c> (required) of baseline <c>baselineIndex</c> (default 0) of
  /// corridor <c>corridorName</c> (required) and rebuilds the corridor. Changes the drawing; returns what was deleted.
  /// CIVIL3D.INVALID_INPUT for an index out of range, CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor,
  /// CIVIL3D.API_ERROR when Civil 3D doesn't remove the region.
  /// </summary>
  public static Task<object?> DeleteCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);

      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Region index {regionIndex} is out of range.");

      var region = baseline.BaselineRegions[regionIndex];
      var regionName = region.Name;
      var startStation = region.StartStation;
      var endStation = region.EndStation;

      if (!baseline.BaselineRegions.Remove(region))
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D didn't remove region '{regionName}' from corridor '{corridorName}'.");
      }

      corridor.Rebuild();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["deletedRegionIndex"] = regionIndex,
        ["deletedRegionName"] = regionName,
        ["deletedStartStation"] = startStation,
        ["deletedEndStation"] = endStation,
        ["message"] = $"Region '{regionName}' deleted from corridor '{corridorName}'.",
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>The corridor's baseline at <paramref name="index"/>. CIVIL3D.INVALID_INPUT when out of range.</summary>
  private static Baseline GetBaseline(Corridor corridor, int index)
  {
    if (index < 0 || index >= corridor.Baselines.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Baseline index {index} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
    return corridor.Baselines[index];
  }

  /// <summary>A region's targets as answer rows, with the names of the objects each one targets.</summary>
  private static List<Dictionary<string, object?>> ReadTargetMappings(
    BaselineRegion region, Transaction transaction)
  {
    var mappings = new List<Dictionary<string, object?>>();
    foreach (var info in region.GetTargets())
    {
      var targetNames = info.TargetIds.Cast<ObjectId>()
        .Where(id => !id.IsNull)
        .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)))
        .ToList();
      mappings.Add(new Dictionary<string, object?>
      {
        ["parameterName"] = info.DisplayName,
        ["logicalName"] = info.LogicalName,
        ["subassemblyName"] = info.SubassemblyName,
        ["targetType"] = info.TargetType.ToString(),
        ["targetName"] = targetNames.FirstOrDefault(),
        ["targetNames"] = targetNames,
      });
    }

    return mappings;
  }

  /// <summary>
  /// The surface, alignment or profile (any alignment's) named <paramref name="targetName"/>, by
  /// <paramref name="targetType"/>; null when not found or the type is something else.
  /// </summary>
  private static ObjectId? ResolveTargetObjectId(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction, string targetType, string targetName)
  {
    switch (targetType.ToLowerInvariant())
    {
      case "surface":
        foreach (ObjectId id in civilDoc.GetSurfaceIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
          if (string.Equals(s.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "alignment":
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
        {
          var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
          if (string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "profile":
        foreach (ObjectId aid in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
          foreach (ObjectId pid in alignment.GetProfileIds())
          {
            var p = CivilObjectUtils.GetRequiredObject<Profile>(transaction, pid, OpenMode.ForRead);
            if (string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase)) return pid;
          }
        }
        break;
    }
    return null;
  }

}
