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

  private const int TargetPrism = 1;
  private const int VerticalAngle = 1;
  private const int VerticalNone = 3;

  /// <summary>Turns COM's metres into the database's units. Read once per call: asking COM per value is slow on a big job.</summary>
  private sealed record Units(double PerMetre, double None)
  {
    public static Units Of(dynamic project)
    {
      return new Units((double)project.GetProjectSettings().ConvertMetricToUi(1.0), (double)project.SurveyNullValue);
    }

    public double ToMetres(double value) => value / PerMetre;

    /// <summary>A stored length, or null for the database's "no value" marker or a field that doesn't apply to this shot.</summary>
    public double? Length(Func<double> read) => Stored(read) * PerMetre;

    public double? Angle(Func<double> read) => Stored(read) * 180.0 / Math.PI;

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

  private sealed record Shot(int Point, int AngleType, double Angle, int DistanceType, double Distance, int VerticalType, double Vertical, double TargetHeight, string Description);

  // -------------------------------------------------------------------------
  // Databases
  // -------------------------------------------------------------------------

  public static Task<object?> ListSurveyDatabasesAsync()
  {
    return OnCom(() =>
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

  public static Task<object?> ListSurveyNetworksAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
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

  public static Task<object?> ListSurveyPointsAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 500;

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
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

  public static Task<object?> ListSurveyObservationsAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetRequiredString(parameters, "databaseName");
    var networkName = PluginRuntime.GetOptionalString(parameters, "networkName");
    var observationType = PluginRuntime.GetOptionalString(parameters, "observationType") ?? "all";

    return OnCom(() => SurveyCom.WithDatabase<object?>(databaseName, false, project =>
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

  public static Task<object?> ListSurveyFiguresAsync(JsonObject? parameters)
  {
    var databaseName = PluginRuntime.GetOptionalString(parameters, "databaseName");

    return OnCom(() =>
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

  public static Task<object?> GetSurveyFigureAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var databaseName = PluginRuntime.GetOptionalString(parameters, "databaseName");

    return OnCom(() =>
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

  /// <summary>Inserts a figure or a network into the active drawing as one undo step.</summary>
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

  private static T WithNetwork<T>(string databaseName, string networkName, bool forWrite, Func<dynamic, dynamic, T> action)
  {
    return SurveyCom.WithDatabase<T>(databaseName, forWrite, project =>
    {
      var network = SurveyCom.Find(project.Networks, networkName)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey network '{networkName}' not found in '{databaseName}'.");
      return action(project, network);
    });
  }

  /// <summary>Survey calls on the document's context, with what COM raises turned into an answer the AI can read.</summary>
  private static Task<object?> OnCom(Func<object?> action)
  {
    return CivilExecution.WithDocumentLockAsync<object?>((_, _) =>
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
    });
  }

  private static string Named(string[] names, int value) => value >= 0 && value < names.Length ? names[value] : value.ToString();
}
