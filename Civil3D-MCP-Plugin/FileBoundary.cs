// The only place the plugin resolves caller-supplied paths and writes files (tests/security_boundary.test.ts allows
// direct file writes only here and in PluginLog.cs). Serves no JSON-RPC method; every handler that imports, exports,
// opens a drawing or saves a script calls it.
// Rules it enforces: absolute paths only, canonicalized, inside CIVIL3D_IMPORT_ROOTS / CIVIL3D_EXPORT_ROOTS (else
// CIVIL3D_FILE_ROOTS, else the user's Documents folder); per-command extension allowlist; no links or junctions on the
// way; never importing from the suite's shared knowledge folder (SharedFolder.Root, job databases are reference only),
// except a job's User Knowledge folder for reference reads; no overwrite unless overwrite=true; writes go to a hidden
// temp file and are moved into place atomically, with the export directory chain held open against swaps.
// It never touches the drawing.
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Authoritative filesystem boundary for caller-supplied import and export paths.
/// Paths are canonicalized, restricted to configured roots, and checked against
/// a command-specific extension allowlist before Civil 3D or System.IO sees them.
/// </summary>
internal static class FileBoundary
{
  private const string SharedRootsVariable = "CIVIL3D_FILE_ROOTS";
  private const string ImportRootsVariable = "CIVIL3D_IMPORT_ROOTS";
  private const string ExportRootsVariable = "CIVIL3D_EXPORT_ROOTS";
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const int FileAttributeTagInfoClass = 9;

  private static readonly Lazy<string[]> ImportRoots = new(() => LoadRoots(ImportRootsVariable));
  private static readonly Lazy<string[]> ExportRoots = new(() => LoadRoots(ExportRootsVariable));

  /// <summary>A file whose data goes into the drawing (points, surfaces, a drawing opened to edit): never one from the
  /// suite's shared folder, whose job databases are a snapshot for reference only.</summary>
  public static string ResolveImportPath(string rawPath, params string[] allowedExtensions) =>
    ResolveRead(rawPath, userKnowledgeAllowed: false, allowedExtensions);

  /// <summary>
  /// A file only read for reference (a go-by drawing, a template, a title block, a drawing opened read-only): it may be one
  /// of the user's own standards in a job's User Knowledge folder (&lt;shared&gt;\projects\&lt;job&gt;\User Knowledge\), but
  /// nothing else in the shared folder.
  /// </summary>
  public static string ResolveReferencePath(string rawPath, params string[] allowedExtensions) =>
    ResolveRead(rawPath, userKnowledgeAllowed: true, allowedExtensions);

  /// <summary>
  /// Resolves a path to read against the import roots. Throws CIVIL3D.PATH_NOT_ALLOWED inside the shared knowledge
  /// folder (unless a User Knowledge file is allowed) and CIVIL3D.OBJECT_NOT_FOUND when the file doesn't exist, plus
  /// every <see cref="ResolvePath"/> error.
  /// </summary>
  private static string ResolveRead(string rawPath, bool userKnowledgeAllowed, string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ImportRoots.Value, "import", allowedExtensions);
    if (IsInSharedKnowledgeFolder(path) && !(userKnowledgeAllowed && IsInUserKnowledge(path)))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"'{path}' is in the suite's shared knowledge folder (job databases, backups, User Knowledge), which is for reference only " +
        "and never a source for a drawing's data. Ask the user for the current source file in the job folder. (The user's own " +
        "standards in a job's User Knowledge folder can be read as a go-by, template or title block, or opened read-only.)");
    }

    if (!File.Exists(path))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Import file was not found: {path}");
    }

    return path;
  }

  /// <summary>
  /// The canonical path for a file to write, inside the export roots with an allowed extension. Writes nothing.
  /// Throws CIVIL3D.CONFLICT when the file exists and <paramref name="overwrite"/> is false, plus every
  /// <see cref="ResolvePath"/> error. Unlike imports, the shared knowledge folder is not excluded here.
  /// </summary>
  public static string ResolveExportPath(
    string rawPath,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ExportRoots.Value, "export", allowedExtensions);
    if (File.Exists(path) && !overwrite)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }

    return path;
  }

  /// <summary>
  /// Writes <paramref name="content"/> to a caller-supplied export path: resolved as in
  /// <see cref="ResolveExportPath"/>, missing directories created under the root while the directory chain is locked,
  /// written to a hidden temp file and moved into place. Returns the canonical path. Throws CIVIL3D.CONFLICT (exists,
  /// no overwrite), CIVIL3D.FILE_IO_ERROR (write failed; the temp file is removed), CIVIL3D.INVALID_CONFIGURATION
  /// (export root missing) and the path errors.
  /// </summary>
  public static string WriteAllTextAtomic(
    string rawPath,
    string content,
    Encoding encoding,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolveExportPath(rawPath, overwrite, allowedExtensions);
    var directory = Path.GetDirectoryName(path)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Output path must include a directory.");
    using var directoryLock = LockExportDirectoryChain(directory);

    var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
      File.WriteAllText(tempPath, content, encoding);
      File.Move(tempPath, path, overwrite);
      return path;
    }
    catch (IOException) when (!overwrite && File.Exists(path))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to write output file '{path}': {exception.Message}");
    }
    finally
    {
      try
      {
        if (File.Exists(tempPath))
        {
          File.Delete(tempPath);
        }
      }
      catch
      {
        // Preserve the original operation result. Stale temp files use a
        // hidden, collision-resistant name and can be removed later.
      }
    }
  }

  /// <summary>
  /// Writes a file inside a plugin-owned directory (for example the saved-script
  /// library). The file name must be a bare name: separators, traversal, and
  /// reparse-point directories are rejected, and the write is atomic.
  /// </summary>
  public static string WriteFileWithinDirectoryAtomic(
    string directory,
    string fileName,
    string content,
    bool overwrite)
  {
    var path = ResolveFileWithinDirectory(directory, fileName, createDirectory: true);
    if (File.Exists(path) && !overwrite)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"File already exists: {path}. Set overwrite=true to replace it explicitly.");
    }

    var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
      File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
      File.Move(tempPath, path, overwrite);
      return path;
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Unable to write '{path}': {exception.Message}");
    }
    finally
    {
      try
      {
        if (File.Exists(tempPath))
        {
          File.Delete(tempPath);
        }
      }
      catch
      {
        // Best effort cleanup of a hidden temp file.
      }
    }
  }

  /// <summary>Deletes a bare-named file inside a plugin-owned directory.</summary>
  public static bool DeleteFileWithinDirectory(string directory, string fileName)
  {
    var path = ResolveFileWithinDirectory(directory, fileName, createDirectory: false);
    if (!File.Exists(path))
    {
      return false;
    }

    try
    {
      File.Delete(path);
      return true;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.FILE_IO_ERROR", $"Unable to delete '{path}': {exception.Message}");
    }
  }

  /// <summary>
  /// Resolves a bare file name inside <paramref name="directory"/>, rejecting traversal (CIVIL3D.INVALID_INPUT for a
  /// bad name, CIVIL3D.PATH_NOT_ALLOWED for a reparse-point directory or a path that escapes). Creates the directory
  /// when <paramref name="createDirectory"/> is true.
  /// </summary>
  public static string ResolveFileWithinDirectory(string directory, string fileName, bool createDirectory)
  {
    if (string.IsNullOrWhiteSpace(fileName) ||
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        fileName.Contains("..", StringComparison.Ordinal) ||
        !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid file name: '{fileName}'.");
    }

    var root = Path.GetFullPath(directory);
    if (createDirectory)
    {
      Directory.CreateDirectory(root);
    }

    if (Directory.Exists(root) && new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"Directory '{root}' is a reparse point and cannot be used.");
    }

    var path = Path.GetFullPath(Path.Combine(root, fileName));
    var relative = Path.GetRelativePath(root, path);
    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"'{fileName}' resolves outside '{root}'.");
    }

    return path;
  }

  /// <summary>
  /// Canonicalizes an absolute path and checks it against <paramref name="roots"/>, links on the way and the
  /// extension allowlist (empty = any). Throws CIVIL3D.INVALID_INPUT (empty, relative or invalid path),
  /// CIVIL3D.PATH_NOT_ALLOWED (outside the roots, or through a link/junction), CIVIL3D.FILE_TYPE_NOT_ALLOWED.
  /// </summary>
  private static string ResolvePath(
    string rawPath,
    IReadOnlyCollection<string> roots,
    string operation,
    IReadOnlyCollection<string> allowedExtensions)
  {
    if (string.IsNullOrWhiteSpace(rawPath))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"A non-empty {operation} path is required.");
    }

    if (!Path.IsPathFullyQualified(rawPath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"The {operation} path must be absolute: {rawPath}");
    }

    string canonicalPath;
    try
    {
      canonicalPath = Path.GetFullPath(rawPath);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid {operation} path: {exception.Message}");
    }

    var matchedRoot = roots.FirstOrDefault(root => IsWithinRoot(canonicalPath, root));
    if (matchedRoot == null)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"The {operation} path is outside the configured roots: {canonicalPath}");
    }

    RejectReparsePointTraversal(canonicalPath, matchedRoot);

    var allowed = allowedExtensions
      .Select(NormalizeExtension)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var extension = Path.GetExtension(canonicalPath);
    if (allowed.Count > 0 && !allowed.Contains(extension))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_TYPE_NOT_ALLOWED",
        $"Extension '{extension}' is not allowed for this operation. Allowed extensions: {string.Join(", ", allowed.Order())}.");
    }

    return canonicalPath;
  }

  /// <summary>
  /// The roots for an operation: its own variable, else CIVIL3D_FILE_ROOTS (both PATH-separator lists of absolute
  /// folders), else the user's Documents folder. Read once per process (Lazy).
  /// </summary>
  private static string[] LoadRoots(string operationVariable)
  {
    var configured = Environment.GetEnvironmentVariable(operationVariable);
    if (string.IsNullOrWhiteSpace(configured))
    {
      configured = Environment.GetEnvironmentVariable(SharedRootsVariable);
    }

    var roots = SplitRoots(configured).ToList();
    if (roots.Count == 0)
    {
      var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
      if (!string.IsNullOrWhiteSpace(documents))
      {
        roots.Add(documents);
      }
    }

    return roots
      .Select(Path.GetFullPath)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  /// <summary>The configured root list split on ';'. CIVIL3D.INVALID_CONFIGURATION for a root that isn't absolute.</summary>
  private static IEnumerable<string> SplitRoots(string? configured)
  {
    if (string.IsNullOrWhiteSpace(configured))
    {
      yield break;
    }

    foreach (var root in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (!Path.IsPathFullyQualified(root))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_CONFIGURATION",
          $"Configured filesystem root must be absolute: {root}");
      }

      yield return root;
    }
  }

  /// <summary>
  /// True when the path is inside the suite's shared folder (SharedFolder.Root): the job databases are a snapshot for
  /// reference, so nothing is imported or opened from there (contract: "Reference only").
  /// </summary>
  private static bool IsInSharedKnowledgeFolder(string path)
  {
    try
    {
      return IsWithinRoot(path, Path.GetFullPath(SharedFolder.Root()));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
    {
      return false;
    }
  }

  /// <summary>True when the path is inside a job's User Knowledge folder: &lt;shared&gt;\projects\&lt;job&gt;\User Knowledge\...</summary>
  private static bool IsInUserKnowledge(string path)
  {
    try
    {
      var parts = Path.GetRelativePath(Path.GetFullPath(SharedFolder.Root()), path)
        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
      return parts.Length > 3
        && string.Equals(parts[0], "projects", StringComparison.OrdinalIgnoreCase)
        && string.Equals(parts[2], "User Knowledge", StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
    {
      return false;
    }
  }

  /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it (both canonical).</summary>
  private static bool IsWithinRoot(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative)
      && !string.Equals(relative, "..", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  /// <summary>
  /// Walks each existing folder or file from <paramref name="root"/> down to <paramref name="path"/> and refuses any
  /// symbolic link or junction (CIVIL3D.PATH_NOT_ALLOWED); CIVIL3D.FILE_IO_ERROR when attributes can't be read.
  /// </summary>
  private static void RejectReparsePointTraversal(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    if (relative == ".")
    {
      return;
    }

    var current = root;
    foreach (var segment in relative.Split(
      [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
      StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, segment);
      if (!Directory.Exists(current) && !File.Exists(current))
      {
        break;
      }

      try
      {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.PATH_NOT_ALLOWED",
            $"Filesystem links and junctions are not allowed in caller-supplied paths: {current}");
        }
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception exception)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.FILE_IO_ERROR",
          $"Unable to validate filesystem path '{current}': {exception.Message}");
      }
    }
  }

  private static string NormalizeExtension(string extension) =>
    extension.StartsWith('.') ? extension : $".{extension}";

  /// <summary>
  /// Opens a handle on every folder from the longest matching export root down to <paramref name="directory"/>
  /// (creating missing ones), each refusing links and denying delete sharing, so no folder can be swapped for a
  /// junction until the returned lock is disposed. CIVIL3D.PATH_NOT_ALLOWED / INVALID_CONFIGURATION / FILE_IO_ERROR.
  /// </summary>
  private static DirectoryChainLock LockExportDirectoryChain(string directory)
  {
    var canonicalDirectory = Path.GetFullPath(directory);
    var matchedRoot = ExportRoots.Value
      .Where(root => IsWithinRoot(canonicalDirectory, root))
      .OrderByDescending(root => root.Length)
      .FirstOrDefault()
      ?? throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"The export path is outside the configured roots: {canonicalDirectory}");

    if (!Directory.Exists(matchedRoot))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_CONFIGURATION",
        $"Configured export root does not exist: {matchedRoot}");
    }

    var handles = new List<SafeFileHandle>();
    try
    {
      var current = Path.GetFullPath(matchedRoot);
      handles.Add(OpenLockedDirectory(current));
      var relative = Path.GetRelativePath(current, canonicalDirectory);
      if (relative != ".")
      {
        foreach (var segment in relative.Split(
          [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
          StringSplitOptions.RemoveEmptyEntries))
        {
          var next = Path.Combine(current, segment);
          if (!Directory.Exists(next))
          {
            // The parent handle excludes FILE_SHARE_DELETE, so it cannot be
            // swapped for a junction between creation and the next handle open.
            Directory.CreateDirectory(next);
          }
          handles.Add(OpenLockedDirectory(next));
          current = next;
        }
      }
      return new DirectoryChainLock(handles);
    }
    catch
    {
      foreach (var handle in handles) handle.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Opens a folder handle without following reparse points (Win32 CreateFileW with backup semantics, read/write
  /// sharing only) and refuses it if it is a link. CIVIL3D.FILE_IO_ERROR when it can't be opened or inspected.
  /// </summary>
  private static SafeFileHandle OpenLockedDirectory(string path)
  {
    var handle = CreateFileW(
      path,
      0,
      FileShare.ReadWrite,
      IntPtr.Zero,
      FileMode.Open,
      FileFlagBackupSemantics | FileFlagOpenReparsePoint,
      IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to lock filesystem directory '{path}': {error.Message}");
    }

    if (!GetFileInformationByHandleEx(
      handle,
      FileAttributeTagInfoClass,
      out var information,
      (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to inspect filesystem directory '{path}': {error.Message}");
    }

    if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
    {
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"Filesystem links and junctions are not allowed in caller-supplied paths: {path}");
    }
    return handle;
  }

  /// <summary>The folder handles held by <see cref="LockExportDirectoryChain"/>; disposing closes them innermost first.</summary>
  private sealed class DirectoryChainLock(List<SafeFileHandle> handles) : IDisposable
  {
    public void Dispose()
    {
      for (var index = handles.Count - 1; index >= 0; index--)
        handles[index].Dispose();
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileAttributeTagInfo
  {
    public FileAttributes FileAttributes;
    public uint ReparseTag;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(
    string fileName,
    uint desiredAccess,
    FileShare shareMode,
    IntPtr securityAttributes,
    FileMode creationDisposition,
    uint flagsAndAttributes,
    IntPtr templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandleEx(
    SafeFileHandle file,
    int fileInformationClass,
    out FileAttributeTagInfo fileInformation,
    uint bufferSize);
}
