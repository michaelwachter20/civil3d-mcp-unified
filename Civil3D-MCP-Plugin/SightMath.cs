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

  private static double RoundedForDesign(double distance, double gradePercent, double? friction) =>
    Math.Abs(gradePercent) < 1e-9 && friction == null ? Math.Ceiling(distance / 5.0) * 5.0 : distance;
}
