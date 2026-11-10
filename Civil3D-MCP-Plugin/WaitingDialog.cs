// WaitingDialog.cs - names a modal AutoCAD dialog that is blocking the main thread, so a stalled request can say why.
// Used by CivilExecution (the HOST_BUSY message and the main thread id), CommandLineMonitor and DrawingCommands
// (health / command line). Win32 calls only (user32/kernel32); never touches AutoCAD or the drawing.
using System.Runtime.InteropServices;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Finds a window of AutoCAD's waiting for an answer, such as Duplicate Point Number. Nothing on the command line shows
/// one, and every request then stalls behind it with no reason given, so health, command_line and the busy error name it.
/// Safe from any thread: it only asks Windows, and never touches AutoCAD.
/// </summary>
internal static class WaitingDialog
{
  private const string DialogClass = "#32770";

  private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

  [DllImport("kernel32.dll")]
  private static extern uint GetCurrentThreadId();

  [DllImport("user32.dll")]
  private static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, IntPtr parameter);

  [DllImport("user32.dll")]
  private static extern bool IsWindowVisible(IntPtr window);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetClassName(IntPtr window, StringBuilder text, int size);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

  /// <summary>The calling thread's Win32 id (CivilExecution records the main thread's at startup).</summary>
  public static uint CurrentThreadId() => GetCurrentThreadId();

  /// <summary>The title of the first dialog open on AutoCAD's main thread, or null when there is none.</summary>
  public static string? Title()
  {
    var threadId = CivilExecution.MainWindowsThreadId;
    if (threadId == 0)
    {
      return null;
    }

    string? title = null;
    EnumThreadWindows(threadId, (window, _) =>
    {
      var name = new StringBuilder(64);
      GetClassName(window, name, name.Capacity);
      if (!IsWindowVisible(window) || name.ToString() != DialogClass)
      {
        return true;
      }

      var text = new StringBuilder(256);
      GetWindowText(window, text, text.Capacity);
      title = text.Length > 0 ? text.ToString() : "untitled";
      return false;
    }, IntPtr.Zero);
    return title;
  }
}
