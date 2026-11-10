// The plugin's request runtime, between the TCP listener and the handlers.
// Lifecycle: PluginEntry.TurnOn (C3DMCPSTART, the ribbon toggle, CIVIL3D_MCP_AUTOSTART) calls StartServer, which
// starts RpcTcpServer on 127.0.0.1:8080 and a 30 s timer that logs long calls; TurnOff and plugin unload call
// StopServer. RpcTcpServer hands each request's text to HandleRawRequestAsync, which checks the JSON-RPC envelope,
// sets the per-request context (method, id, cancellation, expected drawing) in AsyncLocals, calls
// CommandDispatcher.DispatchAsync and turns the result or exception into the JSON-RPC answer (JsonRpcProtocol).
// CivilExecution calls Queue/Start/Cancel/CompleteHostOperation around every host-gate operation; GetStatus is what
// C3DMCPSTATUS, the ribbon and civil3d_health (DrawingCommands.GetCivil3DHealthAsync) report.
// Also home of JsonRpcDispatchException and the GetRequired*/GetOptional* argument helpers every handler uses.
// No drawing access here; every request line is logged to PluginLog.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>What C3DMCPSTATUS, the ribbon and civil3d_health report: whether the listener runs, and the host
/// queue's depth, capacity and current operation.</summary>
public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  int QueueCapacity,
  long? CurrentOperationStartedAtUnixMs,
  string? CurrentRequestId);

/// <summary>
/// The exception handlers throw for an expected failure. <see cref="Code"/> is a CIVIL3D.* domain code (for example
/// CIVIL3D.OBJECT_NOT_FOUND, CIVIL3D.INVALID_INPUT, CIVIL3D.HOST_BUSY); it is sent back as a JSON-RPC error with the
/// matching number (JsonRpcProtocol.NumericErrorCode) and the code in error.data.code, and logged at info level, not
/// as a fault. Write the message for the AI: say what was wrong and what to do.
/// </summary>
public sealed class JsonRpcDispatchException : Exception
{
  /// <summary>Creates the error with a CIVIL3D.* <paramref name="code"/> and a message for the AI.</summary>
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  /// <summary>The CIVIL3D.* domain code, sent as error.data.code.</summary>
  public string Code { get; }
}

/// <summary>
/// The plugin's request runtime: starts and stops the loopback JSON-RPC listener (RpcTcpServer on <see cref="Port"/>),
/// turns each raw request into a CommandDispatcher call and its JSON-RPC answer, keeps per-request context
/// (method, request id, cancellation, expected drawing) in AsyncLocals that flow into CivilExecution, counts the host
/// queue for health, and offers the argument helpers every handler uses (GetRequiredString, GetOptional*, ...).
/// </summary>
public static class PluginRuntime
{
  /// <summary>The loopback port the listener binds; the TS server's CIVIL3D_PORT default matches it.</summary>
  public const int Port = 8080;

  private static readonly object Sync = new();
  private static RpcTcpServer? _server;
  private static readonly AsyncLocal<string?> CurrentRequestOperation = new();
  private static readonly AsyncLocal<string?> CurrentRequestId = new();
  private static readonly AsyncLocal<CancellationToken> CurrentRequestCancellation = new();
  private static readonly AsyncLocal<string?> CurrentExpectedDrawingIdentity = new();
  private const int MaxQueuedHostOperations = 64;
  private static int _queueDepth;
  private static int _activeOperations;
  private static string? _currentOperation;
  private static string? _currentRequestId;
  private static long? _currentOperationStartedAtUnixMs;
  private static Timer? _longCallLog;

  /// <summary>Starts the listener (C3DMCPSTART, the ribbon toggle, or CIVIL3D_MCP_AUTOSTART). Throws when the port is
  /// in use, e.g. by another Civil 3D window; nothing is kept then, so a later start can retry.</summary>
  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        return;
      }

      // Only keep the server once it is listening, so a failed start (port in use) can be retried.
      var server = new RpcTcpServer(Port, HandleRawRequestAsync);
      server.Start();
      _server = server;
      _longCallLog = new Timer(_ => LogLongCall(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
  }

  /// <summary>Stops the listener and resets the queue counters (C3DMCPSTOP, plugin unload).</summary>
  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _server = null;
      _longCallLog?.Dispose();
      _longCallLog = null;
      _currentOperation = null;
      _activeOperations = 0;
      _queueDepth = 0;
      _currentRequestId = null;
      _currentOperationStartedAtUnixMs = null;
    }
  }

  /// <summary>A consistent snapshot of the listener and queue state.</summary>
  public static PluginStatus GetStatus()
  {
    lock (Sync)
    {
      return new PluginStatus(
        _server != null,
        _activeOperations > 0,
        _currentOperation,
        _queueDepth,
        MaxQueuedHostOperations,
        _currentOperationStartedAtUnixMs,
        _currentRequestId);
    }
  }

  /// <summary>
  /// Handles one JSON-RPC request text and returns the response text; never throws. Checks the envelope (object,
  /// jsonrpc "2.0", string method, object params), sets the request context, dispatches, and maps failures:
  /// JsonRpcDispatchException to its own code, cancellation (the client disconnected) to CIVIL3D.CANCELLED, anything
  /// else to CIVIL3D.INTERNAL_ERROR with the exception's type and message and the log file path (the stack trace is
  /// only in the log).
  /// </summary>
  public static async Task<string> HandleRawRequestAsync(string rawRequest, CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return JsonRpcProtocol.SerializeError(null, -32700, "CIVIL3D.INVALID_JSON", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return JsonRpcProtocol.SerializeError(null, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    if (request["jsonrpc"] is not JsonValue versionValue
      || !versionValue.TryGetValue<string>(out var version)
      || version != "2.0")
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must specify jsonrpc='2.0'.");
    }

    var method = request["method"] is JsonValue methodValue
      && methodValue.TryGetValue<string>(out var methodText)
      ? methodText
      : null;

    if (string.IsNullOrWhiteSpace(method))
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request is missing a string method.");
    }

    if (request["params"] != null && request["params"] is not JsonObject)
    {
      return JsonRpcProtocol.SerializeError(id, -32602, "CIVIL3D.INVALID_INPUT", "JSON-RPC params must be an object when provided.");
    }
    var parameters = request["params"] as JsonObject;

    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    CurrentRequestOperation.Value = method;
    CurrentRequestId.Value = id?.ToJsonString();
    CurrentRequestCancellation.Value = cancellationToken;

    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      // At Info, so that after a crash the log's last line says what was running.
      PluginLog.Info("Dispatch", $"-> {method} [{CurrentRequestId.Value ?? "no-id"}]");
      var result = await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken);
      PluginLog.Debug("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] ok durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeResult(id, result);
    }
    catch (JsonRpcDispatchException ex)
    {
      // Domain-level errors are part of the contract; record at info so they
      // show up in diagnostics without looking like runtime faults.
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] dispatch error {ex.Code} durationMs={timer.ElapsedMilliseconds}: {ex.Message}");
      return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode(ex.Code), ex.Code, ex.Message);
    }
    catch (OperationCanceledException)
    {
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] cancelled durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeError(id, -32010, "CIVIL3D.CANCELLED", $"Operation '{method}' was cancelled.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] unhandled failure durationMs={timer.ElapsedMilliseconds} category={ex.GetType().Name}", ex);
      // Thrown inside Civil 3D, not the plugin: it refused this request, and its own words say why ("Duplicate alignment
      // name"). That is no fault of the plugin's, and asking differently can work.
      if (ThrownByCivil3D(ex))
      {
        return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode("CIVIL3D.API_ERROR"), "CIVIL3D.API_ERROR", $"Civil 3D refused this: {ex.Message}");
      }

      // Loopback-only server: return the cause so callers can act on it; the stack trace is in the log.
      return JsonRpcProtocol.SerializeError(
        id,
        -32603,
        "CIVIL3D.INTERNAL_ERROR",
        $"The Civil 3D plugin encountered an unexpected error ({ex.GetType().Name}: {ex.Message}). Stack trace: {PluginLog.LogFilePath}");
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
    }
  }

  /// <summary>
  /// True when an unexpected exception came from inside Civil 3D/AutoCAD rather than from plugin code: the first
  /// "at Autodesk." stack frame comes before any "at Civil3DMcpPlugin." frame. Such errors are answered as
  /// CIVIL3D.API_ERROR ("Civil 3D refused this") instead of INTERNAL_ERROR. Null reference, out of memory and invalid
  /// cast are always treated as plugin faults.
  /// </summary>
  private static bool ThrownByCivil3D(Exception exception)
  {
    if (exception is NullReferenceException or OutOfMemoryException or InvalidCastException)
    {
      return false;
    }

    // By the stack's text: frames in Civil 3D's native code name no managed type.
    var stack = exception.StackTrace ?? string.Empty;
    var inCivil3D = stack.IndexOf("at Autodesk.", StringComparison.Ordinal);
    var inPlugin = stack.IndexOf("at Civil3DMcpPlugin.", StringComparison.Ordinal);
    return inCivil3D >= 0 && (inPlugin < 0 || inCivil3D < inPlugin);
  }

  /// <summary>The current request's cancellation token (cancelled when the client disconnects); default outside a
  /// request.</summary>
  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  /// <summary>The current request's method name, or "Civil 3D operation" outside a request.</summary>
  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  /// <summary>The current request's JSON-RPC id as JSON text, or null.</summary>
  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  /// <summary>The identity (see <see cref="GetDrawingIdentity"/>) of the active drawing, or null when none is open.
  /// Must be called on the main thread.</summary>
  internal static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  /// <summary>A drawing's identity for "is this still the same drawing" checks: its full file name, or its document
  /// name ("Drawing1.dwg") when it has never been saved. Null for no document.</summary>
  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    var fileName = document.Database.Filename;
    return string.IsNullOrWhiteSpace(fileName) ? document.Name : fileName;
  }

  /// <summary>The drawing background work was started for (set by <see cref="RunWithRequestContextAsync"/>), or null
  /// for an ordinary request, which runs on whatever drawing is active.</summary>
  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  /// <summary>
  /// Runs work under a given request context, for background work that outlives its request (jobs, corridor
  /// rebuilds): the operation name and id for health, its own cancellation token, and the drawing it was started for,
  /// which GetVerifiedActiveDocuments then insists on.
  /// </summary>
  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action)
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
    try
    {
      return await action();
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  /// <summary>
  /// Counts a request into the host queue. Refuses with CIVIL3D.HOST_BUSY beyond 64 waiting, while a long call has the
  /// main thread (waiting would only time out, with the call still running), and when memory is nearly out.
  /// </summary>
  internal static void QueueHostOperation()
  {
    if (HostGuard.CheckMemory(HostGuard.FreeCommitMb, () => Thread.Sleep(1000)) is { } lowMemory)
    {
      throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", lowMemory);
    }

    lock (Sync)
    {
      if (HostGuard.BusyRefusal(_currentOperation, RunningMs()) is { } busy)
      {
        throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", busy);
      }

      if (_queueDepth >= MaxQueuedHostOperations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D host queue is full ({MaxQueuedHostOperations} operations). Retry after current work completes.");
      }

      _queueDepth++;
    }
  }

  private static long RunningMs() => _currentOperationStartedAtUnixMs is long startedAt
    ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt)
    : 0;

  /// <summary>
  /// Leaves a line in the log every 30 s of a long call, with the memory it has, so a freeze can be told from a crash
  /// afterwards: runs from a timer, because the main thread is the one that's busy.
  /// </summary>
  private static void LogLongCall()
  {
    lock (Sync)
    {
      if (_currentOperation != null && RunningMs() >= HostGuard.LongCallMs)
      {
        PluginLog.Info("HostGuard", $"Still running {_currentOperation} after {RunningMs() / 1000} s, {HostGuard.FreeCommitMb()} MB of memory left.");
      }
    }
  }

  /// <summary>Moves a request from waiting to running and records it as the current operation.</summary>
  internal static void StartHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
      _activeOperations++;
      _currentOperation = GetCurrentRequestOperation();
      _currentRequestId = GetCurrentRequestId();
      _currentOperationStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  /// <summary>Takes a request that never started (cancelled or timed out while waiting) out of the queue count.</summary>
  internal static void CancelQueuedHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
    }
  }

  /// <summary>Marks a running operation finished; when none is left running, clears the current operation shown by
  /// health.</summary>
  internal static void CompleteHostOperation()
  {
    lock (Sync)
    {
      _activeOperations = Math.Max(0, _activeOperations - 1);
      if (_activeOperations == 0)
      {
        _currentOperation = null;
        _currentRequestId = null;
        _currentOperationStartedAtUnixMs = null;
      }
    }
  }

  /// <summary>A parameter's raw value, or null when absent (a JSON null also reads as absent).</summary>
  public static object? GetParameter(JsonObject? parameters, string name)
  {
    if (parameters == null)
    {
      return null;
    }

    return parameters.TryGetPropertyValue(name, out var value) ? value : null;
  }

  /// <summary>A required non-empty string; missing, blank or another JSON type is CIVIL3D.INVALID_INPUT naming the
  /// parameter. The other Get* helpers work the same way; the optional ones return null when absent.</summary>
  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    var stringValue = ConvertParameter<string>(value, name, "a string");
    if (string.IsNullOrWhiteSpace(stringValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    }

    return stringValue;
  }

  /// <summary>A required number; missing or not a number is CIVIL3D.INVALID_INPUT.</summary>
  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return ConvertParameter<double>(value, name, "a number");
  }

  /// <summary>A required integer; missing or not an integer (3.5 included) is CIVIL3D.INVALID_INPUT.</summary>
  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return ConvertParameter<int>(value, name, "an integer");
  }

  /// <summary>An optional string, or null when absent; another JSON type is CIVIL3D.INVALID_INPUT. An empty string
  /// is returned as is.</summary>
  public static string? GetOptionalString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : ConvertParameter<string>(value, name, "a string");
  }

  /// <summary>An optional number, or null when absent; not a number is CIVIL3D.INVALID_INPUT.</summary>
  public static double? GetOptionalDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : ConvertParameter<double>(value, name, "a number");
  }

  /// <summary>An optional integer, or null when absent; not an integer is CIVIL3D.INVALID_INPUT.</summary>
  public static int? GetOptionalInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : ConvertParameter<int>(value, name, "an integer");
  }

  /// <summary>An optional boolean, or null when absent; anything but true/false (the string "true" included) is
  /// CIVIL3D.INVALID_INPUT.</summary>
  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : ConvertParameter<bool>(value, name, "true or false");
  }

  // Wrong JSON types become INVALID_INPUT ("Parameter 'code' must be a string, got an object")
  // instead of an InvalidOperationException that surfaces as INTERNAL_ERROR.
  private static T ConvertParameter<T>(JsonNode value, string name, string expected)
  {
    try
    {
      return value.GetValue<T>();
    }
    catch (System.Exception exception) when (exception is InvalidOperationException or FormatException)
    {
      var actual = value switch
      {
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v => v.GetValueKind() switch
        {
          JsonValueKind.String => "a string",
          JsonValueKind.Number => "a number",
          JsonValueKind.True or JsonValueKind.False => "a boolean",
          _ => "null",
        },
        _ => "an unsupported value",
      };
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be {expected}, got {actual}.");
    }
  }

}
