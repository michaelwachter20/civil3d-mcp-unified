using System.Runtime.InteropServices;
using Microsoft.Win32;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Civil 3D's COM interface, for what the .NET API can't do. Parcels: the .NET API can read and edit them but has no
/// call that creates one. A site's ParcelSegments takes lines, and Civil 3D forms a parcel wherever those lines close a
/// loop, as it does for Create Parcel from Objects. Watersheds: a surface's SurfaceAnalysis runs the analysis.
/// </summary>
internal static class LandCom
{
  private const string LandProgIdPrefix = "AeccXUiLand.AeccApplication.";

  /// <summary>The COM site of this name in the active drawing, which has been checked to exist.</summary>
  public static dynamic FindSite(string siteName) => FindByName(Application(LandProgIdPrefix).ActiveDocument.Sites, siteName, "Site");

  /// <summary>The COM surface of this name in the active drawing, which has been checked to exist.</summary>
  public static dynamic FindSurface(string surfaceName) => FindByName(Application(LandProgIdPrefix).ActiveDocument.Surfaces, surfaceName, "Surface");

  private static dynamic FindByName(dynamic collection, string name, string kind)
  {
    for (var index = 0; index < collection.Count; index++)
    {
      dynamic item = collection.Item(index);
      if (string.Equals((string)item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"{kind} '{name}' was not found through COM.");
  }

  /// <summary>
  /// One of Civil 3D's COM applications. The name carries a version (13.9 for Civil 3D 2027) that has no published rule, so it
  /// comes from the registry, newest first: with two releases installed, the running one is the one that answers.
  /// </summary>
  public static dynamic Application(string progIdPrefix)
  {
    dynamic acad = AcadApplication.AcadApplication;
    var names = Registry.ClassesRoot.GetSubKeyNames()
      .Where(name => name.StartsWith(progIdPrefix, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(name => Version.TryParse(name[progIdPrefix.Length..], out var version) ? version : new Version());

    foreach (var name in names)
    {
      try
      {
        return acad.GetInterfaceObject(name);
      }
      catch (COMException)
      {
        // Another release's name: this Civil 3D doesn't answer to it.
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D's COM interface is not available.");
  }
}
