// Source-text guard on the PLUGIN (reads Civil3D-MCP-Plugin/*.cs as text, runs nothing): keeps code patterns that would
// freeze Civil 3D on a very long profile, and keeps the plugin answering health while a long call runs.
// The plugin folder is found from this file's location, so it runs from any working folder. If a plugin file is
// renamed, update this test.

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const PLUGIN_FOLDER = join(import.meta.dirname, "..", "Civil3D-MCP-Plugin");
const plugin = (file: string) => readFileSync(join(PLUGIN_FOLDER, file), "utf8");

/**
 * A 6.5 mile ground profile has 9,662 PVIs and entities. Reading its PVIs takes 37 s by any route, and reading its
 * entities once per station froze Civil 3D for 20 minutes. Nothing in a test can run Civil 3D, so these hold the shape.
 */
describe("what keeps a long profile from freezing Civil 3D", () => {
  it("checks a profile by its entities, not its PVIs", () => {
    expect(plugin("QcCommands.cs")).not.toMatch(/\.PVIs|"PVIs"/);
    expect(plugin("SightDistanceCommands.cs")).not.toMatch(/\.PVIs|"PVIs"/);
  });

  it("reads a profile's curves once, outside the station loop", () => {
    const code = plugin("SightDistanceCommands.cs");
    const loop = code.indexOf("for (double sta = startSta");
    expect(loop).toBeGreaterThan(0);
    expect(code.indexOf("var curves = ReadVerticalCurves")).toBeLessThan(loop);
    expect(code.slice(loop, code.indexOf("return new Dictionary", loop))).not.toContain("Entities");
  });

  it("doesn't count PVIs for every sample", () => {
    const code = plugin("ProfileCommands.cs");
    const start = code.indexOf("private static double ElevationOnProfile");
    expect(start).toBeGreaterThan(0);
    expect(code.slice(start, code.indexOf("private static double ReadProfileGrade"))).not.toContain("PVIs");
  });

  it("answers health without waiting for the main thread", () => {
    const code = plugin("DrawingCommands.cs");
    const start = code.indexOf("public static async Task<object?> GetCivil3DHealthAsync");
    expect(start).toBeGreaterThan(0);
    const body = code.slice(start, code.indexOf("[\"jobs\"]", start));
    expect(body).toContain("OnMainThreadAsync");
    expect(body.replace(/OnMainThreadAsync\([\s\S]*?TimeSpan\.FromSeconds\(\d+\)\)/, "")).not.toMatch(/App\.(GetSystemVariable|DocumentManager)/);
  });

  it("refuses new work while a long call runs or memory is nearly out", () => {
    const code = plugin("PluginRuntime.cs");
    const start = code.indexOf("internal static void QueueHostOperation");
    const body = code.slice(start, code.indexOf("internal static void StartHostOperation"));
    expect(body).toContain("HostGuard.CheckMemory");
    expect(body).toContain("HostGuard.BusyRefusal");
  });
});
