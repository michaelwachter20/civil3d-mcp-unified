namespace Civil3DMcpPlugin;

/// <summary>
/// The suite's shared folder (project-analyzer's docs/suite-knowledge.md, "Where"), found the same way as the MCP server and
/// the other suite tools find it: the saved scripts live under it, and imports from it are refused (FileBoundary).
/// </summary>
internal static class SharedFolder
{
  private const string SharedFolderName = "Project Knowledge";
  private static readonly string[] EarlierSharedFolderNames = { "Civil3DMcp-KnowledgeBase", "Civil3DMcp" };

  /// <summary>
  /// PROJECT_KNOWLEDGE_DIR, else CIVIL3D_KNOWLEDGE_DIR (its earlier name), else Documents\Project Knowledge if it exists,
  /// else an earlier default that exists (Documents\Civil3DMcp-KnowledgeBase, then Documents\Civil3DMcp) as it is, else
  /// Documents\Project Knowledge. No tool renames the folder: an older tool on the same PC would lose it.
  /// Documents is %USERPROFILE%\Documents, as the MCP server and the other suite tools take it, not the shell's
  /// Documents folder: when that is redirected (to OneDrive) they would otherwise use two different folders.
  /// </summary>
  public static string Root()
  {
    foreach (var variable in new[] { "PROJECT_KNOWLEDGE_DIR", "CIVIL3D_KNOWLEDGE_DIR" })
    {
      var configured = Environment.GetEnvironmentVariable(variable);
      if (!string.IsNullOrWhiteSpace(configured))
      {
        return configured.Trim();
      }
    }

    var profile = Environment.GetEnvironmentVariable("USERPROFILE");
    if (string.IsNullOrWhiteSpace(profile))
    {
      profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    var documents = System.IO.Path.Combine(profile.Trim(), "Documents");
    foreach (var name in EarlierSharedFolderNames.Prepend(SharedFolderName))
    {
      var candidate = System.IO.Path.Combine(documents, name);
      if (System.IO.Directory.Exists(candidate))
      {
        return candidate;
      }
    }

    return System.IO.Path.Combine(documents, SharedFolderName);
  }
}
