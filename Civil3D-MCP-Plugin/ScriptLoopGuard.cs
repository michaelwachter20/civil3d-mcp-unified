// ScriptLoopGuard.cs - part of the Roslyn code-execution engine. ScriptEngine.CompileCore calls AddChecks on a script's
// source before compiling it, so every loop checks ScriptContext.CancellationToken and the script timeout
// (CIVIL3D_SCRIPT_TIMEOUT_SECONDS) or a client disconnect can stop it. Pure text work on the source: no drawing, no
// files. The original code (not the guarded copy) is what the sandbox checks and what the hash and cache use; if the
// guarded copy doesn't compile, ScriptEngine compiles the original instead.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Civil3DMcpPlugin;

/// <summary>
/// Lets a script's timeout stop a tight loop. A script runs on Civil 3D's main thread, so the timeout can only cancel
/// a token; a <c>while (true) { }</c> that never looks at it would freeze Civil 3D until it was killed. Before
/// compiling, every loop gets a check of the token at the top of its body.
/// <para>
/// Text is only inserted, never moved, and no line is added, so an error still names the script's own line. The
/// sandbox has already validated the code as written. Not covered: a loop inside a class the script declares or a
/// static lambda (neither can see the script's globals), recursion, goto, one long call into Civil 3D, and a loop
/// that sits inside a <c>try</c> whose catch swallows the cancellation.
/// </para>
/// </summary>
public static class ScriptLoopGuard
{
  private const string Check = "CancellationToken.ThrowIfCancellationRequested();";

  /// <summary>The code with a cancellation check in every loop it can reach, or as it was if it doesn't parse.</summary>
  public static string AddChecks(string code)
  {
    var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Script));
    // Code with errors goes to the compiler as written, so its messages match what the author wrote.
    if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
    {
      return code;
    }

    var inserts = new List<(int Position, string Text)>();
    foreach (var body in tree.GetRoot().DescendantNodes().Select(LoopBody).OfType<StatementSyntax>())
    {
      if (!SeesGlobals(body))
      {
        continue;
      }

      if (body is BlockSyntax block)
      {
        inserts.Add((block.OpenBraceToken.Span.End, " " + Check));
      }
      else
      {
        // while (x) y(); -> while (x) { check y(); }
        inserts.Add((body.SpanStart, "{ " + Check + " "));
        inserts.Add((body.Span.End, " }"));
      }
    }

    // From the end, so earlier positions stay right.
    var text = code;
    foreach (var (position, inserted) in inserts.OrderByDescending(insert => insert.Position))
    {
      text = text.Insert(position, inserted);
    }

    return text;
  }

  /// <summary>The body of a while, do, for or foreach loop; null for any other node.</summary>
  private static StatementSyntax? LoopBody(SyntaxNode node) => node switch
  {
    WhileStatementSyntax loop => loop.Statement,
    DoStatementSyntax loop => loop.Statement,
    ForStatementSyntax loop => loop.Statement,
    CommonForEachStatementSyntax loop => loop.Statement,
    _ => null,
  };

  /// <summary>False inside a declared type or a static lambda or local function: the check wouldn't compile there.</summary>
  private static bool SeesGlobals(SyntaxNode node)
  {
    return !node.Ancestors().Any(ancestor => ancestor switch
    {
      BaseTypeDeclarationSyntax => true,
      LocalFunctionStatementSyntax function => function.Modifiers.Any(SyntaxKind.StaticKeyword),
      AnonymousFunctionExpressionSyntax function => function.Modifiers.Any(SyntaxKind.StaticKeyword),
      _ => false,
    });
  }
}
