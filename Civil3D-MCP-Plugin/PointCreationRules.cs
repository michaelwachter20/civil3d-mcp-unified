using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// What counts as the AI making or deleting COGO points, for PointCreationGate: a script calling CogoPoints.Add or
/// CogoPoints.Remove, and Civil 3D's point-making commands. Kept apart from the gate (no Autodesk or window types) so
/// the file-boundary harness tests it without Civil 3D.
/// </summary>
public static class PointCreationRules
{
  // The point collection itself, or a variable holding it (`var pts = CivilDoc.CogoPoints;`,
  // `CogoPointCollection pts = ...`), so `pts.Add(...)` counts as well as `CivilDoc.CogoPoints.Add(...)`. An Add on
  // anything else (a list, an ObjectIdCollection) does not: before, any Add in a script that also used CogoPoints asked
  // "create points?", even for a script that only deleted points and listed point groups.
  private static readonly Regex Alias = new(@"\b(\w+)\s*=\s*[\w\.]*\bCogoPoints\s*;|\bCogoPointCollection\s+(\w+)\b", RegexOptions.Compiled);

  // The collection handed on to other code (a method argument, a tuple): what is done with it there can't be seen, so
  // it errs toward asking. A foreach over it (`in CivilDoc.CogoPoints)`) is only reading.
  private static readonly Regex HandedOn = new(@"(?<!\bin\s+[\w\.]*)\bCogoPoints\s*[,)]", RegexOptions.Compiled);

  // A CogoPoint object erased directly: deletes a point too.
  private static readonly Regex ErasesPoint = new(@"\bCogoPoint\b[\s\S]*\.\s*Erase\s*\(", RegexOptions.Compiled);

  // Civil 3D's point-making commands (not IMPORTPOINTS: that is its own importer, with its own dialog).
  private static readonly HashSet<string> CreateCommands = new(StringComparer.OrdinalIgnoreCase)
  {
    "CREATEPOINTS", "AECCCREATEPOINTS", "CREATEPOINTMANUAL", "AECCCREATEPOINTMANUAL", "CREATEPOINTSPOLYLINEVERTICES",
  };

  private static IEnumerable<string> CollectionNames(string code) =>
    new[] { "CogoPoints" }.Concat(Alias.Matches(code).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));

  private static bool CallsOnCollection(string code, string methods) =>
    CollectionNames(code).Any(name => Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\s*\.\s*(?:{methods})\s*\("));

  /// <summary>True when <paramref name="code"/> (a script) makes COGO points.</summary>
  public static bool ScriptAddsPoints(string code) => CallsOnCollection(code, @"Add\w*") || HandedOn.IsMatch(code);

  /// <summary>True when <paramref name="code"/> (a script) deletes COGO points.</summary>
  public static bool ScriptRemovesPoints(string code) => CallsOnCollection(code, @"Remove\w*|Clear") || ErasesPoint.IsMatch(code);

  /// <summary>True when <paramref name="commandName"/> (without its _ . - prefixes) is one of Civil 3D's
  /// point-making commands.</summary>
  public static bool IsCreateCommand(string commandName) => CreateCommands.Contains(commandName);
}
