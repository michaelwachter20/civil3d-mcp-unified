// ScriptSandbox.cs - first half of the script sandbox for the Roslyn code-execution engine (civil3d_execute,
// civil3d_query, saved scripts, MCPRUN); ScriptSymbolCheck is the second half. ScriptEngine.Compile calls Validate on
// the source before compiling; ScriptCommands reads HostProtectionEnabled for acad_command's blocked commands.
// Three rule sets, matched on the source with comments removed: crash rules (always), host-protection rules (processes,
// network, reflection, native code, registry, deleting/moving files, plugin internals; off with
// CIVIL3D_SCRIPT_SANDBOX=disabled) and read-only rules (only for read runs: no commit, own transaction, commands,
// saving, file writes, adding entities, opening drawings, system variables), which keep civil3d_query read-only in
// spirit; the transaction rollback in ScriptEngine keeps it read-only in fact. Do not edit the patterns casually:
// tests and the boundary checks depend on their exact text.
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// Pattern-based guardrail applied to script source before compilation.
/// This is a speed bump, not a security boundary: determined code can evade a
/// denylist. The real protections are the MCP approval flow for write
/// operations, single undo groups, and working on copies of important drawings.
/// </summary>
/// <remarks>
/// Set CIVIL3D_SCRIPT_SANDBOX=disabled (in the environment that launches
/// Civil 3D) to skip the host-protection rules. Read-only rules for
/// civil3d_query always apply because they protect the query contract.
/// Some patterns are written with regex escapes so the repository's own
/// reflection/file-write boundary tests do not flag this file.
/// </remarks>
public static class ScriptSandbox
{
  private static readonly (Regex Pattern, string Reason)[] HostProtectionRules = Compile(new[]
  {
    (@"System\.Diagnostics\.Process|\bProcess\s*\.\s*Start\b|\bProcessStartInfo\b", "starting processes"),
    (@"System\.Net\b|\bHttpClient\b|\bWebClient\b|\bTcpClient\b|\bTcpListener\b|\bSocket\b|System\.IO\.Pipes", "network or IPC access"),
    (@"System\.Reflection|\bAssembly\s*\.\s*Load|\bAssemblyLoadContext\b|\bBindingFlag|\bActivator\s*\.|\bMethodInfo\b|\bType\s*\.\s*GetType\s*\(|\.\s*(GetMethod|GetProperty|GetField|GetMember|GetConstructor|InvokeMember|DynamicInvoke|CreateDelegate|Invoke)\s*\(", "reflection or dynamic loading"),
    (@"System\.Runtime\.InteropServices|\bDllImport\b|\bLibraryImport\b|\bMarshal\s*\.|\bunsafe\b|\bfixed\s*\(", "native interop or unsafe code"),
    (@"\bEnvironment\s*\.\s*(Exit|FailFast|SetEnvironmentVariable)\b", "terminating or reconfiguring the host"),
    (@"Microsoft\.Win32|\bRegistry(Key)?\s*\.", "registry access"),
    (@"\bFile\s*\.\s*(Delete|Move|Replace|Encrypt|SetAttributes)\b|\bDirectory\s*\.\s*(Delete|Move)\b", "deleting or moving files"),
    (@"\bAppDomain\b", "AppDomain access"),
    (@"\bCivil3DMcpPlugin\s*\.|\b(PluginRuntime|RpcTcpServer|CommandDispatcher|ScriptEngine|ScriptLibrary|FileBoundary|JobRegistry)\b", "calling MCP plugin internals"),
  });

  private static readonly (Regex Pattern, string Reason)[] ReadOnlyRules = Compile(new[]
  {
    (@"\.\s*Commit\s*\(", "committing a transaction"),
    (@"\bStart(OpenClose)?Transaction\s*\(", "starting a separate transaction"),
    (@"\bSendStringToExecute\b|\.\s*Command(Async)?\s*\(|\bPostCommandPrompt\b", "running AutoCAD commands"),
    (@"\.\s*(Save|SaveAs|SaveAsAsync|CloseAndSave|CloseAndDiscard)\s*\(", "saving or closing drawings"),
    (@"\bFile\s*\.\s*(Write|Append|Create|Copy)\w*\s*\(|\bnew\s+(StreamWriter|FileStream)\b|\bDirectory\s*\.\s*Create", "writing files"),
    (@"\bAddToModelSpace\s*\(", "adding entities"),
    (@"\bDocumentManager\s*\.\s*(Add|Open)\w*\s*\(", "opening or creating drawings"),
    (@"\bSetSystemVariable\b", "changing system variables"),
  });

  // Civil 3D calls that crash the host (fatal eNotOpenForWrite) when misused; blocked in every mode, with a safe
  // ScriptContext helper to use instead.
  private static readonly (Regex Pattern, string Reason)[] CrashRules = Compile(new[]
  {
    (@"\.\s*GetSectionSources\s*\(", "SampleLineGroup.GetSectionSources() updates the group and crashes Civil 3D on a group opened for read; use the SectionSources(groupId) helper in civil3d_execute"),
  });

  /// <summary>False only when CIVIL3D_SCRIPT_SANDBOX=disabled; then the host-protection rules here and in
  /// ScriptSymbolCheck, and acad_command's blocked-command list, are skipped.</summary>
  public static bool HostProtectionEnabled =>
    !string.Equals(Environment.GetEnvironmentVariable("CIVIL3D_SCRIPT_SANDBOX"), "disabled", StringComparison.OrdinalIgnoreCase);

  /// <summary>Throws CIVIL3D.SANDBOX_VIOLATION when the code matches a blocked pattern (naming the script line, the
  /// match and why), CIVIL3D.INVALID_INPUT when the code is empty or longer than 200,000 characters.</summary>
  public static void Validate(string code, bool writeMode)
  {
    if (string.IsNullOrWhiteSpace(code))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Script code cannot be empty.");
    }

    if (code.Length > 200_000)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Script code exceeds the 200,000 character limit.");
    }

    var scanned = StripComments(code);
    Check(scanned, CrashRules, "is blocked because it can crash Civil 3D");

    if (HostProtectionEnabled)
    {
      Check(scanned, HostProtectionRules, "is blocked by the script sandbox");
    }

    if (!writeMode)
    {
      Check(scanned, ReadOnlyRules, "is not allowed in read-only mode; use civil3d_execute (approval-gated) instead");
    }
  }

  /// <summary>Throws CIVIL3D.SANDBOX_VIOLATION for the first rule that matches.</summary>
  private static void Check(string code, (Regex Pattern, string Reason)[] rules, string suffix)
  {
    foreach (var (pattern, reason) in rules)
    {
      var match = pattern.Match(code);
      if (match.Success)
      {
        var line = 1 + code.AsSpan(0, match.Index).Count('\n');
        throw new JsonRpcDispatchException(
          "CIVIL3D.SANDBOX_VIOLATION",
          $"Script line {line}: '{match.Value.Trim()}' ({reason}) {suffix}.");
      }
    }
  }

  /// <summary>The code without comments, so a blocked name in a comment isn't refused. A "//" right after ':', '"' or
  /// '\'' is kept (URLs, strings).</summary>
  private static string StripComments(string code)
  {
    // Remove block and line comments while keeping line numbers stable.
    var withoutBlocks = Regex.Replace(code, @"/\*.*?\*/", match => new string('\n', match.Value.Count(c => c == '\n')), RegexOptions.Singleline);
    return Regex.Replace(withoutBlocks, @"(?<![:""'])//[^\n]*", string.Empty);
  }

  private static (Regex, string)[] Compile((string Pattern, string Reason)[] rules)
  {
    return rules
      .Select(rule => (new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant), rule.Reason))
      .ToArray();
  }
}
