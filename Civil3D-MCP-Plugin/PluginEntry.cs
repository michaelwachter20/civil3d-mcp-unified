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
      PluginRuntime.StartServer();
      PluginLog.Info("PluginEntry", $"Civil3D MCP plugin initialized on port {PluginRuntime.Port}. Log file: {PluginLog.LogFilePath}");
      WriteMessage("Civil3D MCP plugin initialized.");
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
  public void StartCommand()
  {
    PluginRuntime.StartServer();
    WriteMessage($"Civil3D MCP listener started on port {PluginRuntime.Port}.");
  }

  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand()
  {
    PluginRuntime.StopServer();
    WriteMessage("Civil3D MCP listener stopped.");
  }

  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand()
  {
    var status = PluginRuntime.GetStatus();
    WriteMessage($"Civil3D MCP listener running: {status.IsRunning}; pending: {status.QueueDepth}; active: {status.OperationInProgress}; current: {status.CurrentOperation ?? "<none>"}");
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
