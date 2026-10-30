using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

// The library of saved .csx scripts behind civil3d_script (src/tools/domains/scriptingDomain.ts). ScriptCommands.cs
// is the only caller: it saves, lists, shows, retires and runs scripts through this class, and ScriptEngine compiles
// the code. Everything here is file work in the scripts folder (through FileBoundary); nothing touches a drawing.

/// <summary>
/// One argument a saved script declares in its header: a C#-style <c>Name</c>, a <c>Type</c> (one of string,
/// number, integer, boolean, point, string[], json), whether it is required, an optional default and a description.
/// </summary>
internal sealed record ScriptParameter(
  string Name,
  string Type,
  bool Required,
  JsonNode? Default,
  string? Description);

/// <summary>
/// A saved script as read from (or just written to) its .csx file: header fields, the code after the header, the
/// file's path and time, the header's <c>script_version</c>, and, right after a replacing save, where the previous
/// version was archived. <c>Mode</c> is "write" (runs as a drawing change, inside UNDO BEGIN/END) or "read".
/// </summary>
internal sealed record SavedScript(
  string Name,
  string Description,
  string Mode,
  IReadOnlyList<ScriptParameter> Parameters,
  string Code,
  string Path,
  DateTime UpdatedUtc,
  int Version = 1,
  string? ArchivedPrevious = null)
{
  public bool IsWrite => Mode == "write";
}

/// <summary>
/// Reusable scripts saved by the AI (or by hand) for repetitive tasks.
/// Each script is one human-editable .csx file with a comment header:
/// <code>
/// // @c3dmcp-script
/// // name: renumber-points
/// // description: Renumber COGO points in a group starting at a number
/// // mode: write
/// // parameters: [{"name":"group","type":"string","required":true}]
/// // ---
/// ...C# code...
/// </code>
/// Directory: CIVIL3D_SCRIPTS_DIR, or Documents\Project Knowledge\civil3d\scripts (see <see cref="Directory"/>).
/// </summary>
internal static class ScriptLibrary
{
  private const string Marker = "// @c3dmcp-script";
  private const string HeaderEnd = "// ---";
  private const string Extension = ".csx";
  private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);
  private static readonly HashSet<string> ParameterTypes = new(StringComparer.Ordinal)
  {
    "string", "number", "integer", "boolean", "point", "string[]", "json",
  };

  private const string KnowHowFolderName = "civil3d";

  /// <summary>
  /// CIVIL3D_SCRIPTS_DIR, else the know-how folder's scripts (CIVIL3D_MCP_KNOWLEDGE_DIR\scripts, else
  /// &lt;shared&gt;\civil3d\scripts), else the &lt;shared&gt;\scripts used before 2.4.0 while civil3d\scripts doesn't exist yet
  /// (the MCP server moves it there). Nothing is created, renamed or moved here.
  /// </summary>
  public static string Directory
  {
    get
    {
      var configured = Environment.GetEnvironmentVariable("CIVIL3D_SCRIPTS_DIR");
      if (!string.IsNullOrWhiteSpace(configured))
      {
        return configured.Trim();
      }

      var knowHow = Environment.GetEnvironmentVariable("CIVIL3D_MCP_KNOWLEDGE_DIR");
      if (!string.IsNullOrWhiteSpace(knowHow))
      {
        return System.IO.Path.Combine(knowHow.Trim(), "scripts");
      }

      var shared = SharedFolder.Root();
      var own = System.IO.Path.Combine(shared, KnowHowFolderName, "scripts");
      var earlier = System.IO.Path.Combine(shared, "scripts");
      return !System.IO.Directory.Exists(own) && System.IO.Directory.Exists(earlier) ? earlier : own;
    }
  }

  /// <summary>
  /// The file-safe script name: trimmed, lower case, spaces as '-', without a trailing ".csx" (e.g. "Renumber Points"
  /// becomes "renumber-points"). Anything else that isn't 1-64 of a-z, 0-9, '-' or '_' is refused with
  /// CIVIL3D.INVALID_INPUT, which also keeps names from reaching outside the scripts folder.
  /// </summary>
  public static string NormalizeName(string rawName)
  {
    var name = (rawName ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-');
    if (name.EndsWith(Extension, StringComparison.Ordinal))
    {
      name = name[..^Extension.Length];
    }

    if (!NamePattern.IsMatch(name))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Invalid script name '{rawName}'. Use 1-64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");
    }

    return name;
  }

  /// <summary>
  /// Parses a parameter list (from a save request or a script's header) into <see cref="ScriptParameter"/>s; null
  /// gives none. Refuses with CIVIL3D.INVALID_INPUT anything that isn't an array of objects, a name that isn't a C#
  /// identifier, an unknown type or a duplicate name. A missing type means "string".
  /// </summary>
  public static IReadOnlyList<ScriptParameter> ParseParameters(JsonNode? node)
  {
    if (node == null)
    {
      return Array.Empty<ScriptParameter>();
    }

    if (node is not JsonArray array)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "parameters must be an array of {name, type, required?, default?, description?}.");
    }

    try
    {
      return ParseParameterArray(array);
    }
    catch (InvalidOperationException exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid parameter definition: {exception.Message}");
    }
  }

  private static IReadOnlyList<ScriptParameter> ParseParameterArray(JsonArray array)
  {
    var parameters = new List<ScriptParameter>();
    foreach (var item in array)
    {
      if (item is not JsonObject obj)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each parameter must be an object.");
      }

      var name = obj["name"]?.GetValue<string>()?.Trim();
      if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid parameter name '{name}'. Use a C#-style identifier.");
      }

      var type = obj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "string";
      if (!ParameterTypes.Contains(type))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Parameter '{name}' has unsupported type '{type}'. Supported: {string.Join(", ", ParameterTypes)}.");
      }

      if (parameters.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Duplicate parameter '{name}'.");
      }

      parameters.Add(new ScriptParameter(
        name,
        type,
        obj["required"]?.GetValue<bool>() ?? false,
        obj["default"]?.DeepClone(),
        obj["description"]?.GetValue<string>()));
    }

    return parameters;
  }

  /// <summary>
  /// Saves a script as <c>&lt;name&gt;.csx</c> in <see cref="Directory"/> with its header written by
  /// <see cref="Serialize"/>. A description is required (CIVIL3D.INVALID_INPUT) so the script can be found later. An
  /// existing script is replaced only with <paramref name="overwrite"/> (CIVIL3D.CONFLICT otherwise); its current file
  /// is first copied to <see cref="ArchiveDirectory"/> and the version number goes up by one. The write is atomic.
  /// The MCP server checks the text for job data before it gets here.
  /// </summary>
  public static SavedScript Save(
    string rawName,
    string description,
    string code,
    string mode,
    IReadOnlyList<ScriptParameter> parameters,
    bool overwrite)
  {
    var name = NormalizeName(rawName);
    mode = NormalizeMode(mode);
    if (string.IsNullOrWhiteSpace(description))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A description is required so the script can be found and reused later.");
    }

    // Add, never delete: replacing a script keeps the previous version in _archive.
    var existingPath = FileBoundary.ResolveFileWithinDirectory(Directory, name + Extension, createDirectory: false);
    var version = 1;
    string? archived = null;
    if (File.Exists(existingPath))
    {
      if (!overwrite)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT",
          $"Saved script '{name}' already exists. Set overwrite=true to replace it (the current version is archived).");
      }

      version = (TryLoad(existingPath)?.Version ?? 1) + 1;
      archived = Archive(existingPath, name, "replaced");
    }

    var content = Serialize(name, description.Trim(), mode, parameters, code, version);
    var path = FileBoundary.WriteFileWithinDirectoryAtomic(Directory, name + Extension, content, overwrite);
    return new SavedScript(name, description.Trim(), mode, parameters, code, path, DateTime.UtcNow, version, archived);
  }

  /// <summary>Where replaced and retired versions go: <c>_archive</c> inside the scripts folder.</summary>
  public static string ArchiveDirectory => System.IO.Path.Combine(Directory, "_archive");

  /// <summary>
  /// Copies a script file to the archive as <c>&lt;name&gt;.&lt;UTC time&gt;.&lt;reason&gt;.csx</c> (reason
  /// "replaced" or "retired") and returns the archive copy's path.
  /// </summary>
  private static string Archive(string path, string name, string reason)
  {
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    return FileBoundary.WriteFileWithinDirectoryAtomic(
      ArchiveDirectory, $"{name}.{stamp}.{reason}{Extension}", File.ReadAllText(path), overwrite: true);
  }

  /// <summary>
  /// Every valid saved script in the scripts folder, sorted by file name (the archive folder isn't searched). Files
  /// without the <c>// @c3dmcp-script</c> header or that can't be read are skipped (unreadable ones are logged). An
  /// empty list when the folder doesn't exist yet.
  /// </summary>
  public static IReadOnlyList<SavedScript> List()
  {
    var directory = Directory;
    if (!System.IO.Directory.Exists(directory))
    {
      return Array.Empty<SavedScript>();
    }

    var scripts = new List<SavedScript>();
    foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
    {
      var script = TryLoad(path);
      if (script != null)
      {
        scripts.Add(script);
      }
    }

    return scripts;
  }

  /// <summary>
  /// One saved script by name. CIVIL3D.OBJECT_NOT_FOUND (listing the scripts there are) when there is no such file,
  /// CIVIL3D.INVALID_INPUT when the file exists but isn't a valid saved script.
  /// </summary>
  public static SavedScript Get(string rawName)
  {
    var name = NormalizeName(rawName);
    var path = FileBoundary.ResolveFileWithinDirectory(Directory, name + Extension, createDirectory: false);
    if (!File.Exists(path))
    {
      var available = string.Join(", ", List().Select(script => script.Name));
      throw new JsonRpcDispatchException(
        "CIVIL3D.OBJECT_NOT_FOUND",
        $"Saved script '{name}' was not found in {Directory}." + (available.Length > 0 ? $" Available: {available}." : " No scripts are saved yet."));
    }

    return TryLoad(path) ?? throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"'{path}' is not a valid saved script (missing '{Marker}' header).");
  }

  /// <summary>Retires a script: it is copied to _archive, then removed from the active library.</summary>
  /// <returns>The archive copy's path, or null when there was no such script (nothing is done then).</returns>
  public static string? Delete(string rawName)
  {
    var name = NormalizeName(rawName);
    var path = FileBoundary.ResolveFileWithinDirectory(Directory, name + Extension, createDirectory: false);
    if (!File.Exists(path))
    {
      return null;
    }

    var archived = Archive(path, name, "retired");
    FileBoundary.DeleteFileWithinDirectory(Directory, name + Extension);
    return archived;
  }

  /// <summary>Merges defaults, enforces required parameters, and coerces simple types.</summary>
  /// <remarks>
  /// Arguments the script doesn't declare are passed through unchanged. A missing required one is refused with
  /// CIVIL3D.INVALID_INPUT, as is a value that can't be coerced (see <see cref="Coerce"/>; an "integer" given as
  /// 2.6 is rounded to 3 rather than refused).
  /// </remarks>
  public static JsonObject BuildArguments(SavedScript script, JsonObject? provided)
  {
    var result = new JsonObject();
    if (provided != null)
    {
      foreach (var (key, value) in provided)
      {
        result[key] = value?.DeepClone();
      }
    }

    foreach (var parameter in script.Parameters)
    {
      if (result[parameter.Name] == null)
      {
        if (parameter.Default != null)
        {
          result[parameter.Name] = parameter.Default.DeepClone();
        }
        else if (parameter.Required)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Script '{script.Name}' requires argument '{parameter.Name}' ({parameter.Type})" +
            (parameter.Description != null ? $": {parameter.Description}" : "."));
        }
        else
        {
          continue;
        }
      }

      result[parameter.Name] = Coerce(parameter, result[parameter.Name]!);
    }

    return result;
  }

  /// <summary>
  /// A script as the tools show it: name, description, mode, parameters, version, last update (UTC, ISO 8601), path,
  /// where the previous version was archived when it was just replaced, and the code only when
  /// <paramref name="includeCode"/> is true (listing leaves it out to keep the answer small).
  /// </summary>
  public static object Describe(SavedScript script, bool includeCode)
  {
    var summary = new Dictionary<string, object?>
    {
      ["name"] = script.Name,
      ["description"] = script.Description,
      ["mode"] = script.Mode,
      ["parameters"] = script.Parameters.Select(parameter => new Dictionary<string, object?>
      {
        ["name"] = parameter.Name,
        ["type"] = parameter.Type,
        ["required"] = parameter.Required,
        ["default"] = parameter.Default?.DeepClone(),
        ["description"] = parameter.Description,
      }).ToList(),
      ["version"] = script.Version,
      ["updatedUtc"] = script.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
      ["path"] = script.Path,
    };

    if (script.ArchivedPrevious != null)
    {
      summary["previousVersionArchived"] = script.ArchivedPrevious;
    }

    if (includeCode)
    {
      summary["code"] = script.Code;
    }

    return summary;
  }

  /// <summary>"write" (also "execute", and the default) or "read" (also "query", "readonly", "read-only").</summary>
  internal static string NormalizeMode(string? mode)
  {
    return (mode ?? "write").Trim().ToLowerInvariant() switch
    {
      "write" or "execute" => "write",
      "read" or "query" or "readonly" or "read-only" => "read",
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mode must be 'write' or 'read'."),
    };
  }

  /// <summary>
  /// Converts one argument to its declared type, accepting strings for numbers and booleans (invariant culture), a
  /// single value for <c>string[]</c>, and any JSON for <c>string</c> (as its JSON text). A point must be an object
  /// with x and y. Failures become CIVIL3D.INVALID_INPUT naming the argument.
  /// </summary>
  private static JsonNode Coerce(ScriptParameter parameter, JsonNode value)
  {
    try
    {
      switch (parameter.Type)
      {
        case "number":
        {
          if (ScriptJson.TryGetDouble(value, out var d)) return JsonValue.Create(d)!;
          return JsonValue.Create(double.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture))!;
        }
        case "integer":
        {
          if (ScriptJson.TryGetDouble(value, out var d)) return JsonValue.Create((long)Math.Round(d))!;
          return JsonValue.Create(long.Parse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture))!;
        }
        case "boolean":
        {
          if (value is JsonValue v && v.TryGetValue<bool>(out var b)) return JsonValue.Create(b)!;
          return JsonValue.Create(bool.Parse(value.ToString()))!;
        }
        case "point":
          if (value is JsonObject point && point["x"] != null && point["y"] != null) return point.DeepClone();
          throw new FormatException("expected {x, y, z?}");
        case "string[]":
          if (value is JsonArray array) return array.DeepClone();
          return new JsonArray(JsonValue.Create(value.ToString()));
        case "string":
          return value is JsonValue sv && sv.TryGetValue<string>(out var s) ? JsonValue.Create(s)! : JsonValue.Create(value.ToJsonString())!;
        default:
          return value.DeepClone();
      }
    }
    catch (System.Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Argument '{parameter.Name}' must be of type {parameter.Type}: {exception.Message}");
    }
  }

  /// <summary>
  /// The .csx file text: the comment header (marker, name, one-line description, mode, parameters as JSON,
  /// script_version, updated time), the <c>// ---</c> line, then the code with LF line endings and one final newline.
  /// The header is all comments, so the file still compiles as C# script.
  /// </summary>
  private static string Serialize(string name, string description, string mode, IReadOnlyList<ScriptParameter> parameters, string code, int version)
  {
    var parameterJson = new JsonArray(parameters.Select(parameter =>
    {
      var obj = new JsonObject
      {
        ["name"] = parameter.Name,
        ["type"] = parameter.Type,
        ["required"] = parameter.Required,
      };
      if (parameter.Default != null) obj["default"] = parameter.Default.DeepClone();
      if (!string.IsNullOrWhiteSpace(parameter.Description)) obj["description"] = parameter.Description;
      return (JsonNode)obj;
    }).ToArray());

    var builder = new StringBuilder();
    builder.AppendLine(Marker);
    builder.AppendLine($"// name: {name}");
    builder.AppendLine($"// description: {SingleLine(description)}");
    builder.AppendLine($"// mode: {mode}");
    builder.AppendLine($"// parameters: {parameterJson.ToJsonString()}");
    builder.AppendLine($"// script_version: {version}");
    builder.AppendLine($"// updated: {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
    builder.AppendLine(HeaderEnd);
    builder.Append(code.Replace("\r\n", "\n").TrimEnd()).Append('\n');
    return builder.ToString();
  }

  /// <summary>
  /// Reads a .csx file back into a <see cref="SavedScript"/>, or null (logged as a warning when it can't be read or
  /// parsed) when it isn't one: it must start with the marker and have a <c>// ---</c> line. The name falls back to
  /// the file name, the mode to "write", the version to 1.
  /// </summary>
  // NOTE: the header's "name:" wins over the file name, so a file renamed by hand reports its old name.
  private static SavedScript? TryLoad(string path)
  {
    try
    {
      var text = File.ReadAllText(path).Replace("\r\n", "\n");
      // A file edited by hand may end at "// ---" with no code and no final newline; it is still a script.
      if (!text.EndsWith('\n'))
      {
        text += "\n";
      }
      if (!text.StartsWith(Marker, StringComparison.Ordinal))
      {
        return null;
      }

      var end = text.IndexOf("\n" + HeaderEnd + "\n", StringComparison.Ordinal);
      if (end < 0)
      {
        return null;
      }

      var header = text[..end].Split('\n').Skip(1);
      var code = text[(end + HeaderEnd.Length + 2)..];
      var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var line in header)
      {
        var trimmed = line.StartsWith("//", StringComparison.Ordinal) ? line[2..].Trim() : line.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
          fields[trimmed[..colon].Trim()] = trimmed[(colon + 1)..].Trim();
        }
      }

      var name = fields.TryGetValue("name", out var n) ? n : System.IO.Path.GetFileNameWithoutExtension(path);
      var parameters = fields.TryGetValue("parameters", out var p) && p.Length > 0
        ? ParseParameters(JsonNode.Parse(p))
        : Array.Empty<ScriptParameter>();

      return new SavedScript(
        name,
        fields.TryGetValue("description", out var description) ? description : string.Empty,
        NormalizeMode(fields.TryGetValue("mode", out var mode) ? mode : "write"),
        parameters,
        code,
        path,
        File.GetLastWriteTimeUtc(path),
        fields.TryGetValue("script_version", out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
          ? parsed
          : 1);
    }
    catch (System.Exception exception) when (exception is IOException or JsonException or JsonRpcDispatchException or UnauthorizedAccessException or InvalidOperationException or FormatException)
    {
      PluginLog.Warn("ScriptLibrary", $"Skipping unreadable script '{path}': {exception.Message}");
      return null;
    }
  }

  private static string SingleLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
