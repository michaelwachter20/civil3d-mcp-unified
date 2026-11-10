// SightDistanceCommands.cs - read-only sight distance checks. Methods (CommandDispatcher.cs): calculateSightDistance,
// checkStoppingDistance. Caller: src/tools/domains/sightDistanceDomain.ts (civil3d_sight_distance). The formulas are in
// SightMath (pure, tested in tests/FileBoundaryHarness); minimum K values come from ProfileEditCommands.MinimumK.
// Both handlers run in CivilExecution.ReadAsync (main thread, no undo step) and never change the drawing. Alignments
// are found through CivilObjectUtils (late-bound members go through Civil3DCompatibility).
using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

namespace Civil3DMcpPlugin;

/// <summary>
/// Sight distance commands: AASHTO stopping (SSD), passing (PSD) and decision (DSD) distances for a design speed
/// (SightMath), and a profile's vertical curves against the minimum K. A speed with no units follows the drawing: mph
/// in a foot drawing, km/h in a metric one (metres, millimetres...; ProfileEditCommands.DrawingLengthUnits), and a
/// profile's K is compared with the minimum converted to the drawing's length unit (SightMath.KInDrawingUnits).
/// </summary>
public static class SightDistanceCommands
{
  private const double KmhPerMph = 1.60934;
  private const double FeetPerMetre = 3.28084;

  /// <summary>Most stations one checkStoppingDistance walks: a tiny interval would hold the host queue.</summary>
  private const int MaxStationsChecked = 100_000;

  /// <summary>One vertical curve: the stations it runs between, its K, and whether it's a sag.</summary>
  private readonly record struct VerticalCurve(double Start, double End, double K, bool Sag);

  // ─── calculateSightDistance ──────────────────────────────────────────────────

  /// <summary>
  /// <c>calculateSightDistance</c>: required sight distance for a design speed. Reads <c>designSpeed</c> (required),
  /// <c>speedUnits</c> ("mph", else km/h; default follows the drawing), <c>sightDistanceType</c> (stopping/ssd default,
  /// passing/psd, decision/dsd), <c>grade</c> (percent, negative downhill), <c>perceptionReactionTime</c> (default
  /// 2.5 s), <c>frictionCoefficient</c>, and optionally <c>alignmentName</c> + <c>profileName</c> + <c>checkStation</c>
  /// to check the K of the vertical curve at that station. Returns the distance in feet and metres and the minimum K
  /// values. Decision distance is a rough 3.5 x stopping, labelled so. Read-only; a failed station check is reported
  /// as <c>stationCheckError</c>, not thrown.
  /// </summary>
  public static Task<object?> CalculateSightDistanceAsync(JsonObject? parameters)
  {
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var speedUnits = PluginRuntime.GetOptionalString(parameters, "speedUnits");
    var sightType = PluginRuntime.GetOptionalString(parameters, "sightDistanceType") ?? "stopping";
    var grade = PluginRuntime.GetOptionalDouble(parameters, "grade") ?? 0.0;
    var prt = PluginRuntime.GetOptionalDouble(parameters, "perceptionReactionTime") ?? 2.5;
    var userFriction = PluginRuntime.GetOptionalDouble(parameters, "frictionCoefficient");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetOptionalString(parameters, "profileName");
    var checkStation = PluginRuntime.GetOptionalDouble(parameters, "checkStation");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var units = ProfileEditCommands.DrawingLengthUnits(database, civilDoc);
      var metricDrawing = units.Metric;
      var mph = SpeedIsMph(speedUnits, metricDrawing);
      var speedKmh = mph ? designSpeed * KmhPerMph : designSpeed;
      var speedMph = mph ? designSpeed : designSpeed / KmhPerMph;

      var stoppingFeet = mph
        ? SightMath.StoppingFeet(designSpeed, grade, prt, userFriction)
        : SightMath.StoppingMetres(designSpeed, grade, prt, userFriction) * FeetPerMetre;

      // K in the units asked in: feet at mph, metres at km/h, as the Green Book's tables give them.
      var (ksag, kcrest) = ProfileEditCommands.MinimumK(designSpeed, metric: !mph);

      double requiredFeet;
      string sightTypeLabel;
      string source;
      switch (sightType.ToLowerInvariant())
      {
        case "passing":
        case "psd":
          requiredFeet = SightMath.PassingDistanceFeet(speedMph);
          sightTypeLabel = "Passing Sight Distance (PSD)";
          source = "AASHTO Green Book passing sight distance table";
          break;
        case "decision":
        case "dsd":
          // A stand-in: AASHTO's table depends on the avoidance maneuver, which nothing here asks for.
          requiredFeet = stoppingFeet * 3.5;
          sightTypeLabel = "Decision Sight Distance (DSD)";
          source = "Rough rule: 3.5 x stopping sight distance, not AASHTO's table";
          break;
        default:
          requiredFeet = stoppingFeet;
          sightTypeLabel = "Stopping Sight Distance (SSD)";
          source = "AASHTO Green Book stopping sight distance";
          break;
      }

      var result = new Dictionary<string, object?>
      {
        ["designSpeedKmh"] = Math.Round(speedKmh, 1),
        ["designSpeedMph"] = Math.Round(speedMph, 1),
        ["sightDistanceType"] = sightTypeLabel,
        ["grade"] = grade,
        ["frictionCoefficient"] = userFriction ?? SightMath.DesignFriction,
        ["perceptionReactionTime"] = prt,
        ["requiredSightDistanceMetres"] = Math.Round(requiredFeet / FeetPerMetre, 1),
        ["requiredSightDistanceFeet"] = Math.Round(requiredFeet, 1),
        ["minimumKValueCrest"] = kcrest,
        ["minimumKValueSag"] = ksag,
        ["standard"] = source,
      };

      // If alignment + profile provided, check at the requested station
      if (!string.IsNullOrWhiteSpace(alignmentName) && !string.IsNullOrWhiteSpace(profileName) && checkStation.HasValue)
      {
        try
        {
          var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
          var profileId = FindProfileId(alignment, transaction, profileName!);
          var curve = CurveAtStation(ReadVerticalCurves(transaction, profileId), checkStation.Value);
          // A profile's K is in the drawing's units, whichever units the speed came in.
          var (sagTable, crestTable) = ProfileEditCommands.MinimumK(metricDrawing ? speedKmh : speedMph, metricDrawing);
          var sagLimit = SightMath.KInDrawingUnits(sagTable, metricDrawing, units.PerMetre);
          var crestLimit = SightMath.KInDrawingUnits(crestTable, metricDrawing, units.PerMetre);

          result["stationChecked"] = checkStation;
          result["kValueAtStation"] = curve is { } found ? Math.Round(found.K, 1) : (object?)"N/A";
          result["curveTypeAtStation"] = curve is { } atStation ? (atStation.Sag ? "sag" : "crest") : null;
          result["kValueCompliant"] = curve is { } c && c.K >= (c.Sag ? sagLimit : crestLimit);
        }
        catch
        {
          result["stationCheckError"] = "Could not access profile K-values at requested station.";
        }
      }

      return (object?)result;
    });
  }

  // ─── checkStoppingDistance ───────────────────────────────────────────────────

  /// <summary>
  /// <c>checkStoppingDistance</c>: walks a profile every <c>stationInterval</c> (default 25) from <c>stationStart</c>
  /// to <c>stationEnd</c> (default: the whole alignment) and lists stations on a vertical curve whose K is below the
  /// AASHTO minimum for <c>designSpeed</c> (FAIL under 75% of it, else WARN), the minimum in the drawing's length units
  /// (ProfileEditCommands.DrawingLengthUnits). Reads <c>alignmentName</c>, <c>profileName</c>, <c>designSpeed</c>
  /// (required) and <c>speedUnits</c>. Read-only. CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment;
  /// CIVIL3D.INVALID_INPUT for an unknown profile (naming the alignment's profiles), an interval or speed that isn't
  /// positive, or an interval giving more than <see cref="MaxStationsChecked"/> stations.
  /// </summary>
  public static Task<object?> CheckStoppingDistanceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var speedUnits = PluginRuntime.GetOptionalString(parameters, "speedUnits");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var stationInterval = PluginRuntime.GetOptionalDouble(parameters, "stationInterval") ?? 25.0;
    if (!(stationInterval > 0) || double.IsInfinity(stationInterval))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"stationInterval must be a positive distance in drawing units (e.g. 25), not {stationInterval}.");
    }

    if (!(designSpeed > 0) || double.IsInfinity(designSpeed))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"designSpeed must be a positive number, not {designSpeed}.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // K on a profile is in the drawing's units, so the limit is too. A speed with no units follows the drawing.
      var units = ProfileEditCommands.DrawingLengthUnits(database, civilDoc);
      var metricDrawing = units.Metric;
      var mph = SpeedIsMph(speedUnits, metricDrawing);
      var speedKmh = mph ? designSpeed * KmhPerMph : designSpeed;
      var ssdMetres = mph ? SightMath.StoppingFeet(designSpeed) / FeetPerMetre : SightMath.StoppingMetres(designSpeed);

      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignmentLength = CivilObjectUtils.GetPropertyValue<double?>(alignment, "Length") ?? 0.0;
      var startSta = stationStart ?? CivilObjectUtils.GetPropertyValue<double?>(alignment, "StartingStation") ?? 0.0;
      var endSta = stationEnd ?? (startSta + alignmentLength);
      if ((endSta - startSta) / stationInterval > MaxStationsChecked)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"stationInterval {stationInterval} gives more than {MaxStationsChecked} stations from {startSta} to {endSta}: use a larger interval or a shorter range.");
      }

      // A misspelt profile used to give no curves and so "compliant": an answer about nothing.
      var profileId = FindProfileId(alignment, transaction, profileName, out var available);
      if (profileId == ObjectId.Null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Alignment '{alignmentName}' has no profile '{profileName}'. Its profiles: "
          + (available.Count == 0 ? "none." : string.Join(", ", available.Select(name => $"'{name}'")) + "."));
      }

      var curves = ReadVerticalCurves(transaction, profileId);
      var (tableSag, tableCrest) = ProfileEditCommands.MinimumK(metricDrawing ? speedKmh : speedKmh / KmhPerMph, metricDrawing);
      var ksagMin = SightMath.KInDrawingUnits(tableSag, metricDrawing, units.PerMetre);
      var kcrestMin = SightMath.KInDrawingUnits(tableCrest, metricDrawing, units.PerMetre);
      var violations = new List<Dictionary<string, object?>>();
      var checkedCount = 0;

      // The interval is checked positive and the count capped above, so this ends.
      for (double sta = startSta; sta <= endSta; sta += stationInterval)
      {
        checkedCount++;
        if (CurveAtStation(curves, sta) is not { } curve) continue;
        var required = curve.Sag ? ksagMin : kcrestMin;
        if (curve.K < required)
        {
          violations.Add(new Dictionary<string, object?>
          {
            ["station"] = Math.Round(sta, 2),
            ["curveType"] = curve.Sag ? "sag" : "crest",
            ["kValue"] = Math.Round(curve.K, 1),
            ["kValueRequired"] = required,
            ["deficiency"] = Math.Round(required - curve.K, 1),
            ["severity"] = curve.K < required * 0.75 ? "FAIL" : "WARN",
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignmentName,
        ["profileName"] = profileName,
        ["designSpeed"] = designSpeed,
        ["designSpeedUnits"] = mph ? "mph" : "km/h",
        ["designSpeedKmh"] = Math.Round(speedKmh, 1),
        ["requiredSsdMetres"] = Math.Round(ssdMetres, 1),
        ["minimumKValueCrest"] = kcrestMin,
        ["minimumKValueSag"] = ksagMin,
        ["stationsChecked"] = checkedCount,
        ["violationCount"] = violations.Count,
        ["compliant"] = violations.Count == 0,
        ["violations"] = violations,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  /// <summary>The caller's units when given; otherwise the drawing's.</summary>
  private static bool SpeedIsMph(string? speedUnits, bool metricDrawing) =>
    string.IsNullOrWhiteSpace(speedUnits)
      ? !metricDrawing
      : string.Equals(speedUnits, "mph", StringComparison.OrdinalIgnoreCase);

  /// <summary>The alignment's profile with this name (case-insensitive), or ObjectId.Null.</summary>
  private static ObjectId FindProfileId(Autodesk.AutoCAD.DatabaseServices.DBObject alignment, Transaction transaction, string profileName) =>
    FindProfileId(alignment, transaction, profileName, out _);

  /// <summary>As above, also giving the names of the alignment's profiles, for an error that lists them.</summary>
  private static ObjectId FindProfileId(
    Autodesk.AutoCAD.DatabaseServices.DBObject alignment,
    Transaction transaction,
    string profileName,
    out List<string> available)
  {
    available = new List<string>();
    var profileIds = CivilObjectUtils.InvokeMethod(alignment, "GetProfileIds") as IEnumerable;
    if (profileIds == null) return ObjectId.Null;
    foreach (var item in profileIds)
    {
      if (item is ObjectId id && id != ObjectId.Null)
      {
        var profile = transaction.GetObject(id, OpenMode.ForRead, false, true);
        var name = CivilObjectUtils.GetName(profile);
        if (string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase))
          return id;
        if (!string.IsNullOrEmpty(name)) available.Add(name);
      }
    }
    return ObjectId.Null;
  }

  /// <summary>
  /// A profile's vertical curves, read once. Walking the entities for every station held Civil 3D for 20 minutes on a
  /// 6.5 mile ground profile, which has an entity for each surface triangle it crosses: 1,370 stations times 9,662
  /// entities at 0.09 ms each. Null grades are a tangent. A flat curve has no K, so it's left out.
  /// </summary>
  private static List<VerticalCurve> ReadVerticalCurves(Transaction transaction, ObjectId profileId)
  {
    var curves = new List<VerticalCurve>();
    if (profileId == ObjectId.Null) return curves;

    var profile = (CivilProfile)transaction.GetObject(profileId, OpenMode.ForRead, false, true);
    foreach (ProfileEntity entity in profile.Entities)
    {
      (double In, double Out)? grades = entity switch
      {
        ProfileParabolaSymmetric curve => (curve.GradeIn, curve.GradeOut),
        ProfileParabolaAsymmetric curve => (curve.GradeIn, curve.GradeOut),
        ProfileCircular curve => (curve.GradeIn, curve.GradeOut),
        _ => null,
      };
      if (grades is not { } grade) continue;

      // Civil 3D gives grades as ratios (0.02 = 2%); K is per percent.
      var a = Math.Abs(grade.Out - grade.In) * 100;
      if (a > 1e-10 && entity.Length > 0)
      {
        curves.Add(new VerticalCurve(entity.StartStation, entity.EndStation, entity.Length / a, grade.Out > grade.In));
      }
    }

    return curves;
  }

  /// <summary>The curve a station is on, or null on a tangent.</summary>
  private static VerticalCurve? CurveAtStation(List<VerticalCurve> curves, double station)
  {
    foreach (var curve in curves)
    {
      if (station >= curve.Start && station <= curve.End) return curve;
    }

    return null;
  }
}
