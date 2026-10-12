// Sends one JSON-RPC request straight to the plugin (bypasses the MCP server and approval flow).
// Usage: node scripts/try-rpc.mjs <method> ['<json params>']
import net from "node:net";

const [method, params = "{}"] = process.argv.slice(2);
if (!method) {
  console.error("Usage: node scripts/try-rpc.mjs <method> ['<json params>']");
  process.exit(2);
}

const port = Number(process.env.CIVIL3D_PORT ?? 8080);
const host = process.env.CIVIL3D_HOST ?? "127.0.0.1";
const socket = net.connect(port, host, () =>
  socket.write(JSON.stringify({ jsonrpc: "2.0", id: 1, method, params: JSON.parse(params) })));
let data = "";
socket.on("data", (chunk) => {
  data += chunk;
  try {
    console.log(JSON.stringify(JSON.parse(data), null, 2));
    socket.end();
  } catch {
    // Wait for the rest of the response.
  }
});
socket.on("error", (error) => {
  console.error(error.message);
  process.exit(1);
});
