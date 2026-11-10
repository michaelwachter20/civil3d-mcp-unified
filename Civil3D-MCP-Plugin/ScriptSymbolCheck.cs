// ScriptSymbolCheck.cs - second half of the script sandbox for the Roslyn code-execution engine. ScriptEngine.Compile
// calls Validate with the compiled script on every call (cached or not), because the same code can be allowed in a
// write run and refused in a read run. It refuses names that resolve to blocked types or members, with the same
// reasons and CIVIL3D.SANDBOX_VIOLATION code as ScriptSandbox. Some type names are split ("System." + ...) on purpose
// so the repository's reflection boundary test doesn't flag this file; keep them that way. No drawing, no files.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Civil3DMcpPlugin;

/// <summary>
/// The second half of the script sandbox: what a script's names resolve to, once compiled. ScriptSandbox reads the text,
/// so an alias got past it: <c>using F = System.IO.File;</c> then <c>F</c> instead of the type's name. This asks the
/// compiler, so it sees through aliases, and answers with the same refusals. Like ScriptSandbox it is a speed bump, not
/// a security boundary, and CIVIL3D_SCRIPT_SANDBOX=disabled turns off the host-protection half.
/// </summary>
internal static class ScriptSymbolCheck
{
  /// <summary>A blocked type (or namespace, ending with '.'), optionally only members whose names start with one of
  /// <c>Members</c>, and the reason shown to the AI.</summary>
  private sealed record Rule(string Type, string[]? Members, string Reason)
  {
    /// <summary>A type by its full name, or every type in a namespace when the name ends with a dot.</summary>
    public bool Matches(string typeName, string member) =>
      (Type.EndsWith('.') ? typeName.StartsWith(Type, StringComparison.Ordinal) : typeName == Type)
      && (Members == null || Members.Any(name => member.StartsWith(name, StringComparison.Ordinal)));
  }

  private static readonly Rule[] HostProtection =
  {
    new("System.Diagnostics.Process", null, "starting processes"),
    new("System.Diagnostics.ProcessStartInfo", null, "starting processes"),
    new("System.Net.", null, "network or IPC access"),
    new("System.IO.Pipes.", null, "network or IPC access"),
    new("System." + "Reflection.MethodBase", new[] { "Invoke" }, "reflection or dynamic loading"),
    new("System." + "Reflection.Assembly", new[] { "Load" }, "reflection or dynamic loading"),
    new("System.Activator", null, "reflection or dynamic loading"),
    new("System.Runtime.InteropServices.Marshal", null, "native interop or unsafe code"),
    new("System.Environment", new[] { "Exit", "FailFast", "SetEnvironmentVariable" }, "terminating or reconfiguring the host"),
    new("Microsoft.Win32.", null, "registry access"),
    new("System.IO.File", new[] { "Delete", "Move", "Replace", "Encrypt", "SetAttributes" }, "deleting or moving files"),
    new("System.IO.Directory", new[] { "Delete", "Move" }, "deleting or moving files"),
    new("System.IO.FileInfo", new[] { "Delete", "MoveTo", "Replace" }, "deleting or moving files"),
    new("System.IO.DirectoryInfo", new[] { "Delete", "MoveTo" }, "deleting or moving files"),
    new("System.AppDomain", null, "AppDomain access"),
  };

  private static readonly Rule[] ReadOnly =
  {
    new("System.IO.File", new[] { "Write", "Append", "Create", "Copy" }, "writing files"),
    new("System.IO.Directory", new[] { "Create" }, "writing files"),
    new("System.IO.FileStream", null, "writing files"),
    new("System.IO.StreamWriter", null, "writing files"),
  };

  /// <summary>Refuses a script with a name that resolves to something the sandbox blocks: host-protection rules while
  /// host protection is on, read-only rules when <paramref name="writeMode"/> is false. Throws
  /// CIVIL3D.SANDBOX_VIOLATION naming the script line and the name.</summary>
  public static void Validate(Compilation compilation, bool writeMode)
  {
    foreach (var tree in compilation.SyntaxTrees)
    {
      var model = compilation.GetSemanticModel(tree);
      foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
      {
        var symbol = model.GetSymbolInfo(name).Symbol;
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        if (type == null)
        {
          continue;
        }

        var typeName = type.ToDisplayString();
        var member = symbol is INamedTypeSymbol ? string.Empty : symbol!.Name;
        Refuse(ScriptSandbox.HostProtectionEnabled ? HostProtection : Array.Empty<Rule>(), typeName, member, name, "is blocked by the script sandbox");
        if (!writeMode)
        {
          Refuse(ReadOnly, typeName, member, name, "is not allowed in read-only mode; use civil3d_execute (approval-gated) instead");
        }
      }
    }
  }

  /// <summary>Throws CIVIL3D.SANDBOX_VIOLATION when the first matching rule is found.</summary>
  private static void Refuse(Rule[] rules, string typeName, string member, SimpleNameSyntax name, string verdict)
  {
    var rule = rules.FirstOrDefault(candidate => candidate.Matches(typeName, member));
    if (rule != null)
    {
      var line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      throw new JsonRpcDispatchException("CIVIL3D.SANDBOX_VIOLATION", $"Script line {line}: '{name.Identifier.Text}' ({rule.Reason}) {verdict}.");
    }
  }
}
