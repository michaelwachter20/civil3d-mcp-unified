// Gravity pipe network hydraulics. JSON-RPC methods (CommandDispatcher.cs): calculatePipeNetworkHgl,
// analyzePipeNetworkHydraulics, getPipeStructureProperties.
// Callers: civil3d_pipe (src/tools/domains/pipeDomain.ts: calculate_hgl, hydraulic_analysis,
// get_structure_properties) and workflowDomain.ts's pipe_network_design (analyzePipeNetworkHydraulics). In-process,
// HydrologyCommands.RunoffPipeWorkflowAsync (runoffPipeWorkflow) calls the first two, and PipeNetworkCommands uses
// InvertIn/InvertOut for its pipe data.
// All three are read only: they run in CivilExecution.ReadAsync (main thread, no undo step) and never change the
// drawing. Formulas (Manning's, part-full flow, units) are in PipeMath. Networks, pipes and structures are read
// late-bound by member name; that reflection goes through Civil3DCompatibility only. No file access.
using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Pipe = Autodesk.Civil.DatabaseServices.Pipe;
using Structure = Autodesk.Civil.DatabaseServices.Structure;

namespace Civil3DMcpPlugin;

/// <summary>
/// Hydraulic analysis commands for gravity pipe networks.
/// Implements Manning's equation, HGL backwater analysis, and structure property queries.
/// Results are in the drawing's units (feet: cfs and ft/s; otherwise cms and m/s), whatever the key names say.
/// Slopes are in percent. An unknown network or structure is CIVIL3D.OBJECT_NOT_FOUND; a missing required argument
/// is CIVIL3D.INVALID_INPUT.
/// </summary>
public static class PipeHydraulicsCommands
{
  // ─── calculatePipeNetworkHgl ────────────────────────────────────────────────

  /// <summary>
  /// A simple hydraulic grade line for network <c>networkName</c> (required). Optional: <c>tailwaterElevation</c>
  /// (default the outlet pipe's invert out), <c>designFlow</c> (default each pipe's full-flow capacity) and
  /// <c>manningsN</c> (default 0.013). The HGL is walked over the network's connections (PipeMath.WalkHgl): water
  /// runs from each pipe's higher invert to its lower one, each outfall (a structure or free end no pipe leaves)
  /// starts at the tailwater (without one, the lowest invert reaching it), each pipe adds its full-pipe Manning
  /// friction loss to the HGL at its downstream end, and where pipes split the highest HGL controls. So a branch only
  /// carries the losses between it and its own outfall. A pipe is surcharged when the HGL at its upper end is above
  /// its crown. Returns one node per pipe (with its structures and outfall), outfalls first, and <c>outfalls</c> with
  /// each one's starting HGL. A network with no pipes gives an empty node list with a warning. Read only.
  /// </summary>
  public static Task<object?> CalculatePipeNetworkHglAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var tailwaterElevation = PluginRuntime.GetOptionalDouble(parameters, "tailwaterElevation");
    var designFlow = PluginRuntime.GetOptionalDouble(parameters, "designFlow");
    var manningsN = PluginRuntime.GetOptionalDouble(parameters, "manningsN") ?? 0.013;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var units = CivilObjectUtils.LinearUnits(database);
      var pipes = GetPipeData(network, transaction);

      if (pipes.Count == 0)
      {
        return new Dictionary<string, object?>
        {
          ["networkName"] = networkName,
          ["nodeCount"] = 0,
          ["nodes"] = Array.Empty<object>(),
          ["warning"] = "Network contains no pipes.",
        };
      }

      // Each pipe's full-flow capacity and its friction loss at the flow (Manning's, in the drawing's units):
      // L (nQ / (k A R^(2/3)))^2. Water runs from the higher invert to the lower one.
      var byName = new Dictionary<string, (PipeInfo Pipe, double QFull, double HeadLoss)>(StringComparer.Ordinal);
      var walk = new List<PipeMath.HglPipe>();
      var nodeNames = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var pipe in pipes)
      {
        double diameter = pipe.Diameter;
        double area = Math.PI * diameter * diameter / 4.0;
        double hydraulicRadius = diameter / 4.0;
        double qFull = PipeMath.FullFlowCapacity(diameter, pipe.Slope, manningsN, units);
        double flow = designFlow ?? qFull;
        double headLoss = area > 0
          ? Math.Max(0, pipe.Length * Math.Pow(manningsN * flow / (PipeMath.ManningsK(units) * area
              * Math.Pow(hydraulicRadius, 2.0 / 3.0)), 2.0))
          : 0;
        var key = $"{pipe.Name}#{walk.Count}";
        byName[key] = (pipe, qFull, headLoss);
        nodeNames[pipe.StartNode] = pipe.StartStructure ?? $"{pipe.Name} start (no structure)";
        nodeNames[pipe.EndNode] = pipe.EndStructure ?? $"{pipe.Name} end (no structure)";
        var startDown = pipe.InvertIn >= pipe.InvertOut;
        walk.Add(new PipeMath.HglPipe(key,
          startDown ? pipe.EndNode : pipe.StartNode,
          startDown ? pipe.StartNode : pipe.EndNode,
          headLoss,
          Math.Min(pipe.InvertIn, pipe.InvertOut)));
      }

      // Walked over the network's connections, outfall by outfall, so a branch carries only its own path's losses.
      var hglNodes = PipeMath.WalkHgl(walk, tailwaterElevation);
      var nodes = new List<Dictionary<string, object?>>();
      foreach (var hglNode in hglNodes)
      {
        var (pipe, qFull, headLoss) = byName[hglNode.Name];
        var upstreamInvert = Math.Max(pipe.InvertIn, pipe.InvertOut);
        var isSurcharged = hglNode.HglUpstream > upstreamInvert + pipe.Diameter;
        var startDown = pipe.InvertIn >= pipe.InvertOut;
        nodes.Add(new Dictionary<string, object?>
        {
          ["pipeName"] = pipe.Name,
          ["upstreamStructure"] = startDown ? pipe.StartStructure : pipe.EndStructure,
          ["downstreamStructure"] = startDown ? pipe.EndStructure : pipe.StartStructure,
          ["outfall"] = nodeNames[hglNode.Outfall],
          ["invertIn"] = Math.Round(pipe.InvertIn, 3),
          ["invertOut"] = Math.Round(pipe.InvertOut, 3),
          ["diameter"] = Math.Round(pipe.Diameter, 3),
          ["length"] = Math.Round(pipe.Length, 2),
          ["slopePct"] = Math.Round(pipe.Slope, 4),
          ["qFullCfs"] = Math.Round(qFull, 3),
          ["hglDownstream"] = Math.Round(hglNode.HglDownstream, 3),
          ["hglUpstream"] = Math.Round(hglNode.HglUpstream, 3),
          ["headLoss"] = Math.Round(headLoss, 3),
          ["surcharged"] = isSurcharged,
          ["status"] = isSurcharged ? "SURCHARGED" : "FREE_SURFACE",
        });
      }

      // Where each outfall's HGL starts: the tailwater, else the lowest invert reaching it.
      var downstreamOf = walk.ToDictionary(pipe => pipe.Name, pipe => pipe.DownstreamNode, StringComparer.Ordinal);
      var outfalls = hglNodes
        .Where(node => downstreamOf[node.Name] == node.Outfall)
        .GroupBy(node => node.Outfall)
        .Select(group => new Dictionary<string, object?>
        {
          ["name"] = nodeNames[group.Key],
          ["startHgl"] = Math.Round(group.First().HglDownstream, 3),
        })
        .ToList();
      var hglAtOutlet = tailwaterElevation ?? hglNodes.Where(node => downstreamOf[node.Name] == node.Outfall)
        .Select(node => node.HglDownstream).DefaultIfEmpty(0).Min();

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["tailwaterElevation"] = Math.Round(hglAtOutlet, 3),
        ["outfalls"] = outfalls,
        ["manningsN"] = manningsN,
        ["designFlow"] = designFlow,
        // The *Cfs / *Fps keys keep their names; their values are in these units (cms and m/s in a drawing in meters).
        ["units"] = units,
        ["flowUnit"] = PipeMath.FlowUnit(units),
        ["pipeCount"] = pipes.Count,
        ["surchargedCount"] = nodes.Count(n => (bool)(n["surcharged"] ?? false)),
        ["nodes"] = nodes,
      };
    });
  }

  // ─── analyzePipeNetworkHydraulics ──────────────────────────────────────────

  /// <summary>
  /// Checks every pipe of network <c>networkName</c> (required) against design limits. Optional: <c>designFlow</c>
  /// (default each pipe's full-flow capacity), <c>manningsN</c> (0.013), <c>minSlope</c> (percent, 0.5),
  /// <c>minCoverDepth</c> (2 ft), <c>minVelocity</c> (2 ft/s) and <c>maxVelocity</c> (10 ft/s); the three feet
  /// defaults are converted to meters when the drawing isn't in feet. Per pipe: full-flow capacity, part-full velocity, depth
  /// ratio, Froude number, cover, and the limits it breaks (status OK or FAIL). Cover is checked only for pipes with a
  /// reference surface. Also lists each structure's rim, sump and depth. Read only.
  /// </summary>
  public static Task<object?> AnalyzePipeNetworkHydraulicsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var designFlow = PluginRuntime.GetOptionalDouble(parameters, "designFlow");
    var manningsN = PluginRuntime.GetOptionalDouble(parameters, "manningsN") ?? 0.013;
    var minCoverDepth = PluginRuntime.GetOptionalDouble(parameters, "minCoverDepth");
    var minVelocity = PluginRuntime.GetOptionalDouble(parameters, "minVelocity");
    var maxVelocity = PluginRuntime.GetOptionalDouble(parameters, "maxVelocity");
    var minSlope = PluginRuntime.GetOptionalDouble(parameters, "minSlope") ?? 0.5;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var units = CivilObjectUtils.LinearUnits(database);
      var pipes = GetPipeData(network, transaction);
      var structures = GetStructureData(network, transaction);

      // The defaults are the usual feet and ft/s limits, so any drawing not in feet (which PipeMath works in meters)
      // gets them in metres.
      var toDrawingUnits = units == "feet" ? 1.0 : 0.3048;
      var minCoverLimit = minCoverDepth ?? 2.0 * toDrawingUnits;
      var minVelocityLimit = minVelocity ?? 2.0 * toDrawingUnits;
      var maxVelocityLimit = maxVelocity ?? 10.0 * toDrawingUnits;

      var results = new List<Dictionary<string, object?>>();
      var violations = new List<Dictionary<string, object?>>();

      foreach (var pipe in pipes)
      {
        double diameter = pipe.Diameter;
        double area = Math.PI * diameter * diameter / 4.0;
        double hydraulicRadius = diameter / 4.0;
        double qFull = PipeMath.FullFlowCapacity(diameter, pipe.Slope, manningsN, units);
        double vFull = area > 0 ? qFull / area : 0;

        double actualFlow = designFlow ?? qFull;
        double capacityRatio = qFull > 0 ? actualFlow / qFull : 0;
        // At the depth this flow actually runs: Q over the full area understates it, badly in a lightly loaded pipe.
        var (velocity, froudeNumber, depthRatio) = PipeMath.PartFullFlow(actualFlow, qFull, diameter, units);

        var pipeViolations = new List<string>();
        if (pipe.Slope < minSlope) pipeViolations.Add($"slope {pipe.Slope:F3}% < min {minSlope}%");
        if (pipe.Cover is double cover && cover < minCoverLimit) pipeViolations.Add($"cover {cover:F2} < min {minCoverLimit:0.##} {units}");
        if (velocity < minVelocityLimit) pipeViolations.Add($"velocity {velocity:F2} < min {minVelocityLimit:0.##} {PipeMath.VelocityUnit(units)}");
        if (velocity > maxVelocityLimit) pipeViolations.Add($"velocity {velocity:F2} > max {maxVelocityLimit:0.##} {PipeMath.VelocityUnit(units)}");
        if (capacityRatio > 1.0) pipeViolations.Add($"flow {actualFlow:F2} exceeds capacity {qFull:F2} {PipeMath.FlowUnit(units)}");

        var pipeResult = new Dictionary<string, object?>
        {
          ["pipeName"] = pipe.Name,
          ["diameter"] = Math.Round(diameter, 3),
          ["length"] = Math.Round(pipe.Length, 2),
          ["slopePct"] = Math.Round(pipe.Slope, 4),
          ["invertIn"] = Math.Round(pipe.InvertIn, 3),
          ["invertOut"] = Math.Round(pipe.InvertOut, 3),
          ["qFullCfs"] = Math.Round(qFull, 3),
          ["designFlowCfs"] = Math.Round(actualFlow, 3),
          ["capacityRatio"] = Math.Round(capacityRatio, 3),
          ["velocityFps"] = Math.Round(velocity, 3),
          ["depthRatio"] = Math.Round(depthRatio, 3),
          ["froudeNumber"] = Math.Round(froudeNumber, 3),
          ["cover"] = pipe.Cover is double known ? Math.Round(known, 3) : null,
          ["status"] = pipeViolations.Count > 0 ? "FAIL" : "OK",
          ["violations"] = pipeViolations,
        };

        results.Add(pipeResult);
        if (pipeViolations.Count > 0)
        {
          violations.Add(new Dictionary<string, object?>
          {
            ["pipeName"] = pipe.Name,
            ["violations"] = pipeViolations,
          });
        }
      }

      var structureSummary = structures.Select(s => new Dictionary<string, object?>
      {
        ["name"] = s.Name,
        ["rimElevation"] = Math.Round(s.RimElevation, 3),
        ["sumpElevation"] = Math.Round(s.SumpElevation, 3),
        ["depth"] = Math.Round(s.RimElevation - s.SumpElevation, 3),
        ["connectedPipes"] = s.ConnectedPipes,
      }).ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["manningsN"] = manningsN,
        // The *Cfs / *Fps keys keep their names; their values are in these units (cms and m/s in a drawing in meters).
        ["units"] = units,
        ["flowUnit"] = PipeMath.FlowUnit(units),
        ["velocityUnit"] = PipeMath.VelocityUnit(units),
        ["pipeCount"] = pipes.Count,
        ["passCount"] = results.Count(r => (string)(r["status"] ?? "") == "OK"),
        ["failCount"] = violations.Count,
        ["pipes"] = results,
        ["violations"] = violations,
        ["structures"] = structureSummary,
      };
    });
  }

  // ─── getPipeStructureProperties ─────────────────────────────────────────────

  /// <summary>
  /// Structure <c>structureName</c> in network <c>networkName</c> (both required): type, x and y, rim and sump
  /// elevations, depth (rim less sump, given as both sumpDepth and totalDepth), barrel count, and each connected
  /// pipe's inverts and inner diameter. Values a release doesn't expose fall back to 0 (rim, x, y), the rim (sump)
  /// or 1 (barrels). Read only.
  /// </summary>
  public static Task<object?> GetPipeStructurePropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName);
      var structure = FindStructureByName(network, transaction, structureName);

      var rimElevation = GetAnyDouble(structure, "RimElevation", "SurfaceElevation", "Elevation") ?? 0.0;
      var sumpElevation = GetAnyDouble(structure, "SumpElevation") ?? rimElevation;
      // What add_structure's sumpDepth sets; Civil 3D's own SumpDepth is below the lowest pipe, and 0 here.
      var depth = rimElevation - sumpElevation;
      var sumpDepth = depth;

      // Get connected pipes and their inverts
      var connectedPipes = new List<Dictionary<string, object?>>();
      foreach (var pipeId in ConnectedPipeIds(structure))
      {
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pipeId, OpenMode.ForRead);
        connectedPipes.Add(new Dictionary<string, object?>
        {
          ["name"] = pipe.Name,
          ["invertIn"] = Math.Round(InvertIn(pipe), 3),
          ["invertOut"] = Math.Round(InvertOut(pipe), 3),
          ["diameter"] = Math.Round(pipe.InnerDiameterOrWidth, 3),
        });
      }

      var location = GetPointProperty(structure, "Location", "Position", "CenterPoint");

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["structureName"] = structureName,
        ["type"] = GetAnyString(structure, "PartType", "StructureType", "PartDescription") ?? structure.GetType().Name,
        ["x"] = Math.Round(location?.X ?? 0.0, 3),
        ["y"] = Math.Round(location?.Y ?? 0.0, 3),
        ["rimElevation"] = Math.Round(rimElevation, 3),
        ["sumpElevation"] = Math.Round(sumpElevation, 3),
        ["sumpDepth"] = Math.Round(sumpDepth, 3),
        ["totalDepth"] = Math.Round(depth, 3),
        ["barrelCount"] = GetAnyDouble(structure, "BarrelCount", "NumberOfBarrels") ?? 1.0,
        ["connectedPipes"] = connectedPipes,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  /// <summary>A pipe as the hydraulics need it. StartNode / EndNode name what each end connects to: the structure's
  /// handle, or a key of its own for an end with no structure; StartStructure / EndStructure are the structures'
  /// names (null for a free end).</summary>
  private sealed record PipeInfo(string Name, double Diameter, double Length, double Slope, double InvertIn, double InvertOut,
    double? Cover, string StartNode, string EndNode, string? StartStructure, string? EndStructure);
  private sealed record StructureInfo(string Name, double RimElevation, double SumpElevation, List<string> ConnectedPipes);

  /// <summary>The network named <paramref name="name"/> (case-insensitive), found through whichever of several
  /// network collection members exist, or CIVIL3D.OBJECT_NOT_FOUND.</summary>
  private static DBObject FindPipeNetworkByName(object civilDoc, Transaction transaction, string name)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMember(civilDoc, "PipeNetworkCollection"),
      GetNamedMember(civilDoc, "NetworkCollection"),
      GetNamedMember(civilDoc, "PipeNetworks"),
      GetNamedMember(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIds(candidate))
      {
        var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
          return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");
  }

  /// <summary>The structure of this network named <paramref name="structureName"/> (case-insensitive), or
  /// CIVIL3D.OBJECT_NOT_FOUND.</summary>
  private static DBObject FindStructureByName(DBObject network, Transaction transaction, string structureName)
  {
    foreach (var objectId in GetChildObjectIds(network, "GetStructureIds", "StructureIds", "Structures", "StructureCollection"))
    {
      var structure = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(structure), structureName, StringComparison.OrdinalIgnoreCase))
        return structure;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{structureName}' was not found.");
  }

  /// <summary>A pipe's least cover, or null with no reference surface: Civil 3D gives 0 then, not a real depth.</summary>
  private static double? CoverOf(DBObject pipe)
  {
    try
    {
      return pipe is Pipe civilPipe && !civilPipe.RefSurfaceId.IsNull
        ? Math.Min(civilPipe.CoverOfStartPoint, civilPipe.CoverOfEndpoint)
        : null;
    }
    catch (Exception exception) when (exception.GetType().Name == "CivilException")
    {
      return null;
    }
  }

  /// <summary>A pipe's start invert: its end points are on the centerline, half the inner height above the invert.</summary>
  internal static double InvertIn(Pipe pipe) => pipe.StartPoint.Z - pipe.InnerHeight / 2;

  /// <summary>A pipe's end invert, the same way as <see cref="InvertIn"/>.</summary>
  internal static double InvertOut(Pipe pipe) => pipe.EndPoint.Z - pipe.InnerHeight / 2;

  /// <summary>The ids of the pipes connected to a structure; none when the object isn't a Civil 3D structure.</summary>
  private static IEnumerable<ObjectId> ConnectedPipeIds(DBObject structure)
  {
    if (structure is Structure civilStructure)
    {
      for (var index = 0; index < civilStructure.ConnectedPipesCount; index++)
      {
        yield return civilStructure.get_ConnectedPipe(index);
      }
    }
  }

  /// <summary>Each pipe's name, inner diameter (default 1.0 when unreadable), 3D length, percent slope, inverts, least
  /// cover and the structures at its ends, read late-bound with fallbacks.</summary>
  private static List<PipeInfo> GetPipeData(DBObject network, Transaction transaction)
  {
    var results = new List<PipeInfo>();
    foreach (var objectId in GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
    {
      var pipe = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      var startPt = GetPointProperty(pipe, "StartPoint", "PointAtStart");
      var endPt = GetPointProperty(pipe, "EndPoint", "PointAtEnd");
      var invertIn = pipe is Pipe startOf ? InvertIn(startOf) : startPt?.Z ?? 0.0;
      var invertOut = pipe is Pipe endOf ? InvertOut(endOf) : endPt?.Z ?? 0.0;
      var length = GetAnyDouble(pipe, "Length3D", "Length2D", "Length") ?? 0.0;
      // In percent from the pipe's ends (PipeMath.SlopePercent): Civil 3D's own Slope value isn't documented as a
      // percent or a ratio.
      var slope = (startPt is Point3d a && endPt is Point3d b ? PipeMath.SlopePercent(a.X, a.Y, a.Z, b.X, b.Y, b.Z) : null)
        ?? GetAnyDouble(pipe, "Slope", "FlowSlope") ?? (length > 0 ? (invertIn - invertOut) / length * 100 : 0);
      var diameter = GetAnyDouble(pipe, "InnerDiameterOrWidth", "InnerDiameter", "Diameter") ?? 1.0;
      // What each end connects to, for the HGL walk over the network's topology.
      var startStructureId = pipe is Pipe connectedStart ? connectedStart.StartStructureId : ObjectId.Null;
      var endStructureId = pipe is Pipe connectedEnd ? connectedEnd.EndStructureId : ObjectId.Null;
      var startNode = startStructureId.IsNull ? $"free:{objectId.Handle}:start" : $"structure:{startStructureId.Handle}";
      var endNode = endStructureId.IsNull ? $"free:{objectId.Handle}:end" : $"structure:{endStructureId.Handle}";
      results.Add(new PipeInfo(CivilObjectUtils.GetName(pipe) ?? objectId.Handle.ToString(), diameter, length, slope, invertIn, invertOut,
        CoverOf(pipe), startNode, endNode, ResolveObjectName(transaction, startStructureId), ResolveObjectName(transaction, endStructureId)));
    }
    return results;
  }

  /// <summary>Each structure's name, rim and sump elevations and connected pipe names, read late-bound with fallbacks.
  /// </summary>
  private static List<StructureInfo> GetStructureData(DBObject network, Transaction transaction)
  {
    var results = new List<StructureInfo>();
    foreach (var objectId in GetChildObjectIds(network, "GetStructureIds", "StructureIds", "Structures", "StructureCollection"))
    {
      var structure = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, OpenMode.ForRead);
      var rim = GetAnyDouble(structure, "RimElevation", "SurfaceElevation", "Elevation") ?? 0.0;
      var sumpDepth = GetAnyDouble(structure, "SumpDepth") ?? 0.0;
      var sump = GetAnyDouble(structure, "SumpElevation") ?? (rim - sumpDepth);
      var pipeIds = ConnectedPipeIds(structure)
        .Select(id => ResolveObjectName(transaction, id) ?? id.Handle.ToString()).ToList();
      results.Add(new StructureInfo(CivilObjectUtils.GetName(structure) ?? objectId.Handle.ToString(), rim, sump, pipeIds));
    }
    return results;
  }

  /// <summary>Child ids from the first of <paramref name="memberNames"/> that the object has (a property, or a method
  /// with no arguments); later names are not tried once one answers.</summary>
  private static IEnumerable<ObjectId> GetChildObjectIds(DBObject parent, params string[] memberNames)
  {
    foreach (var name in memberNames)
    {
      var value = GetNamedMember(parent, name);
      if (value != null)
      {
        foreach (var id in ToObjectIds(value))
          yield return id;
        yield break;
      }
    }
  }

  /// <summary>The ids in a collection: items that are ids, or items with an ObjectId/Id member.</summary>
  private static IEnumerable<ObjectId> ToObjectIds(object? value)
  {
    if (value == null) yield break;

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId id && id != ObjectId.Null)
          yield return id;
        else
        {
          var itemId = GetAnyObjectId(item, "ObjectId", "Id");
          if (itemId != ObjectId.Null) yield return itemId;
        }
      }
    }
  }

  /// <summary>A property, else the result of a method, of that name, through Civil3DCompatibility; null when neither
  /// exists.</summary>
  private static object? GetNamedMember(object? obj, string name)
  {
    if (obj == null) return null;
    return Civil3DCompatibility.GetPropertyValue(obj, name)
      ?? Civil3DCompatibility.InvokeMethod(obj, name);
  }

  private static ObjectId GetAnyObjectId(object? obj, params string[] names)
  {
    if (obj == null) return ObjectId.Null;
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val is ObjectId id && id != ObjectId.Null) return id;
    }
    return ObjectId.Null;
  }

  private static double? GetAnyDouble(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val != null)
      {
        try { return Convert.ToDouble(val); }
        catch { /* try next */ }
      }
    }
    return null;
  }

  private static string? GetAnyString(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val != null) return val.ToString();
    }
    return null;
  }

  private static Point3d? GetPointProperty(DBObject obj, params string[] names)
  {
    foreach (var name in names)
    {
      var val = GetNamedMember(obj, name);
      if (val is Point3d pt) return pt;
    }
    return null;
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId id)
  {
    if (id == ObjectId.Null) return null;
    try
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead, false, true);
      return CivilObjectUtils.GetName(obj);
    }
    catch { return null; }
  }
}
