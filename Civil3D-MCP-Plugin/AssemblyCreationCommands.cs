// Corridor assemblies: list, inspect, create, add stock subassemblies, edit or delete a subassembly.
// JSON-RPC methods (CommandDispatcher): listAssemblies, getAssembly, createAssembly, createSubassembly, editAssembly.
// Called by src/tools/domains/assemblyDomain.ts (civil3d_assembly).
// listAssemblies/getAssembly use CivilExecution.ReadAsync; createAssembly, createSubassembly and editAssembly use
// WriteAsync (one undo step; a thrown error leaves the transaction uncommitted, so nothing changes). Subassemblies
// come from Civil 3D's stock catalog (ImportStockSubassembly); their parameters live in ParamsDouble/Long/String.
// Late-bound property access for non-stock subassemblies goes through Civil3DCompatibility. No file access.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Assembly operations implemented with the documented Civil 3D API.
/// Dynamic stock-subassembly parameters are the only compatibility access.
/// </summary>
public static class AssemblyCreationCommands
{
  // A stock subassembly's Side parameter.
  private const int SideRight = 0;
  private const int SideLeft = 1;

  /// <summary>
  /// listAssemblies: every assembly (name, handle, subassembly count, type) with the corridors whose regions use it
  /// (<c>usedByCorridors</c>). Read only; no parameters.
  /// </summary>
  public static Task<object?> ListAssembliesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var regions = new List<(ObjectId Assembly, string Corridor)>();
      foreach (ObjectId corridorId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
        foreach (var baseline in corridor.Baselines)
        {
          foreach (BaselineRegion region in baseline.BaselineRegions)
          {
            regions.Add((region.AssemblyId, corridor.Name));
          }
        }
      }

      var assemblies = civilDoc.AssemblyCollection
        .Select(id => CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForRead))
        .Select(assembly =>
        {
          var summary = ToAssemblySummary(assembly);
          summary["usedByCorridors"] = regions.Where(region => region.Assembly == assembly.ObjectId).Select(region => region.Corridor).Distinct().ToList();
          return summary;
        })
        .ToList();

      return new Dictionary<string, object?> { ["assemblies"] = assemblies };
    });
  }

  /// <summary>
  /// getAssembly: assembly <c>name</c> (required) with style, type, its subassemblies (name, handle, type, side,
  /// parameters) and the corridors that use it. Read only. CIVIL3D.OBJECT_NOT_FOUND for an unknown assembly.
  /// </summary>
  public static Task<object?> GetAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, name, OpenMode.ForRead);
      var usedByCorridors = new List<string>();

      foreach (ObjectId corridorId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
        foreach (Baseline baseline in corridor.Baselines)
        {
          foreach (BaselineRegion region in baseline.BaselineRegions)
          {
            if (region.AssemblyId == assembly.ObjectId && !usedByCorridors.Contains(corridor.Name))
              usedByCorridors.Add(corridor.Name);
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
        ["style"] = GetStyleName(assembly, transaction) ?? string.Empty,
        ["type"] = assembly.Type.ToString(),
        ["subassemblies"] = GetSubassemblies(assembly, transaction),
        ["usedByCorridors"] = usedByCorridors,
      };
    });
  }

  /// <summary>
  /// createAssembly: adds an empty assembly <c>name</c> at (<c>insertX</c>, <c>insertY</c>) of type
  /// <c>assemblyType</c> (an AssemblyType enum name, case-insensitive), all required, with optional
  /// <c>description</c>. Changes the drawing. CIVIL3D.INVALID_INPUT for an unknown assembly type (the message lists
  /// the valid ones).
  /// </summary>
  public static Task<object?> CreateAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var assemblyTypeText = PluginRuntime.GetRequiredString(parameters, "assemblyType");
    if (!Enum.TryParse<AssemblyType>(assemblyTypeText, ignoreCase: true, out var assemblyType))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Invalid assemblyType '{assemblyTypeText}'. Use {string.Join(", ", Enum.GetNames<AssemblyType>())}.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblyId = civilDoc.AssemblyCollection.Add(name, assemblyType, new Point3d(insertX, insertY, 0));
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, assemblyId, OpenMode.ForWrite);
      assembly.Description = description;

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["assemblyType"] = assembly.Type.ToString(),
        ["created"] = true,
      };
    });
  }

  /// <summary>
  /// createSubassembly: imports stock subassembly <c>subassemblyType</c> (e.g. BasicLane or Subassembly.BasicLane) on
  /// <c>side</c> Left, Right or Both (one per side) and attaches it to assembly <c>assemblyName</c> (all required).
  /// Optional <c>parameters</c> object sets its stock parameters (Width, Slope...). Each gets a unique generated
  /// name. Changes the drawing. Throws CIVIL3D.INVALID_INPUT for a bad side or a parameter the subassembly doesn't
  /// have (message lists its parameters), CIVIL3D.API_ERROR when it has no Side parameter, CIVIL3D.OBJECT_NOT_FOUND
  /// for an unknown assembly.
  /// </summary>
  public static Task<object?> CreateSubassemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyType = PluginRuntime.GetRequiredString(parameters, "subassemblyType");
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    if (!new[] { "Left", "Right", "Both" }.Contains(side, StringComparer.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be Left, Right, or Both.");

    var subParams = ReadParameters(parameters?["parameters"] as JsonObject);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
      var requestedSides = side.Equals("Both", StringComparison.OrdinalIgnoreCase)
        ? new[] { "Left", "Right" }
        : new[] { side };
      var created = new List<Dictionary<string, object?>>();

      foreach (var requestedSide in requestedSides)
      {
        var subassemblyName = $"{subassemblyType}-{requestedSide}-{Guid.NewGuid():N}";
        // The stock catalog's macros are "Subassembly.BasicLane"; the short name is what people write.
        var macro = subassemblyType.Contains('.') ? subassemblyType : "Subassembly." + subassemblyType;
        var subassemblyId = civilDoc.SubassemblyCollection.ImportStockSubassembly(
          subassemblyName,
          macro,
          assembly.Location);
        var subassembly = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, subassemblyId, OpenMode.ForWrite);
        var sideParameter = subassembly.ParamsLong.FirstOrDefault(parameter => parameter.Key == "Side");
        if (sideParameter == null)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.API_ERROR",
            $"Stock subassembly '{subassemblyType}' has no Side parameter. No implicit side was assumed.");
        }

        sideParameter.Value = requestedSide.Equals("Left", StringComparison.OrdinalIgnoreCase) ? SideLeft : SideRight;
        var unknown = subParams.Keys.Except(ApplySubassemblyParameters(subassembly, subParams), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Stock subassembly '{subassemblyType}' has no parameter {string.Join(", ", unknown)}. Its parameters: {string.Join(", ", ParameterValues(subassembly).Keys)}.");
        }

        assembly.AddSubassembly(subassemblyId);

        created.Add(new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassemblyName,
          ["handle"] = CivilObjectUtils.GetHandle(subassembly),
          ["side"] = requestedSide,
        });
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyType"] = subassemblyType,
        ["subassemblies"] = created,
        ["added"] = true,
      };
    });
  }

  /// <summary>
  /// editAssembly on assembly <c>assemblyName</c> (required). Without <c>subassemblyName</c>: lists its subassemblies
  /// as a read (CivilExecution.ReadAsync: no undo step, the view doesn't move). With it, a write (one undo step):
  /// <c>delete</c> true erases that subassembly; otherwise the <c>parameters</c> object sets its stock parameters or
  /// properties. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown assembly or subassembly, CIVIL3D.INVALID_INPUT when any
  /// parameter couldn't be written (nothing is committed).
  /// </summary>
  public static Task<object?> EditAssemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var deleteSubassembly = PluginRuntime.GetOptionalBool(parameters, "delete") ?? false;
    var editParameters = ReadParameters(parameters?["parameters"] as JsonObject);

    if (string.IsNullOrWhiteSpace(subassemblyName))
    {
      // Only a listing: a write here added an empty undo step and moved the view for nothing.
      return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForRead);
        var subassemblies = GetSubassemblyIds(assembly)
          .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
          .Select(ToSubassemblySummary)
          .ToList();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyCount"] = subassemblies.Count,
          ["subassemblies"] = subassemblies,
        };
      });
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var openMode = deleteSubassembly || editParameters.Count > 0 ? OpenMode.ForWrite : OpenMode.ForRead;
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, openMode);
      var subassemblyIds = GetSubassemblyIds(assembly);

      var targetId = subassemblyIds.FirstOrDefault(id =>
      {
        var candidate = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
        return string.Equals(CivilObjectUtils.GetName(candidate), subassemblyName, StringComparison.OrdinalIgnoreCase);
      });
      if (targetId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Subassembly '{subassemblyName}' not found in assembly '{assemblyName}'.");

      var target = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, targetId, OpenMode.ForWrite);
      if (deleteSubassembly)
      {
        target.Erase();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyName"] = subassemblyName,
          ["deleted"] = true,
        };
      }

      var updated = ApplySubassemblyParameters(target, editParameters);
      if (editParameters.Count > 0 && updated.Count != editParameters.Count)
      {
        var missing = editParameters.Keys.Except(updated, StringComparer.OrdinalIgnoreCase);
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Subassembly parameters were not writable: {string.Join(", ", missing)}. The transaction was not committed.");
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyName"] = subassemblyName,
        ["updatedParameters"] = updated,
        ["updated"] = updated.Count > 0,
      };
    });
  }

  /// <summary>A JSON object of parameter values as a case-insensitive dictionary: numbers as double, booleans, everything else as text.</summary>
  private static Dictionary<string, object?> ReadParameters(JsonObject? values)
  {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    if (values == null) return result;
    foreach (var pair in values)
      result[pair.Key] = pair.Value?.GetValueKind() switch
      {
        System.Text.Json.JsonValueKind.Number => pair.Value.GetValue<double>(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => pair.Value?.ToString(),
      };
    return result;
  }

  /// <summary>The assembly named <paramref name="name"/> (case-insensitive). CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static Assembly FindAssemblyByName(
    CivilDocument civilDocument,
    Transaction transaction,
    string name,
    OpenMode openMode)
  {
    foreach (ObjectId id in civilDocument.AssemblyCollection)
    {
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, openMode);
      if (string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
        return assembly;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{name}' was not found in the drawing.");
  }

  /// <summary>Every subassembly in the assembly's groups, without duplicates.</summary>
  private static List<ObjectId> GetSubassemblyIds(Assembly assembly)
  {
    return assembly.Groups
      .SelectMany(group => group.GetSubassemblyIds().Cast<ObjectId>())
      .Where(id => !id.IsNull)
      .Distinct()
      .ToList();
  }

  /// <summary>
  /// Writes each value as a stock parameter, else as a property (late-bound). Returns the keys that were written;
  /// the caller compares them with what was asked.
  /// </summary>
  private static List<string> ApplySubassemblyParameters(AcDbObject subassembly, IReadOnlyDictionary<string, object?> parameters)
  {
    var updated = new List<string>();
    foreach (var pair in parameters)
    {
      if (subassembly is Subassembly stock && SetStockParameter(stock, pair.Key, pair.Value))
        updated.Add(pair.Key);
      else if (Civil3DCompatibility.TrySetProperty(subassembly, pair.Key, pair.Value))
        updated.Add(pair.Key);
    }
    return updated;
  }

  /// <summary>Sets one of a stock subassembly's own parameters (Width, Slope...), which live in its Params lists, not in properties.</summary>
  private static bool SetStockParameter(Subassembly subassembly, string key, object? value)
  {
    var number = subassembly.ParamsDouble.FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase));
    if (number != null)
    {
      number.Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
      return true;
    }

    var whole = subassembly.ParamsLong.FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase));
    if (whole != null)
    {
      whole.Value = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
      return true;
    }

    var text = subassembly.ParamsString.FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase));
    if (text != null)
    {
      text.Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
      return true;
    }

    return false;
  }

  /// <summary>All of a stock subassembly's parameters (double, long and string) by key.</summary>
  private static Dictionary<string, object?> ParameterValues(Subassembly subassembly)
  {
    var values = new Dictionary<string, object?>();
    foreach (var parameter in subassembly.ParamsDouble) values[parameter.Key] = parameter.Value;
    foreach (var parameter in subassembly.ParamsLong) values[parameter.Key] = parameter.Value;
    foreach (var parameter in subassembly.ParamsString) values[parameter.Key] = parameter.Value;
    return values;
  }

  private static string? GetStyleName(Assembly assembly, Transaction transaction)
  {
    if (assembly.StyleId.IsNull) return null;
    return CivilObjectUtils.GetName(transaction.GetObject(assembly.StyleId, OpenMode.ForRead));
  }

  private static List<Dictionary<string, object?>> GetSubassemblies(Assembly assembly, Transaction transaction)
  {
    return GetSubassemblyIds(assembly)
      .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
      .Select(ToSubassemblySummary)
      .ToList();
  }

  /// <summary>
  /// A subassembly as the answer shape. Stock ones report their Side parameter and Params lists; anything else falls
  /// back to late-bound Side and readable scalar properties.
  /// </summary>
  private static Dictionary<string, object?> ToSubassemblySummary(AcDbObject subassembly)
  {
    if (subassembly is Subassembly stock)
    {
      var side = stock.ParamsLong.FirstOrDefault(parameter => parameter.Key == "Side")?.Value;
      return new Dictionary<string, object?>
      {
        ["name"] = stock.Name,
        ["handle"] = CivilObjectUtils.GetHandle(stock),
        ["type"] = stock.GetType().Name,
        ["className"] = stock.GetType().Name,
        ["side"] = side == SideLeft ? "left" : side == SideRight ? "right" : "none",
        ["parameters"] = ParameterValues(stock),
      };
    }

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassembly.Handle.ToString(),
      ["handle"] = CivilObjectUtils.GetHandle(subassembly),
      ["type"] = subassembly.GetType().Name,
      ["className"] = subassembly.GetType().Name,
      ["side"] = Civil3DCompatibility.GetPropertyValue(subassembly, "Side")?.ToString()?.ToLowerInvariant() ?? "none",
      ["parameters"] = Civil3DCompatibility.GetReadableScalarProperties(subassembly),
    };
  }

  private static Dictionary<string, object?> ToAssemblySummary(Assembly assembly)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = assembly.Name,
      ["handle"] = CivilObjectUtils.GetHandle(assembly),
      ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
      ["type"] = assembly.Type.ToString(),
    };
  }
}
