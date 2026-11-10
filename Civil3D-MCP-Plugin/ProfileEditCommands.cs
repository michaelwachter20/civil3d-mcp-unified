// Profile editing and profile view handlers for civil3d_profile (src/tools/domains/profileDomain.ts); pipeDomain.ts's
// automate_profile_view also calls profileViewCreate.
// JSON-RPC methods: profileAddPvi, profileDeletePvi, profileAddCurve, profileSetGrade, profileViewCreate,
// profileViewBandSet (writes, CivilExecution.WriteAsync: command context, document lock, one transaction, one undo
// step); profileGetElevation (same as ProfileCommands.getProfileElevation) and profileCheckKValues (reads,
// CivilExecution.ReadAsync: main thread, no undo step). MinimumK (AASHTO K tables) is also used by SightDistanceCommands.
// Alignments and profiles are found by name through CivilObjectUtils, styles through LookupUtils. Curve properties in
// the K check are read late-bound through CivilObjectUtils. No file access.
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D vertical profiles and profile views:
/// add_pvi, delete_pvi, add_curve, set_grade, get_elevation,
/// check_k_values, profile_view_create, profile_view_band_set.
/// </summary>
public static class ProfileEditCommands
{
  // ─── profileAddPvi ────────────────────────────────────────────────────────

  /// <summary>
  /// profileAddPvi (changes the drawing, one undo step): adds a PVI at <c>station</c> and <c>elevation</c> to profile
  /// <c>profileName</c> of <c>alignmentName</c> (all required). Returns the values and success. Throws
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment or profile, CIVIL3D.INVALID_INPUT for a station off the
  /// alignment, CIVIL3D.CONFLICT when a PVI is already within 0.001 of the station.
  /// </summary>
  public static Task<object?> AddPviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var elevation = PluginRuntime.GetRequiredDouble(parameters, "elevation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      // Civil 3D says only "Value does not fall within the expected range" for both of these.
      AlignmentCommands.RequireStation(alignment, station);
      foreach (ProfilePVI pvi in profile.PVIs)
      {
        if (Math.Abs(pvi.RawStation - station) < 0.001)
        {
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Profile '{profile.Name}' already has a PVI at station {station}, elevation {pvi.Elevation}.");
        }
      }

      profile.PVIs.AddPVI(station, elevation);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["station"] = station,
        ["elevation"] = elevation,
        ["success"] = true,
      };
    });
  }

  // ─── profileDeletePvi ─────────────────────────────────────────────────────

  /// <summary>profileDeletePvi (changes the drawing, one undo step): removes the PVI within 1 unit of
  /// <c>station</c> (with <c>alignmentName</c> and <c>profileName</c>, all required). Throws CIVIL3D.OBJECT_NOT_FOUND
  /// for an unknown alignment or profile, or when no PVI is that close (naming the nearest).</summary>
  public static Task<object?> DeletePviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      var targetPvi = FindPviNearStation(profile.PVIs, station)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No PVI found near station {station} in profile '{profileName}'.");
      profile.PVIs.RemoveAt(targetPvi.RawStation, targetPvi.Elevation);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["station"] = station,
        ["success"] = true,
      };
    });
  }

  // ─── profileAddCurve ──────────────────────────────────────────────────────

  /// <summary>profileAddCurve (changes the drawing, one undo step): adds a free symmetric parabola of
  /// <c>length</c> at the PVI within 1 unit of <c>pviStation</c> (with <c>alignmentName</c>, <c>profileName</c>; all
  /// required). <c>curveType</c> other than symmetric_parabola (the default) is refused with CIVIL3D.API_ERROR.
  /// Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment, profile or PVI.</summary>
  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var pviStation = PluginRuntime.GetRequiredDouble(parameters, "pviStation");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");
    var curveType = PluginRuntime.GetOptionalString(parameters, "curveType") ?? "symmetric_parabola";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      if (!string.Equals(curveType, "symmetric_parabola", StringComparison.OrdinalIgnoreCase))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.API_ERROR",
          $"Curve type '{curveType}' is not implemented. Civil 3D's typed API path currently supports symmetric_parabola only.");
      }

      var targetPvi = FindPviNearStation(profile.PVIs, pviStation);
      if (targetPvi == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.OBJECT_NOT_FOUND",
          $"No PVI found near station {pviStation} in profile '{profileName}'.");
      }

      profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(targetPvi, length);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["pviStation"] = pviStation,
        ["curveLength"] = length,
        ["curveType"] = curveType,
        ["success"] = true,
      };
    });
  }

  // ─── profileSetGrade ──────────────────────────────────────────────────────

  /// <summary>
  /// profileSetGrade (changes the drawing, one undo step): gives tangent <c>entityIndex</c> of the profile the
  /// <c>grade</c> (a fraction, 0.02 = 2%) by moving the elevation of the PVI at its down-station end; the next
  /// tangent's grade changes with it. All of <c>alignmentName</c>, <c>profileName</c>, <c>entityIndex</c>,
  /// <c>grade</c> are required. The index counts profile.Entities in Civil 3D's own order (not sorted by station).
  /// Returns the PVI station and its previous and new elevation. Throws CIVIL3D.INVALID_INPUT for a grade above 1 in
  /// size, an index out of range or an entity that is a curve, CIVIL3D.API_ERROR when no PVIs are found at its ends.
  /// </summary>
  public static Task<object?> SetGradeAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));
    var grade = PluginRuntime.GetRequiredDouble(parameters, "grade");

    if (Math.Abs(grade) > 1)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Grade is a fraction, as get shows it: 2% is 0.02, not {grade}.");
    }

    // A tangent's grade can't be set directly (Civil 3D keeps it read-only): it comes from the PVIs at its ends, so the
    // PVI up station stays and the one down station moves. The next tangent's grade changes with it.
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      var entities = profile.Entities.Cast<ProfileEntity>().ToList();
      if (entityIndex < 0 || entityIndex >= entities.Count)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Entity index {entityIndex} is out of range: profile '{profile.Name}' has {entities.Count} entities.");
      }

      if (entities[entityIndex] is not ProfileTangent tangent)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Entity {entityIndex} of profile '{profile.Name}' is a curve: a grade belongs to a tangent.");
      }

      var pvis = profile.PVIs.Cast<ProfilePVI>().OrderBy(pvi => pvi.RawStation).ToList();
      var before = pvis.LastOrDefault(pvi => pvi.RawStation <= tangent.StartStation + 0.001);
      var after = pvis.FirstOrDefault(pvi => pvi.RawStation >= tangent.EndStation - 0.001);
      if (before == null || after == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"No PVIs found at the ends of entity {entityIndex} of profile '{profile.Name}'.");
      }

      var previousElevation = after.Elevation;
      var elevation = before.Elevation + grade * (after.RawStation - before.RawStation);
      after.Elevation = elevation;
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["entityIndex"] = entityIndex,
        ["grade"] = grade,
        ["pviStation"] = after.RawStation,
        ["previousElevation"] = previousElevation,
        ["elevation"] = elevation,
        ["success"] = true,
      };
    });
  }

  // ─── profileGetElevation ──────────────────────────────────────────────────

  /// <summary>
  /// profileGetElevation: the same read as ProfileCommands.GetProfileElevationAsync (getProfileElevation), with the
  /// same parameters, answer and errors, under its own method name for the profile editing actions.
  /// </summary>
  public static Task<object?> GetElevationAsync(JsonObject? parameters)
  {
    return ProfileCommands.GetProfileElevationAsync(parameters);
  }

  // ─── profileCheckKValues ──────────────────────────────────────────────────

  /// <summary>
  /// profileCheckKValues (read-only): for each vertical curve of the profile, K = length / |grade change in %| against
  /// the AASHTO stopping-sight-distance minimum for <c>designSpeed</c> (mph and feet, or km/h and metres when the
  /// drawing's INSUNITS is metres). <c>alignmentName</c>, <c>profileName</c>, <c>designSpeed</c> required. Returns the
  /// minimums, one row per curve (sag when the grade rises, else crest) and allPass. Throws CIVIL3D.OBJECT_NOT_FOUND
  /// for an unknown alignment or profile, CIVIL3D.TRANSACTION_FAILED when the profile has no Entities collection.
  /// </summary>
  public static Task<object?> CheckKValuesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);

      var entities = CivilObjectUtils.GetPropertyValue<object>(profile, "Entities");
      if (entities == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Profile '{profileName}' does not expose an Entities collection.");
      }

      // K is length per percent of grade change, so the table follows the drawing's units: feet and mph, or metres
      // and km/h. Both are stopping sight distance values (AASHTO Green Book 2011, Exhibits 3-72 and 3-75).
      var metric = database.Insunits == UnitsValue.Meters;
      var (kSagMin, kCrestMin) = MinimumK(designSpeed, metric);

      var results = new List<Dictionary<string, object?>>();
      var index = 0;
      foreach (var entity in (System.Collections.IEnumerable)entities)
      {
        var entityType = entity?.GetType().Name ?? string.Empty;
        var isCurve = entityType.ToLowerInvariant().Contains("parabola")
          || entityType.ToLowerInvariant().Contains("circular")
          || entityType.ToLowerInvariant().Contains("curve");
        if (!isCurve)
        {
          index++;
          continue;
        }

        var curveLength = CivilObjectUtils.GetPropertyValue<double?>(entity, "Length") ?? 0;
        var gradeIn = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeIn")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "StartGrade") ?? 0;
        var gradeOut = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeOut")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "EndGrade") ?? 0;
        // Civil 3D gives grades as ratios (0.02 = 2%); K is per percent.
        var algebraicDiff = Math.Abs(gradeOut - gradeIn) * 100;
        var kValue = algebraicDiff > 1e-10 ? curveLength / algebraicDiff : double.PositiveInfinity;

        var isSag = gradeOut > gradeIn;
        var requiredK = isSag ? kSagMin : kCrestMin;
        var passes = kValue >= requiredK || double.IsPositiveInfinity(kValue);

        results.Add(new Dictionary<string, object?>
        {
          ["entityIndex"] = index,
          ["curveType"] = isSag ? "sag" : "crest",
          ["curveLength"] = curveLength,
          ["gradeIn"] = gradeIn,
          ["gradeOut"] = gradeOut,
          ["algebraicDifference"] = algebraicDiff,
          ["kValue"] = double.IsPositiveInfinity(kValue) ? null : (object?)kValue,
          ["requiredK"] = requiredK,
          ["passes"] = passes,
        });
        index++;
      }

      var allPass = results.All(r => (bool)(r["passes"] ?? false));
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["designSpeed"] = designSpeed,
        ["designSpeedUnits"] = metric ? "km/h" : "mph",
        ["kSagMinimum"] = kSagMin,
        ["kCrestMinimum"] = kCrestMin,
        ["curves"] = results,
        ["allPass"] = allPass,
        ["summary"] = allPass
          ? $"All {results.Count} vertical curve(s) meet minimum K values for {designSpeed} design speed."
          : $"{results.Count(r => !(bool)(r["passes"] ?? false))} of {results.Count} curve(s) fail minimum K value requirements.",
      };
    });
  }

  // ─── profileViewCreate ────────────────────────────────────────────────────

  /// <summary>
  /// profileViewCreate (changes the drawing, one undo step): a profile view <c>profileViewName</c> of
  /// <c>alignmentName</c> at <c>insertX</c>/<c>insertY</c> (all required), with optional <c>style</c> and
  /// <c>bandSet</c> (default: the drawing's first band set). Returns profileViewName, handle, alignmentName and the
  /// insertion point. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment, style or band set.
  /// </summary>
  public static Task<object?> ProfileViewCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var insertionPoint = new Point3d(insertX, insertY, 0);

      var styleId = LookupUtils.GetProfileViewStyleId(
        civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var bandSetName = PluginRuntime.GetOptionalString(parameters, "bandSet");
      var bandSetId = string.IsNullOrWhiteSpace(bandSetName)
        ? civilDoc.Styles.ProfileViewBandSetStyles.First()
        : LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);

      var pvId = ProfileView.Create(alignment.ObjectId, insertionPoint, profileViewName, bandSetId, styleId);
      var profileView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, pvId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profileView),
        ["alignmentName"] = alignment.Name,
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["success"] = true,
      };
    });
  }

  // ─── profileViewBandSet ───────────────────────────────────────────────────

  /// <summary>profileViewBandSet (changes the drawing, one undo step): imports band set style <c>bandSetName</c> into
  /// the model-space profile view <c>profileViewName</c> (both required). Throws CIVIL3D.OBJECT_NOT_FOUND for an
  /// unknown view or band set.</summary>
  public static Task<object?> ProfileViewBandSetAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var bandSetName = PluginRuntime.GetRequiredString(parameters, "bandSetName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = FindProfileViewByName(civilDoc, transaction, profileViewName);
      var writeView = CivilObjectUtils.GetRequiredObject<ProfileView>(
        transaction, profileView.ObjectId, OpenMode.ForWrite);

      var bandSetId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);

      writeView.Bands.ImportBandSetStyle(bandSetId);

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["bandSetName"] = bandSetName,
        ["success"] = true,
      };
    });
  }

  // ─── Private helpers ─────────────────────────────────────────────────────

  /// <summary>The profile view named <paramref name="name"/> (ignoring case) in model space, open for read;
  /// CIVIL3D.OBJECT_NOT_FOUND otherwise.</summary>
  private static ProfileView FindProfileViewByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    // Profile views live in model space; enumerate all ProfileView objects
    var database = CivilObjectUtils.GetDatabase(civilDoc);
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(
      transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
      transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

    foreach (ObjectId objectId in modelSpace)
    {
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (obj is ProfileView pv
        && string.Equals(pv.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return pv;
      }
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.OBJECT_NOT_FOUND",
      $"Profile view '{name}' was not found in model space.");
  }

  /// <summary>
  /// The PVI at a station, within <see cref="PviTolerance"/> drawing units. Further off is an error, not the nearest
  /// one: "delete the PVI at 500" must not delete the one at 1000. Null only when the profile has no PVIs; throws
  /// CIVIL3D.OBJECT_NOT_FOUND naming the nearest when none is close enough.
  /// </summary>
  private static ProfilePVI? FindPviNearStation(ProfilePVICollection pvis, double targetStation)
  {
    ProfilePVI? closest = null;
    var minDist = double.MaxValue;

    foreach (ProfilePVI pvi in pvis)
    {
      var dist = Math.Abs(pvi.RawStation - targetStation);
      if (dist < minDist)
      {
        minDist = dist;
        closest = pvi;
      }
    }

    if (closest != null && minDist > PviTolerance)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.OBJECT_NOT_FOUND",
        $"No PVI at station {targetStation}: the nearest is at {closest.RawStation}.");
    }

    return closest;
  }

  private const double PviTolerance = 1.0;

  /// <summary>The stopping sight distance K for a design speed: km/h and metres, or mph and feet. Interpolated
  /// between table speeds; below or above the table it takes the first or last row.</summary>
  internal static (double Sag, double Crest) MinimumK(double designSpeed, bool metric) =>
    LookupKValues(metric ? BuildAashtoKTableMetric() : BuildAashtoKTableImperial(), designSpeed);

  /// <summary>AASHTO minimum K for stopping sight distance, metric: (km/h, sag, crest), K in metres per percent.</summary>
  private static List<(double speed, double kSag, double kCrest)> BuildAashtoKTableMetric() =>
  [
    (20, 3, 1),
    (30, 6, 2),
    (40, 9, 4),
    (50, 13, 7),
    (60, 18, 11),
    (70, 23, 17),
    (80, 30, 26),
    (90, 38, 39),
    (100, 45, 52),
    (110, 55, 74),
    (120, 63, 95),
    (130, 73, 124),
  ];

  /// <summary>AASHTO minimum K for stopping sight distance, US customary: (mph, sag, crest), K in feet per percent.</summary>
  private static List<(double speed, double kSag, double kCrest)> BuildAashtoKTableImperial() =>
  [
    (15, 10, 3),
    (20, 17, 7),
    (25, 26, 12),
    (30, 37, 19),
    (35, 49, 29),
    (40, 64, 44),
    (45, 79, 61),
    (50, 96, 84),
    (55, 115, 114),
    (60, 136, 151),
    (65, 157, 193),
    (70, 181, 247),
    (75, 206, 312),
    (80, 231, 384),
  ];

  /// <summary>The row within 0.5 of the speed, else linear interpolation between the rows around it, else the
  /// table's first or last row.</summary>
  private static (double kSag, double kCrest) LookupKValues(
    List<(double speed, double kSag, double kCrest)> table,
    double designSpeed)
  {
    // Find exact match first
    var exact = table.FirstOrDefault(t => Math.Abs(t.speed - designSpeed) < 0.5);
    if (exact != default)
    {
      return (exact.kSag, exact.kCrest);
    }

    // Interpolate between nearest values
    var lower = table.LastOrDefault(t => t.speed <= designSpeed);
    var upper = table.FirstOrDefault(t => t.speed > designSpeed);

    if (lower == default)
    {
      return (table[0].kSag, table[0].kCrest);
    }

    if (upper == default)
    {
      return (table[^1].kSag, table[^1].kCrest);
    }

    var ratio = (designSpeed - lower.speed) / (upper.speed - lower.speed);
    return (
      lower.kSag + ratio * (upper.kSag - lower.kSag),
      lower.kCrest + ratio * (upper.kCrest - lower.kCrest));
  }
}
