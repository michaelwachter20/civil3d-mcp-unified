// Plan production handlers for civil3d_plan_production (src/tools/domains/planProductionDomain.ts): sheet set files,
// layout viewports and PDF publishing.
// JSON-RPC methods: listSheetSets, getSheetSetInfo, getSheetProperties, createSheetSet, addSheet (sheet set .dst files
// through SheetSetCom, run with CivilExecution.OnActiveDocumentAsync: main thread, host gate, no transaction, no
// drawing change); createSheetView, setSheetViewScale (CivilExecution.WriteAsync: command context, document lock, one
// transaction, one undo step; a scale of 0 or less, or an unclear viewport, is refused); publishSheetPdf, exportSheetSet (WriteAsync around PdfPublisher; also reached through
// the publish_sheet_pdf job and WorkflowCommands); setSheetTitleBlock, createPlanProfileSheet,
// updatePlanProfileSheetAlignment (always refused with CIVIL3D.API_ERROR: no API for them; nothing changes).
// Paths: .dst and .pdf outputs, and an existing .dst that addSheet changes, through FileBoundary.ResolveExportPath;
// existing .dst files that are only read through ResolveImportPath.
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
  /// listSheetSets (no parameters, reads only): returns searched (the folders; none for an unsaved drawing) and
  /// sheetSets (name, path, description, sheetCount, or path and error for a file SheetSetCom can't open).
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

  /// <summary>getSheetSetInfo (reads only): the sheet set in the .dst file <c>name</c> (required: a path, inside the
  /// import roots) as SheetSetCom.Read returns it (name, description, sheets). Throws CIVIL3D.INVALID_INPUT when the
  /// name isn't a .dst path, and FileBoundary's errors.</summary>
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
  /// createSheetSet: <c>name</c> and <c>outputPath</c> (.dst, export roots) required; <c>description</c>,
  /// <c>layoutNames</c> and <c>overwrite</c> (default false) optional. Writes the file; the drawing is not changed.
  /// Throws CIVIL3D.INVALID_INPUT for an unsaved drawing (when layouts are named), CIVIL3D.OBJECT_NOT_FOUND for a
  /// missing layout, and FileBoundary's errors (an existing file without overwrite included).
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
  /// addSheet: <c>sheetSetName</c> (.dst path) and <c>sheetName</c> required. Changes the .dst file, so it is resolved
  /// for writing (<see cref="SheetSetFileToChange"/>: export roots, must already exist), not the drawing. Same errors as
  /// createSheetSet, plus CIVIL3D.OBJECT_NOT_FOUND for a sheet set file that doesn't exist.
  /// </summary>
  public static Task<object?> AddSheetAsync(JsonObject? parameters)
  {
    var sheetSetPath = SheetSetFileToChange(PluginRuntime.GetRequiredString(parameters, "sheetSetName"));
    var sheetName = PluginRuntime.GetRequiredString(parameters, "sheetName");
    var sheetNumber = PluginRuntime.GetOptionalString(parameters, "sheetNumber");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layoutName") ?? sheetName;

    return CivilExecution.OnActiveDocumentAsync<object?>(doc =>
    {
      RequireSheetLayouts(doc, [layoutName]);
      return SheetSetCom.AddSheet(sheetSetPath, sheetName, sheetNumber, doc.Name, layoutName);
    });
  }

  /// <summary>A .dst path, checked. A sheet set is a file: the name of one means nothing without where it is.
  /// CIVIL3D.INVALID_INPUT when it doesn't end in .dst; otherwise FileBoundary.ResolveImportPath (file must exist inside
  /// the import roots).</summary>
  private static string SheetSetFile(string sheetSetName)
  {
    RequireDstName(sheetSetName);
    return FileBoundary.ResolveImportPath(sheetSetName, ".dst");
  }

  /// <summary>An existing .dst path that is about to be changed: resolved like an output (FileBoundary.ResolveExportPath,
  /// so inside the export roots and never in the shared knowledge folder; overwriting is the point) and required to
  /// exist. CIVIL3D.INVALID_INPUT when it doesn't end in .dst, CIVIL3D.OBJECT_NOT_FOUND when there is no such file.</summary>
  private static string SheetSetFileToChange(string sheetSetName)
  {
    RequireDstName(sheetSetName);
    var path = FileBoundary.ResolveExportPath(sheetSetName, overwrite: true, ".dst");
    if (!File.Exists(path))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"Sheet set file was not found: {path}. Make it with sheet_set_create first.");
    }

    return path;
  }

  /// <summary>CIVIL3D.INVALID_INPUT unless the name is a .dst path: a sheet set is a file, and its name means nothing
  /// without where it is.</summary>
  private static void RequireDstName(string sheetSetName)
  {
    if (!sheetSetName.EndsWith(".dst", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{sheetSetName}' isn't a sheet set file: give the path of the .dst, as sheet_set_create returned it.");
    }
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

  /// <summary>getSheetProperties (reads only): one sheet of the .dst file <c>sheetSetName</c>, found by
  /// <c>sheetName</c> matching its name or number (both required). Returns name, number, title, layout, drawing and
  /// doNotPlot. Throws CIVIL3D.OBJECT_NOT_FOUND listing the sheets when none matches.</summary>
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

  /// <summary>setSheetTitleBlock: reads <c>sheetName</c> (required) and always throws CIVIL3D.API_ERROR: a title
  /// block is a block on the layout, not a sheet setting. Nothing changes.</summary>
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

  /// <summary>createPlanProfileSheet: reads <c>sheetSetName</c> and <c>alignmentName</c> (required) and always throws
  /// CIVIL3D.API_ERROR: there is no documented API to make plan/profile sheets. Nothing is created.</summary>
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

  /// <summary>updatePlanProfileSheetAlignment: reads <c>alignmentName</c> (required) and always throws
  /// CIVIL3D.API_ERROR: a view frame's alignment is read-only. Nothing changes.</summary>
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

  /// <summary>
  /// createSheetView (changes the drawing, one undo step): adds a viewport to layout <c>layoutName</c> (required) at
  /// <c>centerX</c>/<c>centerY</c> (paper units, default 0,0), <c>width</c> x <c>height</c> (default 8 x 6), aimed at
  /// the middle of the model extents. <c>scale</c> (optional) is the drawing scale denominator, e.g. 40 for 1"=40'
  /// (CustomScale = 1/scale); without it the whole model fits. <c>viewName</c> (optional) recentres it on a named view
  /// and is ignored when there is no such view. Returns handle, layoutName, scale and created. Throws
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown layout and CIVIL3D.INVALID_INPUT for Model or a scale of 0 or less.
  /// </summary>
  public static Task<object?> CreateSheetViewAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewName = PluginRuntime.GetOptionalString(parameters, "viewName");
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "centerX") ?? 0.0;
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "centerY") ?? 0.0;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 8.0;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 6.0;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale");
    if (scale.HasValue)
    {
      RequireScale(scale.Value);
    }

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

  /// <summary>setSheetViewScale (changes the drawing, one undo step): sets CustomScale = 1/<c>scale</c> (required,
  /// above 0: the drawing scale denominator, e.g. 40 for 1"=40') on the model viewport <c>viewportHandle</c> of layout
  /// <c>layoutName</c> (required). Without a handle the layout's only model viewport is used; with several, none, a
  /// handle that matches none of them, or the layout's own paper-space viewport, it refuses (see
  /// <see cref="FindViewport"/>). Returns handle, scale and updated. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// layout and CIVIL3D.INVALID_INPUT for a bad scale or viewport choice (the message lists the model viewports).</summary>
  public static Task<object?> SetSheetViewScaleAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewportHandle = PluginRuntime.GetOptionalString(parameters, "viewportHandle");
    var scale = PluginRuntime.GetRequiredDouble(parameters, "scale");
    RequireScale(scale);

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

  /// <summary>
  /// publishSheetPdf: plots the active drawing's <c>layoutNames</c> (required, non-empty) to one PDF at
  /// <c>outputPath</c> (required, .pdf, export roots; <c>overwrite</c> default false) with optional
  /// <c>paperSize</c> and <c>plotStyleTable</c> (PdfPublisher). Runs as a write (the layouts must be made current) but
  /// leaves no drawing change. Returns outputPath, sheetsPublished, layouts, sizeBytes and published. Throws
  /// CIVIL3D.INVALID_INPUT for an empty list or Model, plus PdfPublisher's and FileBoundary's errors.
  /// </summary>
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
  /// exportSheetSet: <c>sheetSetName</c> (.dst path) and <c>outputPath</c> (.pdf) required; <c>overwrite</c>,
  /// <c>paperSize</c>, <c>plotStyleTable</c> optional. Returns sheetSetName, outputPath, sheetsExported,
  /// sheetsSkipped, layouts, sizeBytes and exported. Throws CIVIL3D.INVALID_INPUT for a set with nothing to plot or
  /// with sheets in other drawings, plus PdfPublisher's and FileBoundary's errors.
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

  /// <summary>The layout named <paramref name="layoutName"/> (ignoring case; Model included), open for read;
  /// CIVIL3D.OBJECT_NOT_FOUND otherwise.</summary>
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

  /// <summary>
  /// The model viewport to change on the layout (open for read). A layout's first viewport (Layout.GetViewports) is
  /// its own paper-space viewport (the sheet itself), which has no drawing scale: it is never picked and naming it is
  /// refused. With
  /// <paramref name="viewportHandle"/> the model viewport with that handle; without one, the layout's only model
  /// viewport. CIVIL3D.INVALID_INPUT, listing the layout's model viewports (handle, centre, size, scale), when the
  /// layout has none, the handle matches none of them, or there are several and no handle was given.
  /// </summary>
  private static Viewport FindViewport(
    Autodesk.AutoCAD.DatabaseServices.Database database,
    Transaction transaction,
    Layout layout,
    string? viewportHandle)
  {
    var layoutBlock = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

    // Layout.GetViewports lists the paper-space viewport first; a layout never opened has none yet, and then every
    // viewport in it is a model viewport (one made by sheet_view_create, say).
    var layoutViewports = layout.GetViewports();
    var paperSpaceId = layoutViewports != null && layoutViewports.Count > 0 ? layoutViewports[0] : ObjectId.Null;
    Viewport? paperSpace = null;
    var modelViewports = new List<Viewport>();
    foreach (ObjectId id in layoutBlock)
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
      if (id == paperSpaceId) paperSpace = vp;
      else modelViewports.Add(vp);
    }

    if (!string.IsNullOrWhiteSpace(viewportHandle))
    {
      var match = modelViewports.FirstOrDefault(vp => string.Equals(vp.Handle.ToString(), viewportHandle, StringComparison.OrdinalIgnoreCase));
      if (match != null) return match;
      var reason = paperSpace != null && string.Equals(paperSpace.Handle.ToString(), viewportHandle, StringComparison.OrdinalIgnoreCase)
        ? $"'{viewportHandle}' is layout '{layout.LayoutName}''s own paper-space viewport, which has no drawing scale."
        : $"Layout '{layout.LayoutName}' has no model viewport with handle '{viewportHandle}'.";
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{reason} Nothing was changed. {DescribeViewports(layout, modelViewports)}");
    }

    if (modelViewports.Count == 1) return modelViewports[0];
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      (modelViewports.Count == 0
        ? $"Layout '{layout.LayoutName}' has no model viewport to scale (make one with sheet_view_create)."
        : $"Layout '{layout.LayoutName}' has {modelViewports.Count} model viewports: send viewportHandle to say which.")
      + $" Nothing was changed. {DescribeViewports(layout, modelViewports)}");
  }

  /// <summary>The layout's model viewports for an error message: handle, centre and size in paper units, and scale
  /// as its denominator (1/CustomScale).</summary>
  private static string DescribeViewports(Layout layout, IReadOnlyCollection<Viewport> viewports)
  {
    if (viewports.Count == 0)
    {
      return string.Empty;
    }

    var invariant = System.Globalization.CultureInfo.InvariantCulture;
    return $"Model viewports on '{layout.LayoutName}': " + string.Join("; ", viewports.Select(vp => string.Format(invariant,
      "handle {0} at ({1:0.##}, {2:0.##}), {3:0.##} x {4:0.##}, scale {5}",
      vp.Handle, vp.CenterPoint.X, vp.CenterPoint.Y, vp.Width, vp.Height,
      vp.CustomScale > 0 ? (1.0 / vp.CustomScale).ToString("0.###", invariant) : "unknown"))) + ".";
  }

  /// <summary>CIVIL3D.INVALID_INPUT unless <paramref name="scale"/> (a drawing scale denominator, CustomScale =
  /// 1/scale) is a finite number above 0.</summary>
  private static void RequireScale(double scale)
  {
    if (!(scale > 0) || double.IsInfinity(scale))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"scale must be the drawing scale's denominator, a number above 0 (e.g. 40 for 1\"=40'), not {scale}. Nothing was changed.");
    }
  }

  /// <summary>Centres the viewport on the named view and takes its height; does nothing when the view doesn't
  /// exist.</summary>
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
}
