// StmCommands.cs - starts Civil 3D's Storm and Sanitary Analysis / Hydraflow (.STM) dialogs. Methods
// (CommandDispatcher.cs): exportStm, importStm, openStormSanitaryAnalysis, listSsaCapabilities. Caller:
// src/tools/domains/hydrologyDomain.ts (civil3d_hydrology). Each handler runs an AutoCAD command (ExportSSA,
// ImportSSA, AnalyzeGravityNetwork) with Editor.CommandAsync inside CivilExecution.ExecuteInCommandContextAsync (the
// host gate, command context, the verified drawing) and answers when the user has closed its dialog; the user picks
// the file there. So no caller-supplied path is accepted (nothing to send through FileBoundary), and whatever ImportSSA
// or the analysis changes happens inside the request as one undo step, not behind the host gate's back.
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Backend commands for STM (Storm and Sanitary Analysis) import/export.
/// Runs Civil 3D's built-in Storm and Sanitary Analysis dialog commands through
/// the host gate (Editor.CommandAsync in command context) and waits for them.
/// </summary>
public static class StmCommands
{
  // ──────────────────────────────────────────────
  //  Export to STM (Hydraflow Storm Sewers file)
  // ──────────────────────────────────────────────
  /// <summary><c>exportStm</c>: runs ExportSSA, which opens Civil 3D's export dialog, and answers when the user has
  /// closed it (<see cref="RunDialogCommandAsync"/>). <c>filePath</c> must be absent (CIVIL3D.INVALID_INPUT otherwise:
  /// the dialog can't take one). Returns status "completed" and notes; the drawing isn't changed.
  /// CIVIL3D.COMMAND_FAILED when the command fails.</summary>
  public static Task<object?> ExportStmAsync(JsonObject? parameters)
  {
    var filePath = PluginRuntime.GetOptionalString(parameters, "filePath");
    if (!string.IsNullOrWhiteSpace(filePath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "ExportSSA is an interactive dialog and cannot bind a caller-supplied filePath. Omit filePath and choose the destination in Civil 3D.");
    }

    // The ExportSSA command opens the Storm and Sanitary Analysis export dialog.
    // The user chooses the destination in the dialog (a filePath was refused above).
    return RunDialogCommandAsync("ExportSSA", "Failed to execute ExportSSA command", () =>
      new Dictionary<string, object?>
      {
        ["command"] = "ExportSSA",
        ["status"] = "completed",
        ["message"] = "The ExportSSA dialog was shown and closed; the .STM file is wherever the user saved it (or nowhere if they cancelled).",
        ["notes"] = new List<string>
        {
          "The ExportSSA command opens a dialog for exporting pipe network and catchment data to a Hydraflow Storm Sewers (.STM) file.",
          "The export includes pipe sizes, invert elevations, structure data, and catchment parameters.",
          "After export, the .STM file can be opened in Hydraflow Storm Sewers for detailed analysis.",
        },
      });
  }

  // ──────────────────────────────────────────────
  //  Import from STM
  // ──────────────────────────────────────────────
  /// <summary><c>importStm</c>: runs ImportSSA, which opens Civil 3D's import dialog; the user picks the .STM file and
  /// the import (which can change pipes and catchments) runs inside this request, through the host gate, and is one
  /// undo step (<see cref="RunDialogCommandAsync"/>). <c>filePath</c> must be absent (CIVIL3D.INVALID_INPUT).
  /// CIVIL3D.COMMAND_FAILED when the command fails.</summary>
  public static Task<object?> ImportStmAsync(JsonObject? parameters)
  {
    var filePath = PluginRuntime.GetOptionalString(parameters, "filePath");
    if (!string.IsNullOrWhiteSpace(filePath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "ImportSSA is an interactive dialog and cannot bind a caller-supplied filePath. Omit filePath and choose the source in Civil 3D.");
    }

    // The ImportSSA command opens the Storm and Sanitary Analysis import dialog.
    return RunDialogCommandAsync("ImportSSA", "Failed to execute ImportSSA command", () =>
      new Dictionary<string, object?>
      {
        ["command"] = "ImportSSA",
        ["status"] = "completed",
        ["message"] = "The ImportSSA dialog was shown and closed. Whatever the user imported is in the drawing now (one undo step); check the pipes before relying on them.",
        ["notes"] = new List<string>
        {
          "The ImportSSA command opens a dialog for importing Hydraflow Storm Sewers (.STM) analysis results back into Civil 3D.",
          "Imported data updates pipe network sizes, invert elevations, and catchment parameters.",
          "Part matching settings should be configured before importing to ensure correct pipe/structure mapping.",
        },
      });
  }

  // ──────────────────────────────────────────────
  //  Open Storm and Sanitary Analysis
  // ──────────────────────────────────────────────
  /// <summary><c>openStormSanitaryAnalysis</c>: takes no file (<c>filePath</c> is CIVIL3D.INVALID_INPUT, as on the
  /// TypeScript side). Runs AnalyzeGravityNetwork (the analysis dialog, which can resize pipes and change their results
  /// in the drawing) inside this request through the host gate, and answers status "completed" when the user has
  /// closed it (<see cref="RunDialogCommandAsync"/>). CIVIL3D.COMMAND_FAILED when the command fails.</summary>
  public static Task<object?> OpenStormSanitaryAnalysisAsync(JsonObject? parameters)
  {
    if (!string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(parameters, "filePath")))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "The analysis dialog works on the drawing's gravity networks and takes no file. Leave out filePath (to bring in an .stm file, use import_stm).");
    }

    return RunDialogCommandAsync("AnalyzeGravityNetwork", "Failed to open Storm and Sanitary Analysis", () =>
      new Dictionary<string, object?>
      {
        ["command"] = "AnalyzeGravityNetwork",
        ["status"] = "completed",
        ["message"] = "The gravity network analysis dialog was shown and closed. Anything it changed in the drawing is one undo step.",
        ["notes"] = new List<string>
        {
          "Use this to perform detailed hydraulic analysis of gravity pipe networks.",
          "The analysis includes HGL/EGL calculations, capacity checks, and flow routing.",
          "Results can be exported to .STM format for further analysis.",
        },
      });
  }

  /// <summary>
  /// Runs <paramref name="commandName"/> (a dialog command) through the host gate in command context with
  /// Editor.CommandAsync, as acad_command does, and answers with <paramref name="answer"/> once the command has ended:
  /// whatever it changes in the drawing happens inside this request, on the verified drawing, as one undo step, with
  /// no other request running meanwhile. While the user has the dialog open, other requests wait (or are refused as
  /// busy after a minute, HostGuard). CIVIL3D.COMMAND_FAILED, starting with <paramref name="failure"/>, when it fails.
  /// </summary>
  private static Task<object?> RunDialogCommandAsync(string commandName, string failure, Func<Dictionary<string, object?>> answer)
  {
    return CivilExecution.ExecuteInCommandContextAsync<object?>(async () =>
    {
      var (doc, _) = CivilExecution.GetVerifiedActiveDocuments();
      try
      {
        await doc.Editor.CommandAsync(commandName);
      }
      catch (System.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.COMMAND_FAILED", $"{failure}: {ex.Message}");
      }

      return answer();
    });
  }

  // ──────────────────────────────────────────────
  //  List SSA Capabilities
  // ──────────────────────────────────────────────
  /// <summary><c>listSsaCapabilities</c>: a fixed description of the three operations above. Doesn't touch Civil 3D.</summary>
  public static Task<object?> ListSsaCapabilitiesAsync()
  {
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["domain"] = "storm_sanitary_analysis",
      ["operations"] = new List<Dictionary<string, object?>>
      {
        new()
        {
          ["name"] = "export_stm",
          ["command"] = "ExportSSA",
          ["status"] = "implemented",
          ["description"] = "Exports pipe network and catchment data to Hydraflow Storm Sewers (.STM) file format.",
          ["dialogRequired"] = true,
        },
        new()
        {
          ["name"] = "import_stm",
          ["command"] = "ImportSSA",
          ["status"] = "implemented",
          ["description"] = "Imports Storm and Sanitary Analysis results from .STM file back into Civil 3D.",
          ["dialogRequired"] = true,
        },
        new()
        {
          ["name"] = "open_ssa",
          ["command"] = "AnalyzeGravityNetwork",
          ["status"] = "implemented",
          ["description"] = "Opens the Storm and Sanitary Analysis dialog for gravity network analysis.",
          ["dialogRequired"] = true,
        },
      },
      ["notes"] = new List<string>
      {
        "STM import/export commands open dialogs that require user interaction for file selection.",
        "For fully automated STM workflows, consider using UI automation (Playwright/MCPControl) to handle dialog interactions.",
        "Part matching settings should be configured before importing STM data.",
      },
    });
  }
}
