// Test stub of the plugin's JsonRpcDispatchException (the exception handlers throw for expected failures, carrying a
// CIVIL3D.<CODE> identifier). The real one lives in the plugin; this copy lets the harness compile without Civil 3D.

namespace Civil3DMcpPlugin;

public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}
