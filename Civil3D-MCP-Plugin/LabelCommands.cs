// Civil 3D label handlers. JSON-RPC methods (CommandDispatcher.cs): listLabelStyles, listLabels, addLabel.
// Callers: civil3d_standards (src/tools/domains/standardsDomain.ts: label_list_styles, label_list, label_add).
// listLabelStyles and listLabels run in CivilExecution.ReadAsync (main thread, no undo step). addLabel runs in
// CivilExecution.WriteAsync (command context, document lock, one transaction, one undo step) and only creates labels
// or assigns a label set (an alignment's or profile's LabelSetId); it never moves or reshapes the labeled object.
// COGO points and their labels are not handled here at all. Labels are made with the typed Create methods; late-bound
// lookups (some style collections, label ids, network members) go through Civil3DCompatibility only. No file access.
using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind the labeling actions of civil3d_standards (listLabelStyles, listLabels, addLabel): label
/// styles per object type, the labels on an object, and new labels on alignments, profiles, surfaces, pipes and
/// structures.
///
/// Every label is made with the typed Create methods: StationOffsetLabel (alignment station), StationElevationLabel
/// (profile station, in the first profile view of the profile's alignment), SurfaceElevationLabel, PipeLabel and
/// StructureLabel. The label classes the earlier late-bound path looked for (ProfileStationLabel and the like) don't
/// exist in Civil 3D 2027, so it always failed. Some style collections (label sets, pipe network label styles) and
/// label ids are still read late-bound through Civil3DCompatibility and CivilObjectUtils.
/// </summary>
public static class LabelCommands
{
  /// <summary>The label styles for <c>objectType</c> (required; alignment and profile give their label set styles,
  /// surface its spot elevation label styles, pipe its plan/profile label styles, structure its label styles, and
  /// pipe_network whatever late-bound collection exists). Any other type is CIVIL3D.INVALID_INPUT before the drawing
  /// is read; a supported type whose collection can't be found gives an empty list. Read only.</summary>
  public static Task<object?> ListLabelStylesAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    if (!new[] { "alignment", "profile", "surface", "pipe", "pipe_network", "structure" }.Contains(objectType, StringComparer.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No label styles for '{objectType}'. Use alignment, profile, surface, pipe, pipe_network or structure.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styles = GetLabelStyleCollection(civilDoc, objectType);
      var styleItems = styles
        .Select(objectId => transaction.GetObject(objectId, OpenMode.ForRead))
        .Where(style => style != null)
        .Select(style => new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(style),
          ["handle"] = style is DBObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["description"] = CivilObjectUtils.GetStringProperty(style, "Description"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["objectType"] = objectType,
        ["styles"] = styleItems,
      };
    });
  }

  /// <summary>The labels attached to object <c>objectName</c> of <c>objectType</c> (both required; alignment,
  /// profile, surface, pipe, structure or pipe_network), with their type, style and text (the override when one is
  /// set). Read only. An unknown type is CIVIL3D.INVALID_INPUT; an unknown object is CIVIL3D.OBJECT_NOT_FOUND. Labels
  /// come from whichever of GetLabelIds/LabelIds/Labels the object has, so an object with none gives an empty list.
  /// </summary>
  public static Task<object?> ListLabelsAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = ResolveTargetObject(civilDoc, transaction, objectType, objectName, OpenMode.ForRead);
      var labels = GetLabelIds(target)
        .Select(objectId => transaction.GetObject(objectId, OpenMode.ForRead))
        .Where(label => label != null)
        .Select(label => new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(label),
          ["handle"] = label is DBObject dbObject ? CivilObjectUtils.GetHandle(dbObject) : null,
          ["type"] = label?.GetType().Name,
          ["style"] = ResolveObjectName(transaction, GetObjectId(label, "StyleId")),
          ["text"] = CivilObjectUtils.GetStringProperty(label, "TextOverride") ?? CivilObjectUtils.GetStringProperty(label, "Text"),
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["objectType"] = objectType,
        ["objectName"] = objectName,
        ["labels"] = labels,
      };
    });
  }

  /// <summary>
  /// Adds a label to object <c>objectName</c> of <c>objectType</c>. Supported combinations: alignment or profile with
  /// <c>labelType = "label_set"</c> (assigns the label set named by <c>labelStyle</c>, replacing the current one),
  /// alignment or profile <c>station</c> (needs <c>station</c>; a profile's goes in the first profile view of its
  /// alignment, styled from the profile view Station Elevation label styles), surface <c>spot_elevation</c> / <c>spot</c> (needs
  /// <c>point</c> {x, y}), and pipe or structure labels (any <c>labelType</c>; a pipe label sits at mid-length, a
  /// structure label at the structure). Anything else is CIVIL3D.INVALID_INPUT listing what is supported.
  /// <c>objectType</c>, <c>objectName</c> and <c>labelType</c> are required. With no <c>labelStyle</c> the
  /// collection's first style is used; a <c>labelStyle</c> that isn't there is CIVIL3D.OBJECT_NOT_FOUND listing the
  /// names that are. A missing station or point, a station off the alignment or profile, or a profile whose alignment
  /// has no profile view, is CIVIL3D.INVALID_INPUT; an unknown object, or no marker style for a label that needs one,
  /// is CIVIL3D.OBJECT_NOT_FOUND. Changes the drawing (one undo step). The
  /// answer gives the style used and the new label's handle (none for a label set).
  /// </summary>
  public static Task<object?> AddLabelAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");
    var labelType = PluginRuntime.GetRequiredString(parameters, "labelType");
    var labelStyle = PluginRuntime.GetOptionalString(parameters, "labelStyle");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var target = ResolveTargetObject(civilDoc, transaction, objectType, objectName, OpenMode.ForWrite);

      if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "label_set", StringComparison.OrdinalIgnoreCase))
      {
        var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelStyle);
        ApplyObjectIdProperty(target, labelSetId, "LabelSetId");
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, labelSetId),
          ["applied"] = true,
        };
      }

      if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "station", StringComparison.OrdinalIgnoreCase))
      {
        var station = PluginRuntime.GetOptionalDouble(parameters, "station")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Alignment station labels require 'station'.");
        var alignment = (Autodesk.Civil.DatabaseServices.Alignment)target;
        AlignmentCommands.RequireStation(alignment, station);
        // Station offset and spot elevation labels need a marker style too; the drawing's first one is used.
        var styleId = FindNamedStyle(((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Styles.LabelStyles.AlignmentLabelStyles.StationOffsetLabelStyles, transaction, labelStyle);
        var labelId = CreateAlignmentStationLabel(alignment, styleId, station, RequireMarkerStyle((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc));
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["station"] = station,
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "label_set", StringComparison.OrdinalIgnoreCase))
      {
        var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, labelStyle);
        ApplyObjectIdProperty(target, labelSetId, "LabelSetId");
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, labelSetId),
          ["applied"] = true,
        };
      }

      if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase) && string.Equals(labelType, "station", StringComparison.OrdinalIgnoreCase))
      {
        var station = PluginRuntime.GetOptionalDouble(parameters, "station")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Profile station labels require 'station'.");
        // A label at one station of a profile is a station elevation label in a profile view, styled from the profile
        // view's Station Elevation label styles (not the profile label set styles, which are for label_set).
        var typedCivilDoc = (Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc;
        var profile = (Autodesk.Civil.DatabaseServices.Profile)target;
        var styleId = FindNamedStyle(typedCivilDoc.Styles.LabelStyles.ProfileViewLabelStyles.StationElevationLabelStyles, transaction, labelStyle);
        if (styleId.IsNull)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The drawing has no profile view Station Elevation label style; add one (or import one from a template) first.");
        }

        var profileViewId = FirstProfileViewId(transaction, profile)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Profile station labels are placed in a profile view, and the alignment of profile '{objectName}' has none. Create a profile view first.");
        double elevation;
        try
        {
          elevation = profile.ElevationAt(station);
        }
        catch (Exception exception) when (exception is not JsonRpcDispatchException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Station {station} is outside profile '{objectName}' ({profile.StartingStation:0.###} to {profile.EndingStation:0.###}).");
        }

        var labelId = Autodesk.Civil.DatabaseServices.StationElevationLabel.Create(profileViewId, styleId, RequireMarkerStyle(typedCivilDoc), station, elevation);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["station"] = station,
          ["elevation"] = elevation,
          ["profileView"] = ResolveObjectName(transaction, profileViewId),
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "surface", StringComparison.OrdinalIgnoreCase) && (string.Equals(labelType, "spot_elevation", StringComparison.OrdinalIgnoreCase) || string.Equals(labelType, "spot", StringComparison.OrdinalIgnoreCase)))
      {
        var pointNode = PluginRuntime.GetParameter(parameters, "point") as JsonObject
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require 'point'.");
        var point = new Point2d(
          pointNode["x"] is JsonValue xValue && xValue.TryGetValue<double>(out var x) ? x : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require point.x as a number."),
          pointNode["y"] is JsonValue yValue && yValue.TryGetValue<double>(out var y) ? y : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Surface spot labels require point.y as a number.")
        );
        var styleId = FindNamedStyle(((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Styles.LabelStyles.SurfaceLabelStyles.SpotElevationLabelStyles, transaction, labelStyle);
        var labelId = Autodesk.Civil.DatabaseServices.SurfaceElevationLabel.Create(target.ObjectId, point, styleId, RequireMarkerStyle((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc));
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["point"] = new Dictionary<string, object?>
          {
            ["x"] = point.X,
            ["y"] = point.Y,
          },
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "pipe", StringComparison.OrdinalIgnoreCase))
      {
        var styleId = FindLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = Autodesk.Civil.DatabaseServices.PipeLabel.Create(target.ObjectId, 0.5, styleId);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      if (string.Equals(objectType, "structure", StringComparison.OrdinalIgnoreCase))
      {
        var styleId = FindLabelStyleId(civilDoc, transaction, objectType, labelStyle);
        var labelId = Autodesk.Civil.DatabaseServices.StructureLabel.Create(target.ObjectId, styleId, ((Autodesk.Civil.DatabaseServices.Structure)target).Position);
        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["labelType"] = labelType,
          ["labelStyle"] = ResolveObjectName(transaction, styleId),
          ["handle"] = ResolveHandle(transaction, labelId),
          ["created"] = true,
        };
      }

      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"addLabel currently supports alignment/profile 'label_set', alignment/profile 'station', surface 'spot_elevation', and basic pipe/structure labels. Requested objectType='{objectType}', labelType='{labelType}'."
      );
    });
  }

  /// <summary>Finds the object to label by type and name (case-insensitive). A profile is searched across every
  /// alignment, so the first profile with that name wins when two alignments have one.</summary>
  private static DBObject ResolveTargetObject(object civilDoc, Transaction transaction, string objectType, string objectName, OpenMode openMode)
  {
    if (string.Equals(objectType, "alignment", StringComparison.OrdinalIgnoreCase))
    {
      return CivilObjectUtils.FindAlignmentByName((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc, transaction, objectName);
    }

    if (string.Equals(objectType, "profile", StringComparison.OrdinalIgnoreCase))
    {
      foreach (ObjectId alignmentId in ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Alignment>(transaction, alignmentId, OpenMode.ForRead);
        foreach (ObjectId profileId in alignment.GetProfileIds())
        {
          var profile = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, profileId, openMode);
          if (string.Equals(CivilObjectUtils.GetName(profile), objectName, StringComparison.OrdinalIgnoreCase))
          {
            return profile;
          }
        }
      }

      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile '{objectName}' was not found.");
    }

    if (string.Equals(objectType, "surface", StringComparison.OrdinalIgnoreCase))
    {
      return CivilObjectUtils.FindSurfaceByName((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc, transaction, objectName, openMode);
    }

    if (string.Equals(objectType, "pipe", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeObject(civilDoc, transaction, objectName, openMode, true);
    }

    if (string.Equals(objectType, "structure", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeObject(civilDoc, transaction, objectName, openMode, false);
    }

    if (string.Equals(objectType, "pipe_network", StringComparison.OrdinalIgnoreCase))
    {
      return ResolvePipeNetwork(civilDoc, transaction, objectName, openMode);
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported label objectType '{objectType}'.");
  }

  /// <summary>The pipe network named <paramref name="objectName"/> (case-insensitive), or CIVIL3D.OBJECT_NOT_FOUND.
  /// </summary>
  private static DBObject ResolvePipeNetwork(object civilDoc, Transaction transaction, string objectName, OpenMode openMode)
  {
    foreach (var objectId in GetPipeNetworkIds(civilDoc))
    {
      var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, openMode);
      if (string.Equals(CivilObjectUtils.GetName(network), objectName, StringComparison.OrdinalIgnoreCase))
      {
        return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{objectName}' was not found.");
  }

  /// <summary>The first pipe (<paramref name="pipe"/> true) or structure with this name in any gravity network, or
  /// CIVIL3D.OBJECT_NOT_FOUND. Names can repeat across networks; the first match wins.</summary>
  private static DBObject ResolvePipeObject(object civilDoc, Transaction transaction, string objectName, OpenMode openMode, bool pipe)
  {
    foreach (var networkId in GetPipeNetworkIds(civilDoc))
    {
      var network = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, networkId, OpenMode.ForRead);
      foreach (var objectId in GetPipeRelatedObjectIds(network, pipe))
      {
        var dbObject = CivilObjectUtils.GetRequiredObject<DBObject>(transaction, objectId, openMode);
        if (string.Equals(CivilObjectUtils.GetName(dbObject), objectName, StringComparison.OrdinalIgnoreCase))
        {
          return dbObject;
        }
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{(pipe ? "Pipe" : "Structure")} '{objectName}' was not found.");
  }

  /// <summary>Pipe network ids, from whichever of several member names this release has; ids can repeat if more
  /// than one member exists.</summary>
  private static IEnumerable<ObjectId> GetPipeNetworkIds(object civilDoc)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMemberValue(civilDoc, "PipeNetworkCollection"),
      GetNamedMemberValue(civilDoc, "NetworkCollection"),
      GetNamedMemberValue(civilDoc, "PipeNetworks"),
      GetNamedMemberValue(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in CivilObjectUtils.ToObjectIds(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  /// <summary>The style collection for an object type, found late-bound with a fallback name for each (surface, pipe
  /// and structure are read typed). An unknown type, or a collection not found, gives no ids; no Styles object at
  /// all is CIVIL3D.TRANSACTION_FAILED.</summary>
  private static IEnumerable<ObjectId> GetLabelStyleCollection(object civilDoc, string objectType)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles") ?? throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", "Civil 3D styles collection is not available.");
    var labelSetStyles = GetNamedMemberValue(styles, "LabelSetStyles");

    object? collection = objectType.ToLowerInvariant() switch
    {
      "alignment" => GetNamedMemberValue(labelSetStyles, "AlignmentLabelSetStyles") ?? GetNamedMemberValue(styles, "AlignmentLabelStyles"),
      "profile" => GetNamedMemberValue(labelSetStyles, "ProfileLabelSetStyles") ?? GetNamedMemberValue(styles, "ProfileLabelStyles"),
      "surface" => ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Styles.LabelStyles.SurfaceLabelStyles.SpotElevationLabelStyles,
      "pipe_network" => GetNamedMemberValue(styles, "PipeNetworkLabelStyles") ?? GetNamedMemberValue(styles, "PipeLabelStyles"),
      "pipe" => ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles,
      "structure" => ((Autodesk.Civil.ApplicationServices.CivilDocument)civilDoc).Styles.LabelStyles.StructureLabelStyles.LabelStyles,
      _ => null,
    };

    if (collection == null)
    {
      return Array.Empty<ObjectId>();
    }

    return CivilObjectUtils.ToObjectIds(collection).Where(objectId => objectId != ObjectId.Null).ToList();
  }

  /// <summary>
  /// The style named <c>labelStyle</c>; with no name, the collection's first style (ObjectId.Null when there is none).
  /// A name that isn't there is refused with the names that are, as in LookupUtils.
  /// </summary>
  private static ObjectId FindLabelStyleId(object civilDoc, Transaction transaction, string objectType, string? labelStyle)
  {
    var named = !string.IsNullOrWhiteSpace(labelStyle);
    var names = new List<string>();

    foreach (var objectId in GetLabelStyleCollection(civilDoc, objectType))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (!named)
      {
        return objectId;
      }

      var name = CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead));
      if (string.Equals(name, labelStyle, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }

      if (!string.IsNullOrEmpty(name))
      {
        names.Add(name);
      }
    }

    return named ? throw LookupUtils.NotFound("Label style", labelStyle!, names) : ObjectId.Null;
  }

  /// <summary>The drawing's first marker style, which station offset, spot elevation and station elevation labels need;
  /// CIVIL3D.OBJECT_NOT_FOUND when the drawing has none (a bare drawing not started from a Civil 3D template).</summary>
  private static ObjectId RequireMarkerStyle(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
  {
    foreach (ObjectId markerStyleId in civilDoc.Styles.MarkerStyles)
    {
      if (!markerStyleId.IsNull) return markerStyleId;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      "This label needs a marker style and the drawing has none. Add one (Settings > General > Multipurpose Styles > Marker Styles) or import one from a template, then try again.");
  }

  /// <summary>The first profile view of the profile's alignment, or null when it has none.</summary>
  private static ObjectId? FirstProfileViewId(Transaction transaction, Autodesk.Civil.DatabaseServices.Profile profile)
  {
    var alignment = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Alignment>(transaction, profile.AlignmentId, OpenMode.ForRead);
    foreach (ObjectId profileViewId in alignment.GetProfileViewIds())
    {
      if (!profileViewId.IsNull) return profileViewId;
    }

    return null;
  }

  /// <summary>A station offset label on the alignment at <paramref name="station"/>, offset 0 (the label is placed
  /// at the alignment point for that station).</summary>
  private static ObjectId CreateAlignmentStationLabel(Autodesk.Civil.DatabaseServices.Alignment alignment, ObjectId styleId, double station, ObjectId markerStyleId)
  {
    double x = 0;
    double y = 0;
    alignment.PointLocation(station, 0, ref x, ref y);
    return Autodesk.Civil.DatabaseServices.StationOffsetLabel.Create(alignment.ObjectId, styleId, markerStyleId, new Point2d(x, y));
  }

  /// <summary>The style named <paramref name="name"/> in one kind of label style; with no name, the first.</summary>
  private static ObjectId FindNamedStyle(IEnumerable<ObjectId> styles, Transaction transaction, string? name)
  {
    var ids = styles.Where(id => id != ObjectId.Null).ToList();
    if (string.IsNullOrWhiteSpace(name))
    {
      return ids.FirstOrDefault();
    }

    var names = new List<string>();
    foreach (var id in ids)
    {
      var styleName = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead));
      if (string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase))
      {
        return id;
      }

      if (styleName != null)
      {
        names.Add(styleName);
      }
    }

    throw LookupUtils.NotFound("Label style", name, names);
  }

  /// <summary>An object's handle, or null for a null id or an object that can't be opened.</summary>
  private static string? ResolveHandle(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead) as DBObject;
      return dbObject == null ? null : CivilObjectUtils.GetHandle(dbObject);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>A network's pipe ids (<paramref name="pipe"/> true) or structure ids, from every member name that
  /// exists (ids can repeat).</summary>
  private static IEnumerable<ObjectId> GetPipeRelatedObjectIds(DBObject network, bool pipe)
  {
    var members = pipe
      ? new[] { "GetPipeIds", "PipeIds", "Pipes", "PipeCollection" }
      : new[] { "GetStructureIds", "StructureIds", "Structures", "StructureCollection" };

    foreach (var name in members)
    {
      var value = CivilObjectUtils.InvokeMethod(network, name) ?? GetNamedMemberValue(network, name);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  /// <summary>An object's label ids, from every one of GetLabelIds/LabelIds/Labels that exists (ids can repeat).
  /// </summary>
  private static IEnumerable<ObjectId> GetLabelIds(object target)
  {
    foreach (var name in new[] { "GetLabelIds", "LabelIds", "Labels" })
    {
      var value = CivilObjectUtils.InvokeMethod(target, name) ?? GetNamedMemberValue(target, name);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  /// <summary>A property or field read by name through Civil3DCompatibility (null when neither exists).</summary>
  private static object? GetNamedMemberValue(object? value, string memberName)
  {
    if (value == null)
    {
      return null;
    }

    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static ObjectId GetObjectId(object? value, string propertyName)
  {
    return CivilObjectUtils.GetPropertyValue<ObjectId>(value, propertyName);
  }

  /// <summary>An object's name, or null for a null id or an object that can't be opened.</summary>
  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      return CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead));
    }
    catch
    {
      return null;
    }
  }

  /// <summary>Sets the first writable property of the given names (e.g. LabelSetId) to <paramref name="objectId"/>;
  /// a null id means the requested style or label set wasn't found (CIVIL3D.OBJECT_NOT_FOUND). An object with none of
  /// the properties writable is CIVIL3D.TRANSACTION_FAILED.</summary>
  private static void ApplyObjectIdProperty(object target, ObjectId objectId, params string[] propertyNames)
  {
    if (objectId == ObjectId.Null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Requested label style or label set was not found.");
    }

    foreach (var propertyName in propertyNames)
    {
      if (Civil3DCompatibility.TrySetProperty(target, propertyName, objectId)) return;
    }

    throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Object '{CivilObjectUtils.GetName(target)}' does not expose a writable label set property.");
  }
}
