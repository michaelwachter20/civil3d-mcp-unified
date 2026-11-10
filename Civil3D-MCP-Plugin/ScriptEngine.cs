// ScriptEngine.cs - compiles and runs C# scripts with Roslyn inside Civil 3D. Callers: ScriptCommands (executeCode,
// queryCode, compileCode, scriptSave, scriptRun, MCPRUN). Steps: ScriptSandbox checks the text, ScriptLoopGuard adds
// cancellation checks to loops, Roslyn compiles against every loaded assembly (Civil3DCompatibility lists them;
// ScriptReferenceResolver keeps memory flat), ScriptSymbolCheck checks what the names resolve to, then the script runs
// with ScriptContext as its globals. Write runs: CivilExecution.ExecuteInCommandContextAsync (host gate, command
// context), document lock, one transaction committed, wrapped in UNDO BEGIN/END so it is one undo step, view follows.
// Read runs: CivilExecution.ExecuteReadOnlyAsync (host gate, main thread, no lock), and the transaction is always
// aborted, so nothing the script did is kept. Any error or timeout aborts the transaction (nothing committed).
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

/// <summary>A compiled script: SHA-256 of its source (the cache key), the Roslyn script and up to 20 compiler warnings.</summary>
internal sealed record CompiledScript(string Hash, Script<object> Script, IReadOnlyList<string> Warnings);

/// <summary>The outcome of one run: the return value as JSON, the Print lines, whether the transaction was committed,
/// time taken, compiler warnings, and whether the run was wrapped in UNDO BEGIN/END.</summary>
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
/// the source; execution happens in one transaction (committed for write runs, aborted for queries). Write runs go
/// through command context under a document lock; read runs go through the read path on the main thread without a
/// lock (see <see cref="RunFromRequestAsync"/>).
/// </summary>
/// <remarks>Based on RoslynExecutor from barbosaihan/civil3d-mcp (MIT).</remarks>
internal static class ScriptEngine
{
  // The file name Roslyn gives the script; runtime stack traces say "script.csx:line N" (see FormatRuntimeError).
  public const string ScriptFileName = "script.csx";
  // A compiled script keeps native memory alive until it leaves this cache, and compiling again takes about a second.
  private const int MaxCacheEntries = 16;

  private static readonly ConcurrentDictionary<string, Lazy<CompiledScript>> Cache = new(StringComparer.Ordinal);
  private static readonly object ReferenceLock = new();
  private static IReadOnlyList<RoslynReference>? _references;
  private static int _referenceFileCount;

  /// <summary>Each assembly file is read once: a null entry is one that can't be a reference (not usable, or unreadable).</summary>
  private static readonly Dictionary<string, RoslynReference?> ReferenceByPath = new(StringComparer.OrdinalIgnoreCase);

  private static readonly ScriptReferenceResolver ReferenceResolver = new(ScriptOptions.Default.MetadataResolver);

  /// <summary>Namespaces every script gets as <c>using</c>s (also reported by getScriptingStatus).</summary>
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
    // So scripts can name ReferenceDrawing / OpenDrawingInfo (plugin internals stay blocked by the sandbox).
    "Civil3DMcpPlugin",
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

  /// <summary>
  /// Validates against the sandbox and compiles (or returns the cached compilation). The cache holds at most 16
  /// scripts and is cleared, with a forced GC, when full. The symbol check runs on every call for the given mode.
  /// Throws CIVIL3D.INVALID_INPUT (empty or over 200,000 characters), CIVIL3D.SANDBOX_VIOLATION and
  /// CIVIL3D.COMPILATION_ERROR (up to 25 errors with line and column). Doesn't touch the drawing.
  /// </summary>
  public static CompiledScript Compile(string code, bool writeMode)
  {
    ScriptSandbox.Validate(code, writeMode);

    var hash = Sha256(code);
    if (Cache.Count >= MaxCacheEntries)
    {
      Cache.Clear();
      // Roslyn's native copies are freed by finalizers, and the GC can't see their size, so it wouldn't run for them.
      GC.Collect();
    }

    var entry = Cache.GetOrAdd(hash, key => new Lazy<CompiledScript>(() => CompileCore(code, key), LazyThreadSafetyMode.ExecutionAndPublication));
    CompiledScript compiled;
    try
    {
      compiled = entry.Value;
    }
    catch
    {
      Cache.TryRemove(hash, out _);
      throw;
    }

    // On every call, cached or not: the same code can be allowed in a write and refused in a read.
    ScriptSymbolCheck.Validate(compiled.Script.GetCompilation(), writeMode);
    return compiled;
  }

  /// <summary>Drops every cached compilation.</summary>
  public static void ClearCache() => Cache.Clear();

  /// <summary>
  /// Runs a compiled script against the active drawing from an MCP request, through the serialized host gate: a
  /// write run in AutoCAD command context (wrapped in UNDO BEGIN/END when undo grouping is on, so all its changes are
  /// one undo step), a read run on the main thread outside command context (no lock, no undo step, transaction
  /// aborted). Uses the request's cancellation token. Throws CIVIL3D.NO_DRAWING / CIVIL3D.CONFLICT (drawing changed
  /// while queued), CIVIL3D.HOST_BUSY, and the run errors of <see cref="RunInDocumentAsync"/>.
  /// </summary>
  public static Task<ScriptRunResult> RunFromRequestAsync(CompiledScript compiled, JsonObject? args, bool write, string description)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    Func<Func<Task<ScriptRunResult>>, Task<ScriptRunResult>> host = write
      ? CivilExecution.ExecuteInCommandContextAsync
      : CivilExecution.ExecuteReadOnlyAsync;
    return host(async () =>
    {
      var (document, civilDocument) = CivilExecution.GetVerifiedActiveDocuments();
      var undoGrouped = write && UndoGroupingEnabled && await TryRunUndoAsync(document, "_BEGIN");
      try
      {
        var result = await RunInDocumentAsync(compiled, document, civilDocument, args, write, cancellationToken, lockDocument: write);
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
  /// Opens one transaction, runs the script with a <see cref="ScriptContext"/> under the timeout
  /// (CIVIL3D_SCRIPT_TIMEOUT_SECONDS) linked with <paramref name="externalToken"/>, serializes the return value while
  /// the transaction is still open, then commits (write: the drawing changes, the view follows) or aborts (read: the
  /// rollback that keeps civil3d_query read-only). On any exception the transaction is disposed without a commit, so
  /// nothing is kept. Reference drawings the script opened are always closed. Throws CIVIL3D.TIMEOUT,
  /// CIVIL3D.SCRIPT_ERROR (exception type, message, script line, last output) or cancellation.
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
    try
    {
      return RunAndFinish(await RunScriptAsync(compiled, context, timeout, externalToken, linked.Token));
    }
    finally
    {
      // Reference drawings (go-bys) are read only: their transactions are always aborted.
      context.CloseReferences();
    }

    ScriptRunResult RunAndFinish(object? returnValue)
    {
      // Serialize while the transactions are still open so DBObjects are readable.
      var json = ScriptResultSerializer.ToJson(returnValue);

      if (write)
      {
        transaction.Commit();
        transaction.Dispose();
        ViewFollower.Follow(document, changes, context.Focus);
      }
      else
      {
        transaction.Abort();
      }

      return new ScriptRunResult(json, context.Output.ToList(), write, stopwatch.ElapsedMilliseconds, compiled.Warnings, false);
    }
  }

  /// <summary>Runs the script and maps failures: our timeout (not the caller's cancel) becomes CIVIL3D.TIMEOUT, a
  /// caller cancel and JsonRpcDispatchException pass through, anything else becomes CIVIL3D.SCRIPT_ERROR.</summary>
  private static async Task<object?> RunScriptAsync(
    CompiledScript compiled,
    ScriptContext context,
    CancellationTokenSource timeout,
    CancellationToken externalToken,
    CancellationToken linkedToken)
  {
    object? returnValue;
    try
    {
      var state = await compiled.Script.RunAsync(context, cancellationToken: linkedToken);
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

    return returnValue;
  }

  /// <summary>Compiles once (behind the cache's Lazy): with the loop guard, else as written; CIVIL3D.COMPILATION_ERROR
  /// on errors.</summary>
  private static CompiledScript CompileCore(string code, string hash)
  {
    // Every loop checks the token, so the timeout can stop a tight one (ScriptLoopGuard). If that doesn't compile (a
    // variable of the script named CancellationToken), the script runs as written, without the guard.
    var options = BuildOptions();
    var script = CSharpScript.Create<object>(ScriptLoopGuard.AddChecks(code), options, typeof(ScriptContext));
    var diagnostics = script.Compile();
    if (diagnostics.Any(diagnostic => diagnostic.Severity == RoslynSeverity.Error))
    {
      script = CSharpScript.Create<object>(code, options, typeof(ScriptContext));
      diagnostics = script.Compile();
    }

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

    PluginLog.Info("Script", $"Compiled {hash[..12]}: {ReferenceResolver.ResolvedCount} assemblies found beyond the loaded ones, private memory {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB.");
    return new CompiledScript(hash, script, warnings);
  }

  /// <summary>Roslyn options: loaded assemblies as references, the default imports, no unsafe code, Release IL with
  /// debug info (for line numbers), UTF-8.</summary>
  private static ScriptOptions BuildOptions()
  {
    return ScriptOptions.Default
      .WithReferences(GetReferences())
      .WithMetadataResolver(ReferenceResolver)
      .WithImports(Imports)
      .WithAllowUnsafe(false)
      // Release: Debug-mode script IL crashed the .NET 10 runtime twice inside Civil 3D 2027
      // (clrjit access violation, then an AV during exception dispatch) on long-running loops.
      // Debug information is still emitted, so runtime errors keep their script line numbers.
      .WithOptimizationLevel(RoslynOptimization.Release)
      .WithEmitDebugInformation(true)
      .WithFilePath(ScriptFileName)
      .WithFileEncoding(Encoding.UTF8);
  }

  /// <summary>One metadata reference per loaded assembly name (from Civil3DCompatibility), rebuilt only when the number
  /// of assembly files changes.</summary>
  private static IReadOnlyList<RoslynReference> GetReferences()
  {
    lock (ReferenceLock)
    {
      // Counted by files on disk, not loaded assemblies: every script loads one more assembly of its own, from memory.
      // Counting those read all of Civil 3D's assemblies again for each new script, about 460 MB that the cached
      // script then kept alive, until a day's scripts had taken 19 GB and the computer ran out of memory.
      var locations = Civil3DCompatibility.GetLoadedAssemblyLocations();
      if (_references != null && locations.Length == _referenceFileCount)
      {
        return _references;
      }

      // One reference per simple assembly name avoids duplicate-type errors
      // when two versions of a dependency are loaded side by side.
      _references = locations
        .GroupBy(location => Path.GetFileNameWithoutExtension(location), StringComparer.OrdinalIgnoreCase)
        .Select(group => ReferenceFor(group.First()))
        .Where(reference => reference != null)
        .Cast<RoslynReference>()
        .ToList();
      _referenceFileCount = locations.Length;
      return _references;
    }
  }

  /// <summary>The reference for one assembly file, read once however many times the list is rebuilt (the caller holds ReferenceLock).</summary>
  private static RoslynReference? ReferenceFor(string location)
  {
    if (ReferenceByPath.TryGetValue(location, out var known))
    {
      return known;
    }

    RoslynReference? reference = null;
    if (Civil3DCompatibility.IsUsableScriptReference(location))
    {
      try
      {
        reference = RoslynReference.CreateFromFile(location);
      }
      catch (System.Exception)
      {
        // Left out of the list, as an unusable assembly is.
      }
    }

    ReferenceByPath[location] = reference;
    return reference;
  }

  /// <summary>Runs <c>UNDO _BEGIN</c> or <c>_END</c>; false (logged) when it fails, and the run is then not grouped.</summary>
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

  /// <summary>The CIVIL3D.SCRIPT_ERROR message: the innermost exception's type and message, the AutoCAD ErrorStatus,
  /// the script line from the stack trace, a note that nothing was committed, and the last 20 output lines.</summary>
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
