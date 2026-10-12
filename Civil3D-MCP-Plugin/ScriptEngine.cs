using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynReference = Microsoft.CodeAnalysis.MetadataReference;
using RoslynOptimization = Microsoft.CodeAnalysis.OptimizationLevel;

namespace Civil3DMcpPlugin;

internal sealed record CompiledScript(string Hash, Script<object> Script, IReadOnlyList<string> Warnings);

internal sealed record ScriptRunResult(
  JsonNode? Result,
  IReadOnlyList<string> Output,
  bool Committed,
  long ElapsedMs,
  IReadOnlyList<string> Warnings,
  bool UndoGrouped);

/// <summary>
/// Compiles and runs C# scripts with Roslyn inside Civil 3D.
/// Compilation happens off the AutoCAD main thread and is cached by SHA-256 of
/// the source; execution happens in command context under a document lock and
/// a single transaction (committed for write runs, aborted for queries).
/// </summary>
/// <remarks>Based on RoslynExecutor from barbosaihan/civil3d-mcp (MIT).</remarks>
internal static class ScriptEngine
{
  public const string ScriptFileName = "script.csx";
  private const int MaxCacheEntries = 256;

  private static readonly ConcurrentDictionary<string, Lazy<CompiledScript>> Cache = new(StringComparer.Ordinal);
  private static readonly object ReferenceLock = new();
  private static IReadOnlyList<RoslynReference>? _references;
  private static int _referenceAssemblyCount;

  internal static readonly string[] Imports =
  {
    "System",
    "System.Collections.Generic",
    "System.Globalization",
    "System.IO",
    "System.Linq",
    "System.Text",
    "System.Text.Json",
    "System.Text.Json.Nodes",
    "Autodesk.AutoCAD.ApplicationServices",
    "Autodesk.AutoCAD.Colors",
    "Autodesk.AutoCAD.DatabaseServices",
    "Autodesk.AutoCAD.EditorInput",
    "Autodesk.AutoCAD.Geometry",
    "Autodesk.Civil",
    "Autodesk.Civil.ApplicationServices",
    "Autodesk.Civil.DatabaseServices",
    "Autodesk.Civil.DatabaseServices.Styles",
    "Autodesk.Civil.Settings",
  };

  /// <summary>Script timeout (CIVIL3D_SCRIPT_TIMEOUT_SECONDS, default 110s — just under the Node command timeout).</summary>
  public static TimeSpan Timeout
  {
    get
    {
      var raw = Environment.GetEnvironmentVariable("CIVIL3D_SCRIPT_TIMEOUT_SECONDS");
      return int.TryParse(raw, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(110);
    }
  }

  /// <summary>Whether write runs are wrapped in UNDO BEGIN/END (CIVIL3D_SCRIPT_UNDO_GROUP, default on).</summary>
  public static bool UndoGroupingEnabled =>
    !string.Equals(Environment.GetEnvironmentVariable("CIVIL3D_SCRIPT_UNDO_GROUP"), "false", StringComparison.OrdinalIgnoreCase);

  /// <summary>Validates against the sandbox and compiles (or returns the cached compilation).</summary>
  public static CompiledScript Compile(string code, bool writeMode)
  {
    ScriptSandbox.Validate(code, writeMode);

    var hash = Sha256(code);
    if (Cache.Count >= MaxCacheEntries)
    {
      Cache.Clear();
    }

    var entry = Cache.GetOrAdd(hash, key => new Lazy<CompiledScript>(() => CompileCore(code, key), LazyThreadSafetyMode.ExecutionAndPublication));
    try
    {
      return entry.Value;
    }
    catch
    {
      Cache.TryRemove(hash, out _);
      throw;
    }
  }

  public static void ClearCache() => Cache.Clear();

  /// <summary>
  /// Runs a compiled script against the active drawing from an MCP request
  /// (marshals into AutoCAD command context through the serialized host gate).
  /// </summary>
  public static Task<ScriptRunResult> RunFromRequestAsync(CompiledScript compiled, JsonObject? args, bool write, string description)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    return CivilExecution.ExecuteInCommandContextAsync(async () =>
    {
      var (document, civilDocument) = CivilExecution.GetVerifiedActiveDocuments();
      var undoGrouped = write && UndoGroupingEnabled && await TryRunUndoAsync(document, "_BEGIN");
      try
      {
        var result = await RunInDocumentAsync(compiled, document, civilDocument, args, write, cancellationToken, lockDocument: true);
        PluginLog.Info("Script", $"{(write ? "execute" : "query")} ok hash={compiled.Hash[..12]} ms={result.ElapsedMs} desc={Truncate(description, 120)}");
        return result with { UndoGrouped = undoGrouped };
      }
      finally
      {
        if (undoGrouped)
        {
          await TryRunUndoAsync(document, "_END");
        }
      }
    });
  }

  /// <summary>
  /// Core execution. Caller must be on the AutoCAD main thread. Set
  /// <paramref name="lockDocument"/> false when already inside a document command
  /// (e.g. MCPRUN), where AutoCAD holds the lock.
  /// </summary>
  public static async Task<ScriptRunResult> RunInDocumentAsync(
    CompiledScript compiled,
    Document document,
    CivilDocument civilDocument,
    JsonObject? args,
    bool write,
    CancellationToken externalToken,
    bool lockDocument)
  {
    using var timeout = new CancellationTokenSource(Timeout);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, timeout.Token);
    var stopwatch = Stopwatch.StartNew();
    var database = document.Database;

    using DocumentLock? documentLock = lockDocument ? document.LockDocument() : null;
    using var changes = ViewFollower.Track(database);
    using var transaction = database.TransactionManager.StartTransaction();
    var context = new ScriptContext(document, civilDocument, database, transaction, args, write, linked.Token);

    object? returnValue;
    try
    {
      var state = await compiled.Script.RunAsync(context, cancellationToken: linked.Token);
      returnValue = state.ReturnValue;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !externalToken.IsCancellationRequested)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.TIMEOUT",
        $"Script exceeded {Timeout.TotalSeconds:0}s and was stopped; no changes were committed. Check CancellationToken in long loops or raise CIVIL3D_SCRIPT_TIMEOUT_SECONDS.");
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (System.Exception exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.SCRIPT_ERROR", FormatRuntimeError(exception, context.Output));
    }

    // Serialize while the transaction is still open so DBObjects are readable.
    var json = ScriptResultSerializer.ToJson(returnValue);

    if (write)
    {
      transaction.Commit();
      transaction.Dispose();
      ViewFollower.Follow(document, changes);
    }
    else
    {
      transaction.Abort();
    }

    return new ScriptRunResult(json, context.Output.ToList(), write, stopwatch.ElapsedMilliseconds, compiled.Warnings, false);
  }

  private static CompiledScript CompileCore(string code, string hash)
  {
    var script = CSharpScript.Create<object>(code, BuildOptions(), typeof(ScriptContext));
    var diagnostics = script.Compile();
    var errors = diagnostics.Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error).ToList();
    if (errors.Count > 0)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.COMPILATION_ERROR",
        "C# compilation failed:\n" + string.Join("\n", errors.Take(25).Select(FormatDiagnostic)) +
        (errors.Count > 25 ? $"\n... and {errors.Count - 25} more errors." : string.Empty));
    }

    var warnings = diagnostics
      .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Warning)
      .Take(20)
      .Select(FormatDiagnostic)
      .ToList();

    return new CompiledScript(hash, script, warnings);
  }

  private static ScriptOptions BuildOptions()
  {
    return ScriptOptions.Default
      .WithReferences(GetReferences())
      .WithImports(Imports)
      .WithAllowUnsafe(false)
      .WithOptimizationLevel(RoslynOptimization.Debug)
      .WithEmitDebugInformation(true)
      .WithFilePath(ScriptFileName)
      .WithFileEncoding(Encoding.UTF8);
  }

  private static IReadOnlyList<RoslynReference> GetReferences()
  {
    lock (ReferenceLock)
    {
      var count = Civil3DCompatibility.GetLoadedAssemblyCount();
      if (_references != null && count == _referenceAssemblyCount)
      {
        return _references;
      }

      // One reference per simple assembly name avoids duplicate-type errors
      // when two versions of a dependency are loaded side by side.
      _references = Civil3DCompatibility.GetLoadedAssemblyLocations()
        .GroupBy(location => Path.GetFileNameWithoutExtension(location), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(location =>
        {
          try
          {
            return (RoslynReference?)RoslynReference.CreateFromFile(location);
          }
          catch (System.Exception)
          {
            return null;
          }
        })
        .Where(reference => reference != null)
        .Cast<RoslynReference>()
        .ToList();
      _referenceAssemblyCount = count;
      return _references;
    }
  }

  private static async Task<bool> TryRunUndoAsync(Document document, string option)
  {
    try
    {
      await document.Editor.CommandAsync("_.UNDO", option);
      return true;
    }
    catch (System.Exception exception)
    {
      PluginLog.Warn("Script", $"UNDO {option} failed; changes will not be grouped: {exception.Message}");
      return false;
    }
  }

  private static string FormatDiagnostic(RoslynDiagnostic diagnostic)
  {
    var span = diagnostic.Location.GetLineSpan();
    var position = span.IsValid
      ? $"Line {span.StartLinePosition.Line + 1}, col {span.StartLinePosition.Character + 1}: "
      : string.Empty;
    return $"{position}{diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
  }

  private static readonly Regex ScriptLinePattern = new(@"script\.csx:line (\d+)", RegexOptions.Compiled);

  private static string FormatRuntimeError(System.Exception exception, IReadOnlyList<string> output)
  {
    var root = exception;
    while ((root is AggregateException || root.GetType().Name == "TargetInvocationException") && root.InnerException != null)
    {
      root = root.InnerException;
    }

    var builder = new StringBuilder();
    builder.Append(root.GetType().Name).Append(": ").Append(root.Message);

    if (root is Autodesk.AutoCAD.Runtime.Exception autocadException)
    {
      builder.Append(" (ErrorStatus.").Append(autocadException.ErrorStatus).Append(')');
    }

    var line = ScriptLinePattern.Match(root.StackTrace ?? string.Empty);
    if (line.Success)
    {
      builder.Append(" at script line ").Append(line.Groups[1].Value);
    }

    builder.Append(". The transaction was rolled back; no changes were committed.");

    if (output.Count > 0)
    {
      builder.Append("\nOutput before failure:\n").Append(string.Join("\n", output.TakeLast(20)));
    }

    return builder.ToString();
  }

  private static string Sha256(string text)
  {
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
  }

  private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
