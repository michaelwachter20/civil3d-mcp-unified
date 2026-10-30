using System;
using System.Collections.Generic;
using System.Globalization;

namespace Civil3DMcpPlugin;

/// <summary>
/// Station arithmetic shared by the sampling commands (sample lines every interval, profile elevations every
/// interval). No Autodesk types, so the file-boundary harness tests it without Civil 3D.
/// </summary>
public static class StationMath
{
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
  /// A sample line's name: "SL-" and the station with up to two decimals and a dot whatever the PC's regional
  /// settings ("SL-1250", "SL-1262.5"; not "SL-1262,5" on a comma-decimal PC).
  /// </summary>
  public static string SampleLineName(double station) =>
    "SL-" + station.ToString("0.##", CultureInfo.InvariantCulture);
}
