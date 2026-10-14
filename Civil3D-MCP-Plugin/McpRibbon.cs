using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// "Civil 3D MCP" ribbon tab. One panel, three rows:
///   [AI Connection: ON/OFF]   toggle that turns the connection on/off for this session
///   (green/red dot) Running / Off   status line, readable at a glance
///   [Details]                 writes queue/activity details to the command line
/// The connection is off until the user turns it on.
/// </summary>
internal static class McpRibbon
{
  private const string TabId = "Civil3DMcp.Tab";
  private static readonly Color OnColor = Color.FromRgb(46, 158, 68);
  private static readonly Color OffColor = Color.FromRgb(198, 40, 40);
  private static readonly Lazy<ImageSource> OnDot = new(() => Dot(OnColor));
  private static readonly Lazy<ImageSource> OffDot = new(() => Dot(OffColor));

  private static RibbonToggleButton? _toggle;
  private static RibbonLabel? _status;
  private static bool _subscribed;

  public static void Initialize()
  {
    if (!_subscribed)
    {
      _subscribed = true;
      // The ribbon may not exist yet at startup, and switching workspaces rebuilds it.
      ComponentManager.ItemInitialized += (_, _) => EnsureTab();
      App.SystemVariableChanged += (_, e) =>
      {
        if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
        {
          EnsureTab();
        }
      };
    }

    EnsureTab();
  }

  /// <summary>Updates the toggle and the status line to match the listener state.</summary>
  public static void Refresh()
  {
    if (_toggle == null || _status == null)
    {
      return;
    }

    var running = PluginRuntime.GetStatus().IsRunning;
    _toggle.IsChecked = running;
    _toggle.Text = running ? "AI Connection: ON" : "AI Connection: OFF";
    _toggle.ToolTip = running
      ? $"AI assistants can work in this Civil 3D session (port {PluginRuntime.Port}). Click to turn off."
      : "AI assistants can't reach this Civil 3D session. Click to turn on.";
    _status.Text = running ? "Running" : "Off";
    _status.Image = running ? OnDot.Value : OffDot.Value;
  }

  private static void EnsureTab()
  {
    try
    {
      var ribbon = ComponentManager.Ribbon;
      if (ribbon == null)
      {
        return;
      }

      if (ribbon.FindTab(TabId) != null)
      {
        Refresh();
        return;
      }

      var source = new RibbonPanelSource { Title = "AI Connection" };
      _toggle = new RibbonToggleButton
      {
        ShowText = true,
        ShowImage = false,
        Size = RibbonItemSize.Standard,
        CommandHandler = new ActionCommand(PluginEntry.ToggleConnection),
      };
      _status = new RibbonLabel { ShowText = true, ShowImage = true };
      var details = new RibbonButton
      {
        Text = "Details",
        ShowText = true,
        ShowImage = false,
        Size = RibbonItemSize.Standard,
        ToolTip = "Show what the AI connection is doing (queue and current request) on the command line.",
        CommandHandler = new ActionCommand(PluginEntry.ShowStatus),
      };
      source.Items.Add(_toggle);
      source.Items.Add(new RibbonRowBreak());
      source.Items.Add(_status);
      source.Items.Add(new RibbonRowBreak());
      source.Items.Add(details);

      var tab = new RibbonTab { Title = "Civil 3D MCP", Id = TabId };
      tab.Panels.Add(new RibbonPanel { Source = source });
      ribbon.Tabs.Add(tab);
      Refresh();
    }
    catch (System.Exception ex)
    {
      // The ribbon is a convenience; commands keep working without it.
      PluginLog.Warn("Ribbon", $"Could not create the Civil 3D MCP ribbon tab: {ex.Message}");
    }
  }

  // Dots are drawn in code so no image files ship with the plugin.
  private static ImageSource Dot(Color color)
  {
    const int size = 16;
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
      var edge = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
      context.DrawEllipse(new SolidColorBrush(color), edge, new System.Windows.Point(size / 2.0, size / 2.0), 6, 6);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
  }

  private sealed class ActionCommand : ICommand
  {
    private readonly Action _action;

    public ActionCommand(Action action) => _action = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _action();
  }
}
