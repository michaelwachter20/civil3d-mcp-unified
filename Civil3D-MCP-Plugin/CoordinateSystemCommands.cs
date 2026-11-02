using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.Settings;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for civil3d_coordinate_system tool: getCoordinateSystemInfo / transformCoordinates.
///
/// Civil 3D API notes:
///   civilDoc.Settings.DrawingSettings.UnitZoneSettings exposes CoordinateSystemCode,
///   Zone, Datum, and projection properties for the active drawing's CRS.
///   AutoCAD's GeoCoordinateTransformer converts between any two coordinate systems by code.
/// </summary>
public static class CoordinateSystemCommands
{
  private const string GeographicCode = "LL84";

  // -------------------------------------------------------------------------
  // getCoordinateSystemInfo
  // -------------------------------------------------------------------------

  public static Task<object?> GetCoordinateSystemInfoAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var csCode = DrawingCode(civilDoc);
      string? zone = null;
      string? datum = null;
      string? projection = null;
      string? description = null;
      int? epsgCode = null;
      var parameters = new Dictionary<string, double>();

      if (csCode != null)
      {
        var coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(csCode);
        zone = coordinateSystem.Category;
        datum = coordinateSystem.Datum;
        projection = coordinateSystem.Projection;
        var definition = GeoCoordinateSystem.Create(csCode);
        description = definition.Description;
        epsgCode = definition.EPSGcode;
        parameters = ProjectionParameters(csCode);
      }

      return new Dictionary<string, object?>
      {
        ["name"] = csCode,
        ["zone"] = zone,
        ["datum"] = datum,
        ["projection"] = projection,
        ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
        ["description"] = description,
        ["epsgCode"] = epsgCode,
        ["centralMeridian"] = Parameter(parameters, "natural origin") ?? Parameter(parameters, "central meridian"),
        ["falseEasting"] = Parameter(parameters, "false easting"),
        ["falseNorthing"] = Parameter(parameters, "false northing"),
        ["scaleFactor"] = Parameter(parameters, "scale"),
        ["projectionParameters"] = parameters,
      };
    });
  }

  // -------------------------------------------------------------------------
  // transformCoordinates
  // -------------------------------------------------------------------------

  public static Task<object?> TransformCoordinatesAsync(JsonObject? parameters)
  {
    var fromSystem = PluginRuntime.GetRequiredString(parameters, "fromSystem");
    var toSystem = PluginRuntime.GetRequiredString(parameters, "toSystem");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var z = PluginRuntime.GetOptionalDouble(parameters, "z") ?? 0.0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var drawingCode = DrawingCode(civilDoc);
      var source = SystemCode(fromSystem, drawingCode);
      var target = SystemCode(toSystem, drawingCode);
      if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
      {
        return new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["z"] = z,
          ["fromSystem"] = fromSystem,
          ["toSystem"] = toSystem,
          ["identityTransform"] = true,
        };
      }

      try
      {
        var result = GeoCoordinateTransformer.TransformPoint(source, target, new Point3d(x, y, z));
        return new Dictionary<string, object?>
        {
          ["x"] = result.X,
          ["y"] = result.Y,
          ["z"] = result.Z,
          ["fromSystem"] = fromSystem,
          ["toSystem"] = toSystem,
          ["fromCode"] = source,
          ["toCode"] = target,
        };
      }
      catch (Autodesk.AutoCAD.Runtime.Exception exception)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Civil 3D couldn't transform from {source} to {target}: check that it knows both codes ({exception.Message}). No coordinates were returned.");
      }
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>The drawing's coordinate system code, or null. A drawing with none answers ".", not an empty string.</summary>
  private static string? DrawingCode(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    var code = civilDoc.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode;
    return string.IsNullOrWhiteSpace(code) || code == "." ? null : code;
  }

  /// <summary>A coordinate system's code. "drawing" is the drawing's own, "geographic" is WGS84 longitude and latitude.</summary>
  private static string SystemCode(string system, string? drawingCode)
  {
    if (string.Equals(system, "geographic", StringComparison.OrdinalIgnoreCase)) return GeographicCode;
    if (!string.Equals(system, "drawing", StringComparison.OrdinalIgnoreCase)) return system;
    if (drawingCode != null) return drawingCode;

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "This drawing has no coordinate system assigned, so its coordinates can't be transformed.");
  }

  /// <summary>The numbers that define a system, by the names Civil 3D gives them.</summary>
  private static Dictionary<string, double> ProjectionParameters(string code)
  {
    var parameters = new Dictionary<string, double>();
    foreach (var parameter in GeoCoordinateSystem.Create(code).GetProjectionParamList(true))
    {
      parameters[parameter.Name] = parameter.Value;
    }

    return parameters;
  }

  private static double? Parameter(Dictionary<string, double> parameters, string word)
  {
    foreach (var (name, value) in parameters)
    {
      if (name.Replace("_", " ").Contains(word, StringComparison.OrdinalIgnoreCase)) return value;
    }

    return null;
  }
}
