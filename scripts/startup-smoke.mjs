// Offline startup check: launches build/index.js with its HTTP bridge on 127.0.0.1:MCP_HTTP_PORT (default 31999), polls
// GET /health for up to about 3 s, requires bridge "ok" and at least one registered tool, then kills the server.
// Doesn't need Civil 3D (the liveness endpoint doesn't call the plugin).
//
//   npm run test:startup   (builds first)    CI: node ./scripts/startup-smoke.mjs in .github/workflows/ci.yml
import { spawn } from "node:child_process";

const port = process.env.MCP_HTTP_PORT ?? "31999";
const child = spawn(process.execPath, ["./build/index.js"], {
  env: { ...process.env, MCP_HTTP_PORT: port, MCP_HTTP_HOST: "127.0.0.1" },
  stdio: ["ignore", "ignore", "pipe"],
});
let stderr = "";
child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });

try {
  let response;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, 100));
    try {
      response = await fetch(`http://127.0.0.1:${port}/health`, {
        headers: process.env.MCP_HTTP_TOKEN
          ? { Authorization: `Bearer ${process.env.MCP_HTTP_TOKEN}` }
          : undefined,
      });
      if (response.ok) break;
    } catch { /* server is still starting */ }
  }
  if (!response?.ok) throw new Error(`Server did not become healthy.\n${stderr}`);
  const body = await response.json();
  if (body.bridge !== "ok" || body.registeredTools < 1) throw new Error(`Unexpected liveness response: ${JSON.stringify(body)}`);
  console.log(`Startup smoke passed with ${body.registeredTools} registered tools.`);
} finally {
  child.kill();
}
