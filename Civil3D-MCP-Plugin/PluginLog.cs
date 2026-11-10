// The plugin's log: one text file, %LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log (or CIVIL3D_MCP_LOG_DIR), rotated at
// 5 MiB with three old copies (plugin.log.1 .. .3). Every part of the plugin writes to it: PluginEntry (load, start,
// stop), PluginRuntime (one line per request "-> method", errors with stack traces), CivilExecution, the handlers.
// civil3d_health reports its path and whether writing works (DrawingCommands.GetCivil3DHealthAsync), and
// CIVIL3D.INTERNAL_ERROR answers point the AI at it for the stack trace.
// The level comes from CIVIL3D_MCP_LOG_LEVEL (debug, info, warn, error; default info).
// Rules: logging never throws into plugin code and never calls AutoCAD, so it is safe from any thread and before
// PluginEntry.Initialize. It writes only its own file, which is why it doesn't go through FileBoundary.
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin-side logger that appends timestamped lines ("[time] [level] [component] message | ExceptionType: message",
/// then the stack trace) to a rotating log file under <c>%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log</c>.
///
/// Writes are synchronous on the calling thread, serialized by a lock; there is no background writer. It is
/// dependency-free and never calls host APIs, so all methods are safe from worker threads and before
/// <c>PluginEntry.Initialize</c> runs. Only when writing fails does a line go to the debugger output, and the failure
/// is kept in <see cref="LastFileError"/>.
/// </summary>
public static class PluginLog
{
  /// <summary>Log levels, lowest first; lines below <see cref="MinimumLevel"/> are dropped.</summary>
  public enum Level
  {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
  }

  private const long MaxLogFileBytes = 5L * 1024 * 1024; // 5 MiB per log file
  private const int RotatedBackupsKept = 3;

  private static readonly object FileSync = new();
  private static readonly Lazy<string> LogFilePathLazy = new(BuildLogFilePath);
  private static Level _minimumLevel = ReadLevelFromEnvironment();
  private static string? _lastFileError;

  /// <summary>Full path of the current log file; creates its folder on first use (falls back to %TEMP%).</summary>
  public static string LogFilePath => LogFilePathLazy.Value;

  /// <summary>The last file-write failure ("IOException: ..."), or null once a write succeeds again.</summary>
  public static string? LastFileError => Volatile.Read(ref _lastFileError);

  /// <summary>True when the last write worked and the file exists; reported by civil3d_health.</summary>
  public static bool IsFileLoggingHealthy => LastFileError == null && File.Exists(LogFilePath);

  /// <summary>Lowest level written; starts from CIVIL3D_MCP_LOG_LEVEL (default Info).</summary>
  public static Level MinimumLevel
  {
    get => _minimumLevel;
    set => _minimumLevel = value;
  }

  /// <summary>Writes a Debug line (dropped at the default Info level). The Info, Warn and Error methods below work the
  /// same way at their level; <paramref name="component"/> is a short tag such as "Dispatch" or "PluginEntry", and an
  /// exception adds its type, message and stack trace.</summary>
  public static void Debug(string component, string message, Exception? exception = null)
    => Write(Level.Debug, component, message, exception);

  /// <summary>Writes an Info line (the default level; every request's start is logged at Info).</summary>
  public static void Info(string component, string message, Exception? exception = null)
    => Write(Level.Info, component, message, exception);

  /// <summary>Writes a Warn line.</summary>
  public static void Warn(string component, string message, Exception? exception = null)
    => Write(Level.Warn, component, message, exception);

  /// <summary>Writes an Error line, with the exception's stack trace when one is given.</summary>
  public static void Error(string component, string message, Exception? exception = null)
    => Write(Level.Error, component, message, exception);

  /// <summary>
  /// Convenience for <c>try { ... } catch { /* log and swallow */ }</c> blocks.
  /// Keeps the "best effort" reflection pattern used throughout the plugin
  /// while making failures visible at debug level.
  /// </summary>
  public static void Swallow(string component, string operation, Exception exception)
  {
    Write(Level.Debug, component, $"Ignored error during {operation}: {exception.Message}", exception);
  }

  /// <summary>Formats one line and appends it, unless its level is below <see cref="MinimumLevel"/>.</summary>
  private static void Write(Level level, string component, string message, Exception? exception)
  {
    if (level < _minimumLevel)
    {
      return;
    }

    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    var line = new StringBuilder()
      .Append('[').Append(timestamp).Append("] ")
      .Append('[').Append(level).Append("] ")
      .Append('[').Append(component).Append("] ")
      .Append(message);

    if (exception != null)
    {
      line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
    }

    var text = line.ToString();

    TryAppendToFile(text, exception);

  }

  /// <summary>Appends the line (and the stack trace, if any) under the file lock, rotating first when the file is
  /// full. Never throws: a failure is kept in <see cref="LastFileError"/> and written to the debugger output.</summary>
  private static void TryAppendToFile(string text, Exception? exception)
  {
    try
    {
      var path = LogFilePathLazy.Value;

      lock (FileSync)
      {
        RotateIfNeeded(path);

        using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
        writer.WriteLine(text);

        if (exception?.StackTrace != null)
        {
          writer.WriteLine(exception.StackTrace);
        }

        Volatile.Write(ref _lastFileError, null);
      }
    }
    catch (Exception fileException)
    {
      // Logging itself must never throw into plugin code paths.
      Volatile.Write(
        ref _lastFileError,
        $"{fileException.GetType().Name}: {fileException.Message}");
      System.Diagnostics.Debug.WriteLine(
        $"Civil3D MCP file logging failed: {LastFileError}");
    }
  }

  /// <summary>When the file has reached 5 MiB, shifts plugin.log -> .1 -> .2 -> .3 (the oldest is deleted). Best
  /// effort: a failure is ignored and the file just keeps growing.</summary>
  private static void RotateIfNeeded(string path)
  {
    try
    {
      var info = new FileInfo(path);
      if (!info.Exists || info.Length < MaxLogFileBytes)
      {
        return;
      }

      for (var index = RotatedBackupsKept; index >= 1; index--)
      {
        var older = $"{path}.{index}";
        var newer = index == 1 ? path : $"{path}.{index - 1}";

        if (File.Exists(older))
        {
          File.Delete(older);
        }

        if (File.Exists(newer))
        {
          File.Move(newer, older);
        }
      }
    }
    catch
    {
      // Best-effort rotation; truncating failures are not worth escalating.
    }
  }

  /// <summary>Picks the log folder (CIVIL3D_MCP_LOG_DIR, else %LOCALAPPDATA%\Civil3DMcpPlugin, else
  /// %TEMP%\Civil3DMcpPlugin when the first can't be created), creates it and returns the plugin.log path.</summary>
  private static string BuildLogFilePath()
  {
    var root = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_DIR")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3DMcpPlugin");

    try
    {
      Directory.CreateDirectory(root);
    }
    catch
    {
      // Fall back to temp if LocalAppData is not writable.
      root = Path.Combine(Path.GetTempPath(), "Civil3DMcpPlugin");
      Directory.CreateDirectory(root);
    }

    return Path.Combine(root, "plugin.log");
  }

  /// <summary>Reads CIVIL3D_MCP_LOG_LEVEL; empty or unknown values mean Info.</summary>
  private static Level ReadLevelFromEnvironment()
  {
    var raw = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_LEVEL");
    if (string.IsNullOrWhiteSpace(raw))
    {
      return Level.Info;
    }

    return raw.Trim().ToLowerInvariant() switch
    {
      "debug" => Level.Debug,
      "info" => Level.Info,
      "warn" or "warning" => Level.Warn,
      "error" => Level.Error,
      _ => Level.Info,
    };
  }
}
