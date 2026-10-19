using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// A drawing other than the active one, opened for reading by a script, e.g. a "go-by"
/// (a finished drawing to learn from). Either another drawing open in the same Civil 3D
/// window or a .dwg file read in the background without opening it in the editor.
/// Its transaction is always aborted when the script ends, so nothing changes in it.
/// <code>
/// var goBy = Reference("Site-GoBy");
/// foreach (ObjectId id in goBy.CivilDoc.Styles.LabelStyles.PointLabelStyles) Print(goBy.Get&lt;LabelStyle&gt;(id).Name);
/// </code>
/// </summary>
public sealed class ReferenceDrawing
{
  private readonly Transaction _transaction;
  private readonly bool _ownsDatabase;

  internal ReferenceDrawing(string name, string path, bool isOpenInEditor, Database database, bool ownsDatabase)
  {
    Name = name;
    Path = path;
    IsOpenInEditor = isOpenInEditor;
    Database = database;
    _ownsDatabase = ownsDatabase;
    CivilDoc = CivilDocument.GetCivilDocument(database);
    _transaction = database.TransactionManager.StartTransaction();
  }

  /// <summary>Drawing file name, e.g. "Site-GoBy.dwg".</summary>
  public string Name { get; }

  /// <summary>Full path of the drawing file (empty for an unsaved drawing).</summary>
  public string Path { get; }

  /// <summary>True when the drawing is open in a Civil 3D tab; false when read from a file.</summary>
  public bool IsOpenInEditor { get; }

  /// <summary>The drawing's database (read only: changes are always rolled back).</summary>
  public Database Database { get; }

  /// <summary>The drawing's Civil 3D document: styles, point groups, surfaces, alignments, settings.</summary>
  public CivilDocument CivilDoc { get; }

  /// <summary>Opens an object of this drawing for read.</summary>
  public T Get<T>(ObjectId id) where T : DBObject
  {
    return _transaction.GetObject(id, OpenMode.ForRead) as T
      ?? throw new InvalidOperationException($"Object {id.Handle} in {Name} is not a {typeof(T).Name}.");
  }

  /// <summary>Model space of this drawing, for read.</summary>
  public BlockTableRecord ModelSpace => Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Database));

  /// <summary>Finds an object of this drawing by its hexadecimal handle.</summary>
  public ObjectId FromHandle(string handle)
  {
    return Database.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
  }

  public override string ToString() => $"{Name} ({(IsOpenInEditor ? "open drawing" : "file")}, read only)";

  internal void Close()
  {
    try
    {
      _transaction.Abort();
      _transaction.Dispose();
    }
    catch (System.Exception exception)
    {
      PluginLog.Warn("ReferenceDrawing", $"Closing {Name} failed: {exception.Message}");
    }

    if (_ownsDatabase)
    {
      Database.Dispose();
    }
  }

  /// <summary>Drawings open in this Civil 3D window: file name, path, and whether it is the active one.</summary>
  internal static IReadOnlyList<OpenDrawingInfo> ListOpen()
  {
    var manager = AcadApplication.DocumentManager;
    var active = manager.MdiActiveDocument;
    var list = new List<OpenDrawingInfo>();
    foreach (Document document in manager)
    {
      list.Add(new OpenDrawingInfo(System.IO.Path.GetFileName(document.Name), document.Name, document == active));
    }

    return list;
  }

  /// <summary>
  /// Opens a reference by open-drawing name ("Site-GoBy" or "Site-GoBy.dwg") or by .dwg path.
  /// Open drawings are matched first; a path that isn't open is read from disk.
  /// </summary>
  internal static ReferenceDrawing Open(string nameOrPath, Document activeDocument)
  {
    if (string.IsNullOrWhiteSpace(nameOrPath))
    {
      throw new ArgumentException("Reference(name) needs a drawing name or a .dwg path.");
    }

    var wanted = nameOrPath.Trim();
    var wantedFile = System.IO.Path.GetFileName(wanted);
    var wantedStem = System.IO.Path.GetFileNameWithoutExtension(wantedFile);
    var looksLikePath = wanted.Contains('\\') || wanted.Contains('/');
    var matches = new List<Document>();
    foreach (Document document in AcadApplication.DocumentManager)
    {
      var name = System.IO.Path.GetFileName(document.Name);
      var sameDrawing = looksLikePath
        ? string.Equals(System.IO.Path.GetFullPath(document.Name), SafeFullPath(wanted), StringComparison.OrdinalIgnoreCase)
        : string.Equals(name, wantedFile, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(System.IO.Path.GetFileNameWithoutExtension(name), wantedStem, StringComparison.OrdinalIgnoreCase);
      if (sameDrawing)
      {
        matches.Add(document);
      }
    }

    if (matches.Count > 1)
    {
      throw new ArgumentException($"'{wanted}' matches {matches.Count} open drawings; pass the full path instead.");
    }

    if (matches.Count == 1)
    {
      var document = matches[0];
      if (document == activeDocument)
      {
        throw new ArgumentException($"'{System.IO.Path.GetFileName(document.Name)}' is the active drawing; use Database, CivilDoc and Transaction for it.");
      }

      return new ReferenceDrawing(System.IO.Path.GetFileName(document.Name), document.Name, true, document.Database, ownsDatabase: false);
    }

    if (!looksLikePath)
    {
      var open = string.Join(", ", ListOpen().Where(info => !info.Active).Select(info => info.Name));
      throw new ArgumentException(
        $"No open drawing named '{wanted}'." + (open.Length > 0 ? $" Other open drawings: {open}." : " No other drawings are open.") +
        " To read a drawing that isn't open, pass its full .dwg path.");
    }

    var path = FileBoundary.ResolveImportPath(wanted, ".dwg");
    var database = new Database(false, true);
    try
    {
      database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
      database.CloseInput(true);
      return new ReferenceDrawing(System.IO.Path.GetFileName(path), path, false, database, ownsDatabase: true);
    }
    catch
    {
      database.Dispose();
      throw;
    }
  }

  private static string SafeFullPath(string path)
  {
    try
    {
      return System.IO.Path.GetFullPath(path);
    }
    catch (System.Exception)
    {
      return path;
    }
  }
}

/// <summary>A drawing open in this Civil 3D window.</summary>
public sealed record OpenDrawingInfo(string Name, string Path, bool Active);
