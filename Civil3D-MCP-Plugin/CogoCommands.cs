// Coordinate-geometry math: inverse, direction-distance, traverse with closure, horizontal curve solve.
// JSON-RPC methods (CommandDispatcher): cogoInverse, cogoDirectionDistance, cogoTraverse, cogoCurveSolve.
// Called by src/tools/domains/geometryDomain.ts (civil3d_geometry cogo actions).
// Pure math on the request's numbers: no CivilExecution, no drawing read or write, no undo step, no files. Bearings
// are decimal degrees clockwise from north (+Y); slopes are percent.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Civil 3D COGO (Coordinate Geometry) calculations: geometry math that does not require an active
/// drawing (inverse, direction-distance, traverse, curve solve). Survey databases are in SurveyCommands.
/// </summary>
public static class CogoCommands
{
  // -------------------------------------------------------------------------
  // cogoInverse  -- bearing + distance between two points
  // -------------------------------------------------------------------------

  /// <summary>
  /// cogoInverse: distance, azimuth (decimal and DMS text) and deltas from (<c>x1</c>, <c>y1</c>) to (<c>x2</c>,
  /// <c>y2</c>), all required. Doesn't touch the drawing. CIVIL3D.INVALID_INPUT for a missing coordinate.
  /// </summary>
  public static Task<object?> CogoInverseAsync(JsonObject? parameters)
  {
    var x1 = PluginRuntime.GetRequiredDouble(parameters, "x1");
    var y1 = PluginRuntime.GetRequiredDouble(parameters, "y1");
    var x2 = PluginRuntime.GetRequiredDouble(parameters, "x2");
    var y2 = PluginRuntime.GetRequiredDouble(parameters, "y2");

    return Task.FromResult<object?>(ComputeInverse(x1, y1, x2, y2));
  }

  // -------------------------------------------------------------------------
  // cogoDirectionDistance  -- project a point given bearing and distance
  // -------------------------------------------------------------------------

  /// <summary>
  /// cogoDirectionDistance: the point <c>distance</c> along azimuth <c>bearingDegrees</c> from (<c>fromX</c>,
  /// <c>fromY</c>) (all required), with optional <c>fromZ</c> and percent <c>slope</c> for the z change. Doesn't touch
  /// the drawing. CIVIL3D.INVALID_INPUT for a missing value.
  /// </summary>
  public static Task<object?> CogoDirectionDistanceAsync(JsonObject? parameters)
  {
    var fromX = PluginRuntime.GetRequiredDouble(parameters, "fromX");
    var fromY = PluginRuntime.GetRequiredDouble(parameters, "fromY");
    var bearingDegrees = PluginRuntime.GetRequiredDouble(parameters, "bearingDegrees");
    var distance = PluginRuntime.GetRequiredDouble(parameters, "distance");
    var fromZ = PluginRuntime.GetOptionalDouble(parameters, "fromZ") ?? 0;
    var slope = PluginRuntime.GetOptionalDouble(parameters, "slope"); // optional percent slope

    var bearingRadians = BearingToRadians(bearingDegrees);
    var dx = Math.Sin(bearingRadians) * distance;
    var dy = Math.Cos(bearingRadians) * distance;
    var dz = slope.HasValue ? distance * (slope.Value / 100.0) : 0;

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["fromX"] = fromX,
      ["fromY"] = fromY,
      ["fromZ"] = fromZ,
      ["toX"] = fromX + dx,
      ["toY"] = fromY + dy,
      ["toZ"] = fromZ + dz,
      ["bearingDegrees"] = bearingDegrees,
      ["distance"] = distance,
    });
  }

  // -------------------------------------------------------------------------
  // cogoTraverse  -- solve a series of bearing/distance courses
  // -------------------------------------------------------------------------

  /// <summary>
  /// cogoTraverse: runs <c>courses</c> (required, objects with <c>bearingDegrees</c>, <c>distance</c>, optional
  /// <c>slope</c> and <c>description</c>) from (<c>startX</c>, <c>startY</c>, optional <c>startZ</c>). Returns every
  /// point with its running station, the end point and total length; with <c>isClosed</c> true also the closure
  /// error, its azimuth and precision (1:n, null when perfect). Doesn't touch the drawing. CIVIL3D.INVALID_INPUT for
  /// no courses or a malformed course.
  /// </summary>
  public static Task<object?> CogoTraverseAsync(JsonObject? parameters)
  {
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startZ = PluginRuntime.GetOptionalDouble(parameters, "startZ") ?? 0;
    var coursesNode = PluginRuntime.GetParameter(parameters, "courses") as JsonArray;
    var isClosed = PluginRuntime.GetOptionalBool(parameters, "isClosed") ?? false;

    if (coursesNode == null || coursesNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "cogoTraverse requires at least one course.");
    }

    var points = new List<Dictionary<string, object?>>
    {
      new() { ["x"] = startX, ["y"] = startY, ["z"] = startZ, ["station"] = 0.0 },
    };

    var currentX = startX;
    var currentY = startY;
    var currentZ = startZ;
    var totalDist = 0.0;

    foreach (var courseNode in coursesNode)
    {
      if (courseNode is not JsonObject course)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each course must be a JSON object.");
      }

      var bearing = course["bearingDegrees"]?.GetValue<double>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Course missing bearingDegrees.");
      var dist = course["distance"]?.GetValue<double>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Course missing distance.");
      var slope = course["slope"]?.GetValue<double?>() ?? null;
      var description = course["description"]?.GetValue<string?>() ?? string.Empty;

      var bearingRad = BearingToRadians(bearing);
      currentX += Math.Sin(bearingRad) * dist;
      currentY += Math.Cos(bearingRad) * dist;
      if (slope.HasValue) currentZ += dist * (slope.Value / 100.0);
      totalDist += dist;

      points.Add(new Dictionary<string, object?>
      {
        ["x"] = currentX,
        ["y"] = currentY,
        ["z"] = currentZ,
        ["station"] = totalDist,
        ["description"] = description,
      });
    }

    // Closure error (for closed traverse)
    double? closureError = null;
    double? closureBearing = null;
    double? closurePrecision = null;

    if (isClosed)
    {
      var errorX = currentX - startX;
      var errorY = currentY - startY;
      closureError = Math.Sqrt(errorX * errorX + errorY * errorY);
      if (closureError > 0)
      {
        closureBearing = RadiansToBearing(Math.Atan2(errorX, errorY));
      }
      closurePrecision = totalDist > 0 && closureError > 0
        ? totalDist / closureError.Value
        : double.PositiveInfinity;
    }

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["startPoint"] = new Dictionary<string, double> { ["x"] = startX, ["y"] = startY, ["z"] = startZ },
      ["endPoint"] = new Dictionary<string, double> { ["x"] = currentX, ["y"] = currentY, ["z"] = currentZ },
      ["totalLength"] = totalDist,
      ["courseCount"] = coursesNode.Count,
      ["points"] = points,
      ["isClosed"] = isClosed,
      ["closureError"] = closureError,
      ["closureBearingDegrees"] = closureBearing,
      ["closurePrecision"] = closurePrecision == double.PositiveInfinity ? null : (object?)closurePrecision,
    });
  }

  // -------------------------------------------------------------------------
  // cogoCurveSolve  -- solve horizontal curve from any two of: radius/delta/length/tangent/chord (CurveMath)
  // -------------------------------------------------------------------------

  /// <summary>
  /// cogoCurveSolve: a circular curve's radius, delta, length, tangent, chord, external, middle ordinate and degree
  /// of curve (arc definition) from optional <c>radius</c>, <c>deltaDegrees</c>, <c>length</c>, <c>tangent</c>,
  /// <c>chord</c>: exactly two of them, any pair (CurveMath.Solve: closed form, or bisection for length with tangent
  /// and length with chord). Not exactly two, a value that isn't positive, or a pair that can't make a curve is
  /// CIVIL3D.INVALID_INPUT saying why; a solve that doesn't converge is CIVIL3D.API_ERROR. Doesn't touch the drawing.
  /// </summary>
  public static Task<object?> CogoCurveSolveAsync(JsonObject? parameters)
  {
    var radius = PluginRuntime.GetOptionalDouble(parameters, "radius");
    var deltaDegreesInput = PluginRuntime.GetOptionalDouble(parameters, "deltaDegrees");
    var length = PluginRuntime.GetOptionalDouble(parameters, "length");
    var tangent = PluginRuntime.GetOptionalDouble(parameters, "tangent");
    var chord = PluginRuntime.GetOptionalDouble(parameters, "chord");

    var (R, delta) = CurveMath.Solve(radius, deltaDegreesInput, length, tangent, chord);

    var L = R * delta;
    var T = R * Math.Tan(delta / 2);
    var C = 2 * R * Math.Sin(delta / 2);
    var E = R * (1 / Math.Cos(delta / 2) - 1);
    var M = R * (1 - Math.Cos(delta / 2));

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["radius"] = R,
      ["deltaDegrees"] = delta * 180.0 / Math.PI,
      ["length"] = L,
      ["tangent"] = T,
      ["chord"] = C,
      ["externalDistance"] = E,
      ["middleOrdinate"] = M,
      ["degree"] = 5729.578 / R, // degree of curve (arc definition)
    });
  }

  // -------------------------------------------------------------------------
  // Pure math helpers (no drawing context needed)
  // -------------------------------------------------------------------------

  private static Dictionary<string, object?> ComputeInverse(double x1, double y1, double x2, double y2)
  {
    var dx = x2 - x1;
    var dy = y2 - y1;
    var distance = Math.Sqrt(dx * dx + dy * dy);
    var bearingDeg = distance > 0 ? RadiansToBearing(Math.Atan2(dx, dy)) : 0;

    return new Dictionary<string, object?>
    {
      ["fromX"] = x1,
      ["fromY"] = y1,
      ["toX"] = x2,
      ["toY"] = y2,
      ["distance"] = distance,
      ["bearingDegrees"] = bearingDeg,
      ["bearingDms"] = DecimalToDms(bearingDeg),
      ["deltaX"] = dx,
      ["deltaY"] = dy,
    };
  }

  private static double BearingToRadians(double bearingDegrees)
  {
    // Bearing is measured clockwise from North (Y axis)
    // Normalize to [0, 360)
    var normalized = ((bearingDegrees % 360) + 360) % 360;
    return normalized * Math.PI / 180.0;
  }

  private static double RadiansToBearing(double atan2Result)
  {
    // atan2(dx, dy) gives angle from North, but we want clockwise
    var deg = atan2Result * 180.0 / Math.PI;
    return ((deg % 360) + 360) % 360;
  }

  /// <summary>Decimal degrees as DDD°MM'SS.S" text (truncates, so 59.96" can show as 60.0").</summary>
  private static string DecimalToDms(double degrees)
  {
    var d = (int)degrees;
    var mFrac = (degrees - d) * 60;
    var m = (int)mFrac;
    var s = (mFrac - m) * 60;
    return $"{d:D3}°{m:D2}'{s:00.0}\"";
  }
}
