// Pure design arithmetic and text shared by the QC, detention, parcel, quantity and cost commands, in the drawing's own
// units: a rough minimum curve radius per design speed, detention storage and orifice size, area conversions, pay-item
// unit names, and RFC 4180 CSV fields with dot decimals.
// Used by QcCommands (qcCheckAlignment), DetentionCommands (calculateDetentionBasinSize), ParcelEditingCommands
// (reportParcels), QuantityCommands (qtyParcelAreas, qtyExportToCsv) and CostEstimationCommands (exportPayItems,
// calculateMaterialCostEstimate). Serves no JSON-RPC method itself.
// No Autodesk types, no drawing or file access: tests/FileBoundaryHarness compiles and tests it without Civil 3D.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3DMcpPlugin;

/// <summary>
/// Unit-aware design arithmetic and CSV text for the plugin's commands. Units are the words
/// <c>CivilObjectUtils.LinearUnits</c> gives: "feet" (survey feet included), "meters", or "other"; anything not in
/// feet is worked in metric, as PipeMath does.
/// </summary>
public static class DesignMath
{
  /// <summary>Square meters in one square foot (exact, from 0.3048 m per foot).</summary>
  public const double SquareMetersPerSquareFoot = 0.09290304;

  /// <summary>Square meters in one acre (exact, 43,560 square feet).</summary>
  public const double SquareMetersPerAcre = 4046.8564224;

  /// <summary>Cubic meters in one cubic foot (exact).</summary>
  public const double CubicMetersPerCubicFoot = 0.028316846592;

  /// <summary>The design speed unit of a drawing: mph in feet, km/h otherwise.</summary>
  public static string SpeedUnit(string linearUnits) => linearUnits == "feet" ? "mph" : "km/h";

  /// <summary>
  /// A rough minimum horizontal curve radius for a design speed, R = V² / (127 (e + f)) in km/h and meters or
  /// V² / (15 (e + f)) in mph and feet, with e + f fixed at 15/127 (so V² / 15 in metric). A sanity check that flags a
  /// curve far too tight for the speed, not a design check: real e and f depend on the speed and the agency.
  /// </summary>
  public static double RoughMinimumRadius(double designSpeed, string linearUnits)
  {
    const double sideFriction = 15.0 / 127.0;
    return linearUnits == "feet"
      ? designSpeed * designSpeed / (15.0 * sideFriction)
      : designSpeed * designSpeed / (127.0 * sideFriction);
  }

  /// <summary>
  /// Detention storage by the Modified Rational Method, (Qin - Qout) * t * 60 for a storm of <paramref name="minutes"/>,
  /// in the flow's cubic units (cfs gives ft³, m³/s gives m³); the triangular-hydrograph estimate is half of it.
  /// </summary>
  public static double DetentionStorage(double inflow, double outflow, double minutes, bool triangular)
  {
    var volume = (inflow - outflow) * minutes * 60.0;
    return triangular ? 0.5 * volume : volume;
  }

  /// <summary>Cubic feet to cubic meters.</summary>
  public static double CubicFeetToCubicMeters(double cubicFeet) => cubicFeet * CubicMetersPerCubicFoot;

  /// <summary>Cubic meters to cubic feet.</summary>
  public static double CubicMetersToCubicFeet(double cubicMeters) => cubicMeters / CubicMetersPerCubicFoot;

  /// <summary>
  /// The orifice that passes <paramref name="flow"/> at <paramref name="head"/>: Q = Cd A sqrt(2 g h), so
  /// A = Q / (Cd sqrt(2 g h)), and its round diameter. Units are the caller's (g in the same length unit per s²).
  /// </summary>
  public static (double Area, double Diameter) OrificeSize(double flow, double head, double dischargeCoefficient, double gravity)
  {
    var area = flow / (dischargeCoefficient * Math.Sqrt(2.0 * gravity * Math.Max(head, 1e-9)));
    return (area, Math.Sqrt(4.0 * area / Math.PI));
  }

  /// <summary>The area unit a drawing's areas are in: "sqft" in feet, "sqm" in meters, "square drawing units" otherwise.</summary>
  public static string NativeAreaUnit(string linearUnits) => linearUnits switch
  {
    "feet" => "sqft",
    "meters" => "sqm",
    _ => "square drawing units",
  };

  /// <summary>
  /// An area in the drawing's square units converted to <paramref name="targetUnit"/> (sqft, acres, sqm or ha).
  /// CIVIL3D.INVALID_INPUT for another unit, or for a drawing in neither feet nor meters (its square unit isn't known).
  /// </summary>
  public static double ConvertArea(double areaInDrawingUnits, string linearUnits, string targetUnit)
  {
    var squareMetersPerUnit = linearUnits switch
    {
      "feet" => SquareMetersPerSquareFoot,
      "meters" => 1.0,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"The drawing's units are neither feet nor meters, so its areas can't be converted to '{targetUnit}'. Leave units out to get the area in square drawing units."),
    };
    var squareMeters = areaInDrawingUnits * squareMetersPerUnit;
    return targetUnit.Trim().ToLowerInvariant() switch
    {
      "sqft" => linearUnits == "feet" ? areaInDrawingUnits : squareMeters / SquareMetersPerSquareFoot,
      "sqm" => squareMeters,
      "acres" => squareMeters / SquareMetersPerAcre,
      "ha" => squareMeters / 10000.0,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Unknown area units '{targetUnit}': send sqft, acres, sqm or ha."),
    };
  }

  /// <summary>The pay-item unit for a length: "LF" (linear feet) in a feet drawing, "M" in meters, "units" otherwise.</summary>
  public static string LengthPayUnit(string linearUnits) => linearUnits switch
  {
    "feet" => "LF",
    "meters" => "M",
    _ => "units",
  };

  /// <summary>
  /// One CSV field by RFC 4180: text with a comma, a double quote, a line break or a leading/trailing space is
  /// wrapped in quotes with its quotes doubled; numbers are written with a dot whatever the PC's culture; null is empty.
  /// </summary>
  public static string CsvField(object? value)
  {
    var text = value switch
    {
      null => string.Empty,
      string s => s,
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? string.Empty,
    };

    var needsQuotes = text.IndexOfAny([',', '"', '\r', '\n']) >= 0
      || (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])));
    return needsQuotes ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
  }

  /// <summary>A CSV record: each value through <see cref="CsvField"/>, joined by commas (no line ending).</summary>
  public static string CsvLine(IEnumerable<object?> values) => string.Join(",", values.Select(CsvField));

  /// <summary>Points as "x y;x y;..." with dot decimals, for a single CSV field (CsvField quotes it when needed).</summary>
  public static string VertexList(IEnumerable<(double X, double Y)> points) =>
    string.Join(";", points.Select(point => string.Create(CultureInfo.InvariantCulture, $"{point.X} {point.Y}")));
}
