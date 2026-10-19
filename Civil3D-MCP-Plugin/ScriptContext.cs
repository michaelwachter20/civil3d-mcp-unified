using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Globals exposed to Roslyn scripts. Every public member is usable by name in
/// script code, e.g. <c>Transaction.GetObject(id, OpenMode.ForRead)</c> or
/// <c>Print("done")</c>.
/// </summary>
/// <remarks>Ported from barbosaihan/civil3d-mcp (MIT) and extended.</remarks>
public sealed class ScriptContext
{
  private const int MaxOutputLines = 500;
  private const int MaxOutputCharacters = 64 * 1024;

  private readonly List<string> _output = new();
  private int _outputCharacters;
  private bool _outputTruncated;
  private BlockTableRecord? _modelSpace;
  private readonly Dictionary<string, ReferenceDrawing> _references = new(StringComparer.OrdinalIgnoreCase);

  public ScriptContext(
    Document document,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction,
    JsonObject? args,
    bool isWriteMode,
    CancellationToken cancellationToken)
  {
    Document = document;
    CivilDoc = civilDoc;
    Database = database;
    Transaction = transaction;
    Editor = document.Editor;
    Args = new ScriptArgs(args);
    IsWriteMode = isWriteMode;
    CancellationToken = cancellationToken;
  }

  /// <summary>Active AutoCAD document.</summary>
  public Document Document { get; }

  /// <summary>Active Civil 3D document (surfaces, alignments, styles, ...).</summary>
  public CivilDocument CivilDoc { get; }

  /// <summary>Drawing database.</summary>
  public Database Database { get; }

  /// <summary>Open transaction. Committed after an execute/write run, aborted after a query.</summary>
  public Transaction Transaction { get; }

  /// <summary>Document editor (selection, prompts, messages).</summary>
  public Editor Editor { get; }

  /// <summary>Named arguments passed with the call or saved-script run.</summary>
  public ScriptArgs Args { get; }

  /// <summary>True when changes will be committed.</summary>
  public bool IsWriteMode { get; }

  /// <summary>Signalled when the caller cancels or the request times out. Check it in long loops.</summary>
  public CancellationToken CancellationToken { get; }

  /// <summary>Model space, opened for write in write mode and for read otherwise.</summary>
  public BlockTableRecord ModelSpace => _modelSpace ??= (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database),
    IsWriteMode ? OpenMode.ForWrite : OpenMode.ForRead);

  /// <summary>Opens an object through the script transaction.</summary>
  public T Get<T>(ObjectId id, OpenMode mode = OpenMode.ForRead) where T : DBObject
  {
    return Transaction.GetObject(id, mode) as T
      ?? throw new InvalidOperationException($"Object {id.Handle} is not a {typeof(T).Name}.");
  }

  /// <summary>Adds an entity to model space and the transaction; returns its ObjectId.</summary>
  public ObjectId AddToModelSpace(Entity entity)
  {
    if (!IsWriteMode)
    {
      throw new InvalidOperationException("AddToModelSpace requires write mode (civil3d_execute or a write-mode saved script).");
    }

    var id = ModelSpace.AppendEntity(entity);
    Transaction.AddNewlyCreatedDBObject(entity, true);
    return id;
  }

  /// <summary>
  /// Names the area this run is about, so after a committed write the view zooms to it (in or out)
  /// instead of to every object the change touched. Calls accumulate.
  /// </summary>
  public void FocusOn(Point3d min, Point3d max)
  {
    var extents = new Extents3d(
      new Point3d(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
      new Point3d(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
    if (Focus == null)
    {
      Focus = extents;
    }
    else
    {
      var combined = Focus.Value;
      combined.AddExtents(extents);
      Focus = combined;
    }
  }

  /// <summary>Focuses on a set of points (e.g. the points a change was built from).</summary>
  public void FocusOn(IEnumerable<Point3d> points)
  {
    var list = points.ToList();
    if (list.Count == 0) return;
    FocusOn(new Point3d(list.Min(p => p.X), list.Min(p => p.Y), list.Min(p => p.Z)),
            new Point3d(list.Max(p => p.X), list.Max(p => p.Y), list.Max(p => p.Z)));
  }

  /// <summary>Focuses on entities (their bounds).</summary>
  public void FocusOn(IEnumerable<ObjectId> ids)
  {
    foreach (var id in ids)
    {
      if (Transaction.GetObject(id, OpenMode.ForRead) is Entity entity && entity.Bounds is Extents3d bounds)
      {
        FocusOn(bounds.MinPoint, bounds.MaxPoint);
      }
    }
  }

  internal Extents3d? Focus { get; private set; }

  /// <summary>Drawings open in this Civil 3D window (Name, Path, Active).</summary>
  public IReadOnlyList<OpenDrawingInfo> OpenDrawings => ReferenceDrawing.ListOpen();

  /// <summary>
  /// Opens another drawing for reading, such as a go-by: an open drawing by name ("Site-GoBy"),
  /// or any .dwg by full path (read in the background, not opened in the editor). Changes to it
  /// are always rolled back. Repeated calls with the same name return the same reference.
  /// </summary>
  public ReferenceDrawing Reference(string nameOrPath)
  {
    var key = (nameOrPath ?? string.Empty).Trim();
    if (_references.TryGetValue(key, out var existing))
    {
      return existing;
    }

    var reference = ReferenceDrawing.Open(key, Document);
    _references[key] = reference;
    return reference;
  }

  internal void CloseReferences()
  {
    foreach (var reference in _references.Values)
    {
      reference.Close();
    }

    _references.Clear();
  }

  /// <summary>Finds an object by its hexadecimal handle string (e.g. "2A4F").</summary>
  public ObjectId FromHandle(string handle)
  {
    var value = Convert.ToInt64(handle, 16);
    return Database.GetObjectId(false, new Handle(value), 0);
  }

  /// <summary>Appends a line to the output returned with the result (also usable from MCPRUN).</summary>
  public void Print(object? value)
  {
    if (_outputTruncated)
    {
      return;
    }

    var text = value switch
    {
      null => "null",
      string s => s,
      IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? string.Empty,
    };

    if (_output.Count >= MaxOutputLines || _outputCharacters + text.Length > MaxOutputCharacters)
    {
      _output.Add("... output truncated ...");
      _outputTruncated = true;
      return;
    }

    _output.Add(text);
    _outputCharacters += text.Length;
  }

  internal IReadOnlyList<string> Output => _output;
}

/// <summary>
/// Read-only view of script arguments with typed accessors.
/// <code>var name = Args.GetString("surface", "EG"); var step = Args.GetDouble("interval", 25);</code>
/// </summary>
public sealed class ScriptArgs
{
  private readonly JsonObject _values;

  internal ScriptArgs(JsonObject? values)
  {
    _values = values ?? new JsonObject();
  }

  public bool Has(string name) => _values.ContainsKey(name) && _values[name] != null;

  public IEnumerable<string> Names => _values.Select(pair => pair.Key);

  public JsonNode? Raw(string name) => _values[name];

  public string GetString(string name, string? defaultValue = null)
  {
    var node = _values[name];
    if (node == null)
    {
      return defaultValue ?? throw new ArgumentException($"Missing required argument '{name}'.");
    }

    return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
  }

  public double GetDouble(string name, double? defaultValue = null)
  {
    var node = _values[name];
    if (node == null)
    {
      return defaultValue ?? throw new ArgumentException($"Missing required argument '{name}'.");
    }

    if (ScriptJson.TryGetDouble(node, out var number)) return number;
    if (node is JsonValue value && value.TryGetValue<string>(out var text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;

    throw new ArgumentException($"Argument '{name}' is not a number.");
  }

  public int GetInt(string name, int? defaultValue = null)
  {
    if (!Has(name))
    {
      return defaultValue ?? throw new ArgumentException($"Missing required argument '{name}'.");
    }

    return (int)Math.Round(GetDouble(name));
  }

  public bool GetBool(string name, bool? defaultValue = null)
  {
    var node = _values[name];
    if (node == null)
    {
      return defaultValue ?? throw new ArgumentException($"Missing required argument '{name}'.");
    }

    if (node is JsonValue value)
    {
      if (value.TryGetValue<bool>(out var flag)) return flag;
      if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag)) return flag;
      if (ScriptJson.TryGetDouble(value, out var number)) return number != 0;
    }

    throw new ArgumentException($"Argument '{name}' is not a boolean.");
  }

  /// <summary>Reads {x, y, z?} as a Point3d.</summary>
  public Point3d GetPoint(string name)
  {
    if (_values[name] is not JsonObject point)
    {
      throw new ArgumentException($"Argument '{name}' must be an object with x and y.");
    }

    return ScriptJson.ToPoint3d(point, name);
  }

  /// <summary>Reads an array of strings (or a single string) as a list.</summary>
  public List<string> GetStringList(string name)
  {
    return _values[name] switch
    {
      null => new List<string>(),
      JsonArray array => array.Select(item => item?.ToString() ?? string.Empty).ToList(),
      var single => new List<string> { single.ToString() },
    };
  }

  /// <summary>Deserializes an argument into any JSON-compatible type.</summary>
  public T? Get<T>(string name, T? defaultValue = default)
  {
    var node = _values[name];
    return node == null ? defaultValue : node.Deserialize<T>();
  }

  public override string ToString() => _values.ToJsonString();
}

/// <summary>
/// Numeric reads that work for both parsed JSON and values built in code.
/// <c>TryGetValue&lt;double&gt;</c> only converts parsed numbers; a value created with
/// <c>JsonValue.Create(21L)</c> reports false, so check the JSON kind instead.
/// </summary>
internal static class ScriptJson
{
  public static bool TryGetDouble(JsonNode? node, out double number)
  {
    number = 0;
    return node is JsonValue value &&
      value.GetValueKind() == JsonValueKind.Number &&
      double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
  }

  public static Point3d ToPoint3d(JsonObject point, string name)
  {
    return new Point3d(
      Coordinate(point, "x", name, required: true),
      Coordinate(point, "y", name, required: true),
      Coordinate(point, "z", name, required: false));
  }

  private static double Coordinate(JsonObject point, string axis, string name, bool required)
  {
    var node = point[axis];
    if (node == null)
    {
      return required ? throw new ArgumentException($"'{name}.{axis}' is required.") : 0;
    }

    return TryGetDouble(node, out var number) ? number : throw new ArgumentException($"'{name}.{axis}' must be a number.");
  }
}

/// <summary>Builds output text for MCPRUN without exposing internals.</summary>
internal static class ScriptOutputFormatter
{
  public static string Join(IEnumerable<string> lines)
  {
    var builder = new StringBuilder();
    foreach (var line in lines)
    {
      builder.Append('\n').Append(line);
    }

    return builder.ToString();
  }
}
