// SuperelevationCommands.cs - alignment superelevation. Methods (CommandDispatcher.cs): getSuperelevation,
// setSuperelevation, setSuperelevationStation, checkSuperelevationDesign, generateSuperelevationReport. Caller:
// src/tools/domains/superelevationDomain.ts (civil3d_superelevation). Reads run in CivilExecution.ReadAsync (no undo
// step); setSuperelevationStation is the only write, in CivilExecution.WriteAsync (one transaction, one undo step).
// setSuperelevation always refuses (no API runs the wizard). Rule: no design standard is assumed; checks need the
// engineer's limits, and nothing claims a violation without them. The report file goes through FileBoundary.
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_superelevation_* tools.
///
/// Civil 3D API notes:
///   Alignment.SuperelevationCurves exposes calculated curve groups and their
///   typed critical stations. Wizard calculation settings are not exposed by
///   the managed API and therefore are never inferred, but a reviewed table can be
///   entered one critical station at a time.
/// </summary>
public static class SuperelevationCommands
{
  // -------------------------------------------------------------------------
  // getSuperelevation
  // -------------------------------------------------------------------------

  /// <summary><c>getSuperelevation</c>: reads <c>alignmentName</c> (required) and <c>includeRawData</c> (bool). Returns
  /// design speeds, superelevation type, curve count, whether any superelevation was calculated, and with
  /// <c>includeRawData</c> the critical-station table (slopes in percent). Wizard settings come back null (not exposed).
  /// Read-only. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment.</summary>
  public static Task<object?> GetSuperelevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var includeRawData = PluginRuntime.GetOptionalBool(parameters, "includeRawData") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);

      var curves = alignment.SuperelevationCurves;
      var designSpeeds = alignment.DesignSpeeds
        .Select(speed => new Dictionary<string, object?>
        {
          ["station"] = speed.Station,
          ["value"] = speed.Value,
          ["comment"] = speed.Comment,
        })
        .ToList();

      var result = new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeed"] = designSpeeds.FirstOrDefault()?.GetValueOrDefault("value"),
        ["designSpeeds"] = designSpeeds,
        ["attainmentMethod"] = null,
        ["normalCrownSlope"] = null,
        ["pivotPoint"] = null,
        ["superelevationType"] = alignment.SuperelevationType.ToString(),
        ["curveCount"] = curves.Count,
        // Every curve is listed whether or not it was ever designed, with its start and end as critical stations;
        // calculated superelevation adds the transition stations between them.
        ["hasSuperelevation"] = alignment.SuperelevationCriticalStations.Count > curves.Count * 2,
        ["managedApiLimit"] = "Civil 3D does not expose wizard attainment, crown, or pivot settings through the managed API.",
      };

      if (includeRawData)
      {
        result["rawTable"] = ReadSuperelevationTable(alignment);
      }

      return result;
    });
  }

  // -------------------------------------------------------------------------
  // setSuperelevation
  // -------------------------------------------------------------------------

  /// <summary><c>setSuperelevation</c>: checks <c>alignmentName</c>, <c>designSpeed</c>, <c>normalCrownSlope</c> are
  /// given, then always throws CIVIL3D.API_ERROR: the wizard can't be run through the API. Changes nothing; use
  /// setSuperelevationStation for a reviewed table.</summary>
  public static Task<object?> SetSuperelevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var normalCrownSlope = PluginRuntime.GetRequiredDouble(parameters, "normalCrownSlope");
    var attainmentMethod = PluginRuntime.GetOptionalString(parameters, "attainmentMethod") ?? "AASHTO_2011";
    var pivotPoint = PluginRuntime.GetOptionalString(parameters, "pivotPoint") ?? "centerline";

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D has no call that runs the superelevation wizard. Use the wizard, or enter a reviewed table with set_station; no alignment data was modified.");
  }

  // -------------------------------------------------------------------------
  // setSuperelevationStation
  // -------------------------------------------------------------------------

  /// <summary>
  /// Sets the cross slopes at one critical station, adding the station if it isn't one yet. Slopes are percent, as the
  /// table reads them. A road without a median has no inner lanes, so a side's slope goes on whichever segments it has.
  /// <c>setSuperelevationStation</c>: reads <c>alignmentName</c>, <c>station</c> (required), <c>stationType</c> (a
  /// SuperelevationCriticalStationType name, default Manual) and at least one of <c>leftLaneSlope</c>,
  /// <c>rightLaneSlope</c>, <c>leftShoulderSlope</c>, <c>rightShoulderSlope</c>. Changes the drawing (WriteAsync, one
  /// undo step). CIVIL3D.INVALID_INPUT (bad type, no slope, station off the alignment), CIVIL3D.OBJECT_NOT_FOUND.
  /// </summary>
  public static Task<object?> SetSuperelevationStationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var typeName = PluginRuntime.GetOptionalString(parameters, "stationType") ?? nameof(SuperelevationCriticalStationType.Manual);
    if (!Enum.TryParse<SuperelevationCriticalStationType>(typeName, true, out var stationType))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{typeName}' isn't a critical station type: {string.Join(", ", Enum.GetNames<SuperelevationCriticalStationType>())}.");
    }

    var slopes = new (string Name, SuperelevationCrossSegmentType[] Segments)[]
    {
      ("leftLaneSlope", [SuperelevationCrossSegmentType.LeftOutLaneCrossSlope, SuperelevationCrossSegmentType.LeftInLaneCrossSlope]),
      ("rightLaneSlope", [SuperelevationCrossSegmentType.RightOutLaneCrossSlope, SuperelevationCrossSegmentType.RightInLaneCrossSlope]),
      ("leftShoulderSlope", [SuperelevationCrossSegmentType.LeftOutShoulderCrossSlope, SuperelevationCrossSegmentType.LeftInShoulderCrossSlope]),
      ("rightShoulderSlope", [SuperelevationCrossSegmentType.RightOutShoulderCrossSlope, SuperelevationCrossSegmentType.RightInShoulderCrossSlope]),
    }
      .Select(slope => (slope.Segments, Percent: PluginRuntime.GetOptionalDouble(parameters, slope.Name)))
      .Where(slope => slope.Percent.HasValue)
      .ToList();
    if (slopes.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give at least one of leftLaneSlope, rightLaneSlope, leftShoulderSlope, rightShoulderSlope.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      const double tolerance = 0.001;
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      if (station < alignment.StartingStation || station > alignment.EndingStation)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Station {station} is off alignment '{alignmentName}' ({alignment.StartingStation} to {alignment.EndingStation}).");
      }

      alignment.UpgradeOpen();
      var stations = alignment.SuperelevationCriticalStations;
      var added = !stations.IsCriticalStation(station, tolerance);
      if (added) stations.Add(station, stationType);
      var critical = stations.GetCriticalStationAt(station, tolerance);

      var written = 0;
      foreach (var (segments, percent) in slopes)
      {
        foreach (var segment in segments)
        {
          try
          {
            critical.SetSlope(segment, percent!.Value / 100.0);
            written++;
          }
          catch (InvalidOperationException)
          {
            // This road has no such segment.
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["station"] = critical.Station,
        ["stationType"] = critical.StationType.ToString(),
        ["added"] = added,
        ["segmentsWritten"] = written,
        ["leftOutLaneSlope"] = TryGetSlopePercent(critical, SuperelevationCrossSegmentType.LeftOutLaneCrossSlope),
        ["rightOutLaneSlope"] = TryGetSlopePercent(critical, SuperelevationCrossSegmentType.RightOutLaneCrossSlope),
      };
    });
  }

  // -------------------------------------------------------------------------
  // checkSuperelevationDesign
  // -------------------------------------------------------------------------

  /// <summary><c>checkSuperelevationDesign</c>: compares each critical station's largest lane cross slope (percent)
  /// with <c>maxSuperelevation</c> (required, same units, engineer-approved). Reads <c>alignmentName</c>,
  /// <c>designSpeed</c> (required, echoed only) and <c>checkAttainmentLength</c> / <c>checkRunoffLength</c> (default
  /// true; both must be false, as the API can't verify them). Read-only. Throws CIVIL3D.INVALID_INPUT (no max),
  /// CIVIL3D.API_ERROR (length checks asked for, no calculated stations, a station with no lane slope),
  /// CIVIL3D.OBJECT_NOT_FOUND.</summary>
  public static Task<object?> CheckSuperelevationDesignAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var maxSuperelevation = PluginRuntime.GetOptionalDouble(parameters, "maxSuperelevation")
      ?? throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "checkSuperelevationDesign requires an engineer-approved 'maxSuperelevation'; the plugin does not assume a design standard.");
    var checkAttainmentLength = PluginRuntime.GetOptionalBool(parameters, "checkAttainmentLength") ?? true;
    var checkRunoffLength = PluginRuntime.GetOptionalBool(parameters, "checkRunoffLength") ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var violations = new List<Dictionary<string, object?>>();
      var passed = true;

      if (checkAttainmentLength || checkRunoffLength)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          "The managed Civil 3D API does not expose enough criteria data to verify attainment or runoff length. Set both checks to false to evaluate only the explicit maximum cross-slope limit.");
      }

      var rawTable = ReadSuperelevationTable(alignment);
      if (rawTable.Count == 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Alignment '{alignmentName}' has no calculated superelevation critical stations to check.");
      }

      // Check each station for violations
      foreach (var row in rawTable)
      {
        var leftSlope = row["leftSlope"] as double?;
        var rightSlope = row["rightSlope"] as double?;
        var station = Convert.ToDouble(row["station"]);

        if (!leftSlope.HasValue && !rightSlope.HasValue)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.API_ERROR",
            $"No lane cross-slope is defined at station {station} on alignment '{alignmentName}'.");
        }

        if ((leftSlope.HasValue && Math.Abs(leftSlope.Value) > maxSuperelevation)
          || (rightSlope.HasValue && Math.Abs(rightSlope.Value) > maxSuperelevation))
        {
          passed = false;
          violations.Add(new Dictionary<string, object?>
          {
            ["station"] = station,
            ["violationType"] = "MAX_SUPERELEVATION_EXCEEDED",
            ["leftSlope"] = leftSlope,
            ["rightSlope"] = rightSlope,
            ["maxAllowed"] = maxSuperelevation,
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeed"] = designSpeed,
        ["maxSuperelevation"] = maxSuperelevation,
        ["passed"] = passed,
        ["violationCount"] = violations.Count,
        ["violations"] = violations,
      };
    });
  }

  // -------------------------------------------------------------------------
  // generateSuperelevationReport
  // -------------------------------------------------------------------------

  /// <summary><c>generateSuperelevationReport</c>: a plain-text report (design speeds, type, and with
  /// <c>includeRunoffTable</c>, default true, the critical-station table). Reads <c>alignmentName</c> (required),
  /// <c>outputPath</c> (optional .txt, written atomically through FileBoundary; <c>overwrite</c> to replace) and
  /// <c>includeViolations</c>, which must be false (CIVIL3D.INVALID_INPUT otherwise: no criteria, no violations).
  /// Returns the text inline when no path is given. Doesn't change the drawing; path errors come from FileBoundary.</summary>
  public static Task<object?> GenerateSuperelevationReportAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var includeRunoffTable = PluginRuntime.GetOptionalBool(parameters, "includeRunoffTable") ?? true;
    var includeViolations = PluginRuntime.GetOptionalBool(parameters, "includeViolations") ?? true;
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      if (includeViolations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          "A report cannot claim design violations without an explicit reviewed criteria set. Set includeViolations=false to export the Civil 3D data table only.");
      }

      var designSpeeds = alignment.DesignSpeeds.Select(speed => speed.Value).ToList();
      var rawTable = includeRunoffTable ? ReadSuperelevationTable(alignment) : new List<Dictionary<string, object?>>();
      var sb = new StringBuilder();

      sb.AppendLine($"SUPERELEVATION REPORT — {alignmentName}");
      sb.AppendLine($"Design Speeds:       {(designSpeeds.Count == 0 ? "not assigned" : string.Join(", ", designSpeeds))}");
      sb.AppendLine($"Superelevation Type: {alignment.SuperelevationType}");
      sb.AppendLine($"Report Date:         {DateTime.Now:yyyy-MM-dd HH:mm}");
      sb.AppendLine(new string('-', 70));

      if (includeRunoffTable && rawTable.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("SUPERELEVATION TABLE");
        sb.AppendLine($"{"Station",-14} {"Left Slope %",-14} {"Right Slope %",-14} {"Notes",-20}");
        sb.AppendLine(new string('-', 62));
        foreach (var row in rawTable)
        {
          // A lane with no cross slope at a station is null: a dash, not 0.00 (Convert.ToDouble(null) is 0), which
          // would read as a flat lane.
          var station = row.TryGetValue("station", out var st) && st != null ? $"{Convert.ToDouble(st):F3}" : "—";
          var left = row.TryGetValue("leftSlope", out var ls) && ls != null ? $"{Convert.ToDouble(ls):F2}" : "—";
          var right = row.TryGetValue("rightSlope", out var rs) && rs != null ? $"{Convert.ToDouble(rs):F2}" : "—";
          var notes = row.TryGetValue("notes", out var n) ? n?.ToString() ?? "" : "";
          sb.AppendLine($"{station,-14} {left,-14} {right,-14} {notes,-20}");
        }
      }

      var reportText = sb.ToString();

      if (!string.IsNullOrWhiteSpace(outputPath))
      {
        outputPath = FileBoundary.WriteAllTextAtomic(
          outputPath, reportText, Encoding.UTF8, overwrite, ".txt");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["designSpeeds"] = designSpeeds,
        ["superelevationType"] = alignment.SuperelevationType.ToString(),
        ["rowCount"] = rawTable.Count,
        ["report"] = string.IsNullOrWhiteSpace(outputPath) ? reportText : null,
        ["outputPath"] = outputPath,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>Every critical station of every superelevation curve: station, types, the four lane slopes in percent
  /// (null where the road has no such segment) and the largest-magnitude slope per side.</summary>
  private static List<Dictionary<string, object?>> ReadSuperelevationTable(Alignment alignment)
  {
    var table = new List<Dictionary<string, object?>>();

    foreach (SuperelevationCurve curve in alignment.SuperelevationCurves)
    {
      foreach (SuperelevationCriticalStation station in curve.CriticalStations)
      {
        var leftOut = TryGetSlopePercent(station, SuperelevationCrossSegmentType.LeftOutLaneCrossSlope);
        var leftIn = TryGetSlopePercent(station, SuperelevationCrossSegmentType.LeftInLaneCrossSlope);
        var rightOut = TryGetSlopePercent(station, SuperelevationCrossSegmentType.RightOutLaneCrossSlope);
        var rightIn = TryGetSlopePercent(station, SuperelevationCrossSegmentType.RightInLaneCrossSlope);
        table.Add(new Dictionary<string, object?>
        {
          ["curveName"] = curve.Name,
          ["station"] = station.Station,
          ["stationType"] = station.StationType.ToString(),
          ["transitionRegionType"] = station.TransitionRegionType.ToString(),
          ["leftOutLaneSlope"] = leftOut,
          ["leftInLaneSlope"] = leftIn,
          ["rightOutLaneSlope"] = rightOut,
          ["rightInLaneSlope"] = rightIn,
          ["leftSlope"] = MaximumMagnitude(leftOut, leftIn),
          ["rightSlope"] = MaximumMagnitude(rightOut, rightIn),
        });
      }
    }

    return table;
  }

  /// <summary>A segment's slope in percent, or null when the station has no such segment (the API throws then).</summary>
  private static double? TryGetSlopePercent(
    SuperelevationCriticalStation station,
    SuperelevationCrossSegmentType segmentType)
  {
    try
    {
      return station.GetSlope(segmentType) * 100.0;
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }

  /// <summary>The value with the largest absolute size, sign kept; null when all are null.</summary>
  private static double? MaximumMagnitude(params double?[] values)
  {
    return values
      .Where(value => value.HasValue)
      .OrderByDescending(value => Math.Abs(value!.Value))
      .FirstOrDefault();
  }
}
