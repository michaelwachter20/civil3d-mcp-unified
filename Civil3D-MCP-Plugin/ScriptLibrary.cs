using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

internal sealed record ScriptParameter(
  string Name,
  string Type,
  bool Required,
  JsonNode? Default,
  string? Description);

internal sealed record SavedScript(
  string Name,
  string Description,
  string Mode,
  IReadOnlyList<ScriptParameter> Parameters,
  string Code,
  string Path,
  DateTime UpdatedUtc)
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
/// Directory: CIVIL3D_SCRIPTS_DIR, or Documents\Civil3DMcp\scripts.
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

  public static string Directory
  {
    get
    {
      var configured = Environment.GetEnvironmentVariable("CIVIL3D_SCRIPTS_DIR");
      return string.IsNullOrWhiteSpace(configured)
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Civil3DMcp", "scripts")
        : configured.Trim();
    }
  }

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

    var content = Serialize(name, description.Trim(), mode, parameters, code);
    var path = FileBoundary.WriteFileWithinDirectoryAtomic(Directory, name + Extension, content, overwrite);
    return new SavedScript(name, description.Trim(), mode, parameters, code, path, DateTime.UtcNow);
  }

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

  public static bool Delete(string rawName)
  {
    var name = NormalizeName(rawName);
    return FileBoundary.DeleteFileWithinDirectory(Directory, name + Extension);
  }

  /// <summary>Merges defaults, enforces required parameters, and coerces simple types.</summary>
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
      ["updatedUtc"] = script.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
      ["path"] = script.Path,
    };

    if (includeCode)
    {
      summary["code"] = script.Code;
    }

    return summary;
  }

  internal static string NormalizeMode(string? mode)
  {
    return (mode ?? "write").Trim().ToLowerInvariant() switch
    {
      "write" or "execute" => "write",
      "read" or "query" or "readonly" or "read-only" => "read",
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mode must be 'write' or 'read'."),
    };
  }

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
        $"Argument '{parameter.Name}' must be a {parameter.Type}: {exception.Message}");
    }
  }

  private static string Serialize(string name, string description, string mode, IReadOnlyList<ScriptParameter> parameters, string code)
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
    builder.AppendLine($"// updated: {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
    builder.AppendLine(HeaderEnd);
    builder.Append(code.Replace("\r\n", "\n").TrimEnd()).Append('\n');
    return builder.ToString();
  }

  private static SavedScript? TryLoad(string path)
  {
    try
    {
      var text = File.ReadAllText(path).Replace("\r\n", "\n");
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
        File.GetLastWriteTimeUtc(path));
    }
    catch (System.Exception exception) when (exception is IOException or JsonException or JsonRpcDispatchException or UnauthorizedAccessException or InvalidOperationException or FormatException)
    {
      PluginLog.Warn("ScriptLibrary", $"Skipping unreadable script '{path}': {exception.Message}");
      return null;
    }
  }

  private static string SingleLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
