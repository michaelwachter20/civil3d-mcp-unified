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
/// The lookups are forgiving on purpose, so a create call still succeeds without naming a style: no name, or a name
/// that isn't there, gives a default (the current layer, the first style in the collection, or <c>ObjectId.Null</c>
/// for sites, band sets and plot styles, which callers read as "leave it"). Names compare case-insensitively.
/// </para>
/// </summary>
// NOTE: because an unknown style or layer name falls back silently, a misspelled name (e.g. "Elm Stret Design")
// gives the first style or the current layer, and the answer doesn't say the requested one wasn't found.
public static class LookupUtils
{
  /// <summary>The layer named <paramref name="layerName"/>, or the current layer (CLAYER) when none is named or it doesn't exist.</summary>
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

    return database.Clayer;
  }

  /// <summary>The site named <paramref name="siteName"/>, or <c>ObjectId.Null</c> (no site, i.e. siteless) when none is named or it doesn't exist.</summary>
  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  // The style lookups below return the named style, else the collection's first style (see GetStyleId), unless
  // their own comment says otherwise.

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
  /// The style in <paramref name="collection"/> whose name matches <paramref name="styleName"/> (case-insensitive),
  /// else the first style, else <c>ObjectId.Null</c> for an empty collection.
  /// </summary>
  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName)
  {
    var fallback = ObjectId.Null;

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return fallback;
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
