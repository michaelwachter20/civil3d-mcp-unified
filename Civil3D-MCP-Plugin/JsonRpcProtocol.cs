// JSON-RPC 2.0 response serialization for the plugin, the domain-code to numeric-code table, and one shared
// parameter reader (PositiveIntegers, for point numbers).
// Serves no JSON-RPC method: PluginRuntime.HandleRawRequestAsync and RpcTcpServer use it to write every answer that
// goes back to the TypeScript server (src/utils/SocketClient.ts turns errors into Civil3DRpcError). Results are
// camelCase JSON; errors carry the number in error.code and the CIVIL3D.* domain code in error.data.code
// (tests/security_boundary.test.ts checks those strings). Pure serialization and parsing: no drawing, no files; also
// compiled into tests/FileBoundaryHarness.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>Builds the plugin's JSON-RPC 2.0 response strings and maps CIVIL3D.* domain codes to JSON-RPC error numbers.</summary>
internal static class JsonRpcProtocol
{
  /// <summary>
  /// A success response: <c>{jsonrpc, id, result}</c> with the handler's return value serialized in camelCase
  /// (dictionaries keep their own keys).
  /// </summary>
  public static string SerializeResult(JsonNode? id, object? result)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["result"] = result == null ? null : JsonSerializer.SerializeToNode(result, SerializerOptions),
    };

    return response.ToJsonString(SerializerOptions);
  }

  /// <summary>An error response: <c>{jsonrpc, id, error: {code: numericCode, message, data: {code: domainCode}}}</c>.</summary>
  public static string SerializeError(JsonNode? id, int numericCode, string domainCode, string message)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["error"] = new JsonObject
      {
        ["code"] = numericCode,
        ["message"] = message,
        ["data"] = new JsonObject
        {
          ["code"] = domainCode,
        },
      },
    };

    return response.ToJsonString(SerializerOptions);
  }

  /// <summary>
  /// The JSON-RPC error number for a CIVIL3D.* domain code; codes not listed (e.g. CIVIL3D.USER_DECLINED) get the
  /// generic -32000. CIVIL3D.INTERNAL_ERROR maps to JSON-RPC's own -32603 (PluginRuntime still writes that number
  /// directly for an unexpected exception; both must stay the same).
  /// </summary>
  public static int NumericErrorCode(string domainCode) => domainCode switch
  {
    "CIVIL3D.INVALID_JSON" => -32700,
    "CIVIL3D.INVALID_REQUEST" => -32600,
    "CIVIL3D.METHOD_NOT_FOUND" => -32601,
    "CIVIL3D.INVALID_INPUT" => -32602,
    "CIVIL3D.INTERNAL_ERROR" => -32603,
    "CIVIL3D.NO_DRAWING" or "CIVIL3D.UNAVAILABLE" => -32001,
    "CIVIL3D.OBJECT_NOT_FOUND" => -32004,
    "CIVIL3D.TIMEOUT" => -32008,
    "CIVIL3D.CONFLICT" => -32009,
    "CIVIL3D.CANCELLED" => -32010,
    "CIVIL3D.HOST_BUSY" => -32029,
    "CIVIL3D.PATH_NOT_ALLOWED" => -32040,
    "CIVIL3D.FILE_TYPE_NOT_ALLOWED" => -32041,
    "CIVIL3D.FILE_IO_ERROR" => -32042,
    "CIVIL3D.AUTH_REQUIRED" => -32043,
    "CIVIL3D.FORBIDDEN" => -32044,
    "CIVIL3D.API_ERROR" => -32050,
    "CIVIL3D.TRANSACTION_FAILED" => -32051,
    "CIVIL3D.COMMAND_FAILED" => -32052,
    "CIVIL3D.COMPILATION_ERROR" => -32053,
    "CIVIL3D.SCRIPT_ERROR" => -32054,
    "CIVIL3D.SANDBOX_VIOLATION" => -32045,
    "CIVIL3D.INVALID_CONFIGURATION" => -32046,
    _ => -32000,
  };

  /// <summary>
  /// A request's array of whole positive numbers (COGO point numbers), in order; empty when <paramref name="array"/> is
  /// null. Anything else in it (a fraction such as 12.5, 0, a negative, text, null) is CIVIL3D.INVALID_INPUT naming
  /// <paramref name="parameterName"/> and the bad item, never silently dropped or truncated: a point number that was
  /// read wrong would act on another point.
  /// </summary>
  public static List<uint> PositiveIntegers(JsonArray? array, string parameterName)
  {
    var numbers = new List<uint>();
    if (array == null)
    {
      return numbers;
    }

    for (var index = 0; index < array.Count; index++)
    {
      var item = array[index];
      // Read through the JSON text, so a number built in code (JsonValue of an int) reads the same as one parsed.
      if (item is JsonValue value
          && value.GetValueKind() == JsonValueKind.Number
          && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
          && number >= 1
          && number <= uint.MaxValue
          && number == Math.Floor(number))
      {
        numbers.Add((uint)number);
        continue;
      }

      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"{parameterName}[{index}] is {item?.ToJsonString() ?? "null"}: point numbers are whole numbers of 1 or more. Send them as integers, e.g. [101, 102].");
    }

    return numbers;
  }

  /// <summary>camelCase property names, compact output.</summary>
  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };
}
