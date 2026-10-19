using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

public sealed class PluginEntry : IExtensionApplication
{
  public void Initialize()
  {
    try
    {
      CivilExecution.CaptureMainThreadContext();
      CommandLineMonitor.Initialize();
      // Opt-in: AI clients can connect only after the user runs C3DMCPSTART in this session.
      // CIVIL3D_MCP_AUTOSTART=true restores starting the listener as soon as the plugin loads.
      if (string.Equals(Environment.GetEnvironmentVariable("CIVIL3D_MCP_AUTOSTART"), "true", StringComparison.OrdinalIgnoreCase))
      {
        PluginRuntime.StartServer();
        PluginLog.Info("PluginEntry", $"Civil3D MCP plugin loaded; listener auto-started on port {PluginRuntime.Port}. Log file: {PluginLog.LogFilePath}");
        WriteMessage($"Civil3D MCP plugin loaded; AI connection is ON (port {PluginRuntime.Port}). C3DMCPSTOP turns it off.");
      }
      else
      {
        PluginLog.Info("PluginEntry", $"Civil3D MCP plugin loaded; listener off until C3DMCPSTART. Log file: {PluginLog.LogFilePath}");
        WriteMessage("Civil3D MCP plugin loaded. Turn the AI connection on from the Civil 3D MCP ribbon tab or with C3DMCPSTART.");
      }

      McpRibbon.Initialize();
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Plugin failed to initialize", ex);
      WriteMessage($"Civil3D MCP plugin failed to initialize: {ex.Message}");
    }
  }

  public void Terminate()
  {
    try
    {
      PluginRuntime.StopServer();
      PluginLog.Info("PluginEntry", "Civil3D MCP plugin terminated cleanly.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Error during plugin termination", ex);
    }
  }

  [CommandMethod("C3DMCPSTART")]
  public void StartCommand() => TurnOn();

  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand() => TurnOff();

  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand() => ShowStatus();

  /// <summary>Turns the AI connection on for this session (used by C3DMCPSTART and the ribbon toggle).</summary>
  internal static void TurnOn()
  {
    try
    {
      PluginRuntime.StartServer();
      PluginLog.Info("PluginEntry", $"Listener started on port {PluginRuntime.Port}.");
      WriteMessage($"Civil3D MCP: AI connection is ON (port {PluginRuntime.Port}). Type C3DMCPSTOP or use the ribbon to turn it off.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Starting the listener failed", ex);
      var message = $"Civil3D MCP could not start on port {PluginRuntime.Port}: {ex.Message}\n" +
        "Another Civil 3D window may already have the AI connection on; turn it off there first.";
      WriteMessage(message);
      if (App.DocumentManager.MdiActiveDocument == null) App.ShowAlertDialog(message);
    }
    finally
    {
      McpRibbon.Refresh();
    }
  }

  /// <summary>Turns the AI connection off for this session.</summary>
  internal static void TurnOff()
  {
    PluginRuntime.StopServer();
    PluginLog.Info("PluginEntry", "Listener stopped.");
    WriteMessage("Civil3D MCP: AI connection is OFF for this session.");
    McpRibbon.Refresh();
  }

  internal static void ToggleConnection()
  {
    if (PluginRuntime.GetStatus().IsRunning) TurnOff(); else TurnOn();
  }

  internal static void ShowStatus()
  {
    var status = PluginRuntime.GetStatus();
    var message = status.IsRunning
      ? $"Civil3D MCP: AI connection is ON (port {PluginRuntime.Port}). Queued: {status.QueueDepth}; working: {(status.OperationInProgress ? status.CurrentOperation ?? "yes" : "idle")}."
      : "Civil3D MCP: AI connection is OFF. Use C3DMCPSTART or the Civil 3D MCP ribbon tab to turn it on.";
    WriteMessage(message);
    if (App.DocumentManager.MdiActiveDocument == null) App.ShowAlertDialog(message);
    McpRibbon.Refresh();
  }
  /// <summary>Toggles moving the view to show objects changed by MCP work.</summary>
  [CommandMethod("C3DMCPFOLLOW")]
  public void FollowViewCommand()
  {
    ViewFollower.Enabled = !ViewFollower.Enabled;
    WriteMessage($"Civil3D MCP follow view: {(ViewFollower.Enabled ? "ON (the view moves to show MCP changes)" : "OFF")}.");
  }

  /// <summary>Runs a saved script by name, prompting for its parameters.</summary>
  [CommandMethod("MCPRUN", CommandFlags.Modal)]
  public void RunSavedScriptCommand()
  {
    ScriptCommands.RunSavedScriptInteractive();
  }

  /// <summary>Lists saved scripts.</summary>
  [CommandMethod("MCPSCRIPTS", CommandFlags.Modal)]
  public void ListSavedScriptsCommand()
  {
    ScriptCommands.ListSavedScriptsInteractive();
  }

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
