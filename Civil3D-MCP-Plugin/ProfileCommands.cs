using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind civil3d_profile's basic read, create and delete actions (src/tools/domains/profileDomain.ts;
/// pipeDomain.ts's <c>automate_profile_view</c> also lists profiles and makes a surface profile): <c>listProfiles</c>,
/// <c>getProfile</c>, <c>getProfileElevation</c>, <c>sampleProfileElevations</c>, <c>createProfileFromSurface</c>,
/// <c>createLayoutProfile</c> and <c>deleteProfile</c>. PVI and curve editing is in ProfileEditCommands.cs.
/// <para>
/// A profile is found by alignment name and profile name. Stations and elevations are in the drawing's units, and
/// grades are as Civil 3D returns them (<c>Profile.GradeAt</c>, <c>ProfileTangent.Grade</c>).
/// </para>
/// </summary>
public static class ProfileCommands
{
  /// <summary>
  /// The profiles of <c>alignmentName</c>: name, handle, type (surface, layout or superimposed), station range and
  /// elevation range. <c>style</c> is always empty here (getProfile reads it). Read only.
  /// </summary>
  public static Task<object?> ListProfilesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead))
        .Select(ToProfileSummary)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profiles"] = profiles,
      };
    });
  }

  /// <summary>
  /// One profile in detail: type, style, layer, station and elevation ranges, PVI count and every entity (tangent,
  /// parabola, asymmetric parabola or circular curve) with its stations, elevations, length and, for tangents,
  /// grade. Read only.
  /// </summary>
  public static Task<object?> GetProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var entities = ReadProfileEntities(profile);

      return new Dictionary<string, object?>
      {
        ["name"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["type"] = MapProfileType(profile.ProfileType.ToString()),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(profile.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = profile.Layer,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = GetElevationExtents(profile).Min,
        ["maxElevation"] = GetElevationExtents(profile).Max,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["pviCount"] = CountPvis(profile),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = CivilObjectUtils.LinearUnits(database),
          ["vertical"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  /// <summary>
  /// Elevation and grade of a profile at one <c>station</c>. A station outside the profile (or one Civil 3D can't
  /// evaluate) is CIVIL3D.API_ERROR with Civil 3D's message. Read only.
  /// </summary>
  public static Task<object?> GetProfileElevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var elevation = ReadProfileElevation(profile, station);
      var grade = ReadProfileGrade(profile, station);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["elevation"] = elevation,
        ["grade"] = grade,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // The most samples one sampleProfileElevations call returns.
  private const int MaxProfileSamples = 100_000;

  /// <summary>
  /// Elevation and grade every <c>interval</c> from <c>startStation</c> to <c>endStation</c> (default: the profile's
  /// own range). The end station is included only when it falls on the interval. Used by civil3d_profile's
  /// <c>sample_elevations</c> and <c>report</c> actions. Read only.
  /// </summary>
  // An interval of zero or less would make the loop below run forever on the AutoCAD main thread, so it is refused
  // (here and in profileDomain.ts's schema), and so is one giving more than MaxProfileSamples samples.
  public static Task<object?> SampleProfileElevationsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var interval = PluginRuntime.GetRequiredDouble(parameters, "interval");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    if (!(interval > 0) || double.IsInfinity(interval))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'interval' must be a positive number (got {interval}).");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var from = startStation ?? profile.StartingStation;
      var to = endStation ?? profile.EndingStation;
      if ((to - from) / interval > MaxProfileSamples)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"An interval of {interval} gives more than {MaxProfileSamples:N0} samples from {from} to {to}; use a larger one.");
      }

      var samples = new List<Dictionary<string, object?>>();

      foreach (var station in StationMath.IntervalStations(from, to, interval))
      {
        samples.Add(new Dictionary<string, object?>
        {
          ["station"] = station,
          ["elevation"] = ReadProfileElevation(profile, station),
          ["grade"] = ReadProfileGrade(profile, station),
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["startStation"] = from,
        ["endStation"] = to,
        ["interval"] = interval,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  /// <summary>
  /// Creates a surface (existing ground) profile <c>profileName</c> of <c>surfaceName</c> along
  /// <c>alignmentName</c>, with the optional <c>layer</c>, <c>style</c> and <c>labelSet</c> names (defaults from
  /// LookupUtils otherwise). Writes to the drawing.
  /// </summary>
  public static Task<object?> CreateProfileFromSurfaceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  /// <summary>
  /// Creates an empty layout (design) profile <c>profileName</c> on <c>alignmentName</c>, ready for PVIs to be added
  /// (ProfileEditCommands), with the optional <c>layer</c>, <c>style</c> and <c>labelSet</c>. Writes to the drawing.
  /// </summary>
  public static Task<object?> CreateLayoutProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  /// <summary>
  /// Erases one profile of an alignment. Anything that depends on it (profile view graphs, corridor baselines) is
  /// Civil 3D's to handle; the approval policy requires an approval token for this delete. Writes to the drawing.
  /// </summary>
  public static Task<object?> DeleteProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      profile.Erase();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profileName,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToProfileSummary(Profile profile)
  {
    var extents = GetElevationExtents(profile);
    return new Dictionary<string, object?>
    {
      ["name"] = profile.Name,
      ["handle"] = CivilObjectUtils.GetHandle(profile),
      ["type"] = MapProfileType(profile.ProfileType.ToString()),
      ["style"] = string.Empty,
      ["startStation"] = profile.StartingStation,
      ["endStation"] = profile.EndingStation,
      ["minElevation"] = extents.Min,
      ["maxElevation"] = extents.Max,
    };
  }

  private static List<Dictionary<string, object?>> ReadProfileEntities(Profile profile)
  {
    var entities = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (ProfileEntity entity in profile.Entities)
    {
      entities.Add(new Dictionary<string, object?>
      {
        ["index"] = index++,
        ["type"] = MapProfileEntityType(entity.EntityType.ToString()),
        ["startStation"] = entity.StartStation,
        ["endStation"] = entity.EndStation,
        ["startElevation"] = entity.StartElevation,
        ["endElevation"] = entity.EndElevation,
        ["grade"] = entity is ProfileTangent tangent ? tangent.Grade : null,
        ["length"] = entity.Length,
      });
    }

    return entities;
  }

  private static (double Min, double Max) GetElevationExtents(Profile profile)
  {
    // Civil 3D calculates profile-wide extents, including interior vertical
    // curve extrema and the numeric fallback required for empty layouts.
    return (profile.ElevationMin, profile.ElevationMax);
  }

  private static int CountPvis(Profile profile)
  {
    return profile.PVIs.Count;
  }

  private static double ReadProfileElevation(Profile profile, double station)
  {
    try
    {
      return profile.ElevationAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read elevation on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static double ReadProfileGrade(Profile profile, double station)
  {
    try
    {
      return profile.GradeAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read grade on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  /// <summary>Civil 3D's profile type name mapped to "surface", "superimposed" or (anything else) "layout".</summary>
  // NOTE: this looks for "surface" in the ProfileType enum name; if surface profiles report as "EG" (existing
  // ground), as the enum names them, they are reported as "layout" here.
  private static string MapProfileType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("surface"))
    {
      return "surface";
    }

    if (text.Contains("super"))
    {
      return "superimposed";
    }

    return "layout";
  }

  /// <summary>Civil 3D's profile entity type name mapped to the tool's names: tangent, parabola, asymmetric_parabola, circular_curve.</summary>
  private static string MapProfileEntityType(string value)
  {
    var text = value.ToLowerInvariant();
    if (text.Contains("asymmetric"))
    {
      return "asymmetric_parabola";
    }

    if (text.Contains("parabola"))
    {
      return "parabola";
    }

    if (text.Contains("curve"))
    {
      return "circular_curve";
    }

    return "tangent";
  }
}
