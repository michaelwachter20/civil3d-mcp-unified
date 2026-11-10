// Detention basin sizing (pure math) and a stage-storage table measured from a TIN surface.
// JSON-RPC methods (CommandDispatcher): calculateDetentionBasinSize, calculateDetentionStageStorage. Called by
// src/tools/domains/detentionDomain.ts (civil3d_detention), and by HydrologyCommands' runoffDetentionWorkflow.
// calculateDetentionBasinSize never touches the drawing. calculateDetentionStageStorage runs in
// CivilExecution.MeasureAsync: it extracts contours into the drawing to measure them, inside a transaction that is never
// committed, so nothing is left behind (one blank undo step from the lock). No file access, no reflection.
using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Detention basin sizing and stage-storage commands.
/// Implements Modified Rational Method storage estimation and
/// stage-storage table generation from Civil 3D surfaces.
/// </summary>
public static class DetentionCommands
{
  // ─── calculateDetentionBasinSize ─────────────────────────────────────────────

  /// <summary>
  /// calculateDetentionBasinSize: required storage for peak <c>inflow</c> and allowed <c>outflow</c> (cfs, required)
  /// over <c>stormDuration</c> minutes (default 60) by <c>method</c> modified_rational (default) or
  /// triangular_hydrograph (half of it), a trapezoidal basin with <c>bottomWidth</c> (default 10), length twice that,
  /// <c>sideSlope</c> z:1 (default 3) and <c>freeboardDepth</c> (default 1), and an orifice for the outflow at
  /// full-depth head. Units are US customary (ft, cfs) whatever the drawing. Outflow >= inflow answers an
  /// <c>error</c> field instead of throwing. Doesn't touch the drawing.
  /// </summary>
  public static Task<object?> CalculateDetentionBasinSizeAsync(JsonObject? parameters)
  {
    var inflow = PluginRuntime.GetRequiredDouble(parameters, "inflow");
    var outflow = PluginRuntime.GetRequiredDouble(parameters, "outflow");
    var stormDuration = PluginRuntime.GetOptionalDouble(parameters, "stormDuration") ?? 60.0;
    var method = PluginRuntime.GetOptionalString(parameters, "method") ?? "modified_rational";
    var sideSlope = PluginRuntime.GetOptionalDouble(parameters, "sideSlope") ?? 3.0;
    var bottomWidth = PluginRuntime.GetOptionalDouble(parameters, "bottomWidth") ?? 10.0;
    var freeboardDepth = PluginRuntime.GetOptionalDouble(parameters, "freeboardDepth") ?? 1.0;

    if (outflow >= inflow)
    {
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["error"] = "Outflow must be less than inflow for detention to be required.",
        ["inflow"] = inflow,
        ["outflow"] = outflow,
        ["storageRequired"] = false,
      });
    }

    // Modified Rational Method: Vs = (Qi - Qo) * tc * 60  [cubic feet]
    double storageVolumeCf;
    string methodLabel;

    switch (method.ToLowerInvariant())
    {
      case "triangular_hydrograph":
        // Approximate: Vs ≈ 0.5 * (Qi - Qo) * tc * 60
        storageVolumeCf = 0.5 * (inflow - outflow) * stormDuration * 60.0;
        methodLabel = "Triangular Hydrograph Approximation";
        break;
      default: // modified_rational
        storageVolumeCf = (inflow - outflow) * stormDuration * 60.0;
        methodLabel = "Modified Rational Method";
        break;
    }

    var storageVolumeAcFt = storageVolumeCf / 43560.0;
    var storageVolumeCuM = storageVolumeCf / 35.3147;

    // Basin geometry — trapezoidal cross-section, assuming L = 2 * W for plan view
    // V = d * (L*W + (L + 2*H*z) * (W + 2*H*z) + 4 * (L + H*z) * (W + H*z)) / 6  (frustum formula)
    // Simplified: iterate depth to find where volume matches
    double basinDepth = EstimateBasinDepth(storageVolumeCf, bottomWidth, sideSlope);
    double basinLength = 2.0 * bottomWidth;
    double topWidth = bottomWidth + 2.0 * sideSlope * basinDepth;
    double topLength = basinLength + 2.0 * sideSlope * basinDepth;
    double totalDepth = basinDepth + freeboardDepth;

    // Orifice sizing: Q = Cd * A * sqrt(2*g*h)  →  A = Q / (Cd * sqrt(2*g*h))
    double cd = 0.6;
    double g = 32.2; // ft/s²
    double headH = basinDepth; // design head = full basin depth
    double orificeArea = outflow / (cd * Math.Sqrt(2.0 * g * headH));
    double orificeDiameterFt = Math.Sqrt(4.0 * orificeArea / Math.PI);
    double orificeDiameterIn = orificeDiameterFt * 12.0;

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["method"] = methodLabel,
      ["inflow"] = inflow,
      ["outflow"] = outflow,
      ["stormDurationMinutes"] = stormDuration,
      ["requiredStorageCubicFeet"] = Math.Round(storageVolumeCf, 0),
      ["requiredStorageAcreFeet"] = Math.Round(storageVolumeAcFt, 3),
      ["requiredStorageCubicMeters"] = Math.Round(storageVolumeCuM, 1),
      ["basinGeometry"] = new Dictionary<string, object?>
      {
        ["bottomWidth"] = Math.Round(bottomWidth, 1),
        ["bottomLength"] = Math.Round(basinLength, 1),
        ["sideSlope"] = $"{sideSlope}:1",
        ["designDepth"] = Math.Round(basinDepth, 2),
        ["freeboardDepth"] = Math.Round(freeboardDepth, 2),
        ["totalDepth"] = Math.Round(totalDepth, 2),
        ["topWidth"] = Math.Round(topWidth, 1),
        ["topLength"] = Math.Round(topLength, 1),
      },
      ["outletOrifice"] = new Dictionary<string, object?>
      {
        ["designFlow"] = outflow,
        ["dischargeCoefficient"] = cd,
        ["designHead"] = Math.Round(headH, 2),
        ["requiredAreaSqFt"] = Math.Round(orificeArea, 4),
        ["requiredDiameterInches"] = Math.Round(orificeDiameterIn, 2),
        ["recommendedDiameterInches"] = Math.Round(Math.Ceiling(orificeDiameterIn * 2) / 2.0, 1),
      },
      ["notes"] = new[]
      {
        "Storage volume uses simplified Modified Rational Method — confirm with routing software for permit submittals.",
        "Basin dimensions assume trapezoidal cross-section with L = 2W plan ratio.",
        "Orifice size based on full-depth head — verify with stage-discharge routing.",
        "Add freeboard to design depth before specifying construction drawings.",
      },
    });
  }

  // ─── calculateDetentionStageStorage ──────────────────────────────────────────

  /// <summary>
  /// calculateDetentionStageStorage: for TIN surface <c>surfaceName</c> from <c>bottomElevation</c> to
  /// <c>topElevation</c> (all required) every <c>elevationIncrement</c> (default 0.5), the area inside the contours and
  /// the cumulative volume (conic formula) at each stage, stopping where a contour runs off the surface or the basin
  /// spills. In a feet drawing each stage also gets the outlet discharge (<c>outletType</c> orifice with
  /// <c>outletDiameter</c> in inches, or weir with <c>weirLength</c>; <c>dischargeCoefficient</c> default 0.6 / 3.33).
  /// Leaves the drawing unchanged. CIVIL3D.INVALID_INPUT for top &lt;= bottom, a non-positive increment or a surface that
  /// isn't TIN; CIVIL3D.OBJECT_NOT_FOUND for an unknown surface.
  /// </summary>
  public static Task<object?> CalculateDetentionStageStorageAsync(JsonObject? parameters)
  {
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var bottomElevation = PluginRuntime.GetRequiredDouble(parameters, "bottomElevation");
    var topElevation = PluginRuntime.GetRequiredDouble(parameters, "topElevation");
    var elevIncrement = PluginRuntime.GetOptionalDouble(parameters, "elevationIncrement") ?? 0.5;
    var outletType = PluginRuntime.GetOptionalString(parameters, "outletType") ?? "orifice";
    var outletDiameter = PluginRuntime.GetOptionalDouble(parameters, "outletDiameter"); // inches
    var weirLength = PluginRuntime.GetOptionalDouble(parameters, "weirLength");
    var cd = PluginRuntime.GetOptionalDouble(parameters, "dischargeCoefficient")
            ?? (string.Equals(outletType, "weir", StringComparison.OrdinalIgnoreCase) ? 3.33 : 0.6);

    if (topElevation <= bottomElevation)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "topElevation must be greater than bottomElevation.");
    }

    if (elevIncrement <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "elevationIncrement must be positive.");
    }

    // Contours are extracted into the drawing to measure them. MeasureAsync never commits, so none are left behind.
    return CivilExecution.MeasureAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForWrite) is not TinSurface surface)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Surface '{surfaceName}' isn't a TIN surface.");
      }

      var metric = database.Insunits == UnitsValue.Meters;
      var steps = (int)Math.Ceiling((topElevation - bottomElevation) / elevIncrement - 1e-9);
      var rows = new List<Dictionary<string, object?>>();
      var notes = new List<string>
      {
        "Area is what the surface's contours enclose at each stage, so a mound inside the basin isn't taken out.",
        "Volume between stages is by the conic formula, exact for a basin with straight sides.",
      };
      double previousArea = 0, previousElevation = bottomElevation, volume = 0;

      for (var step = 0; step <= steps; step++)
      {
        var elevation = Math.Min(bottomElevation + step * elevIncrement, topElevation);
        var (area, open) = AreaAt(surface, elevation, transaction);
        if (open)
        {
          notes.Add($"Stopped at {elevation}: the contour there runs off the surface, so the basin doesn't hold water to that height.");
          break;
        }

        // Above the rim there's no contour at all, which isn't a basin with no area: the water has gone.
        if (area <= 0 && previousArea > 0)
        {
          notes.Add($"Stopped at {elevation}: the surface is lower than that everywhere, so {previousElevation} is the top of the basin.");
          break;
        }

        var depth = elevation - previousElevation;
        volume += depth / 3.0 * (previousArea + area + Math.Sqrt(previousArea * area));
        rows.Add(new Dictionary<string, object?>
        {
          ["elevation"] = elevation,
          ["area"] = Math.Round(area, 2),
          ["cumulativeVolume"] = Math.Round(volume, 2),
          ["discharge"] = metric ? null : Discharge(outletType, outletDiameter, weirLength, cd, elevation - bottomElevation),
        });
        previousArea = area;
        previousElevation = elevation;
      }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surfaceName,
        ["stages"] = rows,
        ["totalVolume"] = Math.Round(volume, 2),
        ["units"] = metric ? "metres" : "feet, cfs",
        ["notes"] = notes,
      };
    });
  }

  /// <summary>
  /// The plan area a surface's contours enclose at an elevation. Civil 3D marks these polylines not closed even when they
  /// end where they start, so the ends are compared. <c>Open</c> is true for a contour that runs off the surface.
  /// </summary>
  private static (double Area, bool Open) AreaAt(TinSurface surface, double elevation, Transaction transaction)
  {
    var area = 0.0;
    foreach (ObjectId id in surface.ExtractContoursAt(elevation))
    {
      var contour = (Curve)transaction.GetObject(id, OpenMode.ForRead);
      if (contour.StartPoint.DistanceTo(contour.EndPoint) > 1e-6)
      {
        return (0, true);
      }

      area += contour.Area;
    }

    return (area, false);
  }

  /// <summary>Flow through the outlet at a head in feet, in cfs; null without the size it needs.</summary>
  private static double? Discharge(string outletType, double? diameterInches, double? weirLength, double coefficient, double head)
  {
    if (string.Equals(outletType, "weir", StringComparison.OrdinalIgnoreCase))
    {
      return weirLength is { } length ? Math.Round(coefficient * length * Math.Pow(head, 1.5), 3) : null;
    }

    if (diameterInches is not { } inches)
    {
      return null;
    }

    var feet = inches / 12.0;
    var orificeArea = Math.PI * feet * feet / 4.0;
    return Math.Round(coefficient * orificeArea * Math.Sqrt(2.0 * 32.2 * Math.Max(head, 0)), 3);
  }

  // ─── Private helpers ─────────────────────────────────────────────────────────

  /// <summary>
  /// The depth at which a trapezoidal basin (bottom W x 2W, side slope z:1) holds <paramref name="targetVolumeCf"/>,
  /// by proportional iteration clamped to 0.5-50; never less than 1.
  /// </summary>
  private static double EstimateBasinDepth(double targetVolumeCf, double bottomWidth, double sideSlope)
  {
    double basinLength = 2.0 * bottomWidth;
    double depth = 1.0;
    for (int iter = 0; iter < 50; iter++)
    {
      double vol = TrapezoidalVolume(depth, bottomWidth, basinLength, sideSlope);
      if (Math.Abs(vol - targetVolumeCf) < 1.0) break;
      depth *= (targetVolumeCf / vol);
      depth = Math.Max(0.5, Math.Min(depth, 50.0));
    }
    return Math.Max(1.0, depth);
  }

  /// <summary>Volume of a rectangular basin with sloped sides to <paramref name="depth"/> (prismoidal formula).</summary>
  private static double TrapezoidalVolume(double depth, double bottomWidth, double bottomLength, double sideSlope)
  {
    double topWidth = bottomWidth + 2 * sideSlope * depth;
    double topLength = bottomLength + 2 * sideSlope * depth;
    // Frustum formula: V = d/6 * (A_bottom + A_top + 4*A_mid)
    double aMid = (bottomWidth + sideSlope * depth) * (bottomLength + sideSlope * depth);
    return depth / 6.0 * (bottomWidth * bottomLength + topWidth * topLength + 4 * aMid);
  }

}
