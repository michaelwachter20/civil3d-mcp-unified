// ScriptCommands.cs - the JSON-RPC face of the Roslyn code-execution engine and of acad_command.
// Methods (CommandDispatcher.cs): executeCode, queryCode, compileCode, getScriptingStatus, runAcadCommand,
// scriptSave, scriptList, scriptGet, scriptRun, scriptDelete. Caller: src/tools/domains/scriptingDomain.ts
// (civil3d_execute, civil3d_query, acad_command, civil3d_script). PluginEntry calls the MCPRUN / MCPSCRIPTS helpers.
// The work is done by ScriptSandbox + ScriptSymbolCheck (what code may do), ScriptEngine (compile and run inside the
// host gate: write runs in command context with a lock, one transaction and UNDO BEGIN/END; read runs on the main
// thread, transaction always aborted), ScriptContext (the script globals) and ScriptLibrary (saved .csx files,
// written only through FileBoundary). Code that creates COGO points asks the user first (PointCreationGate).
// Approval for write runs is enforced on the Node side before a request gets here.
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// JSON-RPC handlers for code execution (civil3d_execute / civil3d_query),
/// raw AutoCAD commands (acad_command), and the saved-script library, plus the
/// MCPRUN / MCPSCRIPTS command-line commands.
/// </summary>
public static class ScriptCommands
{
  // Commands acad_command refuses while host protection is on: they start programs, load code, run script files,
  // close the session or the AI connection, or open dialogs that would block the host queue.
  private static readonly HashSet<string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
  {
    "SHELL", "SH", "START", "NETLOAD", "APPLOAD", "ARX", "SECURELOAD", "TRUSTEDPATHS",
    "VBALOAD", "VBARUN", "VBAIDE", "VBAMAN", "QUIT", "EXIT", "CLOSE", "CLOSEALL",
    "C3DMCPSTOP", "C3DMCPSTART", "MCPRUN", "SCRIPT", "RSCRIPT", "OPTIONS", "CUI", "PUBLISH",
  };

  // ---------------------------------------------------------------------------
  // Code execution
  // ---------------------------------------------------------------------------

  /// <summary>
  /// <c>executeCode</c> (civil3d_execute): compiles and runs AI-written C# as a drawing change. Reads <c>code</c>
  /// (required), <c>description</c> and <c>args</c> (object, given to the script as <c>Args</c>). The transaction is
  /// committed and the run is one undo step. Returns the run (see <see cref="Describe"/>). Throws
  /// CIVIL3D.SANDBOX_VIOLATION, CIVIL3D.COMPILATION_ERROR, CIVIL3D.SCRIPT_ERROR, CIVIL3D.TIMEOUT (nothing committed
  /// in those cases), and the PointCreationGate refusal when the user says no to code that creates COGO points.
  /// </summary>
  public static async Task<object?> ExecuteCodeAsync(JsonObject? parameters)
  {
    return await RunCodeAsync(parameters, write: true);
  }

  /// <summary>
  /// <c>queryCode</c> (civil3d_query): runs AI-written C# read-only. Same parameters as <see cref="ExecuteCodeAsync"/>;
  /// the read-only sandbox rules also apply and the transaction is always aborted, so the drawing is not changed and
  /// no undo step is added. Same error codes, minus the point-creation question.
  /// </summary>
  public static async Task<object?> QueryCodeAsync(JsonObject? parameters)
  {
    return await RunCodeAsync(parameters, write: false);
  }

  /// <summary>Shared body of execute/query: point-creation question (writes only), compile off the main thread, run
  /// through the host gate, describe the result.</summary>
  private static async Task<object?> RunCodeAsync(JsonObject? parameters, bool write)
  {
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? (write ? "execute" : "query");
    var args = parameters?["args"] as JsonObject;
    var pointAction = write ? PointCreationGate.ScriptPointAction(code) : null;
    if (pointAction != null)
    {
      await PointCreationGate.RequireAsync($"C# run by the AI ({description}) that will {pointAction} COGO points.",
        PointCreationGate.ScriptDetails("Code written by the AI for this request.", description, args, code), pointAction);
    }

    // Compile on the request thread so AutoCAD is not blocked by Roslyn.
    var compiled = await Task.Run(() => ScriptEngine.Compile(code, write));
    var run = await ScriptEngine.RunFromRequestAsync(compiled, args, write, description);
    return Describe(run, write ? "execute" : "query", null);
  }

  /// <summary>
  /// <c>compileCode</c>: compile-only check, used by the AI to validate code before saving or executing. Reads
  /// <c>code</c> (required) and <c>mode</c> ("write" default, or "read"); runs the sandbox and symbol checks for that
  /// mode. Returns success, mode, the source hash and compiler warnings. Doesn't touch the drawing. Throws
  /// CIVIL3D.SANDBOX_VIOLATION / CIVIL3D.COMPILATION_ERROR / CIVIL3D.INVALID_INPUT.
  /// </summary>
  public static async Task<object?> CompileCodeAsync(JsonObject? parameters)
  {
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    var write = ScriptLibrary.NormalizeMode(PluginRuntime.GetOptionalString(parameters, "mode")) == "write";
    var compiled = await Task.Run(() => ScriptEngine.Compile(code, write));
    return new Dictionary<string, object?>
    {
      ["success"] = true,
      ["mode"] = write ? "write" : "read",
      ["hash"] = compiled.Hash,
      ["warnings"] = compiled.Warnings,
    };
  }

  /// <summary><c>getScriptingStatus</c>: no parameters. Returns the scripts folder, saved-script count, whether host
  /// protection is on, the timeout, undo grouping, view following, the script globals and default imports. Doesn't
  /// touch the drawing.</summary>
  public static Task<object?> GetScriptingStatusAsync()
  {
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["roslyn"] = true,
      ["scriptsDirectory"] = ScriptLibrary.Directory,
      ["savedScriptCount"] = ScriptLibrary.List().Count,
      ["sandboxHostProtection"] = ScriptSandbox.HostProtectionEnabled,
      ["timeoutSeconds"] = (int)ScriptEngine.Timeout.TotalSeconds,
      ["undoGrouping"] = ScriptEngine.UndoGroupingEnabled,
      ["followView"] = ViewFollower.Enabled,
      ["globals"] = new[] { "Document", "CivilDoc", "Database", "Transaction", "Editor", "Args", "ModelSpace", "IsWriteMode", "CancellationToken", "Print(obj)", "Get<T>(id, mode)", "AddToModelSpace(entity)", "FromHandle(hex)" },
      ["imports"] = ScriptEngine.Imports,
    });
  }

  // ---------------------------------------------------------------------------
  // Raw AutoCAD commands
  // ---------------------------------------------------------------------------

  /// <summary>
  /// <c>runAcadCommand</c> (acad_command): runs one AutoCAD command with its prompt answers through
  /// <c>Editor.CommandAsync</c>. Reads <c>command</c> (required): a string, or an array whose first item is the command
  /// name and the rest are answers (strings, numbers, booleans as _Yes/_No, {x,y,z?} points, "" for Enter). Runs in
  /// command context through the host gate (not WriteAsync: no transaction of its own), so it can change the drawing.
  /// Command-line output isn't captured. Throws CIVIL3D.INVALID_INPUT (no command name), CIVIL3D.SANDBOX_VIOLATION
  /// (AutoLISP or a blocked command while host protection is on), CIVIL3D.COMMAND_FAILED, and asks the user first
  /// for commands that create COGO points.
  /// </summary>
  public static async Task<object?> RunAcadCommandAsync(JsonObject? parameters)
  {
    var tokens = ParseCommandTokens(parameters);
    var commandName = tokens[0] as string ?? string.Empty;
    var normalized = commandName.TrimStart('_', '.', '-', '+').ToUpperInvariant();

    if (ScriptSandbox.HostProtectionEnabled)
    {
      if (commandName.TrimStart().StartsWith('('))
      {
        throw new JsonRpcDispatchException("CIVIL3D.SANDBOX_VIOLATION", "AutoLISP expressions are blocked. Use civil3d_execute with C# instead.");
      }

      if (BlockedCommands.Contains(normalized))
      {
        throw new JsonRpcDispatchException("CIVIL3D.SANDBOX_VIOLATION", $"The '{normalized}' command is blocked for AI use.");
      }
    }

    if (PointCreationGate.IsCreateCommand(normalized))
    {
      await PointCreationGate.RequireAsync($"the Civil 3D command {normalized}, which creates COGO points.");
    }

    return await CivilExecution.ExecuteInCommandContextAsync<object?>(async () =>
    {
      var (document, _) = CivilExecution.GetVerifiedActiveDocuments();
      var started = DateTime.UtcNow;
      try
      {
        await document.Editor.CommandAsync(tokens);
      }
      catch (System.Exception exception)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.COMMAND_FAILED",
          $"Command '{commandName}' failed: {exception.Message}. Check the prompt sequence (use \"\" for Enter) and prefix with '_.' for language independence.");
      }

      PluginLog.Info("Script", $"acad_command {commandName} tokens={tokens.Length}");
      return new Dictionary<string, object?>
      {
        ["success"] = true,
        ["command"] = commandName,
        ["tokenCount"] = tokens.Length,
        ["elapsedMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
        ["note"] = "Command-line output is not captured. Verify results with civil3d_query.",
      };
    });
  }

  /// <summary>Turns <c>command</c> into the object[] CommandAsync takes (strings, invariant-culture numbers, Point3d,
  /// _Yes/_No). CIVIL3D.INVALID_INPUT when it is missing, empty, or the first token isn't a command name.</summary>
  private static object[] ParseCommandTokens(JsonObject? parameters)
  {
    var node = parameters?["command"] ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "command is required.");
    var items = node is JsonArray array ? array.ToList() : new List<JsonNode?> { node };
    if (items.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "command must contain at least the command name.");
    }

    var tokens = new object[items.Count];
    for (var index = 0; index < items.Count; index++)
    {
      tokens[index] = items[index] switch
      {
        null => string.Empty,
        JsonObject point when point["x"] != null && point["y"] != null => ScriptJson.ToPoint3d(point, $"command[{index}]"),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when ScriptJson.TryGetDouble(value, out var number) => number.ToString("R", CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "_Yes" : "_No",
        var other => other.ToJsonString(),
      };
    }

    if (tokens[0] is not string first || string.IsNullOrWhiteSpace(first))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The first command token must be the command name.");
    }

    return tokens;
  }

  // ---------------------------------------------------------------------------
  // Saved scripts
  // ---------------------------------------------------------------------------

  /// <summary>
  /// <c>scriptSave</c> (civil3d_script action=save): reads <c>name</c>, <c>description</c>, <c>code</c> (all required),
  /// <c>mode</c> (write/read), <c>overwrite</c> (bool) and <c>parameters</c> (array). The code must compile in its mode
  /// first. Writes a .csx file in the scripts folder (ScriptLibrary, via FileBoundary; a replaced version is archived);
  /// doesn't touch the drawing. Throws CIVIL3D.INVALID_INPUT, CIVIL3D.CONFLICT (exists, no overwrite), and the
  /// sandbox/compile errors. The MCP server checks the text for job data before sending it.
  /// </summary>
  public static async Task<object?> ScriptSaveAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var description = PluginRuntime.GetRequiredString(parameters, "description");
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    var mode = ScriptLibrary.NormalizeMode(PluginRuntime.GetOptionalString(parameters, "mode"));
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var scriptParameters = ScriptLibrary.ParseParameters(parameters?["parameters"]);

    // Refuse to save code that does not compile in its declared mode.
    var compiled = await Task.Run(() => ScriptEngine.Compile(code, mode == "write"));
    var saved = ScriptLibrary.Save(name, description, code, mode, scriptParameters, overwrite);
    PluginLog.Info("Script", $"saved script '{saved.Name}' mode={saved.Mode} path={saved.Path}");

    return new Dictionary<string, object?>
    {
      ["success"] = true,
      ["script"] = ScriptLibrary.Describe(saved, includeCode: false),
      ["warnings"] = compiled.Warnings,
      ["runFromCivil3D"] = $"Type MCPRUN and enter '{saved.Name}'.",
    };
  }

  /// <summary><c>scriptList</c>: saved scripts (without code), optionally filtered by <c>query</c> (case-insensitive,
  /// in name or description). Reads files only.</summary>
  public static Task<object?> ScriptListAsync(JsonObject? parameters)
  {
    var query = PluginRuntime.GetOptionalString(parameters, "query")?.Trim();
    var scripts = ScriptLibrary.List()
      .Where(script => string.IsNullOrEmpty(query) ||
        script.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        script.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
      .Select(script => ScriptLibrary.Describe(script, includeCode: false))
      .ToList();

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["directory"] = ScriptLibrary.Directory,
      ["count"] = scripts.Count,
      ["scripts"] = scripts,
    });
  }

  /// <summary><c>scriptGet</c>: one saved script with its code, by <c>name</c> (required). CIVIL3D.OBJECT_NOT_FOUND /
  /// CIVIL3D.INVALID_INPUT from ScriptLibrary.Get.</summary>
  public static Task<object?> ScriptGetAsync(JsonObject? parameters)
  {
    var script = ScriptLibrary.Get(PluginRuntime.GetRequiredString(parameters, "name"));
    return Task.FromResult<object?>(ScriptLibrary.Describe(script, includeCode: true));
  }

  /// <summary>
  /// <c>scriptRun</c>: runs a saved script by <c>name</c> (required) with <c>args</c> (merged with its declared
  /// defaults and coerced to their types). A "write" script changes the drawing like executeCode (one undo step); a
  /// "read" script runs like queryCode (rolled back). Throws CIVIL3D.OBJECT_NOT_FOUND, CIVIL3D.INVALID_INPUT (missing
  /// or wrong-typed argument), and the sandbox, compile, script and timeout errors.
  /// </summary>
  public static async Task<object?> ScriptRunAsync(JsonObject? parameters)
  {
    var script = ScriptLibrary.Get(PluginRuntime.GetRequiredString(parameters, "name"));
    var args = ScriptLibrary.BuildArguments(script, parameters?["args"] as JsonObject);
    var pointAction = script.IsWrite ? PointCreationGate.ScriptPointAction(script.Code) : null;
    if (pointAction != null)
    {
      await PointCreationGate.RequireAsync($"the saved script '{script.Name}', which will {pointAction} COGO points.",
        PointCreationGate.ScriptDetails($"Saved script '{script.Name}':", script.Description, parameters?["args"] as JsonObject, script.Code),
        pointAction);
    }

    var compiled = await Task.Run(() => ScriptEngine.Compile(script.Code, script.IsWrite));
    var run = await ScriptEngine.RunFromRequestAsync(compiled, args, script.IsWrite, $"script:{script.Name}");
    return Describe(run, script.IsWrite ? "execute" : "query", script.Name);
  }

  /// <summary><c>scriptDelete</c>: retires the saved script <c>name</c> (required): it is copied to <c>_archive</c>
  /// and then removed from the library, never lost. Returns the archive path. CIVIL3D.OBJECT_NOT_FOUND when there is
  /// no such script.</summary>
  public static Task<object?> ScriptDeleteAsync(JsonObject? parameters)
  {
    var name = ScriptLibrary.NormalizeName(PluginRuntime.GetRequiredString(parameters, "name"));
    var archivedTo = ScriptLibrary.Delete(name);
    if (archivedTo == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Saved script '{name}' was not found.");
    }

    PluginLog.Info("Script", $"retired script '{name}' to the archive");
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["success"] = true,
      ["deleted"] = name,
      ["archivedTo"] = archivedTo,
    });
  }

  /// <summary>The JSON answer for a run: success, mode ("execute"/"query"), committed, undoGrouped, elapsedMs, the
  /// script's serialized return value (<c>result</c>), its Print lines (<c>output</c>), and warnings / undo note when
  /// they apply.</summary>
  private static Dictionary<string, object?> Describe(ScriptRunResult run, string mode, string? scriptName)
  {
    var result = new Dictionary<string, object?>
    {
      ["success"] = true,
      ["mode"] = mode,
      ["committed"] = run.Committed,
      ["undoGrouped"] = run.UndoGrouped,
      ["elapsedMs"] = run.ElapsedMs,
      ["result"] = run.Result,
      ["output"] = run.Output,
    };

    if (scriptName != null) result["script"] = scriptName;
    if (run.Warnings.Count > 0) result["warnings"] = run.Warnings;
    if (run.Committed && run.UndoGrouped) result["undo"] = "All changes from this run form one undo step (UNDO 1 reverts them).";
    return result;
  }

  // ---------------------------------------------------------------------------
  // Command line: MCPRUN / MCPSCRIPTS (invoked from PluginEntry)
  // ---------------------------------------------------------------------------

  /// <summary>MCPSCRIPTS: prints the saved scripts (name, mode, parameters, description) on the command line.</summary>
  internal static void ListSavedScriptsInteractive()
  {
    var editor = App.DocumentManager.MdiActiveDocument?.Editor;
    if (editor == null) return;

    var scripts = ScriptLibrary.List();
    if (scripts.Count == 0)
    {
      editor.WriteMessage($"\nNo saved scripts in {ScriptLibrary.Directory}.");
      return;
    }

    editor.WriteMessage($"\nSaved scripts ({ScriptLibrary.Directory}):");
    foreach (var script in scripts)
    {
      var parameters = script.Parameters.Count == 0 ? string.Empty : $" ({string.Join(", ", script.Parameters.Select(p => p.Name))})";
      editor.WriteMessage($"\n  {script.Name} [{script.Mode}]{parameters} - {script.Description}");
    }
  }

  /// <summary>
  /// MCPRUN: the user runs a saved script by hand. Prompts for the name ("?" lists them) and each declared parameter,
  /// compiles, and runs it synchronously inside the user's own command (which already holds the document lock and
  /// makes the undo step), not through the host gate or the AI connection. A write script commits; a read script
  /// rolls back. Errors are written to the command line, never thrown.
  /// </summary>
  internal static void RunSavedScriptInteractive()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    if (document == null) return;
    var editor = document.Editor;

    try
    {
      var nameOptions = new PromptStringOptions("\nSaved script name (? to list): ") { AllowSpaces = false };
      var nameResult = editor.GetString(nameOptions);
      if (nameResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nameResult.StringResult)) return;
      if (nameResult.StringResult.Trim() == "?")
      {
        ListSavedScriptsInteractive();
        return;
      }

      var script = ScriptLibrary.Get(nameResult.StringResult);
      var provided = new JsonObject();
      foreach (var parameter in script.Parameters)
      {
        var value = PromptForParameter(editor, parameter);
        if (value == null)
        {
          if (parameter.Default == null && parameter.Required)
          {
            editor.WriteMessage("\n*Cancel*");
            return;
          }

          continue;
        }

        provided[parameter.Name] = value;
      }

      var args = ScriptLibrary.BuildArguments(script, provided);
      editor.WriteMessage($"\nCompiling '{script.Name}'...");
      var compiled = ScriptEngine.Compile(script.Code, script.IsWrite);
      var civilDocument = CivilApplication.ActiveDocument;

      // Inside a document command AutoCAD already holds the lock and groups undo.
      var run = ScriptEngine
        .RunInDocumentAsync(compiled, document, civilDocument, args, script.IsWrite, CancellationToken.None, lockDocument: false)
        .GetAwaiter()
        .GetResult();

      foreach (var line in run.Output)
      {
        editor.WriteMessage($"\n{line}");
      }

      var resultText = run.Result?.ToJsonString() ?? "null";
      if (resultText.Length > 2000) resultText = resultText[..2000] + "...";
      editor.WriteMessage($"\n{script.Name}: {(run.Committed ? "committed" : "read-only")} in {run.ElapsedMs} ms. Result: {resultText}");
    }
    catch (JsonRpcDispatchException exception)
    {
      editor.WriteMessage($"\nMCPRUN: {exception.Message}");
    }
    catch (System.Exception exception)
    {
      PluginLog.Error("Script", "MCPRUN failed", exception);
      editor.WriteMessage($"\nMCPRUN failed: {exception.Message}");
    }
  }

  /// <summary>Asks for one script parameter with the prompt that fits its type (point pick, number, Yes/No keyword,
  /// or text; "string[]"/"json" text is parsed as JSON, else split on commas). Null when the user skips or cancels.</summary>
  private static JsonNode? PromptForParameter(Editor editor, ScriptParameter parameter)
  {
    var label = $"\n{parameter.Name}" + (parameter.Description != null ? $" ({parameter.Description})" : string.Empty);
    switch (parameter.Type)
    {
      case "point":
      {
        var result = editor.GetPoint(new PromptPointOptions(label + ": ") { AllowNone = !parameter.Required || parameter.Default != null });
        if (result.Status != PromptStatus.OK) return null;
        return new JsonObject { ["x"] = result.Value.X, ["y"] = result.Value.Y, ["z"] = result.Value.Z };
      }
      case "number":
      case "integer":
      {
        var options = new PromptDoubleOptions(label + ": ") { AllowNone = !parameter.Required || parameter.Default != null };
        if (ScriptJson.TryGetDouble(parameter.Default, out var defaultNumber))
        {
          options.DefaultValue = defaultNumber;
          options.UseDefaultValue = true;
        }

        var result = editor.GetDouble(options);
        if (result.Status != PromptStatus.OK) return null;
        return parameter.Type == "integer" ? JsonValue.Create((long)Math.Round(result.Value)) : JsonValue.Create(result.Value);
      }
      case "boolean":
      {
        var options = new PromptKeywordOptions(label) { AllowNone = !parameter.Required || parameter.Default != null };
        options.Keywords.Add("Yes");
        options.Keywords.Add("No");
        if (parameter.Default is JsonValue b && b.TryGetValue<bool>(out var defaultFlag))
        {
          options.Keywords.Default = defaultFlag ? "Yes" : "No";
        }

        var result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return null;
        return JsonValue.Create(result.StringResult == "Yes");
      }
      default:
      {
        var defaultText = parameter.Default is JsonValue s && s.TryGetValue<string>(out var text) ? text : parameter.Default?.ToJsonString();
        var options = new PromptStringOptions(label + (defaultText != null ? $" <{defaultText}>" : string.Empty) + ": ") { AllowSpaces = true };
        var result = editor.GetString(options);
        if (result.Status != PromptStatus.OK) return null;
        if (string.IsNullOrEmpty(result.StringResult)) return null;
        if (parameter.Type is "string[]" or "json")
        {
          try
          {
            return JsonNode.Parse(result.StringResult);
          }
          catch (System.Text.Json.JsonException)
          {
            return parameter.Type == "string[]"
              ? new JsonArray(result.StringResult.Split(',').Select(item => (JsonNode?)JsonValue.Create(item.Trim())).ToArray())
              : JsonValue.Create(result.StringResult);
          }
        }

        return JsonValue.Create(result.StringResult);
      }
    }
  }
}
