using System.Text.Json.Nodes;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System.Linq;
using System.Globalization;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind civil3d_section (src/tools/domains/sectionDomain.ts): sample line groups and sample lines
/// along an alignment, section views and section view groups, and a CSV export of the sampled section points. The
/// dispatcher names are <c>listSampleLineGroups</c>, <c>createSampleLines</c>, <c>getSectionData</c>,
/// <c>createSectionViews</c>, <c>listSectionViews</c>, <c>updateSectionViewStyles</c>, <c>createSectionViewGroup</c>
/// and <c>exportSectionData</c>.
/// <para>
/// Alignments, sample line groups and styles are found by name (case-insensitive). Stations, offsets and elevations
/// are in the drawing's own units; the answers name them (<c>CivilObjectUtils.LinearUnits</c>). Section view layout
/// (rows, gaps) comes from the drawing's draft placement settings, because the .NET API doesn't take it per call, so
/// asking for it is refused rather than silently ignored.
/// </para>
/// </summary>
public static class SectionCommands
{
  /// <summary>
  /// Lists the sample line groups of one alignment (<c>alignmentName</c>, required) with each group's name, handle,
  /// sample line count and the stations of its sample lines. Read only.
  /// </summary>
  public static Task<object?> ListSampleLineGroupsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var sampleLineGroups = new List<Dictionary<string, object?>>();
      var groupIds = CivilObjectUtils.InvokeMethod(alignment, "GetSampleLineGroupIds") as ObjectIdCollection;

      if (groupIds != null)
      {
        foreach (ObjectId groupId in groupIds)
        {
          var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
          var stations = new List<double>();
          foreach (ObjectId sampleLineId in group.GetSampleLineIds())
          {
            var sampleLine = CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, sampleLineId, OpenMode.ForRead);
            stations.Add(sampleLine.Station);
          }

          sampleLineGroups.Add(new Dictionary<string, object?>
          {
            ["name"] = group.Name,
            ["handle"] = CivilObjectUtils.GetHandle(group),
            ["sampleLineCount"] = stations.Count,
            ["stations"] = stations,
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["sampleLineGroups"] = sampleLineGroups,
      };
    });
  }

  /// <summary>
  /// Creates a new sample line group <c>groupName</c> on <c>alignmentName</c> and one sample line per station, each
  /// a straight line from <c>leftWidth</c> left of the alignment to <c>rightWidth</c> right of it (drawing units,
  /// both positive). Stations come from <c>stations</c> (an array, duplicates dropped, sorted) or else every
  /// <c>interval</c> from the alignment's start; with neither it refuses with CIVIL3D.INVALID_INPUT. <c>surfaces</c>
  /// (optional names) limits which section sources are sampled; without it every source is sampled. Sample lines are
  /// named "SL-" plus the station (e.g. "SL-1250"). Writes to the drawing in one undo step.
  /// </summary>
  // NOTE: the interval loop adds the interval to a running double, so rounding can drop the alignment's ending
  // station even when the length is an exact multiple of the interval; and the "SL-{station:0.##}" name uses the
  // current culture, so a comma-decimal locale gives names like "SL-12,5".
  public static Task<object?> CreateSampleLinesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var groupName = PluginRuntime.GetRequiredString(parameters, "groupName");
    var leftWidth = PluginRuntime.GetRequiredDouble(parameters, "leftWidth");
    var rightWidth = PluginRuntime.GetRequiredDouble(parameters, "rightWidth");
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval");
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var groupId = SampleLineGroup.Create(groupName, alignment.ObjectId);
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForWrite);
      var sectionSources = group.GetSectionSources();
      var requestedSurfaces = (PluginRuntime.GetParameter(parameters, "surfaces") as JsonArray)?.Select(node => node?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (SectionSource source in sectionSources)
      {
        var sourceObject = transaction.GetObject(source.SourceId, OpenMode.ForRead);
        var sourceName = CivilObjectUtils.GetName(sourceObject);
        source.IsSampled = requestedSurfaces.Count == 0 || (sourceName != null && requestedSurfaces.Contains(sourceName));
      }

      var stations = new List<double>();
      if (stationsNode != null && stationsNode.Count > 0)
      {
        stations.AddRange(stationsNode.Select(node => node?.GetValue<double>() ?? 0));
      }
      else if (interval.HasValue && interval.Value > 0)
      {
        for (var station = alignment.StartingStation; station <= alignment.EndingStation; station += interval.Value)
        {
          stations.Add(station);
        }
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createSampleLines requires either stations or interval.");
      }

      var createdStations = new List<double>();
      foreach (var station in stations.Distinct().OrderBy(value => value))
      {
        double x1 = 0;
        double y1 = 0;
        double x2 = 0;
        double y2 = 0;
        alignment.PointLocation(station, -leftWidth, ref x1, ref y1);
        alignment.PointLocation(station, rightWidth, ref x2, ref y2);
        var points = new Point2dCollection
        {
          new(x1, y1),
          new(x2, y2),
        };
        SampleLine.Create($"SL-{station:0.##}", groupId, points);
        createdStations.Add(station);
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["created"] = createdStations.Count,
        ["stations"] = createdStations,
      };
    });
  }

  /// <summary>
  /// Finds the sample line at <c>station</c> (within 0.0001) in sample line group <c>sampleLineGroupName</c> of
  /// <c>alignmentName</c> and returns its station and the drawing's units. CIVIL3D.OBJECT_NOT_FOUND when the
  /// alignment has no groups, the group isn't there or no sample line is at that station. Read only.
  /// </summary>
  // NOTE: "surfaces" in the answer is always an empty list: the section data at the sample line is never read, so
  // the caller learns only that the sample line exists (exportSectionData is the path that reads section points).
  public static Task<object?> GetSectionDataAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var groupIds = CivilObjectUtils.InvokeMethod(alignment, "GetSampleLineGroupIds") as ObjectIdCollection;
      if (groupIds == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No sample line groups exist for alignment '{alignmentName}'.");
      }

      foreach (ObjectId groupId in groupIds)
      {
        var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
        if (!string.Equals(group.Name, sampleLineGroupName, StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        var sampleLine = group.GetSampleLineIds()
          .Cast<ObjectId>()
          .Select(id => CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, id, OpenMode.ForRead))
          .FirstOrDefault(line => Math.Abs(line.Station - station) < 0.0001);

        if (sampleLine == null)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Sample line at station {station} was not found.");
        }

        return new Dictionary<string, object?>
        {
          ["station"] = sampleLine.Station,
          ["surfaces"] = new List<object>(),
          ["units"] = new Dictionary<string, object?>
          {
            ["horizontal"] = CivilObjectUtils.LinearUnits(database),
            ["vertical"] = CivilObjectUtils.LinearUnits(database),
          },
        };
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Sample line group '{sampleLineGroupName}' was not found.");
    });
  }

  // -------------------------------------------------------------------------
  // createSectionViews
  // -------------------------------------------------------------------------

  /// <summary>
  /// Creates a section view group for every sample line of <c>sampleLineGroupName</c> on <c>alignmentName</c>,
  /// placed from (<c>insertionX</c>, <c>insertionY</c>) by the drawing's draft placement settings, and applies the
  /// optional section view <c>style</c> and <c>bandSetStyle</c> to every new view. <c>leftOffset</c> /
  /// <c>rightOffset</c> (together, left less than right) set the offset range; <c>stationStart</c> /
  /// <c>stationEnd</c> limit the station range (default: the whole alignment). Refuses <c>rows</c> and
  /// <c>gapBetweenViews</c> with CIVIL3D.API_ERROR because the API has no per-call layout. Writes to the drawing.
  /// </summary>
  public static Task<object?> CreateSectionViewsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var insertionX = PluginRuntime.GetRequiredDouble(parameters, "insertionX");
    var insertionY = PluginRuntime.GetRequiredDouble(parameters, "insertionY");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var leftOffset = PluginRuntime.GetOptionalDouble(parameters, "leftOffset");
    var rightOffset = PluginRuntime.GetOptionalDouble(parameters, "rightOffset");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows");
    var gapBetweenViews = PluginRuntime.GetOptionalDouble(parameters, "gapBetweenViews");

    if (rows.HasValue || gapBetweenViews.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D SectionViewGroup draft placement uses drawing settings; per-call rows and gapBetweenViews are not exposed by the .NET API.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);
      var insertionPoint = new Point3d(insertionX, insertionY, 0);

      var createdGroup = CreateSectionViewGroup(
        alignment,
        group,
        insertionPoint,
        leftOffset,
        rightOffset,
        stationStart,
        stationEnd);
      var createdViews = OpenSectionViews(createdGroup, transaction, OpenMode.ForWrite).ToList();
      ApplySectionViewStyles(createdViews, styleId, bandSetId, applyToAll: true);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["created"] = createdViews.Count,
        ["layoutSource"] = "Civil 3D SectionViewGroup draft placement settings",
        ["insertionPoint"] = new Dictionary<string, object?>
        {
          ["x"] = insertionPoint.X,
          ["y"] = insertionPoint.Y,
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // listSectionViews
  // -------------------------------------------------------------------------

  /// <summary>
  /// Lists section views (name, handle, style, alignment and sample line group) for one alignment or, without
  /// <c>alignmentName</c>, every alignment, optionally only in <c>sampleLineGroupName</c>. Finding none is an error
  /// (CIVIL3D.OBJECT_NOT_FOUND), not an empty list. Read only.
  /// </summary>
  public static Task<object?> ListSectionViewsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetOptionalString(parameters, "sampleLineGroupName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      List<Dictionary<string, object?>> result = new();
      var alignments = new List<Alignment>();
      if (string.IsNullOrWhiteSpace(alignmentName))
      {
        alignments.AddRange(
          civilDoc.GetAlignmentIds()
            .Cast<ObjectId>()
            .Select(id => CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead)));
      }
      else
      {
        alignments.Add(CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName));
      }

      foreach (var alignment in alignments)
      {
        foreach (var group in ListSampleLineGroups(alignment, transaction, sampleLineGroupName))
        {
          foreach (var view in EnumerateSectionViews(group, transaction))
          {
            result.Add(MapSectionViewSummary(view, group, alignment));
          }
        }
      }

      if (result.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No section views found for the requested filters.");
      }

      return new Dictionary<string, object?>
      {
        ["sectionViews"] = result,
        ["count"] = result.Count,
      };
    });
  }

  // -------------------------------------------------------------------------
  // updateSectionViewStyles
  // -------------------------------------------------------------------------

  /// <summary>
  /// Sets the section view <c>style</c> and/or imports the band set <c>bandSetStyle</c> (at least one required,
  /// CIVIL3D.INVALID_INPUT otherwise) on the section views of <c>sampleLineGroupName</c>. <c>applyToAll</c>
  /// (default true) false changes only the first view. Returns how many views changed. Writes to the drawing.
  /// </summary>
  public static Task<object?> UpdateSectionViewStylesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var applyToAll = PluginRuntime.GetOptionalBool(parameters, "applyToAll") ?? true;

    if (style == null && bandSetStyle == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "updateSectionViewStyles requires 'style' or 'bandSetStyle'.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = string.IsNullOrWhiteSpace(style) ? ObjectId.Null : LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);

      var sectionViews = EnumerateSectionViews(group, transaction).ToList();
      if (sectionViews.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No section views exist for sample line group '{sampleLineGroupName}'.");
      }

      var styleUpdated = ApplySectionViewStyles(sectionViews, styleId, bandSetId, applyToAll);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["updated"] = styleUpdated,
      };
    });
  }

  // -------------------------------------------------------------------------
  // createSectionViewGroup
  // -------------------------------------------------------------------------

  /// <summary>
  /// Like <see cref="CreateSectionViewsAsync"/> over the whole alignment, but also sets the group's
  /// <c>plotStyle</c> (group plot style name) and takes no band set or ranges. Refuses <c>rows</c>, <c>columns</c>,
  /// <c>gapX</c> and <c>gapY</c> with CIVIL3D.API_ERROR (draft placement uses the drawing's settings). Writes to the
  /// drawing.
  /// </summary>
  public static Task<object?> CreateSectionViewGroupAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var insertionX = PluginRuntime.GetRequiredDouble(parameters, "insertionX");
    var insertionY = PluginRuntime.GetRequiredDouble(parameters, "insertionY");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var plotStyle = PluginRuntime.GetOptionalString(parameters, "plotStyle");
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows");
    var columns = PluginRuntime.GetOptionalInt(parameters, "columns");
    var gapX = PluginRuntime.GetOptionalDouble(parameters, "gapX");
    var gapY = PluginRuntime.GetOptionalDouble(parameters, "gapY");

    if (rows.HasValue || columns.HasValue || gapX.HasValue || gapY.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D SectionViewGroup draft placement uses drawing settings; per-call rows, columns, gapX, and gapY are not exposed by the .NET API.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var plotStyleId = LookupUtils.GetGroupPlotStyleId(civilDoc, transaction, plotStyle);
      var insertionPoint = new Point3d(insertionX, insertionY, 0);

      var createdGroup = CreateSectionViewGroup(
        alignment,
        group,
        insertionPoint,
        null,
        null,
        null,
        null);
      if (plotStyleId != ObjectId.Null)
      {
        createdGroup.PlotStyleId = plotStyleId;
      }
      var createdViews = OpenSectionViews(createdGroup, transaction, OpenMode.ForWrite).ToList();
      ApplySectionViewStyles(createdViews, styleId, ObjectId.Null, applyToAll: true);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["created"] = createdViews.Count,
        ["layoutSource"] = "Civil 3D SectionViewGroup draft placement settings",
        ["insertionPoint"] = new Dictionary<string, object?>
        {
          ["x"] = insertionPoint.X,
          ["y"] = insertionPoint.Y,
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // exportSectionData
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes a CSV of every section point of every sample line in <c>sampleLineGroupName</c> (optionally only
  /// between <c>stationStart</c> and <c>stationEnd</c>): station, section source name, offset and, unless
  /// <c>includeElevations</c> is false, elevation, in drawing units with invariant-culture numbers. The file goes to
  /// <c>outputPath</c> through <c>FileBoundary.WriteAllTextAtomic</c> (allowed roots, <c>.csv</c> only, no overwrite
  /// without <c>overwrite: true</c>). <c>includeMaterials</c> is refused (CIVIL3D.INVALID_INPUT) before anything is
  /// written. Reads the drawing only; the one side effect is the file.
  /// </summary>
  public static Task<object?> ExportSectionDataAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var includeElevations = PluginRuntime.GetOptionalBool(parameters, "includeElevations") ?? true;
    var includeMaterials = PluginRuntime.GetOptionalBool(parameters, "includeMaterials") ?? false;
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");

    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    if (includeMaterials)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Section material quantities are not exposed by this export path. Set includeMaterials=false; no file was written.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var csv = new StringBuilder("Station,Source,Offset");
      if (includeElevations) csv.Append(",Elevation");
      csv.AppendLine();
      var rowsWritten = 0;

      foreach (ObjectId sampleLineId in group.GetSampleLineIds())
      {
        var sampleLine = CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, sampleLineId, OpenMode.ForRead);
        if (stationStart.HasValue && sampleLine.Station < stationStart.Value) continue;
        if (stationEnd.HasValue && sampleLine.Station > stationEnd.Value) continue;

        foreach (ObjectId sectionId in sampleLine.GetSectionIds())
        {
          var section = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Section>(transaction, sectionId, OpenMode.ForRead);
          foreach (SectionPoint point in section.SectionPoints)
          {
            csv.Append(sampleLine.Station.ToString("G17", CultureInfo.InvariantCulture))
              .Append(',').Append(EscapeCsv(section.SourceName))
              .Append(',').Append(point.Location.X.ToString("G17", CultureInfo.InvariantCulture));
            if (includeElevations)
              csv.Append(',').Append(point.Location.Y.ToString("G17", CultureInfo.InvariantCulture));
            csv.AppendLine();
            rowsWritten++;
          }
        }
      }

      var canonicalPath = FileBoundary.WriteAllTextAtomic(
        outputPath, csv.ToString(), Encoding.UTF8, overwrite, ".csv");
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["outputPath"] = canonicalPath,
        ["rowsWritten"] = rowsWritten,
        ["includeElevations"] = includeElevations,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  /// <summary>Quotes a CSV field that holds a comma, quote or line break, doubling any quotes inside.</summary>
  private static string EscapeCsv(string value)
  {
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
      return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
  }

  private static SampleLineGroup FindSampleLineGroup(Alignment alignment, Transaction transaction, string sampleLineGroupName)
  {
    var groupIds = alignment.GetSampleLineGroupIds();
    if (groupIds.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No sample line groups exist for alignment '{alignment.Name}'.");
    }

    foreach (ObjectId groupId in groupIds)
    {
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
      if (string.Equals(group.Name, sampleLineGroupName, StringComparison.OrdinalIgnoreCase))
      {
        return group;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Sample line group '{sampleLineGroupName}' was not found.");
  }

  private static IEnumerable<SampleLineGroup> ListSampleLineGroups(Alignment alignment, Transaction transaction, string? sampleLineGroupName = null)
  {
    var groupIds = alignment.GetSampleLineGroupIds();
    if (groupIds.Count == 0)
    {
      return Enumerable.Empty<SampleLineGroup>();
    }

    var result = new List<SampleLineGroup>();
    foreach (ObjectId groupId in groupIds)
    {
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
      if (string.IsNullOrWhiteSpace(sampleLineGroupName) || string.Equals(group.Name, sampleLineGroupName, StringComparison.OrdinalIgnoreCase))
      {
        result.Add(group);
      }
    }

    return result;
  }

  private static IEnumerable<SectionView> EnumerateSectionViews(SampleLineGroup group, Transaction transaction)
  {
    foreach (SectionViewGroup sectionViewGroup in group.SectionViewGroups)
    {
      foreach (var sectionView in OpenSectionViews(sectionViewGroup, transaction, OpenMode.ForRead))
      {
        yield return sectionView;
      }
    }
  }

  private static IEnumerable<SectionView> OpenSectionViews(SectionViewGroup group, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId viewId in group.GetSectionViewIds())
    {
      if (viewId != ObjectId.Null)
      {
        yield return CivilObjectUtils.GetRequiredObject<SectionView>(transaction, viewId, openMode);
      }
    }
  }

  /// <summary>
  /// Adds a section view group to <paramref name="group"/> with draft placement at <paramref name="insertionPoint"/>,
  /// over the given station range (default: the alignment's start and end) and, when both are given, offset range.
  /// Refuses one offset without the other, or left not less than right, with CIVIL3D.INVALID_INPUT.
  /// </summary>
  private static SectionViewGroup CreateSectionViewGroup(
    Alignment alignment,
    SampleLineGroup group,
    Point3d insertionPoint,
    double? leftOffset,
    double? rightOffset,
    double? stationStart,
    double? stationEnd)
  {
    if (leftOffset.HasValue != rightOffset.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "leftOffset and rightOffset must be supplied together.");
    }
    if (leftOffset.HasValue && leftOffset.Value >= rightOffset!.Value)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "leftOffset must be less than rightOffset for Civil 3D section-view ranges.");
    }

    using var rangeOptions = new SectionViewGroupCreationRangeOptions(group.ObjectId);
    if (leftOffset.HasValue)
    {
      rangeOptions.SetOffsetRange(leftOffset.Value, rightOffset!.Value);
    }
    var placementOptions = new SectionViewGroupCreationPlacementOptions();
    placementOptions.UseDraftPlacement();
    var start = stationStart ?? alignment.StartingStation;
    var end = stationEnd ?? alignment.EndingStation;
    return group.SectionViewGroups.Add(insertionPoint, start, end, rangeOptions, placementOptions);
  }

  /// <summary>
  /// Sets the style and/or imports the band set (either may be <c>ObjectId.Null</c> to leave it) on every view, or
  /// only the first when <paramref name="applyToAll"/> is false, opening views for write as needed. Returns how many
  /// views changed.
  /// </summary>
  private static int ApplySectionViewStyles(
    IReadOnlyList<SectionView> sectionViews,
    ObjectId styleId,
    ObjectId bandSetStyleId,
    bool applyToAll)
  {
    int updated = 0;
    var stylesToProcess = applyToAll ? sectionViews : sectionViews.Take(1);
    foreach (var view in stylesToProcess)
    {
      if (!view.IsWriteEnabled)
      {
        view.UpgradeOpen();
      }

      var changed = false;
      if (styleId != ObjectId.Null)
      {
        view.StyleId = styleId;
        changed = true;
      }

      if (bandSetStyleId != ObjectId.Null)
      {
        view.Bands.ImportBandSetStyle(bandSetStyleId);
        changed = true;
      }

      if (changed)
      {
        updated++;
      }
    }

    return updated;
  }

  private static Dictionary<string, object?> MapSectionViewSummary(SectionView view, SampleLineGroup group, Alignment alignment)
  {
    return new Dictionary<string, object?>
    {
      ["alignmentName"] = alignment.Name,
      ["sampleLineGroupName"] = group.Name,
      ["name"] = view.Name,
      ["handle"] = CivilObjectUtils.GetHandle(view),
      ["style"] = view.StyleName,
    };
  }

}
