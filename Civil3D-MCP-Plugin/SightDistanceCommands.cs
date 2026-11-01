using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Sight distance commands: AASHTO stopping (SSD), passing (PSD) and decision (DSD) distances for a design speed
/// (SightMath), and a profile's vertical curves against the minimum K. A speed with no units follows the drawing: mph
/// in a foot drawing, km/h in a metre one.
/// </summary>
public static class SightDistanceCommands
{
  private const double KmhPerMph = 1.60934;
  private const double FeetPerMetre = 3.28084;

  // ─── calculateSightDistance ──────────────────────────────────────────────────

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
      var metricDrawing = database.Insunits == UnitsValue.Meters;
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
          var curve = profileId != ObjectId.Null ? CurveAtStation(transaction, profileId, checkStation.Value) : null;
          // A profile's K is in the drawing's units, whichever units the speed came in.
          var (sagLimit, crestLimit) = ProfileEditCommands.MinimumK(metricDrawing ? speedKmh : speedMph, metricDrawing);

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

  public static Task<object?> CheckStoppingDistanceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");
    var speedUnits = PluginRuntime.GetOptionalString(parameters, "speedUnits");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var stationInterval = PluginRuntime.GetOptionalDouble(parameters, "stationInterval") ?? 25.0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // K on a profile is in the drawing's units, so the limit is too. A speed with no units follows the drawing.
      var metricDrawing = database.Insunits == UnitsValue.Meters;
      var mph = SpeedIsMph(speedUnits, metricDrawing);
      var speedKmh = mph ? designSpeed * KmhPerMph : designSpeed;
      var ssdMetres = mph ? SightMath.StoppingFeet(designSpeed) / FeetPerMetre : SightMath.StoppingMetres(designSpeed);

      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignmentLength = CivilObjectUtils.GetPropertyValue<double?>(alignment, "Length") ?? 0.0;
      var startSta = stationStart ?? CivilObjectUtils.GetPropertyValue<double?>(alignment, "StartingStation") ?? 0.0;
      var endSta = stationEnd ?? (startSta + alignmentLength);

      var profileId = FindProfileId(alignment, transaction, profileName);
      var (ksagMin, kcrestMin) = ProfileEditCommands.MinimumK(metricDrawing ? speedKmh : speedKmh / KmhPerMph, metricDrawing);
      var violations = new List<Dictionary<string, object?>>();
      var checkedCount = 0;

      for (double sta = startSta; sta <= endSta; sta += stationInterval)
      {
        checkedCount++;
        if ((profileId != ObjectId.Null ? CurveAtStation(transaction, profileId, sta) : null) is not { } curve) continue;
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

  private static ObjectId FindProfileId(DBObject alignment, Transaction transaction, string profileName)
  {
    var profileIds = CivilObjectUtils.InvokeMethod(alignment, "GetProfileIds") as IEnumerable;
    if (profileIds == null) return ObjectId.Null;
    foreach (var item in profileIds)
    {
      if (item is ObjectId id && id != ObjectId.Null)
      {
        var profile = transaction.GetObject(id, OpenMode.ForRead, false, true);
        if (string.Equals(CivilObjectUtils.GetName(profile), profileName, StringComparison.OrdinalIgnoreCase))
          return id;
      }
    }
    return ObjectId.Null;
  }

  /// <summary>
  /// The K of the vertical curve at a station and whether it's a sag; null on a tangent or a flat curve. Civil 3D's
  /// classes are ProfileParabolaSymmetric, ProfileParabolaAsymmetric and ProfileCircular, none with "curve" in the name.
  /// </summary>
  private static (double K, bool Sag)? CurveAtStation(Transaction transaction, ObjectId profileId, double station)
  {
    try
    {
      var profile = transaction.GetObject(profileId, OpenMode.ForRead, false, true);
      var entities = CivilObjectUtils.GetPropertyValue<object>(profile, "Entities");
      if (entities is not IEnumerable enumerable) return null;

      foreach (var entity in enumerable)
      {
        if (entity == null) continue;
        var startSta = CivilObjectUtils.GetPropertyValue<double?>(entity, "StartStation") ?? 0;
        var endSta = CivilObjectUtils.GetPropertyValue<double?>(entity, "EndStation") ?? 0;
        if (station < startSta || station > endSta) continue;

        var entityType = entity.GetType().Name;
        if (entityType.IndexOf("parabola", StringComparison.OrdinalIgnoreCase) < 0
          && entityType.IndexOf("circular", StringComparison.OrdinalIgnoreCase) < 0
          && entityType.IndexOf("curve", StringComparison.OrdinalIgnoreCase) < 0) continue;

        var length = CivilObjectUtils.GetPropertyValue<double?>(entity, "Length") ?? 0;
        var gradeIn = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeIn")
                   ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "StartGrade") ?? 0;
        var gradeOut = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeOut")
                    ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "EndGrade") ?? 0;
        // Civil 3D gives grades as ratios (0.02 = 2%); K is per percent.
        var a = Math.Abs(gradeOut - gradeIn) * 100;
        if (a > 1e-10 && length > 0) return (length / a, gradeOut > gradeIn);
      }
      return null;
    }
    catch { return null; }
  }
}
