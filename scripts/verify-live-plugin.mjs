// Live read-only check that the plugin answers: calls getCivil3DHealth and getProjectContext (drawing info, object
// types, selection) over the compiled TCP client in build/ (127.0.0.1:8080 by default) and prints both as JSON.
//
//   npm run test:live-plugin   (builds first; also the first step of test:live-all)
//
// Needs Civil 3D 2027 open with the AI connection on and a person at the machine; changes nothing in the drawing.
// Automated sessions must not run it. Exit code 1 with a message when the plugin can't be reached.
import { withApplicationConnection } from "../build/utils/ConnectionManager.js";
import { getProjectContext } from "../build/orchestration/ProjectContextService.js";

/** Reads health and project context from the plugin and writes them to stdout. */
async function main() {
  const health = await withApplicationConnection(async (client) =>
    client.sendCommand("getCivil3DHealth", {}),
  );
  const context = await getProjectContext();

  process.stdout.write(`${JSON.stringify({ health, context }, null, 2)}\n`);
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error);
  process.stderr.write(`Live Civil 3D plugin validation failed: ${message}\n`);
  process.exitCode = 1;
});
