// Pay-item quantities and a preliminary cost estimate from the drawing: earthwork between two surfaces, pipe network
// lengths and structure counts, priced with the caller's unit prices.
// JSON-RPC methods (CommandDispatcher): exportPayItems, calculateMaterialCostEstimate. Called by
// src/tools/domains/costEstimationDomain.ts (civil3d_cost_estimation).
// Both run in CivilExecution.MeasureAsync: earthwork needs a temporary TIN volume surface, which is created in a
// transaction that is never committed (the drawing keeps nothing; the lock leaves one blank undo step). The CSV is
// written only through FileBoundary.WriteAllTextAtomic (allowed roots, .csv only, no overwrite unless asked). Pipe
// network access is late-bound through CivilObjectUtils. Corridor material quantities come from the corridor's QTO
// material lists (QuantityCommands.CorridorMaterialLists); when there are none the estimate goes on with a note. CSV
// fields are RFC 4180 quoted by DesignMath; lengths and volumes are labelled in the drawing's units (LF/CY or M/M3).
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using TinVolumeSurface = Autodesk.Civil.DatabaseServices.TinVolumeSurface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Pay item export and construction cost estimation commands.
/// Pulls quantities from Civil 3D (earthwork, corridor layers, pipes, structures)
/// and combines them with user-supplied unit prices to generate cost estimates.
/// </summary>
public static class CostEstimationCommands
{
  // ─── exportPayItems ──────────────────────────────────────────────────────────

  /// <summary>
  /// exportPayItems: writes a quantity CSV to <c>outputPath</c> (required) and returns its lines. Includes earthwork
  /// cut/fill when <c>includeEarthwork</c> (default true) and both <c>baseSurface</c> and <c>designSurface</c> are
  /// given, pipe network lengths (<c>includePipeLengths</c>), structure counts by part type
  /// (<c>includeStructureCounts</c>). Optional <c>payItems</c> (<c>{code, description, unit, unitPrice}</c>) add price
  /// columns when a line matches by code or description; <c>overwrite</c> allows replacing the file. With
  /// <c>corridorName</c> and <c>includeCorridorMaterials</c> (default true) each QTO material list of the corridor
  /// (QuantityCommands.CorridorMaterialLists) adds a cut and a fill line; when there is none, or Civil 3D can't compute
  /// one, the estimate goes on without it and <c>notes</c> says why. Lengths are LF in a feet drawing and M in meters,
  /// volumes CY or M3. Doesn't change the drawing; writes one file (RFC 4180 CSV). Throws CIVIL3D.API_ERROR when
  /// volumes are needed and the drawing units aren't feet or meters, CIVIL3D.OBJECT_NOT_FOUND / INVALID_INPUT for the
  /// surfaces or the corridor, and FileBoundary's path errors (e.g. CIVIL3D.PATH_NOT_ALLOWED).
  /// </summary>
  public static Task<object?> ExportPayItemsAsync(JsonObject? parameters)
  {
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");
    var baseSurface = PluginRuntime.GetOptionalString(parameters, "baseSurface");
    var designSurface = PluginRuntime.GetOptionalString(parameters, "designSurface");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var includeEarthwork = PluginRuntime.GetOptionalBool(parameters, "includeEarthwork") ?? true;
    var includeCorridorMaterials = PluginRuntime.GetOptionalBool(parameters, "includeCorridorMaterials") ?? true;
    var includePipeLengths = PluginRuntime.GetOptionalBool(parameters, "includePipeLengths") ?? true;
    var includeStructureCounts = PluginRuntime.GetOptionalBool(parameters, "includeStructureCounts") ?? true;
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    // Parse pay items from parameters
    var payItemsNode = parameters?["payItems"] as JsonArray;
    var payItems = ParsePayItems(payItemsNode);

    return CivilExecution.MeasureAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var quantityLines = new List<Dictionary<string, object?>>();
      var notes = new List<string>();

      // Earthwork volumes
      if (includeEarthwork && !string.IsNullOrWhiteSpace(baseSurface) && !string.IsNullOrWhiteSpace(designSurface))
      {
        var (cut, fill, unit) = GetEarthworkVolumes(civilDoc, database, transaction, baseSurface!, designSurface!);
        quantityLines.Add(MakeLine("203.01", "Unclassified Excavation (Cut)", unit, Math.Round(cut, 1), payItems));
        quantityLines.Add(MakeLine("203.02", "Embankment (Fill)", unit, Math.Round(fill, 1), payItems));
      }

      // Civil 3D keeps corridor material totals in the QTO material lists of the sample-line groups, not on the corridor.
      if (includeCorridorMaterials && !string.IsNullOrWhiteSpace(corridorName))
      {
        foreach (var (description, unit, quantity) in CorridorMaterialQuantities(civilDoc, database, transaction, corridorName!, notes))
        {
          quantityLines.Add(MakeLine(string.Empty, description, unit, quantity, payItems));
        }
      }

      // Pipe network lengths
      if (includePipeLengths)
      {
        var lengthUnit = DesignMath.LengthPayUnit(CivilObjectUtils.LinearUnits(database));
        var pipeNetworks = GetAllPipeNetworkLengths(civilDoc, transaction);
        foreach (var (netName, length) in pipeNetworks)
        {
          quantityLines.Add(MakeLine(string.Empty, $"Pipe Network '{netName}' — Total Length", lengthUnit, Math.Round(length, 1), payItems));
        }
      }

      // Structure counts
      if (includeStructureCounts)
      {
        var structureCounts = GetStructureCounts(civilDoc, transaction);
        foreach (var (typeName, count) in structureCounts)
        {
          quantityLines.Add(MakeLine(string.Empty, typeName, "EA", count, payItems));
        }
      }

      // Write CSV
      var csvContent = BuildCsv(quantityLines, payItems.Count > 0);
      var canonicalOutputPath = FileBoundary.WriteAllTextAtomic(
        outputPath, csvContent, Encoding.UTF8, overwrite, ".csv");

      return new Dictionary<string, object?>
      {
        ["outputPath"] = canonicalOutputPath,
        ["lineItemCount"] = quantityLines.Count,
        ["hasPricing"] = payItems.Count > 0,
        ["quantityLines"] = quantityLines,
        ["notes"] = notes,
        ["exported"] = true,
      };
    });
  }

  // ─── calculateMaterialCostEstimate ───────────────────────────────────────────

  /// <summary>
  /// calculateMaterialCostEstimate: prices the drawing's quantities (earthwork when <c>baseSurface</c> and
  /// <c>designSurface</c> are given, every pipe network's length, structure counts) with <c>payItems</c> (required, at
  /// least one with a description), adds <c>mobilizationPercent</c> (default 5) and <c>contingencyPercent</c> (default
  /// 0), and returns line items, totals and the pay items that matched nothing. <c>corridorName</c> adds the cut and
  /// fill of each of the corridor's QTO material lists as in exportPayItems (skipped with a <c>notes</c> entry when
  /// there are none or Civil 3D can't compute them). Lengths are LF in a feet drawing and M in meters. With
  /// <c>outputPath</c> also writes an RFC 4180 CSV (<c>overwrite</c> optional). Doesn't change the drawing. Throws
  /// CIVIL3D.INVALID_INPUT without pay items, CIVIL3D.API_ERROR for volumes in unsupported units, plus surface,
  /// corridor and FileBoundary errors.
  /// </summary>
  public static Task<object?> CalculateMaterialCostEstimateAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");
    var baseSurface = PluginRuntime.GetOptionalString(parameters, "baseSurface");
    var designSurface = PluginRuntime.GetOptionalString(parameters, "designSurface");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var contingencyPct = PluginRuntime.GetOptionalDouble(parameters, "contingencyPercent") ?? 0.0;
    var mobilizationPct = PluginRuntime.GetOptionalDouble(parameters, "mobilizationPercent") ?? 5.0;
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    var payItemsNode = parameters?["payItems"] as JsonArray;
    var payItems = ParsePayItems(payItemsNode);

    if (payItems.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least one pay item with a unit price is required to generate a cost estimate.");
    }

    return CivilExecution.MeasureAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var lineItems = new List<Dictionary<string, object?>>();
      var matched = new HashSet<PayItem>();
      var notes = new List<string>();
      double subtotal = 0;

      // Earthwork
      if (!string.IsNullOrWhiteSpace(baseSurface) && !string.IsNullOrWhiteSpace(designSurface))
      {
        var (cut, fill, unit) = GetEarthworkVolumes(civilDoc, database, transaction, baseSurface!, designSurface!);
        AddCostLine(lineItems, payItems, matched, "203.01", "Unclassified Excavation (Cut)", unit, Math.Round(cut, 1), ref subtotal);
        AddCostLine(lineItems, payItems, matched, "203.02", "Embankment (Fill)", unit, Math.Round(fill, 1), ref subtotal);
      }

      // Corridor materials, from the QTO material lists (see exportPayItems)
      if (!string.IsNullOrWhiteSpace(corridorName))
      {
        foreach (var (description, unit, quantity) in CorridorMaterialQuantities(civilDoc, database, transaction, corridorName!, notes))
        {
          AddCostLine(lineItems, payItems, matched, string.Empty, description, unit, quantity, ref subtotal);
        }
      }

      // Pipe lengths
      var lengthUnit = DesignMath.LengthPayUnit(CivilObjectUtils.LinearUnits(database));
      var pipeNetworks = GetAllPipeNetworkLengths(civilDoc, transaction);
      foreach (var (netName, length) in pipeNetworks)
      {
        AddCostLine(lineItems, payItems, matched, string.Empty, $"Pipe Network '{netName}'", lengthUnit, Math.Round(length, 1), ref subtotal);
      }

      // Structure counts
      var structs = GetStructureCounts(civilDoc, transaction);
      foreach (var (typeName, count) in structs)
      {
        AddCostLine(lineItems, payItems, matched, string.Empty, typeName, "EA", count, ref subtotal);
      }

      double mobilizationCost = subtotal * mobilizationPct / 100.0;
      double contingencyCost = (subtotal + mobilizationCost) * contingencyPct / 100.0;
      double total = subtotal + mobilizationCost + contingencyCost;

      var result = new Dictionary<string, object?>
      {
        ["lineItems"] = lineItems,
        ["subtotal"] = Math.Round(subtotal, 2),
        ["mobilizationPercent"] = mobilizationPct,
        ["mobilizationCost"] = Math.Round(mobilizationCost, 2),
        ["contingencyPercent"] = contingencyPct,
        ["contingencyCost"] = Math.Round(contingencyCost, 2),
        ["totalEstimatedCost"] = Math.Round(total, 2),
        ["totalFormatted"] = $"${total:N2}",
        ["unmatchedPayItems"] = payItems.Where(item => !matched.Contains(item)).Select(item => item.Description).ToList(),
        ["notes"] = notes,
        ["note"] = "Estimate is preliminary. Field verification of quantities required for bid documents. "
          + "Pay items are prices: each is matched to a quantity from the drawing by code or description, and one that matches nothing adds nothing.",
      };

      if (!string.IsNullOrWhiteSpace(outputPath))
      {
        var csv = BuildCostCsv(lineItems, subtotal, mobilizationCost, contingencyCost, total);
        result["outputPath"] = FileBoundary.WriteAllTextAtomic(
          outputPath!, csv, Encoding.UTF8, overwrite, ".csv");
        result["exported"] = true;
      }

      return (object?)result;
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  private sealed record PayItem(string Code, string Description, string Unit, double UnitPrice);

  /// <summary>The caller's pay items; entries without a description are dropped and a bad unitPrice counts as 0.</summary>
  private static List<PayItem> ParsePayItems(JsonArray? node)
  {
    var items = new List<PayItem>();
    if (node == null) return items;
    foreach (var item in node)
    {
      if (item is not JsonObject obj) continue;
      var code = obj["code"]?.GetValue<string>() ?? string.Empty;
      var desc = obj["description"]?.GetValue<string>() ?? string.Empty;
      var unit = obj["unit"]?.GetValue<string>() ?? string.Empty;
      double price = 0;
      try { price = obj["unitPrice"]?.GetValue<double>() ?? 0; } catch { }
      if (!string.IsNullOrWhiteSpace(desc))
        items.Add(new PayItem(code, desc, unit, price));
    }
    return items;
  }

  /// <summary>A quantity line for exportPayItems, priced when a pay item matches its code or exact description.</summary>
  private static Dictionary<string, object?> MakeLine(string code, string description, string unit, double quantity, List<PayItem> payItems)
  {
    var match = payItems.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)
      || string.Equals(p.Description, description, StringComparison.OrdinalIgnoreCase));
    return new Dictionary<string, object?>
    {
      ["payItemCode"] = !string.IsNullOrEmpty(code) ? code : (match?.Code ?? string.Empty),
      ["description"] = description,
      ["unit"] = unit,
      ["quantity"] = quantity,
      ["unitPrice"] = match?.UnitPrice,
      ["totalCost"] = match != null ? Math.Round(match.UnitPrice * quantity, 2) : (object?)null,
    };
  }

  /// <summary>
  /// Adds a priced line for the estimate: matched by code, or by either description containing the other. Adds the
  /// line total to <paramref name="subtotal"/> and records the pay item as matched.
  /// </summary>
  private static void AddCostLine(List<Dictionary<string, object?>> lineItems, List<PayItem> payItems, HashSet<PayItem> matched,
    string code, string description, string unit, double quantity, ref double subtotal)
  {
    // A line with no code matches on its description alone: two empty codes aren't a match.
    var match = payItems.FirstOrDefault(p => (!string.IsNullOrEmpty(code) && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
      || description.IndexOf(p.Description, StringComparison.OrdinalIgnoreCase) >= 0
      || p.Description.IndexOf(description, StringComparison.OrdinalIgnoreCase) >= 0);

    var lineTotal = match != null ? match.UnitPrice * quantity : 0;
    subtotal += lineTotal;
    if (match != null) matched.Add(match);

    lineItems.Add(new Dictionary<string, object?>
    {
      ["payItemCode"] = !string.IsNullOrEmpty(code) ? code : (match?.Code ?? string.Empty),
      ["description"] = description,
      ["unit"] = unit,
      ["quantity"] = quantity,
      ["unitPrice"] = match?.UnitPrice ?? 0,
      ["lineTotal"] = Math.Round(lineTotal, 2),
    });
  }

  /// <summary>
  /// Unadjusted cut and fill between two named surfaces from a temporary TIN volume surface (only valid inside
  /// MeasureAsync's uncommitted transaction): M3 in a meters drawing, CY (cubic feet / 27) in a feet or survey-feet
  /// drawing. CIVIL3D.API_ERROR for other units; surface lookups and RequireDifferentSurfaces throw their own codes.
  /// </summary>
  private static (double Cut, double Fill, string Unit) GetEarthworkVolumes(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction, string baseSurface, string designSurface)
  {
    var baseSurf = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurface, OpenMode.ForRead);
    var desSurf = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, designSurface, OpenMode.ForRead);
    CivilObjectUtils.RequireDifferentSurfaces(baseSurf, desSurf);
    var temporaryName = $"MCP_TMP_COST_VOLUME_{Guid.NewGuid():N}";
    var volumeSurfaceId = TinVolumeSurface.Create(temporaryName, baseSurf.ObjectId, desSurf.ObjectId);
    var volumeSurface = CivilObjectUtils.GetRequiredObject<TinVolumeSurface>(transaction, volumeSurfaceId, OpenMode.ForRead);
    var properties = volumeSurface.GetVolumeProperties();
    var (factor, unit) = PayVolumeUnit(database);
    return (properties.UnadjustedCutVolume * factor, properties.UnadjustedFillVolume * factor, unit);
  }

  /// <summary>How a volume in the drawing's cubic units becomes a pay quantity: M3 as is in a meters drawing, CY (cubic
  /// feet / 27) in a feet or survey-feet drawing. CIVIL3D.API_ERROR for other units.</summary>
  private static (double Factor, string Unit) PayVolumeUnit(Database database)
  {
    return CivilObjectUtils.LinearUnits(database) switch
    {
      "meters" => (1.0, "M3"),
      "feet" => (1.0 / 27.0, "CY"),
      _ => throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Cannot convert drawing units '{database.Insunits}' to cubic yards or cubic meters for cost estimation."),
    };
  }

  /// <summary>
  /// Pay quantities from corridor <paramref name="corridorName"/>'s QTO material lists (QuantityCommands
  /// .CorridorMaterialLists): a cut and a fill line per list, in CY or M3. Never fails the estimate for what Civil 3D
  /// can't give: no material list, or a list it can't compute, adds a line to <paramref name="notes"/> instead. An
  /// unknown corridor is still CIVIL3D.OBJECT_NOT_FOUND (a wrong name should be fixed, not skipped).
  /// </summary>
  private static List<(string Description, string Unit, double Quantity)> CorridorMaterialQuantities(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction, string corridorName, List<string> notes)
  {
    var lines = new List<(string, string, double)>();
    var lists = QuantityCommands.CorridorMaterialLists(civilDoc, transaction, corridorName, withVolumes: true);
    if (lists.Count == 0)
    {
      notes.Add(QuantityCommands.NoMaterialListNote(corridorName) + " Corridor materials were left out of this estimate.");
      return lines;
    }

    var (factor, unit) = PayVolumeUnit(database);
    foreach (var list in lists)
    {
      if (list.Error != null || list.CutVolume is null || list.FillVolume is null)
      {
        notes.Add($"Material list '{list.MaterialList}' ({list.SampleLineGroup}) was left out: {list.Error ?? "it has no sample-line results"}");
        continue;
      }

      lines.Add(($"Corridor '{corridorName}' {list.MaterialList} — Cut", unit, Math.Round(list.CutVolume.Value * factor, 1)));
      lines.Add(($"Corridor '{corridorName}' {list.MaterialList} — Fill", unit, Math.Round(list.FillVolume.Value * factor, 1)));
    }

    return lines;
  }

  /// <summary>
  /// Total pipe length (3D, else 2D, else Length) per gravity pipe network, in drawing units, read late-bound; networks
  /// with no length are left out. Any error yields whatever was collected so far.
  /// </summary>
  private static List<(string NetworkName, double Length)> GetAllPipeNetworkLengths(object civilDoc, Transaction transaction)
  {
    var results = new List<(string, double)>();
    try
    {
      var candidates = new object?[]
      {
        CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      };

      foreach (var candidate in candidates)
      {
        if (candidate is not IEnumerable enumerable) continue;
        foreach (var item in enumerable)
        {
          if (item is not ObjectId id || id == ObjectId.Null) continue;
          var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, id, OpenMode.ForRead);
          var networkName = CivilObjectUtils.GetName(network) ?? "Unknown";
          double totalLength = 0;

          // Sum all pipe lengths
          var pipeIds = CivilObjectUtils.InvokeMethod(network, "GetPipeIds") as IEnumerable;
          if (pipeIds != null)
          {
            foreach (var pItem in pipeIds)
            {
              if (pItem is not ObjectId pId || pId == ObjectId.Null) continue;
              var pipe = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, pId, OpenMode.ForRead);
              var length = CivilObjectUtils.GetPropertyValue<double?>(pipe, "Length3D")
                        ?? CivilObjectUtils.GetPropertyValue<double?>(pipe, "Length2D")
                        ?? CivilObjectUtils.GetPropertyValue<double?>(pipe, "Length") ?? 0;
              totalLength += length;
            }
          }

          if (totalLength > 0) results.Add((networkName, totalLength));
        }
      }
    }
    catch { /* skip */ }
    return results;
  }

  /// <summary>Structure counts across all pipe networks by part type (else description), read late-bound; errors yield what was counted.</summary>
  private static List<(string TypeName, double Count)> GetStructureCounts(object civilDoc, Transaction transaction)
  {
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    try
    {
      var networkIds = CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds") as IEnumerable;
      if (networkIds == null) return [];

      foreach (var netItem in networkIds)
      {
        if (netItem is not ObjectId netId || netId == ObjectId.Null) continue;
        var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, netId, OpenMode.ForRead);

        var structIds = CivilObjectUtils.InvokeMethod(network, "GetStructureIds") as IEnumerable;
        if (structIds == null) continue;

        foreach (var sItem in structIds)
        {
          if (sItem is not ObjectId sId || sId == ObjectId.Null) continue;
          var structure = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, sId, OpenMode.ForRead);
          var typeName = CivilObjectUtils.GetStringProperty(structure, "PartType")
                      ?? CivilObjectUtils.GetStringProperty(structure, "PartDescription")
                      ?? "Structure";
          counts[typeName] = counts.GetValueOrDefault(typeName) + 1;
        }
      }
    }
    catch { /* skip */ }

    return counts.Select(kv => ($"{kv.Key} (Structure)", (double)kv.Value)).ToList();
  }

  /// <summary>The pay-item CSV text, with price columns only when pay items were given. Every field is RFC 4180 quoted
  /// when it needs it and numbers use a dot (DesignMath.CsvLine).</summary>
  private static string BuildCsv(List<Dictionary<string, object?>> lines, bool hasPricing)
  {
    var sb = new StringBuilder();
    sb.AppendLine(hasPricing
      ? "Pay Item Code,Description,Unit,Quantity,Unit Price,Total Cost"
      : "Pay Item Code,Description,Unit,Quantity");

    foreach (var line in lines)
    {
      sb.AppendLine(hasPricing
        ? DesignMath.CsvLine([line["payItemCode"], line["description"], line["unit"], line["quantity"], line["unitPrice"], line["totalCost"]])
        : DesignMath.CsvLine([line["payItemCode"], line["description"], line["unit"], line["quantity"]]));
    }
    return sb.ToString();
  }

  /// <summary>The cost-estimate CSV text: line items, then subtotal, mobilization, contingency and total rows, quoted
  /// and formatted like <see cref="BuildCsv"/>.</summary>
  private static string BuildCostCsv(List<Dictionary<string, object?>> lineItems, double subtotal, double mob, double contingency, double total)
  {
    var sb = new StringBuilder();
    sb.AppendLine("Pay Item Code,Description,Unit,Quantity,Unit Price,Line Total");
    foreach (var item in lineItems)
      sb.AppendLine(DesignMath.CsvLine([item["payItemCode"], item["description"], item["unit"], item["quantity"], item["unitPrice"], item["lineTotal"]]));
    sb.AppendLine();
    sb.AppendLine(DesignMath.CsvLine([null, null, null, null, "Subtotal", Math.Round(subtotal, 2)]));
    sb.AppendLine(DesignMath.CsvLine([null, null, null, null, "Mobilization", Math.Round(mob, 2)]));
    sb.AppendLine(DesignMath.CsvLine([null, null, null, null, "Contingency", Math.Round(contingency, 2)]));
    sb.AppendLine(DesignMath.CsvLine([null, null, null, null, "TOTAL", Math.Round(total, 2)]));
    return sb.ToString();
  }
}
