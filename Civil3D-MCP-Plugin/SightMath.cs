// SightMath.cs - pure AASHTO sight-distance formulas (stopping and passing) used by SightDistanceCommands
// (calculateSightDistance, checkStoppingDistance; civil3d_sight_distance, src/tools/domains/sightDistanceDomain.ts),
// and the drawing-unit conversions those and ProfileEditCommands (profileCheckKValues) use for K. No Civil 3D types and no drawing access, so tests/FileBoundaryHarness compiles this file and checks the numbers.
namespace Civil3DMcpPlugin;

/// <summary>
/// AASHTO sight distances. Stopping distance is reaction plus braking, at AASHTO's deceleration of 11.2 ft/s² (3.4 m/s²),
/// so friction is that rate over gravity, 0.348, at every speed. Design values are rounded up to the next 5 ft or 5 m,
/// which gives the published table: 1.47 x 40 x 2.5 + 40² / (30 x 0.348) = 300.3 ft at 40 mph, published as 305.
/// On a grade the value isn't rounded, since the published grade tables aren't either. A caller's own friction replaces
/// the 0.348. No Civil 3D types here, so the test harness can run this file.
/// </summary>
public static class SightMath
{
  /// <summary>AASHTO's design deceleration (11.2 ft/s²) over gravity, used when the caller gives no friction.</summary>
  public const double DesignFriction = 0.348;

  private static readonly double[] PassingSpeeds = [20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80];
  private static readonly double[] PassingFeet = [400, 450, 500, 550, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400];

  /// <summary>Stopping sight distance in feet. <paramref name="gradePercent"/> is negative downhill.</summary>
  public static double StoppingFeet(double mph, double gradePercent = 0, double reactionSeconds = 2.5, double? friction = null)
  {
    var distance = 1.47 * mph * reactionSeconds + mph * mph / (30.0 * ((friction ?? DesignFriction) + gradePercent / 100.0));
    return RoundedForDesign(distance, gradePercent, friction);
  }

  /// <summary>Stopping sight distance in metres. <paramref name="gradePercent"/> is negative downhill.</summary>
  public static double StoppingMetres(double kmh, double gradePercent = 0, double reactionSeconds = 2.5, double? friction = null)
  {
    var distance = 0.278 * kmh * reactionSeconds + kmh * kmh / (254.0 * ((friction ?? DesignFriction) + gradePercent / 100.0));
    return RoundedForDesign(distance, gradePercent, friction);
  }

  /// <summary>AASHTO's minimum passing sight distance in feet, between rows interpolated, outside them the nearest.</summary>
  public static double PassingDistanceFeet(double mph)
  {
    if (mph <= PassingSpeeds[0]) return PassingFeet[0];
    if (mph >= PassingSpeeds[^1]) return PassingFeet[^1];
    var upper = Array.FindIndex(PassingSpeeds, speed => speed >= mph);
    var ratio = (mph - PassingSpeeds[upper - 1]) / (PassingSpeeds[upper] - PassingSpeeds[upper - 1]);
    return PassingFeet[upper - 1] + ratio * (PassingFeet[upper] - PassingFeet[upper - 1]);
  }

  /// <summary>
  /// One metre in a drawing's length units, from the name of its unit: INSUNITS (<c>UnitsValue.ToString()</c>) or
  /// Civil 3D's drawing unit setting. 1 in Meters, 1000 in Millimeters, 3.28084 in Feet, 3.280833 in US survey feet.
  /// Null for Undefined or a name this doesn't know, so a caller can fall back or say so rather than assume feet.
  /// </summary>
  public static double? UnitsPerMetre(string? unitsName) => unitsName?.Trim().ToLowerInvariant() switch
  {
    "meters" or "metres" or "meter" or "metre" => 1.0,
    "millimeters" or "millimetres" or "millimeter" or "millimetre" => 1000.0,
    "centimeters" or "centimetres" or "centimeter" or "centimetre" => 100.0,
    "decimeters" or "decimetres" or "decimeter" or "decimetre" => 10.0,
    "kilometers" or "kilometres" or "kilometer" or "kilometre" => 0.001,
    "feet" or "foot" => 1.0 / 0.3048,
    "inches" or "inch" => 1.0 / 0.0254,
    "yards" or "yard" => 1.0 / 0.9144,
    "miles" or "mile" => 1.0 / 1609.344,
    "ussurveyfeet" or "ussurveyfoot" or "surveyfeet" or "surveyfoot" => 3937.0 / 1200.0,
    "ussurveyinch" or "ussurveyinches" => 39.37,
    "ussurveyyard" or "ussurveyyards" => 3937.0 / 3600.0,
    "ussurveymile" or "ussurveymiles" => 3937.0 / 1200.0 / 5280.0,
    _ => null,
  };

  /// <summary>
  /// Whether a drawing unit (same names as <see cref="UnitsPerMetre"/>) is metric, so speeds default to km/h and the
  /// metric K table applies: metres, but also millimetres, centimetres, decimetres and kilometres.
  /// </summary>
  public static bool IsMetricUnits(string? unitsName) => unitsName?.Trim().ToLowerInvariant() switch
  {
    "meters" or "metres" or "meter" or "metre" => true,
    "millimeters" or "millimetres" or "millimeter" or "millimetre" => true,
    "centimeters" or "centimetres" or "centimeter" or "centimetre" => true,
    "decimeters" or "decimetres" or "decimeter" or "decimetre" => true,
    "kilometers" or "kilometres" or "kilometer" or "kilometre" => true,
    _ => false,
  };

  /// <summary>
  /// A minimum K from an AASHTO table (metres per percent when <paramref name="metricTable"/>, else feet per percent)
  /// in the drawing's length units per percent, since a profile's K (curve length over grade change) is in drawing
  /// units: 13 m/% is 13000 in a millimetre drawing, 96 ft/% is 1152 in an inch one. A null
  /// <paramref name="unitsPerMetre"/> (units unknown) leaves the table value as it is.
  /// </summary>
  public static double KInDrawingUnits(double tableK, bool metricTable, double? unitsPerMetre) =>
    unitsPerMetre is { } perMetre ? tableK * (metricTable ? 1.0 : 0.3048) * perMetre : tableK;

  /// <summary>Rounds up to the next 5 only on level ground with the design friction (the published table's case).</summary>
  private static double RoundedForDesign(double distance, double gradePercent, double? friction) =>
    Math.Abs(gradePercent) < 1e-9 && friction == null ? Math.Ceiling(distance / 5.0) * 5.0 : distance;
}
