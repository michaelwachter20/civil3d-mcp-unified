// SurveyCommands.cs - handlers for Civil 3D survey databases (civil3d_survey, src/tools/domains/surveyDomain.ts).
// Methods (CommandDispatcher.cs): listSurveyDatabases, createSurveyDatabase, listSurveyNetworks, createSurveyNetwork,
// listSurveyPoints, addSurveyPoint, addSurveySetup, listSurveyObservations, adjustSurveyNetwork, listSurveyFigures,
// getSurveyFigure, createSurveyFigure, addSurveyToDrawing, importSurveyLandXml. Everything goes through SurveyCom
// (late-bound COM), and COM errors become CIVIL3D.API_ERROR. The lists and getSurveyFigure only read (databases opened
// read-only) and run on the read path (CivilExecution.OnActiveDocumentAsync: host gate, main thread, no lock, no
// command context, no transaction, no undo step); the calls that change something run through
// CivilExecution.WithDocumentLockAsync (host gate, command context, document lock, no transaction: COM opens objects
// itself). Survey databases are files in the survey working folder, so changes to them are not drawing changes and
// Undo can't take them back; only addSurveyToDrawing changes the drawing (one undo mark). adjustSurveyNetwork and
// importSurveyLandXml always refuse (checked against Civil 3D 2027's survey COM interop: it has the adjustment settings
// but no call that runs an adjustment or imports a LandXML file). Survey data is the user's: never move or edit points
// unless asked.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Microsoft.CSharp.RuntimeBinder;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Civil 3D's survey databases: the databases themselves, their networks, points, setups and observations,
/// and figures. All of it goes through SurveyCom, because the .NET API has none of it.
///
/// A survey database is a folder on disk, not part of the drawing, so nothing here can be undone with Undo except
/// add_to_drawing. Network adjustment and LandXML import have no call in the interface and stay in the Survey tab.
/// Angles go in and come out in decimal degrees; COM holds them in radians. Lengths and coordinates go in and come out
/// in the database's units, whatever those are; COM holds them in metres, so a foot entered as it stands lands 3.28 feet
/// out.
/// </summary>
public static class SurveyCommands
{
  private static readonly string[] AngleTypes = { "", "angle", "deflection", "face1", "face2", "azimuth", "bearing_ne", "bearing_se", "bearing_sw", "bearing_nw" };
  private static readonly string[] DistanceTypes = { "", "slope", "horizontal", "none" };
  private static readonly string[] VerticalTypes = { "", "vertical_angle", "vertical_distance", "none" };
  private static readonly string[] DirectionTypes = { "bearing_ne", "bearing_se", "bearing_sw", "bearing_nw", "azimuth" };
  private static readonly string[] DistanceUnits = { "", "feet", "meters", "survey_feet" };

  // COM enum values (index into the arrays above where one exists).
  private const int TargetPrism = 1;
  private const int VerticalAngle = 1;
  private const int VerticalNone = 3;

  /// <summary>Turns COM's metres into the database's units. Read once per call: asking COM per value is slow on a big job.</summary>
  private sealed record Units(double PerMetre, double None)
  {
    /// <summary>The open database's metres-to-units factor and its "no value" marker.</summary>
    public static Units Of(dynamic project)
    {
      return new Units((double)project.GetProjectSettings().ConvertMetricToUi(1.0), (double)project.SurveyNullValue);
    }

    public double ToMetres(double value) => value / PerMetre;

    /// <summary>A stored length, or null for the database's "no value" marker or a field that doesn't apply to this shot.</summary>
    public double? Length(Func<double> read) => Stored(read) * PerMetre;

    /// <summary>A stored angle (radians) in degrees, or null as for <see cref="Length"/>.</summary>
    public double? Angle(Func<double> read) => Stored(read) * 180.0 / Math.PI;

    /// <summary>The raw value, or null for NaN, the "no value" marker or a property COM won't read for this item.</summary>
    private double? Stored(Func<double> read)
    {
      try
      {
        var value = read();
        return double.IsNaN(value) || Math.Abs(value - None) < 1e-6 ? null : value;
      }
      catch (COMException)
      {
        // "The property is disabled in current context": a shot with no vertical has no vertical to read.
        return null;
      }
    }
  }

  /// <summary>One observation to add, already converted to COM's enum values and radians (distance still in the
  /// database's units).</summary>
  private sealed record Shot(int Point, int AngleType, double Angle, int DistanceType, double Distance, int VerticalType, double Vertical, double TargetHeight, string Description);

  // -------------------------------------------------------------------------
  // Databases
  // -------------------------------------------------------------------------

  /// <summary><c>listSurveyDatabases</c>: no parameters. The survey working folder and each database's name, path,
  /// distance unit and network/figure/point counts (a database that can't be opened shows <c>openError</c> instead).
  /// Opens each database read-only; changes nothing.</summary>
  public static Task<object?> ListSurveyDatabasesAsync()
  {
    return OnComRead(() =>
    {
      dynamic projects = SurveyCom.Document().Projects;
      var databases = new List<object?>();
      foreach (var project in SurveyCom.Items(projects))
      {
        databases.Add(DescribeDatabase((string)project.Name, (string)project.Path));
      }

      return new Dictionary<string, object?>
      {
        ["workingFolder"] = (string)projects.GetWorkingFolder(),
        ["databaseCount"] = databases.Count,
        ["databases"] = databases,
      };
    });
  }

  /// <summary>One database's summary, opened read-only; a COM error (e.g. locked by another user) is reported, not thrown.</summary>
  private static Dictionary<string, object?> DescribeDatabase(string name, string path)
  {
    try
    {
      return SurveyCom.WithDatabase<Dictionary<string, object?>>(name, false, project =>
      {
        var settings = project.GetProjectSettings();
        return new Dictionary<string, object?>
        {
          ["name"] = name,
          ["path"] = path,
          ["distanceUnit"] = Named(DistanceUnits, (int)settings.DistanceUnit),
          ["networkCount"] = (int)project.Networks.Count,
          ["figureCount"] = (int)project.Figures.Count,
          ["pointCount"] = SurveyCom.Numbers((object?)project.GetPointNumbers()).Count,
        };
      });
    }
    catch (COMException exception)
    {
      // One database another user has locked shouldn't hide the others.
      return new Dictionary<string, object?> { ["name"] = name, ["path"] = path, ["openError"] = exception.Message };
    }
  }

  /// <summary><c>createSurveyDatabase</c>: reads <c>name</c> (required; becomes a folder name) and <c>distanceUnit</c>
  /// (optional: feet, meters, survey_feet). Creates the database in the survey working folder (on disk, not in the
  /// drawing). CIVIL3D.INVALID_INPUT for a bad unit, a name that can't be a folder, or a name already in use.</summary>
  public static Task<object?> CreateSurveyDatabaseAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var distanceUnit = PluginRuntime.GetOptionalString(parameters, "distanceUnit");
    var unit = distanceUnit == null ? 0 : Array.IndexOf(DistanceUnits, distanceUnit);
    if (unit < 0 || (distanceUnit != null && unit == 0))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"distanceUnit is one of {string.Join(", ", DistanceUnits.Skip(1))}.");
    }

    if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != name.Trim().TrimEnd('.'))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' can't be a survey database name: it becomes a folder name.");
    }

    return OnCom(() =>
    {
      dynamic projects = SurveyCom.Document().Projects;
      if (SurveyCom.Find(projects, name) != null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Survey database '{name}' already exists.");
      }

      var project = projects.Create(name);
      if (unit > 0)
      {
        SurveyCom.WithDatabase<object?>(name, true, opened =>
        {
          var settings = opened.GetProjectSettings();
          settings.DistanceUnit = unit;
          opened.UpdateProjectSettings(settings);
          return null;
        });
      }

      return new Dictionary<string, object?>
      {
        ["status"] = "created",
        ["name"] = (string)project.Name,
        ["path"] = (string)project.Path,
        ["distanceUnit"] = distanceUnit ?? Named(DistanceUnits, (int)project.GetProjectSettings().DistanceUnit),
        ["workingFolder"] = (string)projects.GetWorkingFolder(),
      };
    });
  }

  // -------------------------------------------------------------------------
  // Networks
  // -------------------------------------------------------------------------

  /// <summary><c>listSurveyNetworks</c>: reads <c>databaseName</c> (required). Each network's name, description,
  /// control/non-control point, setup, direction and traverse counts, and whether it is in the drawing. Read-only.
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown database.</summary>
  public static Task<object?> ListSurveyNetworksAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");

    return OnComRead(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
    {
      dynamic drawing = SurveyCom.Document().Database;
      var networks = new List<object?>();
      foreach (var network in SurveyCom.Items(project.Networks))
      {
        networks.Add(new Dictionary<string, object?>
        {
          ["name"] = (string)network.Name,
          ["description"] = (string)network.Description,
          ["controlPoints"] = (int)network.ControlPoints.Count,
          ["nonControlPoints"] = (int)network.NonControlPoints.Count,
          ["setups"] = (int)network.Setups.Count,
          ["directions"] = (int)network.Directions.Count,
          ["traverses"] = (int)network.Traverses.Count,
          ["inDrawing"] = (bool)network.IsInDrawing(drawing),
        });
      }

      return new Dictionary<string, object?> { ["databaseName"] = databaseName, ["networkCount"] = networks.Count, ["networks"] = networks };
    }));
  }

  /// <summary><c>createSurveyNetwork</c>: reads <c>databaseName</c>, <c>networkName</c> (required), <c>description</c>.
  /// Adds and saves a network in the survey database (not the drawing). CIVIL3D.INVALID_INPUT when the name exists,
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown database.</summary>
  public static Task<object?> CreateSurveyNetworkAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var description = PluginRuntime.GetOptionalString(parameters, "description");

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, true, project =>
    {
      if (SurveyCom.Find(project.Networks, networkName) != null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Survey network '{networkName}' already exists in '{databaseName}'.");
      }

      var network = project.Networks.Create(networkName);
      if (description != null) network.Description = description;
      network.Save();
      return new Dictionary<string, object?> { ["status"] = "created", ["databaseName"] = databaseName, ["networkName"] = (string)network.Name };
    }));
  }

  // -------------------------------------------------------------------------
  // Points
  // -------------------------------------------------------------------------

  /// <summary><c>listSurveyPoints</c>: reads <c>databaseName</c> (required) and <c>limit</c> (default 500). Points by
  /// number with easting/northing/elevation in the database's units, control/setup flags and drawing coordinates;
  /// <c>truncated</c> when there are more. Read-only.</summary>
  public static Task<object?> ListSurveyPointsAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 500;

    return OnComRead(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
    {
      dynamic drawing = SurveyCom.Document().Database;
      var numbers = SurveyCom.Numbers((object?)project.GetPointNumbers());
      numbers.Sort();
      var units = (Units)Units.Of(project);
      var points = new List<object?>();
      foreach (var number in numbers.Take(limit))
      {
        points.Add(DescribePoint(project.GetPointByNumber(number), units, drawing));
      }

      return new Dictionary<string, object?>
      {
        ["databaseName"] = databaseName,
        ["distanceUnit"] = Named(DistanceUnits, (int)project.GetProjectSettings().DistanceUnit),
        ["pointCount"] = numbers.Count,
        ["truncated"] = numbers.Count > limit,
        ["points"] = points,
      };
    }));
  }

  /// <summary>One survey point as JSON, including where it lands in the drawing.</summary>
  private static Dictionary<string, object?> DescribePoint(dynamic point, Units units, dynamic drawing)
  {
    var inDrawing = (double[])point.CalculateDrawingCoordinate(drawing);
    return new Dictionary<string, object?>
    {
      ["number"] = (int)point.Number,
      ["name"] = (string)point.Name,
      ["description"] = (string)point.Description,
      ["easting"] = units.Length(() => (double)point.Easting),
      ["northing"] = units.Length(() => (double)point.Northing),
      ["elevation"] = units.Length(() => (double)point.Elevation),
      ["isControl"] = (bool)point.IsControlPoint,
      ["isSetup"] = (bool)point.IsSetupPoint,
      ["drawingX"] = inDrawing[0],
      ["drawingY"] = inDrawing[1],
    };
  }

  /// <summary><c>addSurveyPoint</c>: reads <c>databaseName</c>, <c>networkName</c>, <c>pointNumber</c>,
  /// <c>easting</c>, <c>northing</c> (required), <c>elevation</c>, <c>pointName</c>, <c>description</c>,
  /// <c>control</c> (default true). Adds a control or non-control point to the network in the survey database, in the
  /// database's units. CIVIL3D.INVALID_INPUT (missing number, number in use), CIVIL3D.OBJECT_NOT_FOUND.</summary>
  public static Task<object?> AddSurveyPointAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var number = PluginRuntime.GetOptionalInt(parameters, "pointNumber")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pointNumber is required.");
    var easting = PluginRuntime.GetRequiredDouble(parameters, "easting");
    var northing = PluginRuntime.GetRequiredDouble(parameters, "northing");
    var elevation = PluginRuntime.GetOptionalDouble(parameters, "elevation");
    var name = PluginRuntime.GetOptionalString(parameters, "pointName") ?? string.Empty;
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var control = PluginRuntime.GetOptionalBool(parameters, "control") ?? true;

    return OnCom(() => WithNetwork(databaseName, networkName, true, (project, network) =>
    {
      if (SurveyCom.Numbers((object?)project.GetPointNumbers()).Contains(number))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Point {number} already exists in '{databaseName}'.");
      }

      var units = (Units)Units.Of(project);
      var collection = control ? network.ControlPoints : network.NonControlPoints;
      var point = collection.Create(number, name, description, units.ToMetres(easting), units.ToMetres(northing), elevation.HasValue ? units.ToMetres(elevation.Value) : units.None);
      point.Save();
      network.Save();
      return new Dictionary<string, object?>
      {
        ["status"] = "added",
        ["databaseName"] = databaseName,
        ["networkName"] = networkName,
        ["pointNumber"] = number,
        ["control"] = control,
      };
    }));
  }

  // -------------------------------------------------------------------------
  // Setups and observations
  // -------------------------------------------------------------------------

  /// <summary><c>addSurveySetup</c>: reads <c>databaseName</c>, <c>networkName</c>, <c>stationPoint</c>,
  /// <c>backsightPoint</c> (required) and <c>observations</c> (array, see <see cref="ReadShots"/>). Adds a setup and its
  /// shots to the survey database. CIVIL3D.INVALID_INPUT when the station or backsight point isn't in the database or
  /// an observation is malformed, CIVIL3D.OBJECT_NOT_FOUND.</summary>
  public static Task<object?> AddSurveySetupAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var station = PluginRuntime.GetOptionalInt(parameters, "stationPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "stationPoint is required.");
    var backsight = PluginRuntime.GetOptionalInt(parameters, "backsightPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "backsightPoint is required.");
    var shots = ReadShots(parameters);

    return OnCom(() => WithNetwork(databaseName, networkName, true, (project, network) =>
    {
      var known = SurveyCom.Numbers((object?)project.GetPointNumbers());
      var missing = new[] { station, backsight }.Where(number => !known.Contains(number)).ToList();
      if (missing.Count > 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Point(s) {string.Join(", ", missing)} are not in '{databaseName}'. A setup needs its station and backsight first.");
      }

      var units = (Units)Units.Of(project);
      var setup = network.Setups.Create(station, backsight);
      foreach (var shot in shots)
      {
        setup.Observations.Create(shot.Point, shot.AngleType, shot.Angle, shot.DistanceType, units.ToMetres(shot.Distance), shot.VerticalType, shot.Vertical, TargetPrism, units.ToMetres(shot.TargetHeight), shot.Description);
      }

      setup.Save();
      network.Save();
      return new Dictionary<string, object?>
      {
        ["status"] = "added",
        ["databaseName"] = databaseName,
        ["networkName"] = networkName,
        ["stationPoint"] = station,
        ["backsightPoint"] = backsight,
        ["observationCount"] = (int)setup.Observations.Count,
      };
    }));
  }

  /// <summary>Parses <c>observations</c>: each needs <c>pointNumber</c>, <c>angleDegrees</c>, <c>distance</c>;
  /// optional <c>angleType</c> (default "angle"), <c>distanceType</c> (default "horizontal"), <c>verticalDegrees</c>,
  /// <c>targetHeight</c>, <c>description</c>. Degrees become radians. CIVIL3D.INVALID_INPUT on a missing field or an
  /// unknown type.</summary>
  private static List<Shot> ReadShots(JsonObject? parameters)
  {
    var shots = new List<Shot>();
    if (PluginRuntime.GetParameter(parameters, "observations") is not JsonArray items) return shots;

    foreach (var item in items)
    {
      if (item is not JsonObject shot)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each observation must be a JSON object.");
      }

      var point = shot["pointNumber"]?.GetValue<int>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "An observation is missing pointNumber.");
      var angle = shot["angleDegrees"]?.GetValue<double>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "An observation is missing angleDegrees.");
      var distance = shot["distance"]?.GetValue<double>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "An observation is missing distance.");
      var angleType = Array.IndexOf(AngleTypes, shot["angleType"]?.GetValue<string>() ?? "angle");
      var distanceType = Array.IndexOf(DistanceTypes, shot["distanceType"]?.GetValue<string>() ?? "horizontal");
      if (angleType < 1 || distanceType < 1)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "angleType or distanceType isn't one Civil 3D has.");
      }

      var vertical = shot["verticalDegrees"]?.GetValue<double>();
      shots.Add(new Shot(
        point,
        angleType,
        angle * Math.PI / 180.0,
        distanceType,
        distance,
        vertical.HasValue ? VerticalAngle : VerticalNone,
        (vertical ?? 0) * Math.PI / 180.0,
        shot["targetHeight"]?.GetValue<double>() ?? 0,
        shot["description"]?.GetValue<string>() ?? string.Empty));
    }

    return shots;
  }

  /// <summary><c>listSurveyObservations</c>: reads <c>databaseName</c> (required), <c>networkName</c> (optional, all
  /// networks when absent) and <c>observationType</c> (all, angles, distances, directions, gps; default all). Read-only.
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown database or network.</summary>
  public static Task<object?> ListSurveyObservationsAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var networkName = PluginRuntime.GetOptionalString(parameters, "networkName");
    var observationType = PluginRuntime.GetOptionalString(parameters, "observationType") ?? "all";

    return OnComRead(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
    {
      var units = (Units)Units.Of(project);
      var rows = new List<object?>();
      var found = false;
      foreach (var network in SurveyCom.Items(project.Networks))
      {
        var name = (string)network.Name;
        if (networkName != null && !string.Equals(name, networkName, StringComparison.OrdinalIgnoreCase)) continue;

        found = true;
        foreach (var row in ReadObservations(network, name, units, observationType)) rows.Add(row);
      }

      if (networkName != null && !found)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey network '{networkName}' not found in '{databaseName}'.");
      }

      return new Dictionary<string, object?>
      {
        ["databaseName"] = databaseName,
        ["networkName"] = networkName ?? string.Empty,
        ["observationCount"] = rows.Count,
        ["observations"] = rows,
      };
    }));
  }

  /// <summary>
  /// A network's field book: each setup's shots, then its stand-alone directions. "angles" and "distances" keep the
  /// shots that hold one. COM has no GPS vectors, so "gps" finds nothing.
  /// </summary>
  private static IEnumerable<Dictionary<string, object?>> ReadObservations(dynamic network, string networkName, Units units, string filter)
  {
    if (filter is "all" or "angles" or "distances")
    {
      foreach (var setup in SurveyCom.Items(network.Setups))
      {
        foreach (var shot in SurveyCom.Items(setup.Observations))
        {
          var distanceType = (int)shot.DistanceType;
          var verticalType = (int)shot.VerticalType;
          if (filter == "distances" && distanceType == 3) continue;

          yield return new Dictionary<string, object?>
          {
            ["networkName"] = networkName,
            ["kind"] = "observation",
            ["fromPoint"] = (int)setup.StationPointNumber,
            ["backsightPoint"] = (int)setup.BacksightPointNumber,
            ["toPoint"] = (int)shot.PointNumber,
            ["angleDegrees"] = units.Angle(() => (double)shot.Angle),
            ["angleType"] = Named(AngleTypes, (int)shot.AngleType),
            ["distance"] = units.Length(() => (double)shot.Distance),
            ["distanceType"] = Named(DistanceTypes, distanceType),
            ["verticalType"] = Named(VerticalTypes, verticalType),
            ["vertical"] = verticalType == VerticalAngle ? units.Angle(() => (double)shot.Vertical) : units.Length(() => (double)shot.Vertical),
            ["targetHeight"] = units.Length(() => (double)shot.TargetHeight),
            ["description"] = (string)shot.Description,
            ["easting"] = units.Length(() => (double)shot.Easting),
            ["northing"] = units.Length(() => (double)shot.Northing),
            ["elevation"] = units.Length(() => (double)shot.Elevation),
          };
        }
      }
    }

    if (filter is "all" or "directions")
    {
      foreach (var direction in SurveyCom.Items(network.Directions))
      {
        yield return new Dictionary<string, object?>
        {
          ["networkName"] = networkName,
          ["kind"] = "direction",
          ["fromPoint"] = (int)direction.FromPointNumber,
          ["toPoint"] = (int)direction.ToPointNumber,
          ["angleDegrees"] = units.Angle(() => (double)direction.Direction),
          ["angleType"] = Named(DirectionTypes, (int)direction.DirectionType),
        };
      }
    }
  }

  /// <summary><c>adjustSurveyNetwork</c>: checks <c>databaseName</c> and <c>networkName</c> are given, then always
  /// throws CIVIL3D.API_ERROR: the COM API can't run an adjustment, so the user does it in the Survey tab. Changes
  /// nothing.</summary>
  public static Task<object?> AdjustSurveyNetworkAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "databaseName");
    _ = PluginRuntime.GetRequiredString(parameters, "networkName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D's survey interface holds the adjustment settings but has no call that runs an adjustment. Nothing was adjusted; use Network Adjustment in the Survey tab.");
  }

  // -------------------------------------------------------------------------
  // Figures
  // -------------------------------------------------------------------------

  /// <summary><c>listSurveyFigures</c>: reads <c>databaseName</c> (optional; every database when absent). Each
  /// figure's summary (no vertices). Read-only. CIVIL3D.OBJECT_NOT_FOUND for an unknown database.</summary>
  public static Task<object?> ListSurveyFiguresAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetOptionalString(parameters, "databaseName");

    return OnComRead(() =>
    {
      var names = new List<string>();
      foreach (var project in SurveyCom.Items(SurveyCom.Document().Projects))
      {
        names.Add((string)project.Name);
      }

      if (databaseName != null && !names.Contains(databaseName, StringComparer.OrdinalIgnoreCase))
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey database '{databaseName}' was not found in the survey working folder.");
      }

      var figures = new List<object?>();
      foreach (var name in names.Where(name => databaseName == null || string.Equals(name, databaseName, StringComparison.OrdinalIgnoreCase)))
      {
        SurveyCom.WithDatabase<object?>(name, false, project =>
        {
          var units = (Units)Units.Of(project);
          foreach (var figure in SurveyCom.Items(project.Figures))
          {
            figures.Add(DescribeFigure(figure, name, units, false));
          }

          return null;
        });
      }

      return new Dictionary<string, object?> { ["figureCount"] = figures.Count, ["figures"] = figures };
    });
  }

  /// <summary><c>getSurveyFigure</c>: reads <c>name</c> (required) and <c>databaseName</c> (optional; searches every
  /// database, first match wins). The figure with its vertices and point numbers. Read-only.
  /// CIVIL3D.OBJECT_NOT_FOUND when no database has it.</summary>
  public static Task<object?> GetSurveyFigureAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var databaseName = PluginRuntime.GetOptionalString(parameters, "databaseName");

    return OnComRead(() =>
    {
      var names = new List<string>();
      foreach (var project in SurveyCom.Items(SurveyCom.Document().Projects))
      {
        names.Add((string)project.Name);
      }

      foreach (var candidate in names.Where(item => databaseName == null || string.Equals(item, databaseName, StringComparison.OrdinalIgnoreCase)))
      {
        var answer = SurveyCom.WithDatabase<object?>(candidate, false, project =>
        {
          var figure = SurveyCom.Find(project.Figures, name);
          return figure == null ? null : (object?)DescribeFigure(figure, candidate, (Units)Units.Of(project), true);
        });
        if (answer != null) return answer;
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey figure '{name}' not found.");
    });
  }

  /// <summary>A figure's summary (name, counts, closed/breakline/lot-line flags, layer, style); with
  /// <paramref name="withNodes"/> also its vertices (database units) and point numbers.</summary>
  private static Dictionary<string, object?> DescribeFigure(dynamic figure, string databaseName, Units units, bool withNodes)
  {
    var description = new Dictionary<string, object?>
    {
      ["name"] = (string)figure.Name,
      ["databaseName"] = databaseName,
      ["vertexCount"] = (int)figure.FigureNodes.Count,
      ["pointCount"] = (int)figure.PointCount,
      ["isClosed"] = (bool)figure.IsClosed,
      ["isBreakline"] = (bool)figure.IsBreakline,
      ["isLotLine"] = (bool)figure.IsLotLine,
      ["layer"] = (string)figure.Layer,
      ["style"] = (string)figure.Style,
    };

    if (withNodes)
    {
      var vertices = new List<object?>();
      foreach (var node in SurveyCom.Items(figure.FigureNodes))
      {
        vertices.Add(new Dictionary<string, object?>
        {
          ["x"] = units.Length(() => (double)node.X),
          ["y"] = units.Length(() => (double)node.Y),
          ["z"] = units.Length(() => (double)node.Z),
          ["bulge"] = (double)node.Bulge,
          ["point"] = (int)node.Point0,
        });
      }

      description["vertices"] = vertices;
      description["pointNumbers"] = SurveyCom.Numbers((object?)figure.AccumulatePointNumbers());
    }

    return description;
  }

  /// <summary><c>createSurveyFigure</c>: reads <c>databaseName</c>, <c>figureName</c> (required), <c>pointNumbers</c>
  /// (array, at least two, all already in the database), <c>closed</c>, <c>figureStyle</c>, <c>layer</c>. Creates the
  /// figure through those points in the survey database (not the drawing); on a COM failure the half-made figure is
  /// removed. CIVIL3D.INVALID_INPUT (too few points, name in use, unknown points), CIVIL3D.API_ERROR.</summary>
  public static Task<object?> CreateSurveyFigureAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var figureName = PluginRuntime.GetRequiredString(parameters, "figureName");
    var closed = PluginRuntime.GetOptionalBool(parameters, "closed") ?? false;
    var style = PluginRuntime.GetOptionalString(parameters, "figureStyle");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    if (PluginRuntime.GetParameter(parameters, "pointNumbers") is not JsonArray items || items.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pointNumbers needs at least two points.");
    }

    var numbers = items.Select(item => item!.GetValue<int>()).ToList();

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, true, project =>
    {
      var figures = project.Figures;
      if (SurveyCom.Find(figures, figureName) != null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Survey figure '{figureName}' already exists in '{databaseName}'.");
      }

      var known = SurveyCom.Numbers((object?)project.GetPointNumbers());
      var missing = numbers.Where(number => !known.Contains(number)).ToList();
      if (missing.Count > 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Point(s) {string.Join(", ", missing)} are not in '{databaseName}'.");
      }

      var figure = figures.Create(figureName);
      try
      {
        foreach (var number in numbers)
        {
          var point = project.GetPointByNumber(number);
          figure.AddLineByPoint((double)point.Easting, (double)point.Northing, number);
        }

        if (closed) figure.IsClosed = true;
        if (layer != null) figure.Layer = layer;
        if (style != null) figure.Style = style;
        figure.Save();
      }
      catch (COMException)
      {
        // Half a figure is worse than none.
        figures.Remove(figureName);
        throw;
      }

      var answer = DescribeFigure(figure, databaseName, (Units)Units.Of(project), true);
      answer["status"] = "created";
      return answer;
    }));
  }

  // -------------------------------------------------------------------------
  // Drawing
  // -------------------------------------------------------------------------

  /// <summary><c>addSurveyToDrawing</c>: inserts a figure or a network into the active drawing as one undo step (COM
  /// undo marks around AddToDrawing). Reads <c>databaseName</c> (required) and exactly one of <c>figureName</c> /
  /// <c>networkName</c>. Changes the drawing; returns the new object's handle. CIVIL3D.INVALID_INPUT (neither or both
  /// names), CIVIL3D.OBJECT_NOT_FOUND.</summary>
  public static Task<object?> AddSurveyToDrawingAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var figureName = PluginRuntime.GetOptionalString(parameters, "figureName");
    var networkName = PluginRuntime.GetOptionalString(parameters, "networkName");
    if ((figureName == null) == (networkName == null))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either figureName or networkName.");
    }

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
    {
      var collection = figureName != null ? project.Figures : project.Networks;
      var name = figureName ?? networkName!;
      var item = SurveyCom.Find(collection, name)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"'{name}' not found in '{databaseName}'.");

      dynamic document = SurveyCom.Document();
      document.StartUndoMark();
      try
      {
        var id = new ObjectId(new IntPtr((long)item.AddToDrawing(document.Database)));
        return new Dictionary<string, object?>
        {
          ["status"] = "added",
          ["databaseName"] = databaseName,
          ["name"] = name,
          ["handle"] = id.Handle.ToString(),
        };
      }
      finally
      {
        document.EndUndoMark();
      }
    }));
  }

  /// <summary><c>importSurveyLandXml</c>: checks <c>filePath</c> (through FileBoundary.ResolveImportPath, .xml or
  /// .landxml; CIVIL3D.PATH_NOT_ALLOWED etc.) and <c>databaseName</c>, then always throws CIVIL3D.API_ERROR: the COM
  /// API has no LandXML import, so the user uses Import Survey Data. Imports nothing.</summary>
  public static Task<object?> ImportSurveyLandXmlAsync(JsonObject? parameters)
  {
    var filePath = FileBoundary.ResolveImportPath(
      PluginRuntime.GetRequiredString(parameters, "filePath"),
      ".xml", ".landxml");
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Civil 3D's survey interface has no LandXML import. '{filePath}' was not imported into '{databaseName}'; use Import Survey Data in the Survey tab.");
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>Opens the database and finds the network; CIVIL3D.OBJECT_NOT_FOUND when either is missing.</summary>
  private static T WithNetwork<T>(string databaseName, string networkName, bool forWrite, Func<dynamic, dynamic, T> action)
  {
    return SurveyCom.WithDatabase<T>(databaseName, forWrite, project =>
    {
      var network = SurveyCom.Find(project.Networks, networkName)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey network '{networkName}' not found in '{databaseName}'.");
      return action(project, network);
    });
  }

  /// <summary>Survey calls that change something (a survey database, or the drawing for addSurveyToDrawing), with what
  /// COM raises turned into an answer the AI can read. Runs through CivilExecution.WithDocumentLockAsync (host gate,
  /// command context, lock, no transaction); see <see cref="RunCom"/> for the errors.</summary>
  private static Task<object?> OnCom(Func<object?> action)
  {
    return CivilExecution.WithDocumentLockAsync<object?>((_, _) => RunCom(action));
  }

  /// <summary>Survey calls that only read (lists and gets: databases opened read-only, nothing saved). Runs through
  /// CivilExecution.OnActiveDocumentAsync, the read path (host gate, main thread, the verified drawing) with no lock,
  /// no command context and no transaction, since COM opens what it reads itself: no undo step, as for any read.</summary>
  private static Task<object?> OnComRead(Func<object?> action)
  {
    return CivilExecution.OnActiveDocumentAsync<object?>(_ => RunCom(action));
  }

  /// <summary>Runs a survey COM call: COM and late-binding errors become CIVIL3D.API_ERROR; COM objects are released
  /// after every call.</summary>
  private static object? RunCom(Func<object?> action)
  {
    try
    {
      return action();
    }
    catch (COMException exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D's survey interface refused: {exception.Message}");
    }
    catch (RuntimeBinderException exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D's survey interface has no such member: {exception.Message}");
    }
    finally
    {
      SurveyCom.ReleaseObjects();
    }
  }

  /// <summary>The name for a COM enum value, or the number when it has none.</summary>
  private static string Named(string[] names, int value) => value >= 0 && value < names.Length ? names[value] : value.ToString();
}
