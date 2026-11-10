// Harness checks for the plugin safety fixes in FileBoundary (no exports into the shared knowledge folder, the held
// folder lock, the plugin's own temporary files) and JsonRpcProtocol (point numbers read strictly, error numbers).
// Called once from Program.cs with the harness's temporary folders; runs the real plugin sources (see the .csproj),
// needs no Civil 3D, and throws InvalidOperationException on the first failure so the run exits non-zero.

using System.Text;
using System.Text.Json.Nodes;
using Civil3DMcpPlugin;

/// <summary>The checks, in one method so Program.cs needs a single line to run them.</summary>
internal static class SafetyFixChecks
{
  /// <summary>Runs every check. <paramref name="testRoot"/> is the harness's temporary folder (deleted by Program.cs),
  /// <paramref name="allowedRoot"/> the import/export root inside it.</summary>
  public static void Run(string testRoot, string allowedRoot)
  {
    PointNumbersAreReadStrictly();
    ErrorNumbersAreMapped();
    ExportsStayOutOfTheSharedFolder(allowedRoot);
    HeldFolderCanNotBeSwapped(testRoot);
    TemporaryFilesStayInTheirFolder(testRoot);
  }

  /// <summary>Point numbers: whole numbers of 1 or more, anything else refused (never truncated or dropped).</summary>
  private static void PointNumbersAreReadStrictly()
  {
    var read = JsonRpcProtocol.PositiveIntegers(JsonNode.Parse("[101, 102, 4294967295]")!.AsArray(), "pointNumbers");
    Assert(read.SequenceEqual(new uint[] { 101, 102, 4294967295 }), "Whole point numbers weren't read as they are.");
    Assert(JsonRpcProtocol.PositiveIntegers(null, "pointNumbers").Count == 0, "No array should read as no numbers.");
    Assert(JsonRpcProtocol.PositiveIntegers(new JsonArray(7, 8), "pointNumbers").SequenceEqual(new uint[] { 7, 8 }),
      "Numbers built in code (as the workflows pass them) weren't read.");
    foreach (var bad in new[] { "[12.5]", "[0]", "[-3]", "[\"5\"]", "[null]", "[4294967296]", "[1, 2.25]", "[true]" })
    {
      ExpectCode("CIVIL3D.INVALID_INPUT", () => JsonRpcProtocol.PositiveIntegers(JsonNode.Parse(bad)!.AsArray(), "pointNumbers"),
        $"Point numbers {bad} were accepted.");
    }
  }

  /// <summary>Domain codes the plugin throws get their own JSON-RPC numbers instead of the generic -32000.</summary>
  private static void ErrorNumbersAreMapped()
  {
    Assert(JsonRpcProtocol.NumericErrorCode("CIVIL3D.INVALID_CONFIGURATION") == -32046, "INVALID_CONFIGURATION has no number of its own.");
    Assert(JsonRpcProtocol.NumericErrorCode("CIVIL3D.INTERNAL_ERROR") == -32603, "INTERNAL_ERROR isn't JSON-RPC's -32603.");
    Assert(JsonRpcProtocol.NumericErrorCode("CIVIL3D.SOMETHING_NEW") == -32000, "An unknown code should stay -32000.");
  }

  /// <summary>
  /// The default export root (Documents) contains the shared knowledge folder: an export there is refused, while the
  /// plugin's own saved scripts are still written there (WriteFileWithinDirectoryAtomic, as ScriptLibrary does).
  /// </summary>
  private static void ExportsStayOutOfTheSharedFolder(string allowedRoot)
  {
    var sharedRoot = Path.Combine(allowedRoot, "Shared Knowledge");
    Directory.CreateDirectory(Path.Combine(sharedRoot, "projects"));
    var previous = Environment.GetEnvironmentVariable("PROJECT_KNOWLEDGE_DIR");
    Environment.SetEnvironmentVariable("PROJECT_KNOWLEDGE_DIR", sharedRoot);
    try
    {
      var intoShared = Path.Combine(sharedRoot, "projects", "report.csv");
      ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.ResolveExportPath(intoShared, true, ".csv"),
        "An export into the shared folder was resolved.");
      ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.WriteAllTextAtomic(intoShared, "x", Encoding.UTF8, true, ".csv"),
        "An export into the shared folder was written.");
      ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.ResolveExportPath(Path.Combine(sharedRoot, "plan.pdf"), true, ".pdf"),
        "An export at the top of the shared folder was resolved.");
      Assert(!File.Exists(intoShared), "A refused export left a file in the shared folder.");

      var beside = Path.Combine(allowedRoot, "Shared Knowledge reports", "report.csv");
      Assert(FileBoundary.WriteAllTextAtomic(beside, "ok", Encoding.UTF8, false, ".csv") == Path.GetFullPath(beside),
        "An export in a folder next to the shared folder (a similar name) was refused.");

      var scripts = Path.Combine(sharedRoot, "civil3d", "scripts");
      var script = FileBoundary.WriteFileWithinDirectoryAtomic(scripts, "relabel.csx", "// script", overwrite: false);
      Assert(File.ReadAllText(script) == "// script", "Saved scripts in the shared folder can no longer be written.");
    }
    finally
    {
      Environment.SetEnvironmentVariable("PROJECT_KNOWLEDGE_DIR", previous);
    }
  }

  /// <summary>
  /// A held folder (FileBoundary.LockDirectory, used while writing) can't be renamed, nor can the folder above it, so
  /// it can't be swapped for a junction mid-write; files inside can still be made and replaced; the lock is released.
  /// </summary>
  private static void HeldFolderCanNotBeSwapped(string testRoot)
  {
    var parent = Path.Combine(testRoot, "lock-parent");
    var folder = Path.Combine(parent, "held");
    Directory.CreateDirectory(folder);
    using (FileBoundary.LockDirectory(folder))
    {
      ExpectIo(() => Directory.Move(folder, Path.Combine(parent, "moved")), "A held folder was renamed.");
      ExpectIo(() => Directory.Move(parent, Path.Combine(testRoot, "lock-parent-moved")), "The folder above a held one was renamed.");
      File.WriteAllText(Path.Combine(folder, ".a.tmp"), "1");
      File.Move(Path.Combine(folder, ".a.tmp"), Path.Combine(folder, "a.txt"), overwrite: true);
      File.WriteAllText(Path.Combine(folder, ".b.tmp"), "2");
      File.Move(Path.Combine(folder, ".b.tmp"), Path.Combine(folder, "a.txt"), overwrite: true);
      Assert(File.ReadAllText(Path.Combine(folder, "a.txt")) == "2", "A file in a held folder couldn't be replaced.");
    }

    Directory.Move(folder, Path.Combine(parent, "moved"));
    Assert(Directory.Exists(Path.Combine(parent, "moved")), "The folder lock wasn't released.");

    // The saved-script write holds its folder too and still replaces a file there.
    var scripts = Path.Combine(testRoot, "locked-scripts");
    FileBoundary.WriteFileWithinDirectoryAtomic(scripts, "a.csx", "v1", overwrite: false);
    FileBoundary.WriteFileWithinDirectoryAtomic(scripts, "a.csx", "v2", overwrite: true);
    Assert(File.ReadAllText(Path.Combine(scripts, "a.csx")) == "v2", "A held-folder script write didn't replace the file.");
    Directory.Move(scripts, scripts + "-moved");
  }

  /// <summary>Working copies (a point file in the Windows code page) are written and removed only in the plugin's own
  /// temporary folder.</summary>
  private static void TemporaryFilesStayInTheirFolder(string testRoot)
  {
    var bytes = new byte[] { 0x31, 0x2C, 0xB1, 0x0D, 0x0A };
    var copy = FileBoundary.WriteTemporaryFile(bytes, ".txt");
    Assert(string.Equals(Path.GetDirectoryName(copy), Path.GetFullPath(FileBoundary.TemporaryDirectory), StringComparison.OrdinalIgnoreCase)
      && copy.EndsWith(".txt", StringComparison.Ordinal), $"The temporary copy went elsewhere: {copy}");
    Assert(File.ReadAllBytes(copy).SequenceEqual(bytes), "The temporary copy's bytes changed.");
    Assert(FileBoundary.DeleteTemporaryFile(copy) && !File.Exists(copy), "The temporary copy wasn't removed.");
    Assert(!FileBoundary.DeleteTemporaryFile(copy), "Removing a temporary copy twice reported success.");
    var plain = FileBoundary.WriteTemporaryFile([], "");
    Assert(File.Exists(plain) && FileBoundary.DeleteTemporaryFile(plain), "A temporary file without an extension wasn't written and removed.");

    foreach (var badExtension in new[] { "txt", ".t/x", ".a..b", "..\\x", ".verylongextension" })
    {
      ExpectCode("CIVIL3D.INVALID_INPUT", () => FileBoundary.WriteTemporaryFile(bytes, badExtension), $"Extension '{badExtension}' was accepted.");
    }

    var elsewhere = Path.Combine(testRoot, "not-temporary.txt");
    File.WriteAllText(elsewhere, "keep");
    ExpectCode("CIVIL3D.INVALID_INPUT", () => FileBoundary.DeleteTemporaryFile(elsewhere), "A file outside the temporary folder was accepted for removal.");
    ExpectCode("CIVIL3D.INVALID_INPUT", () => FileBoundary.DeleteTemporaryFile(Path.Combine(FileBoundary.TemporaryDirectory, "..", "x.txt")),
      "A path that climbs out of the temporary folder was accepted for removal.");
    Assert(File.Exists(elsewhere), "A file outside the temporary folder was removed.");
  }

  private static void ExpectCode(string expectedCode, Action action, string message)
  {
    try
    {
      action();
    }
    catch (JsonRpcDispatchException exception) when (exception.Code == expectedCode)
    {
      return;
    }

    throw new InvalidOperationException(message);
  }

  private static void ExpectIo(Action action, string message)
  {
    try
    {
      action();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      return;
    }

    throw new InvalidOperationException(message);
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition)
    {
      throw new InvalidOperationException(message);
    }
  }
}
