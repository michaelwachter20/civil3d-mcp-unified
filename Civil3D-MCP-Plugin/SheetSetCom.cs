using Autodesk.AutoCAD.Interop;

namespace Civil3DMcpPlugin;

/// <summary>
/// Sheet sets through AutoCAD's sheet set library (AcSmComponents). A sheet set is a .dst file, not an object in the
/// drawing, so .NET's drawing API has nothing for it. A database must be locked to change it, and every path out
/// unlocks and closes it, or the file stays locked until Civil 3D closes.
/// </summary>
internal static class SheetSetCom
{
  private const string ProgIdVersion = "26";

  private static T Make<T>(string className) =>
    Civil3DCompatibility.CreateComObject<T>($"AcSmComponents.{className}.{ProgIdVersion}");

  /// <summary>Makes the file, and a sheet for each layout of <paramref name="drawingPath"/>; the file on disk is the result.</summary>
  public static Dictionary<string, object?> Create(string path, string name, string? description, string drawingPath, List<string> layoutNames, bool overwrite)
  {
    var manager = Make<IAcSmSheetSetMgr>("AcSmSheetSetMgr");
    var database = manager.CreateDatabase(path, "", overwrite);
    var committed = false;
    database.LockDb(database);
    try
    {
      var sheetSet = database.GetSheetSet();
      sheetSet.SetName(name);
      if (!string.IsNullOrWhiteSpace(description)) sheetSet.SetDesc(description);

      var number = 1;
      foreach (var layoutName in layoutNames)
      {
        var layout = Make<IAcSmAcDbLayoutReference>("AcSmAcDbLayoutReference");
        layout.InitNew((IAcSmPersist)database);
        layout.SetFileName(drawingPath);
        layout.SetName(layoutName);
        var sheet = sheetSet.ImportSheet((AcSmAcDbLayoutReference)layout);
        sheet.SetNumber(number++.ToString());
        sheet.SetTitle(layoutName);
        sheetSet.InsertComponent((IAcSmComponent)sheet, null);
      }

      committed = true;
    }
    finally
    {
      database.UnlockDb(database, committed);
      manager.Close(database);
    }

    return new Dictionary<string, object?> { ["name"] = name, ["path"] = path, ["sheetCount"] = layoutNames.Count, ["created"] = true };
  }

  /// <summary>What a .dst file holds: its name, description and sheets.</summary>
  public static Dictionary<string, object?> Read(string path)
  {
    var manager = Make<IAcSmSheetSetMgr>("AcSmSheetSetMgr");
    var database = manager.OpenDatabase(path, false);
    try
    {
      var sheetSet = database.GetSheetSet();
      var sheets = new List<Dictionary<string, object?>>();
      var enumerator = sheetSet.GetSheetEnumerator();
      for (var component = enumerator.Next(); component != null; component = enumerator.Next())
      {
        if (component is IAcSmSheet sheet)
        {
          sheets.Add(new Dictionary<string, object?> { ["name"] = sheet.GetName(), ["number"] = sheet.GetNumber(), ["title"] = sheet.GetTitle() });
        }
      }

      return new Dictionary<string, object?> { ["name"] = sheetSet.GetName(), ["path"] = path, ["description"] = sheetSet.GetDesc(), ["sheets"] = sheets };
    }
    finally
    {
      manager.Close(database);
    }
  }
}
