// Late-bound access to Civil 3D's COM (ActiveX) API, for the few things the .NET API can't do.
// Callers: ParcelEditingCommands.CreateParcelAsync (createParcel: a site's ParcelSegments forms parcels from closed
// lines), SurfaceCommands (watershed analysis through a COM surface's SurfaceAnalysis) and SurveyCom (the survey COM
// application, by its own ProgID prefix). Serves no JSON-RPC method itself.
// The COM objects are `dynamic`; the application is found through AutoCAD's AcadApplication.GetInterfaceObject with a
// ProgID read from the registry (HKCR, read only). Must be called on the main thread inside the caller's
// CivilExecution read/write, which owns the drawing access and the undo step.
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

  /// <summary>The COM site of this name (ignoring case) in the active drawing, which the caller has checked exists
  /// through .NET; throws CIVIL3D.OBJECT_NOT_FOUND if COM can't find it, CIVIL3D.API_ERROR without COM.</summary>
  public static dynamic FindSite(string siteName) => FindByName(Application(LandProgIdPrefix).ActiveDocument.Sites, siteName, "Site");

  /// <summary>The COM surface of this name (ignoring case) in the active drawing, which the caller has checked exists
  /// through .NET; same errors as <see cref="FindSite"/>.</summary>
  public static dynamic FindSurface(string surfaceName) => FindByName(Application(LandProgIdPrefix).ActiveDocument.Surfaces, surfaceName, "Surface");

  /// <summary>The item of a COM collection whose Name matches (ignoring case); CIVIL3D.OBJECT_NOT_FOUND otherwise.</summary>
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
  /// <paramref name="progIdPrefix"/> is e.g. "AeccXUiLand.AeccApplication.". Throws CIVIL3D.API_ERROR when no
  /// registered version answers.
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
