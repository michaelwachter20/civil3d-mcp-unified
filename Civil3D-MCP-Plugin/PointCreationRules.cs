using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// What counts as the AI making COGO points, for PointCreationGate: a script calling CogoPoints.Add, and Civil 3D's
/// point-making commands. Kept apart from the gate (no Autodesk or window types) so the file-boundary harness tests
/// it without Civil 3D.
/// </summary>
public static class PointCreationRules
{
  // A script that makes COGO points: one that uses the point collection (CogoPoints, CogoPointCollection) and calls an
  // Add anywhere, so `var pts = CivilDoc.CogoPoints; pts.Add(...)` counts too. It errs toward asking: a write script
  // that reads points and adds to a list asks as well.
  private static readonly Regex UsesPoints = new(@"\bCogoPoints\b|\bCogoPointCollection\b", RegexOptions.Compiled);
  private static readonly Regex CallsAdd = new(@"\.\s*Add\s*\(", RegexOptions.Compiled);

  // Civil 3D's point-making commands (not IMPORTPOINTS: that is its own importer, with its own dialog).
  private static readonly HashSet<string> CreateCommands = new(StringComparer.OrdinalIgnoreCase)
  {
    "CREATEPOINTS", "AECCCREATEPOINTS", "CREATEPOINTMANUAL", "AECCCREATEPOINTMANUAL", "CREATEPOINTSPOLYLINEVERTICES",
  };

  /// <summary>True when <paramref name="code"/> (a script) makes COGO points.</summary>
  public static bool ScriptAddsPoints(string code) => UsesPoints.IsMatch(code) && CallsAdd.IsMatch(code);

  /// <summary>True when <paramref name="commandName"/> (without its _ . - prefixes) is one of Civil 3D's
  /// point-making commands.</summary>
  public static bool IsCreateCommand(string commandName) => CreateCommands.Contains(commandName);
}
