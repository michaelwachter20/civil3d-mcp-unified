// Pure gravity-pipe arithmetic: slope from the pipe's ends, Manning full-flow capacity, part-full depth, velocity and
// Froude number, closest distance between two pipe centerlines, sample spacing along a pipe, unit names, and the
// hydraulic grade line walked over the network's topology (WalkHgl).
// Used by PipeNetworkCommands (checkPipeNetworkInterference, pipe data's slopePercent) and PipeHydraulicsCommands
// (HGL, hydraulic analysis), which serve civil3d_pipe (src/tools/domains/pipeDomain.ts); the TS size_network action
// has its own Manning's code. Serves no JSON-RPC method itself.
// No Autodesk types, no drawing or file access: tests/FileBoundaryHarness compiles and tests it without Civil 3D.
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

  /// <summary>
  /// A round pipe running part full: the depth ratio y/D at which Manning's equation carries <paramref name="flowRatio"/>
  /// of the full-pipe flow, with the area and top width there as fractions of the full area and of D. A pipe carrying
  /// little flow is shallow and faster than Q over the full area: at a tenth of capacity it runs at about 0.6 of full
  /// velocity, not a tenth. At or above capacity it counts as full.
  /// </summary>
  public static (double DepthRatio, double AreaRatio, double TopWidthRatio) PartFull(double flowRatio)
  {
    if (flowRatio >= 1)
    {
      return (1, 1, 0);
    }

    if (flowRatio <= 0)
    {
      return (0, 0, 0);
    }

    // Flow rises with depth up to about 0.94 of D, so the first root below that is the normal depth.
    double low = 0, high = 0.938;
    for (var step = 0; step < 60; step++)
    {
      var middle = (low + high) / 2;
      if (FlowRatioAtDepth(middle) < flowRatio) low = middle; else high = middle;
    }

    var depth = (low + high) / 2;
    var angle = 2 * Math.Acos(1 - 2 * depth);
    return (depth, (angle - Math.Sin(angle)) / (2 * Math.PI), Math.Sin(angle / 2));
  }

  /// <summary>Q/Qfull of a round pipe flowing at depth ratio y/D (Manning, same n): area ratio times hydraulic radius
  /// ratio to the 2/3.</summary>
  private static double FlowRatioAtDepth(double depthRatio)
  {
    var angle = 2 * Math.Acos(1 - 2 * depthRatio);
    var areaRatio = (angle - Math.Sin(angle)) / (2 * Math.PI);
    var radiusRatio = 1 - Math.Sin(angle) / angle;
    return areaRatio * Math.Pow(radiusRatio, 2.0 / 3.0);
  }

  /// <summary>Velocity and Froude number at a design flow, from the part-full depth (hydraulic depth = area / top width).</summary>
  public static (double Velocity, double Froude, double DepthRatio) PartFullFlow(double flow, double fullFlow, double diameter, string linearUnits)
  {
    var fullArea = Math.PI * diameter * diameter / 4;
    if (fullArea <= 0 || fullFlow <= 0)
    {
      return (0, 0, 0);
    }

    var (depthRatio, areaRatio, topWidthRatio) = PartFull(flow / fullFlow);
    if (areaRatio <= 0)
    {
      return (0, 0, 0);
    }

    var velocity = flow / (fullArea * areaRatio);
    // Full, a closed pipe has no free surface, so it has no Froude number to speak of.
    var froude = topWidthRatio > 0 ? velocity / Math.Sqrt(Gravity(linearUnits) * fullArea * areaRatio / (diameter * topWidthRatio)) : 0;
    return (velocity, froude, depthRatio);
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

  /// <summary>One pipe of a network for <see cref="WalkHgl"/>: the node (structure, or a free pipe end) water leaves
  /// it at downstream and enters it at upstream, its friction loss, and its invert at the downstream end.</summary>
  public sealed record HglPipe(string Name, string UpstreamNode, string DownstreamNode, double HeadLoss, double DownstreamInvert);

  /// <summary>A pipe's place on the hydraulic grade line: HGL at its downstream and upstream ends, the outfall node its
  /// controlling path drains to, and its order (the step at which the walk reached its downstream node: outfalls
  /// first, rising upstream).</summary>
  public sealed record HglNode(string Name, double HglDownstream, double HglUpstream, string Outfall, int Order);

  /// <summary>
  /// The hydraulic grade line of a gravity network by its topology, not as one chain: each outfall (a node no pipe
  /// leaves) starts at <paramref name="tailwater"/>, or without one at the lowest downstream invert of the pipes that
  /// reach it; every pipe adds its own loss to the HGL at its downstream node, and a node takes the highest HGL of the
  /// pipes leaving it (the controlling path when a network splits). So a branch only carries the losses of the pipes
  /// between it and its outfall, never those of an unrelated branch. A loop is broken at the node where it is first
  /// reached; a loop that reaches no outfall starts at its lowest invert. Returns one entry per pipe, outfalls first.
  /// </summary>
  public static IReadOnlyList<HglNode> WalkHgl(IReadOnlyList<HglPipe> pipes, double? tailwater)
  {
    var leaving = pipes.GroupBy(pipe => pipe.UpstreamNode).ToDictionary(group => group.Key, group => group.Count());
    var entering = pipes.GroupBy(pipe => pipe.DownstreamNode).ToDictionary(group => group.Key, group => group.ToList());
    var nodes = pipes.SelectMany(pipe => new[] { pipe.UpstreamNode, pipe.DownstreamNode }).Distinct().ToList();

    var hgl = new Dictionary<string, double>();          // final HGL of a node
    var outfallOf = new Dictionary<string, string>();
    var candidate = new Dictionary<string, (double Hgl, string Outfall)>();
    var remaining = nodes.ToDictionary(node => node, node => leaving.GetValueOrDefault(node));
    var order = new List<string>();
    var queue = new Queue<string>();

    double LowestInvertAt(string node) =>
      entering.TryGetValue(node, out var into) ? into.Min(pipe => pipe.DownstreamInvert) : 0;

    void Finalize(string node, double value, string outfall)
    {
      hgl[node] = value;
      outfallOf[node] = outfall;
      order.Add(node);
      queue.Enqueue(node);
    }

    foreach (var node in nodes.Where(node => remaining[node] == 0))
    {
      Finalize(node, tailwater ?? LowestInvertAt(node), node);
    }

    while (hgl.Count < nodes.Count || queue.Count > 0)
    {
      while (queue.Count > 0)
      {
        var downstream = queue.Dequeue();
        if (!entering.TryGetValue(downstream, out var into)) continue;
        foreach (var pipe in into)
        {
          var upstream = pipe.UpstreamNode;
          if (hgl.ContainsKey(upstream)) continue;
          var value = hgl[downstream] + pipe.HeadLoss;
          if (!candidate.TryGetValue(upstream, out var best) || value > best.Hgl)
          {
            candidate[upstream] = (value, outfallOf[downstream]);
          }

          if (--remaining[upstream] == 0)
          {
            Finalize(upstream, candidate[upstream].Hgl, candidate[upstream].Outfall);
          }
        }
      }

      if (hgl.Count == nodes.Count) break;

      // What is left is in a loop: settle the node the walk already reached with the highest HGL, else (a loop with no
      // outfall at all) the lowest node of it.
      var open = nodes.Where(node => !hgl.ContainsKey(node)).ToList();
      var reached = open.Where(candidate.ContainsKey).OrderByDescending(node => candidate[node].Hgl).FirstOrDefault();
      if (reached != null)
      {
        Finalize(reached, candidate[reached].Hgl, candidate[reached].Outfall);
      }
      else
      {
        var lowest = open.OrderBy(LowestInvertAt).First();
        Finalize(lowest, LowestInvertAt(lowest), lowest);
      }
    }

    var rank = order.Select((node, index) => (node, index)).ToDictionary(item => item.node, item => item.index);
    return pipes
      .Select(pipe => new HglNode(pipe.Name, hgl[pipe.DownstreamNode], hgl[pipe.DownstreamNode] + pipe.HeadLoss,
        outfallOf[pipe.DownstreamNode], rank[pipe.DownstreamNode]))
      .OrderBy(node => node.Order)
      .ThenBy(node => node.Name, StringComparer.Ordinal)
      .ToList();
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
