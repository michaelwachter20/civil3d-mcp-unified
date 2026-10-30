using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3DMcpPlugin;

/// <summary>
/// Point text for <c>importCogoPoints</c> (Parse) and <c>exportCogoPoints</c> (Format): one point per line, columns separated by commas, semicolons, tabs or
/// spaces. Kept apart from <c>PointCommands</c> (no Autodesk types) so the file-boundary harness can test it
/// without Civil 3D.
/// <para>
/// Formats, as Civil 3D names them: <c>pnezd</c> = point number, northing, easting, elevation, description;
/// <c>penz</c> = point number, easting, northing, elevation, description (optional); <c>xyzd</c> / <c>xyz</c> =
/// easting, northing, elevation (and description). X is the easting and Y the northing, as in AutoCAD. Numbers are
/// read with a dot as the decimal mark whatever the PC's regional settings are, and a description keeps its spaces
/// ("EP 12" stays "EP 12").
/// </para>
/// </summary>
public static class PointText
{
  /// <summary>One point read from a line: its number from the file (null for xyz formats or one that isn't a whole
  /// positive number), X (easting), Y (northing), Z and description.</summary>
  public readonly record struct ParsedPoint(uint? Number, double X, double Y, double Z, string Description);

  /// <summary>The points in <paramref name="data"/>; throws CIVIL3D.INVALID_INPUT naming the first line that doesn't
  /// fit <paramref name="format"/> (too few columns, a coordinate that isn't a number, an unknown format).</summary>
  public static IEnumerable<ParsedPoint> Parse(string data, string format)
  {
    var lineNumber = 0;
    foreach (var rawLine in data.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
    {
      lineNumber++;
      var line = rawLine.Trim();
      if (line.Length == 0)
      {
        continue;
      }

      var tokens = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
      yield return format switch
      {
        // P, N, E, Z, D: X is the easting (column 3), Y the northing (column 2).
        "pnezd" when tokens.Length >= 4 => new ParsedPoint(Number(tokens[0]), Coordinate(tokens[2], line, lineNumber),
          Coordinate(tokens[1], line, lineNumber), Coordinate(tokens[3], line, lineNumber), Rest(tokens, 4)),
        "penz" when tokens.Length >= 4 => new ParsedPoint(Number(tokens[0]), Coordinate(tokens[1], line, lineNumber),
          Coordinate(tokens[2], line, lineNumber), Coordinate(tokens[3], line, lineNumber), Rest(tokens, 4)),
        "xyzd" when tokens.Length >= 3 => new ParsedPoint(null, Coordinate(tokens[0], line, lineNumber),
          Coordinate(tokens[1], line, lineNumber), Coordinate(tokens[2], line, lineNumber), Rest(tokens, 3)),
        "xyz" when tokens.Length >= 3 => new ParsedPoint(null, Coordinate(tokens[0], line, lineNumber),
          Coordinate(tokens[1], line, lineNumber), Coordinate(tokens[2], line, lineNumber), string.Empty),
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Line {lineNumber} doesn't fit point format '{format}' (pnezd, penz, xyzd or xyz): {line}"),
      };
    }
  }

  private static double Coordinate(string token, string line, int lineNumber)
  {
    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
    {
      return value;
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"Line {lineNumber}: '{token}' isn't a number (use a dot for decimals): {line}");
  }

  private static uint? Number(string token)
  {
    return uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
      ? number
      : null;
  }

  /// <summary>One point as a line of <paramref name="format"/> (pnezd, penz, xyzd, xyz, or csv: number, name, X, Y,
  /// Z, description), the columns in the order Parse reads them, numbers with a dot for decimals. Throws
  /// CIVIL3D.INVALID_INPUT for another format.</summary>
  public static string Format(string format, uint number, string? name, double x, double y, double z, string? description,
    string delimiter)
  {
    string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    var d = description ?? string.Empty;
    var columns = format switch
    {
      "pnezd" => new[] { number.ToString(CultureInfo.InvariantCulture), N(y), N(x), N(z), d },
      "penz" => new[] { number.ToString(CultureInfo.InvariantCulture), N(x), N(y), N(z), d },
      "xyzd" => new[] { N(x), N(y), N(z), d },
      "xyz" => new[] { N(x), N(y), N(z) },
      "csv" => new[] { number.ToString(CultureInfo.InvariantCulture), name ?? string.Empty, N(x), N(y), N(z), d },
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported export format: {format}"),
    };
    return string.Join(delimiter, columns);
  }

  /// <summary>The columns from <paramref name="start"/> on, joined by single spaces: the description.</summary>
  private static string Rest(string[] tokens, int start)
  {
    return tokens.Length > start ? string.Join(" ", tokens.Skip(start)) : string.Empty;
  }
}
