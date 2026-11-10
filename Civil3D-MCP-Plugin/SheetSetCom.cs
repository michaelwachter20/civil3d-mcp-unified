// SheetSetCom.cs - sheet set (.dst) access through AutoCAD's AcSmComponents COM library. Only PlanProductionCommands
// calls it (listSheetSets, getSheetSetInfo, createSheetSet, addSheet, publish/export of a set; civil3d_plan_production,
// src/tools/domains/planProductionDomain.ts); DataShortcutCommands uses SameFile. Callers resolve every path through
// FileBoundary (ResolveExportPath / ResolveImportPath) before it gets here. It changes .dst files, never the drawing;
// the COM object is created through Civil3DCompatibility (the one place for late binding). Rule: every path out
// unlocks and closes the sheet set database, or the .dst stays locked until Civil 3D closes.
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.Interop;

namespace Civil3DMcpPlugin;

/// <summary>
/// Sheet sets through AutoCAD's sheet set library (AcSmComponents). A sheet set is a .dst file, not an object in the
/// drawing, so .NET's drawing API has nothing for it. A database must be locked to change it, and every path out
/// unlocks and closes it, or the file stays locked until Civil 3D closes.
/// </summary>
internal static class SheetSetCom
{
  // AcSmComponents' ProgID version suffix ("AcSmComponents.AcSmSheetSetMgr.26").
  private const string ProgIdVersion = "26";

  /// <summary>One sheet: <c>Drawing</c> is the file its layout is in.</summary>
  public sealed record Sheet(string Name, string Number, string Title, string? Layout, string? Drawing, bool DoNotPlot);

  /// <summary>Creates an AcSmComponents COM object by class name.</summary>
  private static T Make<T>(string className) =>
    Civil3DCompatibility.CreateComObject<T>($"AcSmComponents.{className}.{ProgIdVersion}");

  /// <summary>Makes the file, and a sheet for each layout of <paramref name="drawingPath"/>; the file on disk is the result.
  /// Sheets are numbered 1, 2, ... and titled with the layout name. Throws CIVIL3D.API_ERROR when a layout isn't in the
  /// saved drawing.</summary>
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
        AddLayout(database, sheetSet, drawingPath, layoutName, layoutName, number++.ToString());
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

  /// <summary>Adds a layout to a sheet set as a new sheet, numbered after the last one unless given a number. Throws
  /// CIVIL3D.CONFLICT when that layout of that drawing is already a sheet, CIVIL3D.API_ERROR when the layout isn't in
  /// the saved drawing.</summary>
  public static Dictionary<string, object?> AddSheet(string path, string sheetName, string? sheetNumber, string drawingPath, string layoutName)
  {
    var manager = Make<IAcSmSheetSetMgr>("AcSmSheetSetMgr");
    var database = manager.OpenDatabase(path, false);
    try
    {
      var sheetSet = database.GetSheetSet();
      var existing = Walk(sheetSet.GetSheetEnumerator()).Select(Describe).ToList();
      if (existing.Any(sheet => SameFile(sheet.Drawing, drawingPath) && string.Equals(sheet.Layout, layoutName, StringComparison.OrdinalIgnoreCase)))
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Layout '{layoutName}' of this drawing is already a sheet in this set.");
      }

      var number = !string.IsNullOrWhiteSpace(sheetNumber) ? sheetNumber! : NextNumber(existing);
      var committed = false;
      database.LockDb(database);
      try
      {
        AddLayout(database, sheetSet, drawingPath, layoutName, sheetName, number);
        committed = true;
      }
      finally
      {
        database.UnlockDb(database, committed);
      }

      return new Dictionary<string, object?>
      {
        ["name"] = sheetName,
        ["number"] = number,
        ["layout"] = layoutName,
        ["sheetSet"] = path,
        ["sheetCount"] = existing.Count + 1,
        ["added"] = true,
      };
    }
    finally
    {
      manager.Close(database);
    }
  }

  /// <summary>What a .dst file holds: its name, description and sheets.</summary>
  public static Dictionary<string, object?> Read(string path)
  {
    var manager = Make<IAcSmSheetSetMgr>("AcSmSheetSetMgr");
    var database = manager.OpenDatabase(path, false);
    try
    {
      var sheetSet = database.GetSheetSet();
      var sheets = Walk(sheetSet.GetSheetEnumerator()).Select(Describe)
        .Select(sheet => new Dictionary<string, object?>
        {
          ["name"] = sheet.Name,
          ["number"] = sheet.Number,
          ["title"] = sheet.Title,
          ["layout"] = sheet.Layout,
          ["drawing"] = sheet.Drawing,
          ["doNotPlot"] = sheet.DoNotPlot,
        })
        .ToList();

      return new Dictionary<string, object?> { ["name"] = sheetSet.GetName(), ["path"] = path, ["description"] = sheetSet.GetDesc(), ["sheets"] = sheets };
    }
    finally
    {
      manager.Close(database);
    }
  }

  /// <summary>Every sheet of a .dst file, in the set's order.</summary>
  public static List<Sheet> Sheets(string path)
  {
    var manager = Make<IAcSmSheetSetMgr>("AcSmSheetSetMgr");
    var database = manager.OpenDatabase(path, false);
    try
    {
      return Walk(database.GetSheetSet().GetSheetEnumerator()).Select(Describe).ToList();
    }
    finally
    {
      manager.Close(database);
    }
  }

  /// <summary>Whether two paths are the same file, however they're written.</summary>
  public static bool SameFile(string? first, string? second) =>
    !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
    && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

  /// <summary>A sheet set lists sheets and subsets together, and a subset holds more of both.</summary>
  private static IEnumerable<IAcSmSheet> Walk(IAcSmEnumComponent components)
  {
    for (var component = components.Next(); component != null; component = components.Next())
    {
      if (component is IAcSmSheet sheet)
      {
        yield return sheet;
      }
      else if (component is IAcSmSubset subset)
      {
        foreach (var inner in Walk(subset.GetSheetEnumerator()))
        {
          yield return inner;
        }
      }
    }
  }

  /// <summary>A sheet's name, number, title, layout and drawing (resolved path, else the recorded one).</summary>
  private static Sheet Describe(IAcSmSheet sheet)
  {
    var layout = (IAcSmAcDbLayoutReference?)sheet.GetLayout();
    string? drawing = null;
    try
    {
      drawing = layout?.ResolveFileName();
    }
    catch (COMException)
    {
      // The drawing has moved: what the set recorded is still the best name for it.
    }

    return new Sheet(sheet.GetName(), sheet.GetNumber(), sheet.GetTitle(), layout?.GetName(), drawing ?? layout?.GetFileName(), sheet.GetDoNotPlot());
  }

  /// <summary>One past the highest whole number in use, so "1", "2", "A-3" gets "3"; else one past the count.</summary>
  private static string NextNumber(List<Sheet> existing)
  {
    var numbers = existing.Select(sheet => int.TryParse(sheet.Number, out var number) ? number : 0).ToList();
    return (numbers.DefaultIfEmpty(0).Max() is var highest && highest > 0 ? highest + 1 : existing.Count + 1).ToString();
  }

  /// <summary>The last top-level component (sheet or subset), or null for an empty set.</summary>
  private static IAcSmComponent? LastComponent(IAcSmEnumComponent components)
  {
    IAcSmComponent? last = null;
    for (var component = components.Next(); component != null; component = components.Next())
    {
      last = component;
    }

    return last;
  }

  /// <summary>Imports one layout of the saved drawing as a sheet at the end of the set (the database must be locked).
  /// A COM failure becomes CIVIL3D.API_ERROR asking to save the drawing.</summary>
  private static void AddLayout(AcSmDatabase database, AcSmSheetSet sheetSet, string drawingPath, string layoutName, string title, string number)
  {
    try
    {
      var layout = Make<IAcSmAcDbLayoutReference>("AcSmAcDbLayoutReference");
      layout.InitNew((IAcSmPersist)database);
      layout.SetFileName(drawingPath);
      layout.SetName(layoutName);
      var sheet = sheetSet.ImportSheet((AcSmAcDbLayoutReference)layout);
      sheet.SetNumber(number);
      sheet.SetTitle(title);

      // InsertComponent with no "before" puts a sheet at the front, which would publish a set last sheet first.
      var last = LastComponent(sheetSet.GetSheetEnumerator());
      if (last == null)
      {
        sheetSet.InsertComponent((IAcSmComponent)sheet, null);
      }
      else
      {
        sheetSet.InsertComponentAfter((IAcSmComponent)sheet, last);
      }
    }
    catch (COMException exception)
    {
      // It reads the file on disk, so a layout made since the last save isn't there.
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
        $"Layout '{layoutName}' couldn't be added from the saved drawing (0x{exception.HResult:X8}). Save the drawing and try again.");
    }
  }
}
