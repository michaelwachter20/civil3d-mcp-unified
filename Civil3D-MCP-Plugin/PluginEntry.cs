// Where the plugin starts and stops inside Civil 3D, and its command-line commands.
// Lifecycle: when AutoCAD loads Civil3DMcpPlugin.dll it calls Initialize on the main thread. That
// captures the main-thread context for CivilExecution, starts CommandLineMonitor and adds the McpRibbon tab, but does
// NOT open the TCP listener: AI clients can't connect until the user turns the AI connection on with C3DMCPSTART or
// the ribbon toggle (or CIVIL3D_MCP_AUTOSTART=true). Turning it on calls PluginRuntime.StartServer, which binds
// RpcTcpServer to 127.0.0.1:8080 (exclusive, so only one Civil 3D window can have it on); C3DMCPSTOP and plugin
// unload (Terminate) close it. C3DMCPSTATUS and the ribbon's Details button show the queue state that civil3d_health
// also reports. Every step is logged to PluginLog (%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log).
// Other commands: C3DMCPFOLLOW (ViewFollower on/off), MCPRUN and MCPSCRIPTS (saved .csx scripts, ScriptCommands).
// No drawing is read or changed here.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

/// <summary>
/// The plugin's AutoCAD entry point (<see cref="IExtensionApplication"/>) and command class. Owns turning the AI
/// connection on and off; the listener itself lives in <see cref="PluginRuntime"/>.
/// </summary>
public sealed class PluginEntry : IExtensionApplication
{
  /// <summary>
  /// Called by AutoCAD on load, on the main thread: captures the main-thread context, starts the command-line monitor,
  /// starts the listener only when CIVIL3D_MCP_AUTOSTART=true, and builds the ribbon tab. Never throws: a failure is
  /// logged and shown on the command line.
  /// </summary>
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

  /// <summary>Called by AutoCAD on shutdown: stops the listener. Never throws.</summary>
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

  /// <summary>C3DMCPSTART: turns the AI connection on (see <see cref="TurnOn"/>).</summary>
  [CommandMethod("C3DMCPSTART")]
  public void StartCommand() => TurnOn();

  /// <summary>C3DMCPSTOP: turns the AI connection off (see <see cref="TurnOff"/>).</summary>
  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand() => TurnOff();

  /// <summary>C3DMCPSTATUS: shows whether the connection is on and what the host queue is doing.</summary>
  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand() => ShowStatus();

  /// <summary>Turns the AI connection on for this session (used by C3DMCPSTART and the ribbon toggle): starts the
  /// listener on port 8080 and refreshes the ribbon. When the port is taken (usually another Civil 3D window with the
  /// connection on) it logs the error and tells the user on the command line, or in an alert when no drawing is
  /// open; it doesn't throw.</summary>
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

  /// <summary>Turns the AI connection off for this session: stops the listener and refreshes the
  /// ribbon.</summary>
  internal static void TurnOff()
  {
    PluginRuntime.StopServer();
    PluginLog.Info("PluginEntry", "Listener stopped.");
    WriteMessage("Civil3D MCP: AI connection is OFF for this session.");
    McpRibbon.Refresh();
  }

  /// <summary>The ribbon's AI Connection button: turns the connection off when it is on, otherwise on.</summary>
  internal static void ToggleConnection()
  {
    if (PluginRuntime.GetStatus().IsRunning) TurnOff(); else TurnOn();
  }

  /// <summary>Writes the connection state, queue depth and current operation to the command line (an alert when no
  /// drawing is open) and refreshes the ribbon. Used by C3DMCPSTATUS and the ribbon's Details button.</summary>
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

  /// <summary>Writes a line to the active drawing's command line; does nothing when no drawing is open.</summary>
  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
