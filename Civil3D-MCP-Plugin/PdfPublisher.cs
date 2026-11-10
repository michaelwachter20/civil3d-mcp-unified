// Plots layouts of the active drawing to one multi-page PDF with AutoCAD's publish engine ("DWG To PDF.pc3").
// Callers: PlanProductionCommands.PublishSheetPdfAsync (publishSheetPdf) and ExportSheetSetAsync (exportSheetSet),
// civil3d_plan_production (src/tools/domains/planProductionDomain.ts); also reached through the publish_sheet_pdf
// background job (JobCommands) and a WorkflowCommands workflow.
// Serves no JSON-RPC method itself. Runs inside the caller's CivilExecution.WriteAsync (main thread, command context,
// document lock), because a layout must be made current to plot it. The output path is resolved by the caller through
// FileBoundary; the plot engine writes the file. Changes no drawing objects: the current layout and BACKGROUNDPLOT
// are restored afterwards, even after a failure.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plots layouts of the active drawing to one PDF with AutoCAD's plot engine and "DWG To PDF.pc3".
/// <para>
/// BACKGROUNDPLOT is off for the plot: on, the engine returns at once and the file turns up later, so nothing here
/// could say whether it worked. The answer is given only once the file exists. Must run under a document lock, on
/// the main thread (a write): a layout can be plotted only while it is the current one, and the layout the user was on
/// and the system variable are put back afterwards, also after a failure.
/// </para>
/// </summary>
internal static class PdfPublisher
{
  private const string PdfDevice = "DWG To PDF.pc3";

  /// <summary>
  /// Plots <paramref name="layoutNames"/> (in that order, one page each) to <paramref name="outputPath"/> and returns
  /// the PDF's size in bytes. <paramref name="paperSize"/> is part of a media name ("ANSI_D"); <paramref
  /// name="plotStyleTable"/> a .ctb/.stb name. Throws CIVIL3D.HOST_BUSY when AutoCAD is already plotting,
  /// CIVIL3D.INVALID_INPUT for Model, CIVIL3D.OBJECT_NOT_FOUND for an unknown layout or paper size, and
  /// CIVIL3D.API_ERROR when the engine finishes but no file (or an empty one) exists.
  /// </summary>
  public static long Publish(Document document, Transaction transaction, IReadOnlyList<string> layoutNames, string outputPath, string? paperSize, string? plotStyleTable)
  {
    if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
    {
      throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", "Civil 3D is already plotting. Try again when that finishes.");
    }

    var manager = LayoutManager.Current;
    var previousLayout = manager.CurrentLayout;
    var previousBackground = App.GetSystemVariable("BACKGROUNDPLOT");
    App.SetSystemVariable("BACKGROUNDPLOT", 0);
    try
    {
      using var engine = PlotFactory.CreatePublishEngine();
      engine.BeginPlot(null, null);
      for (var index = 0; index < layoutNames.Count; index++)
      {
        var info = PlotInfoFor(manager, transaction, layoutNames[index], paperSize, plotStyleTable);
        if (index == 0)
        {
          engine.BeginDocument(info, document.Name, null, 1, true, outputPath);
        }

        using var page = new PlotPageInfo();
        engine.BeginPage(page, info, index == layoutNames.Count - 1, null);
        engine.BeginGenerateGraphics(null);
        engine.EndGenerateGraphics(null);
        engine.EndPage(null);
      }

      engine.EndDocument(null);
      engine.EndPlot(null);
    }
    finally
    {
      App.SetSystemVariable("BACKGROUNDPLOT", previousBackground);
      manager.CurrentLayout = previousLayout;
    }

    var written = new FileInfo(outputPath);
    if (!written.Exists || written.Length == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D finished plotting but wrote no file at {outputPath}.");
    }

    return written.Length;
  }

  /// <summary>Makes the layout current and builds its validated plot info: the layout's own settings with the PDF
  /// device, the chosen paper, plot type Layout and the optional plot style table. The layout itself is not changed
  /// (the settings are an override). Throws CIVIL3D.INVALID_INPUT for Model, CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// layout.</summary>
  private static PlotInfo PlotInfoFor(LayoutManager manager, Transaction transaction, string layoutName, string? paperSize, string? plotStyleTable)
  {
    if (string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Publish plots layout tabs, not Model. Name a layout, with a viewport on it.");
    }

    var layoutId = manager.GetLayoutId(layoutName);
    if (layoutId.IsNull)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layoutName}' was not found.");
    }

    manager.CurrentLayout = layoutName;
    var layout = CivilObjectUtils.GetRequiredObject<Layout>(transaction, layoutId, OpenMode.ForRead);
    var settings = new PlotSettings(layout.ModelType);
    settings.CopyFrom(layout);

    var validator = PlotSettingsValidator.Current;
    validator.SetPlotConfigurationName(settings, PdfDevice, null);
    validator.RefreshLists(settings);
    validator.SetPlotConfigurationName(settings, PdfDevice, ChooseMedia(validator, settings, layout.CanonicalMediaName, paperSize));
    validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
    if (!string.IsNullOrWhiteSpace(plotStyleTable))
    {
      validator.SetCurrentStyleSheet(settings, plotStyleTable);
    }

    var info = new PlotInfo { Layout = layoutId, OverrideSettings = settings };
    new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
    return info;
  }

  /// <summary>The paper asked for (part of its name, "ANSI_D"), else the layout's own if the PDF device has it, else
  /// 11x17 landscape, else the device's first. An unmatched <paramref name="paperSize"/> throws
  /// CIVIL3D.OBJECT_NOT_FOUND listing the device's media (LookupUtils.NotFound).</summary>
  private static string ChooseMedia(PlotSettingsValidator validator, PlotSettings settings, string layoutMedia, string? paperSize)
  {
    var media = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
    if (!string.IsNullOrWhiteSpace(paperSize))
    {
      return media.FirstOrDefault(name => name.Contains(paperSize, StringComparison.OrdinalIgnoreCase))
        ?? throw LookupUtils.NotFound("Paper size", paperSize, media);
    }

    return media.FirstOrDefault(name => name == layoutMedia)
      ?? media.FirstOrDefault(name => name.Contains("ANSI_B", StringComparison.Ordinal) && name.Contains("17.00_x_11.00", StringComparison.Ordinal))
      ?? media.First();
  }
}
