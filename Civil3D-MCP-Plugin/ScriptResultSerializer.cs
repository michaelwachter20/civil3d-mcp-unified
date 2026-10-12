using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Civil3DMcpPlugin;

/// <summary>
/// Converts arbitrary script return values to JSON while the script
/// transaction is still open. AutoCAD types get compact shapes instead of
/// full property graphs (which are huge and often throw when walked).
/// </summary>
internal static class ScriptResultSerializer
{
  private const int MaxResultCharacters = 4 * 1024 * 1024;

  private static readonly JsonSerializerOptions Options = CreateOptions();

  public static JsonNode? ToJson(object? value)
  {
    if (value == null)
    {
      return null;
    }

    if (value is JsonNode node)
    {
      return node.DeepClone();
    }

    JsonNode? result;
    try
    {
      result = JsonSerializer.SerializeToNode(value, value.GetType(), Options);
    }
    catch (System.Exception exception) when (exception is not OperationCanceledException)
    {
      return new JsonObject
      {
        ["type"] = value.GetType().FullName,
        ["text"] = SafeToString(value),
        ["serializationWarning"] = $"Return value could not be fully serialized ({exception.Message}). Return plain values, anonymous objects, lists, or dictionaries instead.",
      };
    }

    var json = result?.ToJsonString() ?? "null";
    if (json.Length > MaxResultCharacters)
    {
      return new JsonObject
      {
        ["truncated"] = true,
        ["originalCharacters"] = json.Length,
        ["preview"] = json[..100_000],
        ["hint"] = "Result exceeded 4 MB. Return a summary, page through results, or write a CSV export in write mode.",
      };
    }

    return result;
  }

  private static string SafeToString(object value)
  {
    try
    {
      return value.ToString() ?? string.Empty;
    }
    catch (System.Exception exception)
    {
      return $"<ToString failed: {exception.Message}>";
    }
  }

  private static JsonSerializerOptions CreateOptions()
  {
    var options = new JsonSerializerOptions
    {
      ReferenceHandler = ReferenceHandler.IgnoreCycles,
      MaxDepth = 32,
      IncludeFields = true,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      WriteIndented = false,
    };

    options.Converters.Add(new JsonStringEnumConverter());
    options.Converters.Add(new ObjectIdConverter());
    options.Converters.Add(new HandleConverter());
    options.Converters.Add(new Point3dConverter());
    options.Converters.Add(new Point2dConverter());
    options.Converters.Add(new Vector3dConverter());
    options.Converters.Add(new Vector2dConverter());
    options.Converters.Add(new Extents3dConverter());
    options.Converters.Add(new ObjectIdCollectionConverter());
    options.Converters.Add(new RxObjectConverterFactory());
    return options;
  }

  private sealed class ObjectIdConverter : JsonConverter<ObjectId>
  {
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
    {
      if (value.IsNull)
      {
        writer.WriteNullValue();
        return;
      }

      writer.WriteStartObject();
      writer.WriteString("handle", value.Handle.ToString());
      try
      {
        writer.WriteString("type", value.ObjectClass?.Name);
      }
      catch (System.Exception)
      {
        writer.WriteNull("type");
      }
      writer.WriteBoolean("erased", value.IsErased);
      writer.WriteEndObject();
    }
  }

  private sealed class ObjectIdCollectionConverter : JsonConverter<ObjectIdCollection>
  {
    public override ObjectIdCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ObjectIdCollection value, JsonSerializerOptions options)
    {
      writer.WriteStartArray();
      foreach (ObjectId id in value)
      {
        JsonSerializer.Serialize(writer, id, options);
      }
      writer.WriteEndArray();
    }
  }

  private sealed class HandleConverter : JsonConverter<Handle>
  {
    public override Handle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Handle value, JsonSerializerOptions options) =>
      writer.WriteStringValue(value.ToString());
  }

  private sealed class Point3dConverter : JsonConverter<Point3d>
  {
    public override Point3d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Point3d value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      WriteNumber(writer, "x", value.X);
      WriteNumber(writer, "y", value.Y);
      WriteNumber(writer, "z", value.Z);
      writer.WriteEndObject();
    }
  }

  private sealed class Point2dConverter : JsonConverter<Point2d>
  {
    public override Point2d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Point2d value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      WriteNumber(writer, "x", value.X);
      WriteNumber(writer, "y", value.Y);
      writer.WriteEndObject();
    }
  }

  private sealed class Vector3dConverter : JsonConverter<Vector3d>
  {
    public override Vector3d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Vector3d value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      WriteNumber(writer, "x", value.X);
      WriteNumber(writer, "y", value.Y);
      WriteNumber(writer, "z", value.Z);
      writer.WriteEndObject();
    }
  }

  private sealed class Vector2dConverter : JsonConverter<Vector2d>
  {
    public override Vector2d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Vector2d value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      WriteNumber(writer, "x", value.X);
      WriteNumber(writer, "y", value.Y);
      writer.WriteEndObject();
    }
  }

  private sealed class Extents3dConverter : JsonConverter<Extents3d>
  {
    public override Extents3d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, Extents3d value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      writer.WritePropertyName("min");
      JsonSerializer.Serialize(writer, value.MinPoint, options);
      writer.WritePropertyName("max");
      JsonSerializer.Serialize(writer, value.MaxPoint, options);
      writer.WriteEndObject();
    }
  }

  /// <summary>
  /// Summarizes any AutoCAD/Civil runtime object (entities, styles, geometry
  /// classes) as type + handle + common identifying fields.
  /// </summary>
  private sealed class RxObjectConverterFactory : JsonConverterFactory
  {
    public override bool CanConvert(Type typeToConvert) => typeof(RXObject).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
      (JsonConverter)Activator.CreateInstance(typeof(RxObjectConverter<>).MakeGenericType(typeToConvert))!;
  }

  private sealed class RxObjectConverter<T> : JsonConverter<T> where T : RXObject
  {
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      writer.WriteString("type", value.GetType().Name);

      if (value is DBObject dbObject)
      {
        TryWrite(writer, "handle", () => dbObject.Handle.ToString());
        if (dbObject is Autodesk.Civil.DatabaseServices.Entity civilEntity)
        {
          TryWrite(writer, "name", () => civilEntity.Name);
        }
        else if (dbObject is SymbolTableRecord record)
        {
          TryWrite(writer, "name", () => record.Name);
        }

        if (dbObject is Autodesk.AutoCAD.DatabaseServices.Entity entity)
        {
          TryWrite(writer, "layer", () => entity.Layer);
        }
      }

      writer.WriteString("hint", "AutoCAD object summarized; return specific properties for more detail.");
      writer.WriteEndObject();
    }

    private static void TryWrite(Utf8JsonWriter writer, string name, Func<string?> getter)
    {
      try
      {
        writer.WriteString(name, getter());
      }
      catch (System.Exception)
      {
        writer.WriteNull(name);
      }
    }
  }

  private static void WriteNumber(Utf8JsonWriter writer, string name, double value)
  {
    if (double.IsFinite(value))
    {
      writer.WriteNumber(name, value);
    }
    else
    {
      writer.WriteString(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
  }
}
