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

  /// <summary>The paper asked for (part of its name, "ANSI_D"), else the layout's own if the PDF device has it, else 11x17 landscape.</summary>
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
