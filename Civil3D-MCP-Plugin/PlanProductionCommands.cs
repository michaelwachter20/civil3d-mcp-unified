using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for Plan Production / Sheets MCP tools.
///
/// A sheet set is a .dst file, not an object in the drawing, so everything about one goes through SheetSetCom. Sheets
/// made by Create View Frames are ordinary layouts; their view frames are in the drawing and keep the alignment they
/// were made from.
///
/// PDF publishing uses Autodesk.AutoCAD.PlottingServices (PlotInfo, PlotEngine,
/// PlotSettings) which is available in all AutoCAD-platform products.
/// </summary>
public static class PlanProductionCommands
{
  // -------------------------------------------------------------------------
  // listSheetSets
  // -------------------------------------------------------------------------

  /// <summary>
  /// The .dst files beside the active drawing and one folder up, where a sheet set usually sits. A sheet set isn't
  /// tied to a drawing, so there is no asking Civil 3D which ones belong to this job.
  /// </summary>
  public static Task<object?> ListSheetSetsAsync()
  {
    return CivilExecution.OnActiveDocumentAsync<object?>(doc =>
    {
      var folders = new List<string>();
      if (Path.IsPathRooted(doc.Name))
      {
        var folder = Path.GetDirectoryName(doc.Name)!;
        folders.Add(folder);
        if (Path.GetDirectoryName(folder) is { } parent) folders.Add(parent);
      }

      var sheetSets = new List<object?>();
      foreach (var file in folders.SelectMany(folder => Directory.GetFiles(folder, "*.dst")))
      {
        try
        {
          var info = SheetSetCom.Read(file);
          sheetSets.Add(new Dictionary<string, object?>
          {
            ["name"] = info["name"],
            ["path"] = file,
            ["description"] = info["description"],
            ["sheetCount"] = ((System.Collections.ICollection)info["sheets"]!).Count,
          });
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
          sheetSets.Add(new Dictionary<string, object?> { ["path"] = file, ["error"] = exception.Message });
        }
      }

      return new Dictionary<string, object?> { ["searched"] = folders, ["sheetSets"] = sheetSets };
    });
  }

  // -------------------------------------------------------------------------
  // getSheetSetInfo
  // -------------------------------------------------------------------------

  public static Task<object?> GetSheetSetInfoAsync(JsonObject? parameters)
  {
    var path = SheetSetFile(PluginRuntime.GetRequiredString(parameters, "name"));
    return CivilExecution.OnActiveDocumentAsync<object?>(_ => SheetSetCom.Read(path));
  }

  // -------------------------------------------------------------------------
  // createSheetSet
  // -------------------------------------------------------------------------

  /// <summary>
  /// A new sheet set file (.dst) at <c>outputPath</c>, with a sheet for each of <c>layoutNames</c> of the active drawing,
  /// which must be saved, since a sheet is a reference to a layout in a file.
  /// </summary>
  public static Task<object?> CreateSheetSetAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var outputPath = FileBoundary.ResolveExportPath(PluginRuntime.GetRequiredString(parameters, "outputPath"), overwrite, ".dst");
    var layoutNames = (PluginRuntime.GetParameter(parameters, "layoutNames") as JsonArray)?
      .Select(node => node?.GetValue<string>())
      .Where(layout => !string.IsNullOrWhiteSpace(layout))
      .Select(layout => layout!)
      .ToList() ?? [];

    return CivilExecution.OnActiveDocumentAsync<object?>(doc =>
    {
      RequireSheetLayouts(doc, layoutNames);
      return SheetSetCom.Create(outputPath, name, description, doc.Name, layoutNames, overwrite);
    });
  }

  // -------------------------------------------------------------------------
  // addSheet
  // -------------------------------------------------------------------------

  /// <summary>
  /// Adds layout <c>layoutName</c> of the active drawing (default: the one named <c>sheetName</c>) to the sheet set file
  /// <c>sheetSetName</c>, numbered <c>sheetNumber</c> or after the last sheet.
  /// </summary>
  public static Task<object?> AddSheetAsync(JsonObject? parameters)
  {
    var sheetSetPath = SheetSetFile(PluginRuntime.GetRequiredString(parameters, "sheetSetName"));
    var sheetName = PluginRuntime.GetRequiredString(parameters, "sheetName");
    var sheetNumber = PluginRuntime.GetOptionalString(parameters, "sheetNumber");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layoutName") ?? sheetName;

    return CivilExecution.OnActiveDocumentAsync<object?>(doc =>
    {
      RequireSheetLayouts(doc, [layoutName]);
      return SheetSetCom.AddSheet(sheetSetPath, sheetName, sheetNumber, doc.Name, layoutName);
    });
  }

  /// <summary>A .dst path, checked. A sheet set is a file: the name of one means nothing without where it is.</summary>
  private static string SheetSetFile(string sheetSetName)
  {
    if (!sheetSetName.EndsWith(".dst", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{sheetSetName}' isn't a sheet set file: give the path of the .dst, as sheet_set_create returned it.");
    }

    return FileBoundary.ResolveImportPath(sheetSetName, ".dst");
  }

  /// <summary>
  /// Checks before the library is asked, since its own answer is a bare 0x80040211, with an empty sheet set left behind.
  /// A sheet is a paper layout in a saved file, so model space and an unsaved drawing can't be one.
  /// </summary>
  private static void RequireSheetLayouts(Document doc, IReadOnlyCollection<string> layoutNames)
  {
    if (layoutNames.Count == 0)
    {
      return;
    }

    if (!Path.IsPathRooted(doc.Name))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Save the drawing first: a sheet refers to a layout in a saved file.");
    }

    using var transaction = doc.Database.TransactionManager.StartOpenCloseTransaction();
    var layouts = (DBDictionary)transaction.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
    var usable = layouts.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key)
      .Where(name => !string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase))
      .ToList();
    var missing = layoutNames.Where(layout => !usable.Contains(layout, StringComparer.OrdinalIgnoreCase)).ToList();
    if (missing.Count > 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"No layout named {string.Join(", ", missing.Select(layout => $"'{layout}'"))}. This drawing has: {string.Join(", ", usable)}.");
    }
  }

  // -------------------------------------------------------------------------
  // getSheetProperties
  // -------------------------------------------------------------------------

  /// <summary>One sheet of a .dst file, by its name or number.</summary>
  public static Task<object?> GetSheetPropertiesAsync(JsonObject? parameters)
  {
    var path = SheetSetFile(PluginRuntime.GetRequiredString(parameters, "sheetSetName"));
    var sheetName = PluginRuntime.GetRequiredString(parameters, "sheetName");

    return CivilExecution.OnActiveDocumentAsync<object?>(_ =>
    {
      var sheets = SheetSetCom.Sheets(path);
      var sheet = sheets.FirstOrDefault(item => string.Equals(item.Name, sheetName, StringComparison.OrdinalIgnoreCase)
          || string.Equals(item.Number, sheetName, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No sheet '{sheetName}' in this set. It has: {string.Join(", ", sheets.Select(item => item.Name))}.");

      return new Dictionary<string, object?>
      {
        ["name"] = sheet.Name,
        ["number"] = sheet.Number,
        ["title"] = sheet.Title,
        ["layout"] = sheet.Layout,
        ["drawing"] = sheet.Drawing,
        ["doNotPlot"] = sheet.DoNotPlot,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setSheetTitleBlock
  // -------------------------------------------------------------------------

  public static Task<object?> SetSheetTitleBlockAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "A sheet has no title block setting: the title block is a block on the sheet's layout. Insert or swap it on the layout. Nothing was changed.");
  }

  // -------------------------------------------------------------------------
  // createPlanProfileSheet
  // -------------------------------------------------------------------------

  public static Task<object?> CreatePlanProfileSheetAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D does not document a managed plan/profile sheet factory. No sheet, view frame, or placeholder layout was created.");
  }

  // -------------------------------------------------------------------------
  // updatePlanProfileSheetAlignment
  // -------------------------------------------------------------------------

  public static Task<object?> UpdatePlanProfileSheetAlignmentAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "A view frame's alignment can't be changed once it's made (ViewFrame.AlignmentId is read-only). Make new view frames with Create View Frames. Nothing was changed.");
  }

  // -------------------------------------------------------------------------
  // createSheetView
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSheetViewAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewName = PluginRuntime.GetOptionalString(parameters, "viewName");
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "centerX") ?? 0.0;
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "centerY") ?? 0.0;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 8.0;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 6.0;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = FindLayoutByName(database, transaction, layoutName);
      if (layout.ModelType)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A viewport goes on a layout tab, not on Model.");
      }

      // Create viewport in paper space
      var viewport = new Viewport
      {
        CenterPoint = new Point3d(centerX, centerY, 0),
        Width = width,
        Height = height,
      };

      if (scale.HasValue)
      {
        viewport.CustomScale = 1.0 / scale.Value;
      }

      var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var layoutBlock = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

      layoutBlock.AppendEntity(viewport);
      transaction.AddNewlyCreatedDBObject(viewport, true);
      viewport.On = true;

      // A new viewport looks at the origin, which shows nothing on a job in state plane coordinates: aim it at the
      // drawing. With no scale asked for, the whole of it fits.
      if (CivilObjectUtils.ModelExtents(database, transaction) is Extents3d model)
      {
        var modelWidth = model.MaxPoint.X - model.MinPoint.X;
        var modelHeight = model.MaxPoint.Y - model.MinPoint.Y;
        viewport.ViewCenter = new Point2d(model.MinPoint.X + modelWidth / 2, model.MinPoint.Y + modelHeight / 2);
        if (!scale.HasValue)
        {
          viewport.ViewHeight = Math.Max(modelHeight, modelWidth * height / width) * 1.05;
        }
      }

      // If a named view is requested, set it on the viewport
      if (!string.IsNullOrWhiteSpace(viewName))
      {
        TryApplyNamedViewToViewport(database, transaction, viewport, viewName!);
      }

      return new Dictionary<string, object?>
      {
        ["handle"] = viewport.Handle.ToString(),
        ["layoutName"] = layoutName,
        ["scale"] = scale,
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setSheetViewScale
  // -------------------------------------------------------------------------

  public static Task<object?> SetSheetViewScaleAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewportHandle = PluginRuntime.GetOptionalString(parameters, "viewportHandle");
    var scale = PluginRuntime.GetRequiredDouble(parameters, "scale");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = FindLayoutByName(database, transaction, layoutName);
      var viewport = FindViewport(database, transaction, layout, viewportHandle);

      viewport.UpgradeOpen();
      // Setting this makes the scale custom by itself; setting StandardScale to CustomScale as well is refused.
      viewport.CustomScale = 1.0 / scale;

      return new Dictionary<string, object?>
      {
        ["handle"] = viewport.Handle.ToString(),
        ["scale"] = scale,
        ["updated"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // publishSheetPdf
  // -------------------------------------------------------------------------

  public static Task<object?> PublishSheetPdfAsync(JsonObject? parameters)
  {
    var layoutNamesNode = parameters?["layoutNames"] as JsonArray;
    if (layoutNamesNode == null || layoutNamesNode.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'layoutNames' must be a non-empty array.");

    var layoutNames = layoutNamesNode.Select(node => node?.GetValue<string>() ?? string.Empty).ToList();
    var outputPath = FileBoundary.ResolveExportPath(
      PluginRuntime.GetRequiredString(parameters, "outputPath"),
      PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false,
      ".pdf");
    var paperSize = PluginRuntime.GetOptionalString(parameters, "paperSize");
    var plotStyleTable = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var size = PdfPublisher.Publish(doc, transaction, layoutNames, outputPath, paperSize, plotStyleTable);
      return new Dictionary<string, object?>
      {
        ["outputPath"] = outputPath,
        ["sheetsPublished"] = layoutNames.Count,
        ["layouts"] = layoutNames,
        ["sizeBytes"] = size,
        ["published"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // exportSheetSet
  // -------------------------------------------------------------------------

  /// <summary>
  /// Publishes the sheets of the sheet set file <c>sheetSetName</c> to one PDF, in the set's order, leaving out those
  /// marked do-not-plot. The plot engine works on the active drawing, so a set with sheets in other drawings is refused
  /// by name: a PDF missing some of them would look like the whole set.
  /// </summary>
  public static Task<object?> ExportSheetSetAsync(JsonObject? parameters)
  {
    var sheetSetPath = SheetSetFile(PluginRuntime.GetRequiredString(parameters, "sheetSetName"));
    var outputPath = FileBoundary.ResolveExportPath(
      PluginRuntime.GetRequiredString(parameters, "outputPath"),
      PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false,
      ".pdf");
    var paperSize = PluginRuntime.GetOptionalString(parameters, "paperSize");
    var plotStyleTable = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sheets = SheetSetCom.Sheets(sheetSetPath);
      var plotted = sheets.Where(sheet => !sheet.DoNotPlot).ToList();
      if (plotted.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "This sheet set has no sheets to plot.");
      }

      var elsewhere = plotted.Where(sheet => !SheetSetCom.SameFile(sheet.Drawing, doc.Name)).ToList();
      if (elsewhere.Count > 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "Nothing was published: this set has sheets in other drawings, and only the active drawing can be plotted: "
          + string.Join(", ", elsewhere.Select(sheet => $"{sheet.Name} ({Path.GetFileName(sheet.Drawing)})")) + ".");
      }

      var layoutNames = plotted.Select(sheet => sheet.Layout!).ToList();
      var size = PdfPublisher.Publish(doc, transaction, layoutNames, outputPath, paperSize, plotStyleTable);
      return new Dictionary<string, object?>
      {
        ["sheetSetName"] = sheetSetPath,
        ["outputPath"] = outputPath,
        ["sheetsExported"] = layoutNames.Count,
        ["sheetsSkipped"] = sheets.Count - layoutNames.Count,
        ["layouts"] = layoutNames,
        ["sizeBytes"] = size,
        ["exported"] = true,
      };
    });
  }

  // =========================================================================
  // Private helpers
  // =========================================================================

  private static Layout FindLayoutByName(Autodesk.AutoCAD.DatabaseServices.Database database, Transaction transaction, string layoutName)
  {
    var layoutDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
    foreach (DictionaryEntry entry in layoutDict)
    {
      var name = entry.Key as string;
      if (string.Equals(name, layoutName, StringComparison.OrdinalIgnoreCase))
      {
        if (entry.Value is ObjectId oid)
          return (Layout)transaction.GetObject(oid, OpenMode.ForRead);
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layoutName}' was not found.");
  }

  private static Viewport FindViewport(
    Autodesk.AutoCAD.DatabaseServices.Database database,
    Transaction transaction,
    Layout layout,
    string? viewportHandle)
  {
    var layoutBlock = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

    Viewport? first = null;
    foreach (ObjectId id in layoutBlock)
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead);
      if (obj is Viewport vp)
      {
        if (!string.IsNullOrWhiteSpace(viewportHandle) &&
            string.Equals(vp.Handle.ToString(), viewportHandle, StringComparison.OrdinalIgnoreCase))
          return vp;
        first ??= vp;
      }
    }

    if (first != null) return first;
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No viewport found in layout '{layout.LayoutName}'.");
  }

  private static void TryApplyNamedViewToViewport(
    Autodesk.AutoCAD.DatabaseServices.Database database,
    Transaction transaction,
    Viewport viewport,
    string viewName)
  {
    var viewTable = (ViewTable)transaction.GetObject(database.ViewTableId, OpenMode.ForRead);
    if (!viewTable.Has(viewName)) return;

    var viewRecord = (ViewTableRecord)transaction.GetObject(viewTable[viewName], OpenMode.ForRead);
    viewport.ViewCenter = new Point2d(viewRecord.CenterPoint.X, viewRecord.CenterPoint.Y);
    viewport.ViewHeight = viewRecord.Height;
  }

  // -------------------------------------------------------------------------
  // General reflection helpers (mirror of PressureNetworkCommands pattern)
  // -------------------------------------------------------------------------

  private static string? GetName(object? value) => CivilObjectUtils.GetName(value);

}
