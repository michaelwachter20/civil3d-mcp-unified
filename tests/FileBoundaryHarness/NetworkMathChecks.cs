// Harness checks for the network, hydrology and quantity arithmetic the plugin keeps free of Civil 3D: the hydraulic
// grade line walked over a network's topology (PipeMath.WalkHgl), and DesignMath's units, detention sizing and CSV
// quoting. Program.cs calls NetworkMathChecks.Run(); the first failed check throws, which fails the harness run.

using System.Globalization;
using Civil3DMcpPlugin;

/// <summary>Offline checks of PipeMath.WalkHgl and DesignMath (run by Program.cs).</summary>
public static class NetworkMathChecks
{
  /// <summary>Runs every check; throws InvalidOperationException on the first that fails.</summary>
  public static void Run()
  {
    CheckHglWalk();
    CheckUnits();
    CheckCsv();
  }

  private static void CheckHglWalk()
  {
    // A main line O <- A with two branches into A (B and C). Each branch carries only its own loss on top of A's HGL;
    // walked as one chain, C's loss used to be added to B's as well.
    PipeMath.HglPipe P(string name, string up, string down, double loss, double invert = 90) => new(name, up, down, loss, invert);
    var branched = PipeMath.WalkHgl(new[]
    {
      P("B1", "B", "A", 2),
      P("P1", "A", "O", 1),
      P("C1", "C", "A", 5),
    }, tailwater: 100);
    var byName = branched.ToDictionary(node => node.Name);
    Assert(byName["P1"].HglDownstream == 100 && byName["P1"].HglUpstream == 101, "The outfall pipe starts at the tailwater.");
    Assert(byName["B1"].HglDownstream == 101 && byName["B1"].HglUpstream == 103, $"Branch B carries only its own loss: {byName["B1"].HglUpstream}.");
    Assert(byName["C1"].HglDownstream == 101 && byName["C1"].HglUpstream == 106, $"Branch C carries only its own loss: {byName["C1"].HglUpstream}.");
    Assert(branched[0].Name == "P1" && branched.All(node => node.Outfall == "O"), "The outfall pipe comes first, and every pipe drains to O.");

    // Two outfalls without a tailwater: each starts at the lowest invert reaching it.
    var twoOutfalls = PipeMath.WalkHgl(new[]
    {
      P("P1", "A", "O1", 1, invert: 90),
      P("D1", "D", "O2", 3, invert: 50),
    }, tailwater: null).ToDictionary(node => node.Name);
    Assert(twoOutfalls["P1"].HglDownstream == 90 && twoOutfalls["D1"].HglDownstream == 50 && twoOutfalls["D1"].HglUpstream == 53,
      "Each outfall starts at its own invert.");
    Assert(twoOutfalls["D1"].Outfall == "O2" && twoOutfalls["P1"].Outfall == "O1", "Each pipe names its own outfall.");

    // A node that drains two ways takes the higher HGL (the controlling path).
    var split = PipeMath.WalkHgl(new[]
    {
      P("SX", "S", "X", 1, invert: 10),
      P("SY", "S", "Y", 4, invert: 10),
      P("TS", "T", "S", 2),
    }, tailwater: null).ToDictionary(node => node.Name);
    Assert(split["TS"].HglDownstream == 14 && split["TS"].HglUpstream == 16, $"The higher of two paths controls: {split["TS"].HglDownstream}.");

    // A loop with no outfall still ends, with every pipe in the answer.
    var loop = PipeMath.WalkHgl(new[] { P("EF", "E", "F", 1, 5), P("FE", "F", "E", 1, 7) }, tailwater: null);
    Assert(loop.Count == 2 && loop.All(node => !double.IsNaN(node.HglUpstream)), "A loop is walked and ends.");
    Assert(PipeMath.WalkHgl(Array.Empty<PipeMath.HglPipe>(), 1).Count == 0, "No pipes, no nodes.");
  }

  private static void CheckUnits()
  {
    // The rough minimum radius is the same curve in either system: 50 km/h (31.07 mph) gives about 166.7 m.
    var metric = DesignMath.RoughMinimumRadius(50, "meters");
    var feet = DesignMath.RoughMinimumRadius(50 / 1.609344, "feet");
    Assert(Math.Abs(metric - 2500.0 / 15.0) < 1e-9, $"Metric rough minimum radius is V²/15: {metric}.");
    Assert(Math.Abs(feet * 0.3048 - metric) / metric < 0.01, $"mph and feet give the same radius: {feet * 0.3048} m vs {metric} m.");
    Assert(DesignMath.SpeedUnit("feet") == "mph" && DesignMath.SpeedUnit("meters") == "km/h", "Design speed unit follows the drawing.");

    // Detention: (Qin - Qout) t 60, half for the triangular hydrograph; the orifice from Q = Cd A sqrt(2 g h).
    Assert(DesignMath.DetentionStorage(10, 4, 60, triangular: false) == 21600 && DesignMath.DetentionStorage(10, 4, 60, triangular: true) == 10800,
      "Modified Rational storage and its triangular half.");
    var (area, diameter) = DesignMath.OrificeSize(1.0, 2.0, 0.6, 9.80665);
    Assert(Math.Abs(area - 0.26611) < 1e-4 && Math.Abs(diameter - 0.58209) < 1e-4, $"Metric orifice: area {area}, diameter {diameter}.");
    Assert(Math.Abs(DesignMath.CubicMetersToCubicFeet(DesignMath.CubicFeetToCubicMeters(1234.5)) - 1234.5) < 1e-9
           && Math.Abs(DesignMath.CubicFeetToCubicMeters(35.3146667) - 1) < 1e-6, "Cubic feet and cubic meters convert both ways.");

    // Areas: converted from the drawing's own square unit, never assumed to be square feet.
    Assert(Math.Abs(DesignMath.ConvertArea(43560, "feet", "acres") - 1) < 1e-12, "43,560 sq ft is an acre.");
    Assert(Math.Abs(DesignMath.ConvertArea(43560, "feet", "sqm") - 4046.8564224) < 1e-6, "An acre in square meters.");
    Assert(DesignMath.ConvertArea(5000, "feet", "sqft") == 5000, "Square feet stay square feet.");
    Assert(Math.Abs(DesignMath.ConvertArea(10000, "meters", "ha") - 1) < 1e-12, "10,000 m² is a hectare.");
    Assert(Math.Abs(DesignMath.ConvertArea(10000, "meters", "acres") - 2.4710538) < 1e-6, "A hectare in acres, from a metric drawing.");
    Assert(Math.Abs(DesignMath.ConvertArea(1, "meters", "sqft") - 10.7639104) < 1e-6, "A square meter in square feet.");
    ExpectInvalidInput(() => DesignMath.ConvertArea(1, "other", "acres"), "A drawing in neither feet nor meters can't be converted.");
    ExpectInvalidInput(() => DesignMath.ConvertArea(1, "feet", "furlongs"), "An unknown area unit is refused.");
    Assert(DesignMath.NativeAreaUnit("feet") == "sqft" && DesignMath.NativeAreaUnit("meters") == "sqm", "Areas are labelled with the drawing's square unit.");
    Assert(DesignMath.LengthPayUnit("feet") == "LF" && DesignMath.LengthPayUnit("meters") == "M", "Pipe lengths are LF only in feet.");
  }

  private static void CheckCsv()
  {
    Assert(DesignMath.CsvField("plain") == "plain", "Plain text stays bare.");
    Assert(DesignMath.CsvField("Main St, north") == "\"Main St, north\"", "A comma is quoted.");
    Assert(DesignMath.CsvField("12\" RCP") == "\"12\"\" RCP\"", "A quote is doubled inside quotes.");
    Assert(DesignMath.CsvField("two\nlines") == "\"two\nlines\"" && DesignMath.CsvField("cr\rhere") == "\"cr\rhere\"", "Line breaks are quoted.");
    Assert(DesignMath.CsvField(" padded ") == "\" padded \"", "Leading and trailing spaces are kept by quoting.");
    Assert(DesignMath.CsvField(null) == string.Empty, "Null is an empty field.");

    var before = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = new CultureInfo("de-DE");
      Assert(DesignMath.CsvField(1234.5) == "1234.5", $"Numbers use a dot on any PC: {DesignMath.CsvField(1234.5)}");
      Assert(DesignMath.CsvLine(new object?[] { "MH-3, inlet", 2.5, null, 7 }) == "\"MH-3, inlet\",2.5,,7", "A record joins quoted fields.");
      Assert(DesignMath.VertexList(new[] { (1000.5, 2000.25), (1010.0, 2000.25) }) == "1000.5 2000.25;1010 2000.25", "Vertices are x y pairs with dots.");
    }
    finally
    {
      CultureInfo.CurrentCulture = before;
    }
  }

  private static void ExpectInvalidInput(Action action, string message)
  {
    try
    {
      action();
    }
    catch (JsonRpcDispatchException exception) when (exception.Code == "CIVIL3D.INVALID_INPUT")
    {
      return;
    }

    throw new InvalidOperationException(message);
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
