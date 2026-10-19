using Autodesk.AutoCAD.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Lets the AI see what the user sees on the command line: the last command-line lines, the
/// commands running right now, and a short history of commands started/ended/cancelled in every
/// open drawing. Read without the request queue, so it works even when a request is stuck.
/// </summary>
internal static class CommandLineMonitor
{
  private const int MaxHistory = 100;
  private static readonly object Sync = new();
  private static readonly LinkedList<CommandEvent> History = new();
  private static readonly Dictionary<string, int> Active = new(StringComparer.OrdinalIgnoreCase);
  private static bool _initialized;

  internal sealed record CommandEvent(DateTime Utc, string Drawing, string Command, string Event);

  /// <summary>Call on the AutoCAD main thread at plugin load.</summary>
  public static void Initialize()
  {
    if (_initialized)
    {
      return;
    }

    _initialized = true;
    foreach (Document document in App.DocumentManager)
    {
      Attach(document);
    }

    App.DocumentManager.DocumentCreated += (_, e) => Attach(e.Document);
  }

  private static void Attach(Document? document)
  {
    if (document == null)
    {
      return;
    }

    var name = Path.GetFileName(document.Name);
    document.CommandWillStart += (_, e) => Record(name, e.GlobalCommandName, "started", +1);
    document.CommandEnded += (_, e) => Record(name, e.GlobalCommandName, "ended", -1);
    document.CommandCancelled += (_, e) => Record(name, e.GlobalCommandName, "cancelled", -1);
    document.CommandFailed += (_, e) => Record(name, e.GlobalCommandName, "failed", -1);
  }

  private static void Record(string drawing, string command, string what, int delta)
  {
    lock (Sync)
    {
      History.AddLast(new CommandEvent(DateTime.UtcNow, drawing, command, what));
      while (History.Count > MaxHistory)
      {
        History.RemoveFirst();
      }

      Active.TryGetValue(command, out var count);
      count += delta;
      if (count > 0) Active[command] = count; else Active.Remove(command);
    }
  }

  /// <summary>Commands running now, from command events (safe on any thread).</summary>
  public static string ActiveCommandNames()
  {
    lock (Sync)
    {
      return string.Join(", ", Active.Keys);
    }
  }

  public static IReadOnlyList<CommandEvent> RecentCommands(int count)
  {
    lock (Sync)
    {
      return History.Skip(Math.Max(0, History.Count - count)).ToList();
    }
  }

  /// <summary>Last command-line lines. Main thread only.</summary>
  public static IReadOnlyList<string> LastLines(int count)
  {
    var lines = Autodesk.AutoCAD.Internal.Utils.GetLastCommandLines(count, true);
    return lines == null ? Array.Empty<string>() : lines.Cast<string>().ToList();
  }

  public static async Task<object?> GetCommandLineAsync(System.Text.Json.Nodes.JsonObject? parameters)
  {
    var lineCount = Math.Clamp(PluginRuntime.GetOptionalInt(parameters, "lines") ?? 30, 1, 200);
    IReadOnlyList<string>? lines = null;
    string? cmdNames = null;
    bool? commandActive = null;
    string? note = null;
    try
    {
      (lines, cmdNames, commandActive) = await CivilExecution.OnMainThreadAsync(() =>
        (LastLines(lineCount), Convert.ToString(App.GetSystemVariable("CMDNAMES")), Convert.ToInt32(App.GetSystemVariable("CMDACTIVE")) != 0),
        TimeSpan.FromSeconds(3));
    }
    catch (TimeoutException)
    {
      note = "Civil 3D's main thread didn't respond within 3 s (a modal dialog may be open, or Civil 3D is busy). Showing command events only.";
    }

    return new Dictionary<string, object?>
    {
      ["lines"] = lines,
      ["commandActive"] = commandActive,
      ["activeCommands"] = string.IsNullOrWhiteSpace(cmdNames) ? ActiveCommandNames() : cmdNames,
      ["recentCommands"] = RecentCommands(20).Select(e => new Dictionary<string, object?>
      {
        ["utc"] = e.Utc.ToString("HH:mm:ss"),
        ["drawing"] = e.Drawing,
        ["command"] = e.Command,
        ["event"] = e.Event,
      }).ToList(),
      ["note"] = note,
    };
  }
}
