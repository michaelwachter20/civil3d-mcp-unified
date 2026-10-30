using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Name-to-id lookups for the optional layer, site and style arguments the create and edit commands take (profile,
/// alignment, surface, section, parcel, label set, band set and group plot styles). Used throughout the *Commands
/// classes; it never creates anything.
/// <para>
/// A create call still succeeds without naming a style: no name gives a default (the current layer, the first style
/// in the collection, or <c>ObjectId.Null</c> for sites, band sets and plot styles, which callers read as "leave
/// it"). A name that isn't in the drawing is refused with the names that are (<c>CIVIL3D.OBJECT_NOT_FOUND</c>), so a
/// misspelled "Elm Stret Design" isn't quietly replaced by the first style. Names compare case-insensitively. The
/// lookups run inside the command's transaction, so a refusal leaves the drawing as it was.
/// </para>
/// </summary>
public static class LookupUtils
{
  /// <summary>How many of the existing names a "not found" message lists.</summary>
  private const int NamesListed = 25;

  /// <summary>The layer named <paramref name="layerName"/>, or the current layer (CLAYER) when none is named.</summary>
  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return database.Clayer;
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    var names = new List<string>();
    foreach (ObjectId layerId in layerTable)
    {
      names.Add(CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, layerId, OpenMode.ForRead).Name);
    }

    throw NotFound("Layer", layerName, names);
  }

  /// <summary>The site named <paramref name="siteName"/>, or <c>ObjectId.Null</c> (no site, i.e. siteless) when none is named.</summary>
  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    var names = new List<string>();
    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }

      names.Add(site.Name);
    }

    throw NotFound("Site", siteName, names);
  }

  /// <summary>
  /// The refusal for a name that isn't in the drawing, listing the first <see cref="NamesListed"/> that are
  /// (e.g. "Style 'Elm Stret Design' is not in this drawing. Available: Elm Street Design, Standard").
  /// </summary>
  internal static JsonRpcDispatchException NotFound(string what, string name, IReadOnlyCollection<string> available)
  {
    var listed = available.Count == 0
      ? "There are none."
      : "Available: " + string.Join(", ", available.Take(NamesListed))
        + (available.Count > NamesListed ? $", and {available.Count - NamesListed} more." : ".");
    return new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{what} '{name}' is not in this drawing. {listed}");
  }

  // The style lookups below return the named style (a name that isn't there is refused), else with no name the
  // collection's first style (see GetStyleId), unless their own comment says otherwise.

  public static ObjectId GetAlignmentStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.AlignmentStyles, transaction, styleName);
  }

  public static ObjectId GetProfileStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ProfileStyles, transaction, styleName);
  }

  public static ObjectId GetSurfaceStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SurfaceStyles, transaction, styleName);
  }

  public static ObjectId GetAlignmentLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, transaction, styleName);
  }

  /// <summary>A profile view style; the collection is read by property name, and <c>ObjectId.Null</c> when it isn't there.</summary>
  public static ObjectId GetProfileViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    var styles = CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewStyles");
    return styles != null
      ? GetStyleId(styles, transaction, styleName)
      : ObjectId.Null;
  }

  /// <summary>A profile view band set; <c>ObjectId.Null</c> (no band set) when none is named.</summary>
  public static ObjectId GetProfileViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    if (string.IsNullOrWhiteSpace(bandSetName))
    {
      return ObjectId.Null;
    }

    var labelSetStyles = civilDoc.Styles.LabelSetStyles;
    var bandSetStyles = CivilObjectUtils.GetPropertyValue<object>(labelSetStyles, "ProfileViewBandSetStyles");
    return bandSetStyles != null
      ? GetStyleId(bandSetStyles, transaction, bandSetName)
      : ObjectId.Null;
  }

  public static ObjectId GetParcelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ParcelStyles, transaction, styleName);
  }

  public static ObjectId GetParcelAreaLabelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelStyles.ParcelLabelStyles.AreaLabelStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SectionViewStyles, transaction, styleName);
  }

  /// <summary>A section view band set; <c>ObjectId.Null</c> (leave the bands) when none is named.</summary>
  public static ObjectId GetSectionViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return string.IsNullOrWhiteSpace(bandSetName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.SectionViewBandSetStyles, transaction, bandSetName);
  }

  /// <summary>A section view group plot style; <c>ObjectId.Null</c> (keep the default) when none is named.</summary>
  public static ObjectId GetGroupPlotStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return string.IsNullOrWhiteSpace(styleName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.GroupPlotStyles, transaction, styleName);
  }

  /// <summary>The name of the first style in a style collection, or null when it is empty (getDrawingSettings' default styles).</summary>
  public static string? GetFirstStyleName(object? collection, Transaction transaction)
  {
    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }

    return null;
  }

  /// <summary>
  /// The style in <paramref name="collection"/> whose name matches <paramref name="styleName"/> (case-insensitive).
  /// With no name: the first style, or <c>ObjectId.Null</c> for an empty collection. A name that isn't there is
  /// refused (<see cref="NotFound"/>).
  /// </summary>
  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName)
  {
    var named = !string.IsNullOrWhiteSpace(styleName);
    var names = new List<string>();

    foreach (var objectId in EnumerateObjectIds(collection))
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
      if (string.Equals(name, styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }

      if (!string.IsNullOrEmpty(name))
      {
        names.Add(name);
      }
    }

    return named ? throw NotFound("Style", styleName!, names) : ObjectId.Null;
  }

  /// <summary>The ObjectIds in any Civil 3D style collection (they share no common type, so this takes whatever enumerates ids).</summary>
  private static IEnumerable<ObjectId> EnumerateObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }

      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }
}
