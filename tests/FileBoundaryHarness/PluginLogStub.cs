// Test stub of the plugin's PluginLog so the plugin sources compiled into the harness build without Civil 3D.
// It logs nothing. Keep the method signatures in step with Civil3D-MCP-Plugin's real PluginLog.

namespace Civil3DMcpPlugin;

public static class PluginLog
{
  public static void Debug(string component, string message) { }
  public static void Warn(string component, string message) { }
  public static void Error(string component, string message, Exception? exception = null) { }
}
