// StationMath.cs - pure station arithmetic and sample line naming shared by SectionCommands (createSampleLines,
// getSectionData) and ProfileCommands (elevations every interval), and the reading of a caller's vertex list for AcadCommands (createPolyline, create3dPolyline) and AlignmentCommands
// (createAlignment), plus CurveMath, the circular curve solve of CogoCommands (cogoCurveSolve), and
// CoordinateParameterMatch, the central meridian lookup of CoordinateSystemCommands. No Civil 3D types and no drawing access, so tests/FileBoundaryHarness compiles this file and tests
// it without Civil 3D.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Station arithmetic shared by the sampling commands (sample lines every interval, profile elevations every
/// interval). No Autodesk types, so the file-boundary harness tests it without Civil 3D.
/// </summary>
public static class StationMath
{
  /// <summary>
  /// Which of <paramref name="count"/> items to keep to show at most <paramref name="most"/>: evenly spread, the first
  /// and last always in. A real section can have over a thousand points on one surface.
  /// </summary>
  public static List<int> EvenIndices(int count, int most)
  {
    if (count <= most) return Enumerable.Range(0, count).ToList();
    if (most <= 1) return new List<int> { 0 };

    return Enumerable.Range(0, most).Select(index => (int)Math.Round(index * (count - 1) / (double)(most - 1))).Distinct().ToList();
  }

  /// <summary>
  /// Stations every <paramref name="interval"/> from <paramref name="start"/> to <paramref name="end"/>, the end
  /// included when it falls on the interval. Each is worked out as start + i × interval rather than by adding the
  /// interval up, because a running sum drifts: 0.1 added ten times is 0.9999999999999999, so a 0+00 to 1+00 run
  /// every 10 would lose its last station. A station within a millionth of an interval of the end counts as the end.
  /// Empty when the interval isn't a positive number or the end is before the start.
  /// </summary>
  public static List<double> IntervalStations(double start, double end, double interval)
  {
    var stations = new List<double>();
    if (!(interval > 0) || double.IsInfinity(interval) || end < start)
    {
      return stations;
    }

    var count = (long)Math.Floor((end - start) / interval + 1e-6);
    for (long i = 0; i <= count; i++)
    {
      stations.Add(Math.Min(start + i * interval, end));
    }

    return stations;
  }

  /// <summary>
  /// A caller's <c>points</c> array of <c>{x, y}</c> (or <c>{x, y, z}</c> with <paramref name="requireZ"/>) read up
  /// front, for the commands that draw through them (createPolyline, create3dPolyline, createAlignment), so every
  /// point becomes a vertex and the vertex index is the point's index. Anything else is CIVIL3D.INVALID_INPUT naming
  /// the point (0-based) so the AI can fix that one: fewer than two points, a point that isn't an object, or a
  /// coordinate that is missing or not a finite number. Earlier a point that wasn't an object was skipped while its
  /// index was still used as the vertex index, which Civil 3D refused.
  /// </summary>
  public static List<(double X, double Y, double Z)> ReadVertices(JsonArray? points, string method, bool requireZ = false)
  {
    if (points == null || points.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{method} requires 'points': an array of at least two {(requireZ ? "{x, y, z}" : "{x, y}")} objects.");
    }

    var vertices = new List<(double X, double Y, double Z)>(points.Count);
    for (var index = 0; index < points.Count; index++)
    {
      if (points[index] is not JsonObject point)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{method}: point {index} is not an object; send {(requireZ ? "{x, y, z}" : "{x, y}")} with numbers.");
      }

      var x = Coordinate(point, "x", index, method);
      var y = Coordinate(point, "y", index, method);
      var z = requireZ ? Coordinate(point, "z", index, method) : 0.0;
      vertices.Add((x, y, z));
    }

    return vertices;
  }

  /// <summary>One coordinate of point <paramref name="index"/>: a finite number, else CIVIL3D.INVALID_INPUT.</summary>
  private static double Coordinate(JsonObject point, string axis, int index, string method)
  {
    if (point[axis] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number))
    {
      return number;
    }

    var problem = point[axis] == null ? "is missing" : "is not a number";
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{method}: {axis} of point {index} {problem}.");
  }

  /// <summary>
  /// A sample line's name: "SL-" and the station with up to two decimals and a dot whatever the PC's regional
  /// settings ("SL-1250", "SL-1262.5"; not "SL-1262,5" on a comma-decimal PC).
  /// </summary>
  public static string SampleLineName(double station) =>
    "SL-" + station.ToString("0.##", CultureInfo.InvariantCulture);

  /// <summary>
  /// A sample line's name inside its group ("Elm SLG SL-1250"): Civil 3D wants names unique beyond one group, so a second
  /// group on an alignment always failed, and three decimals keep stations 100.001 and 100.004 apart.
  /// </summary>
  public static string SampleLineName(string group, double station) =>
    group + " SL-" + station.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Picking named projection parameters for CoordinateSystemCommands.getCoordinateSystemInfo, kept here (no Autodesk
/// types) so the harness tests it. Civil 3D names them in several styles: "Central Meridian", "Longitude of natural
/// origin", "Origin Longitude", "Latitude of natural origin"...
/// </summary>
public static class CoordinateParameterMatch
{
  /// <summary>
  /// The central meridian: a parameter named "central meridian", else a longitude of an origin or centre ("Longitude
  /// of natural origin", "Origin Longitude", "Longitude of false origin", "Longitude of projection centre"). Never a
  /// latitude: "Latitude of natural origin" also says "natural origin", which an earlier "natural origin" match took.
  /// Null when there is none. Names are compared ignoring case, with underscores read as spaces.
  /// </summary>
  public static double? CentralMeridian(IEnumerable<KeyValuePair<string, double>> parameters)
  {
    var list = parameters.Select(pair => (Name: pair.Key.Replace("_", " ").ToLowerInvariant(), pair.Value)).ToList();
    foreach (var (name, value) in list)
    {
      if (name.Contains("central meridian")) return value;
    }

    foreach (var (name, value) in list)
    {
      if (name.Contains("longitude") && !name.Contains("latitude")
        && (name.Contains("origin") || name.Contains("center") || name.Contains("centre")))
      {
        return value;
      }
    }

    return null;
  }
}

/// <summary>
/// Circular curve solving for CogoCommands.cogoCurveSolve: the radius and central angle from any two of radius, delta,
/// arc length, tangent and chord (all ten pairs). Kept here, without Autodesk types, so the harness tests it.
/// </summary>
public static class CurveMath
{
  private const string Method = "cogoCurveSolve";

  /// <summary>
  /// Radius and central angle (radians) from exactly two of <paramref name="radius"/>, <paramref name="deltaDegrees"/>,
  /// <paramref name="length"/> (arc), <paramref name="tangent"/> and <paramref name="chord"/>, each positive. Closed form
  /// for every pair except length with tangent and length with chord, which are solved by bisection on a function that
  /// rises or falls steadily over the whole range, so there is exactly one answer when there is one at all. Pairs that
  /// can't make a curve are CIVIL3D.INVALID_INPUT saying why (a chord longer than the diameter, a tangent no more than
  /// half the length, a chord no shorter than the length or no shorter than twice the tangent, a delta of 360 or more, or
  /// 180 or more with a tangent); a bisection that doesn't settle is CIVIL3D.API_ERROR rather than a guess. Radius with
  /// tangent or chord, and tangent with chord, give the curve under 180 degrees.
  /// </summary>
  public static (double Radius, double Delta) Solve(double? radius, double? deltaDegrees, double? length, double? tangent, double? chord)
  {
    var given = new (string Name, double? Value)[]
    {
      ("radius", radius), ("deltaDegrees", deltaDegrees), ("length", length), ("tangent", tangent), ("chord", chord),
    };
    var count = given.Count(item => item.Value.HasValue);
    if (count != 2)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"{Method} needs exactly two of: radius, deltaDegrees, length, tangent, chord (any two), not {count}.");
    }

    foreach (var (name, value) in given)
    {
      if (value is { } number && (!(number > 0) || !double.IsFinite(number)))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: {name} must be a positive number, not {number}.");
      }
    }

    if (deltaDegrees >= 360)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: deltaDegrees must be under 360, not {deltaDegrees}.");
    }

    var delta = deltaDegrees * Math.PI / 180.0;
    if (radius is { } r)
    {
      if (delta is { } d) return (r, d);
      if (length is { } l)
      {
        if (l / r >= 2 * Math.PI)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: a length of {l} is a full circle or more at radius {r}.");
        }

        return (r, l / r);
      }

      if (tangent is { } t) return (r, 2 * Math.Atan(t / r));
      var c = chord!.Value;
      if (c > 2 * r)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: a chord of {c} is longer than the diameter ({2 * r}) at radius {r}.");
      }

      return (r, 2 * Math.Asin(c / (2 * r)));
    }

    if (delta is { } angle)
    {
      if (length is { } l) return (l / angle, angle);
      if (tangent is { } t)
      {
        if (deltaDegrees >= 180)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: a tangent only defines a curve under 180 degrees, not {deltaDegrees}.");
        }

        return (t / Math.Tan(angle / 2), angle);
      }

      return (chord!.Value / (2 * Math.Sin(angle / 2)), angle);
    }

    if (length is { } arc)
    {
      if (tangent is { } t)
      {
        // T / L = tan(d/2) / d rises from 1/2 (a flat curve) to infinity at 180 degrees.
        var ratio = t / arc;
        if (ratio <= 0.5)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: the tangent ({t}) must be more than half the length ({arc}) for a curve.");
        }

        var d = Bisect(x => Math.Tan(x / 2) / x - ratio, 0, Math.PI, rising: true, "length and tangent");
        return (arc / d, d);
      }

      // C / L = sin(d/2) / (d/2) falls from 1 (a flat curve) to 0 at 360 degrees.
      var c = chord!.Value;
      if (c >= arc)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: the chord ({c}) must be shorter than the arc length ({arc}).");
      }

      var dc = Bisect(x => Math.Sin(x / 2) / (x / 2) - c / arc, 0, 2 * Math.PI, rising: false, "length and chord");
      return (arc / dc, dc);
    }

    {
      // Tangent and chord: C = 2R sin(d/2) and T = R tan(d/2), so C / T = 2 cos(d/2).
      var t = tangent!.Value;
      var c = chord!.Value;
      if (c >= 2 * t)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{Method}: the chord ({c}) must be shorter than twice the tangent ({2 * t}).");
      }

      var d = 2 * Math.Acos(c / (2 * t));
      return (t / Math.Tan(d / 2), d);
    }
  }

  /// <summary>
  /// The root of a function that rises (or falls) steadily over the open range (<paramref name="low"/>,
  /// <paramref name="high"/>), by halving the range until it is as narrow as a double allows (at most 200 times).
  /// CIVIL3D.API_ERROR when the final range doesn't hold a sign change (the root isn't inside the range, or the
  /// function gave no number), so a curve that couldn't be solved is reported instead of answered with the last
  /// estimate.
  /// </summary>
  internal static double Bisect(Func<double, double> function, double low, double high, bool rising, string pair)
  {
    for (var i = 0; i < 200 && high - low > 1e-15; i++)
    {
      var middle = (low + high) / 2;
      if (middle <= low || middle >= high) break;
      if (function(middle) > 0 == rising) high = middle;
      else low = middle;
    }

    var atLow = function(low);
    var atHigh = function(high);
    var bracketed = double.IsFinite(atLow) && double.IsFinite(atHigh)
      && (rising ? atLow <= 0 && atHigh >= 0 : atLow >= 0 && atHigh <= 0);
    if (!bracketed)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"{Method}: the curve from {pair} did not converge (no solution found between {low} and {high} radians); check the two values.");
    }

    return (low + high) / 2;
  }
}
