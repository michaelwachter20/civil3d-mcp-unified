using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3DMcpPlugin;

/// <summary>
/// Gravity-pipe arithmetic shared by the pipe network and hydraulics commands, in the drawing's own units (no Autodesk
/// types, so the file-boundary harness tests it without Civil 3D).
/// <para>
/// Units follow the drawing (<c>CivilObjectUtils.LinearUnits</c>): in feet, Manning's equation takes k = 1.486 and
/// gives cubic feet per second, and gravity is 32.174 ft/s²; in meters, k = 1, cubic meters per second and
/// 9.80665 m/s². A drawing in other units is treated as meters, and the answers say which units were used.
/// </para>
/// </summary>
public static class PipeMath
{
  /// <summary>
  /// A pipe's slope in percent from its two ends: the fall from start to end over the horizontal distance between
  /// them (positive when it falls toward the end). Worked out from the geometry because the value Civil 3D's
  /// <c>Pipe.Slope</c> returns isn't documented as a percent or a ratio. Null for a vertical or zero-length pipe.
  /// </summary>
  public static double? SlopePercent(double startX, double startY, double startZ, double endX, double endY, double endZ)
  {
    var horizontal = Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
    return horizontal < 1e-9 ? null : (startZ - endZ) / horizontal * 100.0;
  }

  /// <summary>Manning's unit factor: 1.486 in feet, 1 in meters.</summary>
  public static double ManningsK(string linearUnits) => linearUnits == "feet" ? 1.486 : 1.0;

  /// <summary>Gravity in the drawing's units per second squared.</summary>
  public static double Gravity(string linearUnits) => linearUnits == "feet" ? 32.174 : 9.80665;

  /// <summary>The flow unit the answers name: "cfs" in feet, "cms" (cubic meters per second) otherwise.</summary>
  public static string FlowUnit(string linearUnits) => linearUnits == "feet" ? "cfs" : "cms";

  /// <summary>The velocity unit the answers name: "ft/s" in feet, "m/s" otherwise.</summary>
  public static string VelocityUnit(string linearUnits) => linearUnits == "feet" ? "ft/s" : "m/s";

  /// <summary>
  /// The shortest distance between two 3D segments (pipe centerlines) and the parameters (0 at the start, 1 at the
  /// end) of the closest points on each. Standard closest-points-of-two-segments method; a zero-length segment is
  /// treated as a point.
  /// </summary>
  public static (double Distance, double OnFirst, double OnSecond) SegmentDistance(
    (double X, double Y, double Z) p1, (double X, double Y, double Z) q1,
    (double X, double Y, double Z) p2, (double X, double Y, double Z) q2)
  {
    double[] d1 = { q1.X - p1.X, q1.Y - p1.Y, q1.Z - p1.Z };
    double[] d2 = { q2.X - p2.X, q2.Y - p2.Y, q2.Z - p2.Z };
    double[] r = { p1.X - p2.X, p1.Y - p2.Y, p1.Z - p2.Z };
    double Dot(double[] u, double[] v) => u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
    double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
    double s, t;
    const double tiny = 1e-12;
    if (a <= tiny && e <= tiny)
    {
      s = t = 0;
    }
    else if (a <= tiny)
    {
      s = 0;
      t = Math.Clamp(f / e, 0, 1);
    }
    else
    {
      double c = Dot(d1, r);
      if (e <= tiny)
      {
        t = 0;
        s = Math.Clamp(-c / a, 0, 1);
      }
      else
      {
        double b = Dot(d1, d2);
        double denom = a * e - b * b;
        s = denom > tiny ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
        t = (b * s + f) / e;
        if (t < 0)
        {
          t = 0;
          s = Math.Clamp(-c / a, 0, 1);
        }
        else if (t > 1)
        {
          t = 1;
          s = Math.Clamp((b - c) / a, 0, 1);
        }
      }
    }

    double dx = r[0] + d1[0] * s - d2[0] * t, dy = r[1] + d1[1] * s - d2[1] * t, dz = r[2] + d1[2] * s - d2[2] * t;
    return (Math.Sqrt(dx * dx + dy * dy + dz * dz), s, t);
  }

  /// <summary>
  /// Where to sample a pipe against a surface: parameters from 0 to 1 at most <paramref name="spacing"/> drawing
  /// units apart along its horizontal length, both ends included (at least the two ends, at most 1,000 samples).
  /// </summary>
  public static IReadOnlyList<double> SampleParameters(double horizontalLength, double spacing)
  {
    var count = spacing > 0 ? (int)Math.Ceiling(horizontalLength / spacing) : 1;
    count = Math.Clamp(count, 1, 999);
    return Enumerable.Range(0, count + 1).Select(i => (double)i / count).ToList();
  }

  /// <summary>
  /// Full-flow capacity of a circular pipe, Q = (k/n) A R^(2/3) S^(1/2) with R = D/4, for a diameter in drawing units
  /// and a slope in percent (its size only: a pipe sloping back still carries flow). A flat pipe counts as a tiny
  /// slope, not zero.
  /// </summary>
  public static double FullFlowCapacity(double diameter, double slopePercent, double manningsN, string linearUnits)
  {
    var slope = Math.Max(Math.Abs(slopePercent) / 100.0, 1e-6);
    var area = Math.PI * diameter * diameter / 4.0;
    var hydraulicRadius = diameter / 4.0;
    return ManningsK(linearUnits) / manningsN * area * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Sqrt(slope);
  }
}
