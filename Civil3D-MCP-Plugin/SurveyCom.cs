// SurveyCom.cs - late-bound (dynamic) access to Civil 3D's survey COM API (AeccXUiSurvey), used only by
// SurveyCommands (civil3d_survey, src/tools/domains/surveyDomain.ts). The COM application is found by LandCom.
// Survey databases are files in the survey working folder, not drawing objects, so work here does not go through a
// drawing transaction; SurveyCommands still runs it inside the host gate. A database the user already has open is
// never closed on them, and ReleaseObjects must run between calls so a read sees the write before it.
using System.Runtime.InteropServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Civil 3D's survey COM interface. The .NET API has no survey databases at all, so everything goes through here. A
/// survey database lives on disk in the survey working folder, apart from any drawing, and has to be opened before its
/// networks, figures and points can be read, so one call can open it, use it and close it again.
/// </summary>
internal static class SurveyCom
{
  private const string ProgIdPrefix = "AeccXUiSurvey.AeccSurveyApplication.";

  /// <summary>The survey document of the active drawing: the way in to the databases.</summary>
  public static dynamic Document() => LandCom.Application(ProgIdPrefix).ActiveDocument;

  /// <summary>The items of a COM collection. Counted first, so an item that opens or closes something can't shift it.</summary>
  public static IEnumerable<dynamic> Items(dynamic collection)
  {
    var count = (int)collection.Count;
    for (var index = 0; index < count; index++)
    {
      yield return collection.Item(index);
    }
  }

  /// <summary>The item of this name, or null. FindItem raises an error for a name it doesn't know.</summary>
  public static dynamic? Find(dynamic collection, string name)
  {
    foreach (var item in Items(collection))
    {
      if (string.Equals((string)item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
    }

    return null;
  }

  /// <summary>
  /// Runs an action with a survey database open. One the user already has open in the Survey tab is left open; closing
  /// it would pull it out from under them. Opened for write (<c>Open</c>) or read-only (<c>OpenEx(true)</c>). Throws
  /// CIVIL3D.OBJECT_NOT_FOUND when no survey database has that name.
  /// </summary>
  public static T WithDatabase<T>(string name, bool forWrite, Func<dynamic, T> action)
  {
    dynamic document = Document();
    var project = Find(document.Projects, name)
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Survey database '{name}' was not found in the survey working folder.");

    var alreadyOpen = IsCurrent(document, name);
    if (!alreadyOpen)
    {
      if (forWrite) project.Open();
      else project.OpenEx(true);
    }

    try
    {
      return action(project);
    }
    finally
    {
      if (!alreadyOpen) project.Close();
    }
  }

  /// <summary>Whether the named database is the one currently open in the survey document.</summary>
  private static bool IsCurrent(dynamic document, string name)
  {
    try
    {
      var current = document.CurrentProject;
      return current != null && string.Equals((string)current.Name, name, StringComparison.OrdinalIgnoreCase);
    }
    catch (COMException)
    {
      // Civil 3D answers with an error, not null, when no database is open.
      return false;
    }
  }

  /// <summary>
  /// Lets go of the COM objects a call used, which must happen before the next call. Civil 3D hands back the same object
  /// for a database while one is alive, with the data it read then, so without this a read misses the write before it
  /// (10 times out of 10 when measured). They die young, so a quick collection takes them, in about 10 ms where a full
  /// one takes 600.
  /// </summary>
  public static void ReleaseObjects()
  {
    GC.Collect(1, GCCollectionMode.Forced, true);
    GC.WaitForPendingFinalizers();
  }

  /// <summary>The numbers COM hands back as a variant array, which is null for an empty database.</summary>
  public static List<int> Numbers(object? value)
  {
    return value is Array numbers ? numbers.Cast<object>().Select(Convert.ToInt32).ToList() : new List<int>();
  }
}
