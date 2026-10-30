using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods about the drawing as a whole and the Civil 3D window, behind civil3d_drawing
/// (src/tools/domains/drawingRuntimeDomain.ts: info, settings, new, open, save, undo, redo, list_open, switch,
/// selected_objects_info, list_object_types) and civil3d_health (pluginDomain.ts). The orchestrator's
/// ProjectContextService calls <c>getProjectContext</c>, and the approval policy fingerprints the active drawing with
/// <c>getDrawingInfo</c>, so that answer must stay stable for an unchanged drawing.
/// <para>
/// Most methods read through <c>CivilExecution.ReadAsync</c>. Opening, creating and switching drawings run through
/// <c>ExecuteInCommandContextAsync</c> / <c>ExecuteReadOnlyAsync</c> because they act on the document manager, not on
/// one drawing's database. Paths go through <c>FileBoundary</c>.
/// </para>
/// </summary>
public static class DrawingCommands
{
  /// <summary>
  /// The health answer for civil3d_health: Civil 3D and plugin versions, whether a drawing is open, the host queue
  /// (operation in progress, depth, capacity, current request and how long it has run), the user's active commands,
  /// memory use, the plugin log's path and health, and background job counts. Doesn't go through the host queue, so
  /// it answers even while a long request is running.
  /// </summary>
  public static Task<object?> GetCivil3DHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    var doc = App.DocumentManager.MdiActiveDocument;
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var jobs = JobRegistry.GetStats();

    object response = new Dictionary<string, object?>
    {
      ["connected"] = true,
      // ACADVER ends with the licensee's company name in parentheses; report only the version.
      ["civil3dVersion"] = System.Text.RegularExpressions.Regex.Replace(App.GetSystemVariable("ACADVER")?.ToString() ?? string.Empty, @"\s*\(.*\)\s*$", string.Empty),
      ["pluginVersion"] = typeof(PluginEntry).Assembly.GetName().Version?.ToString(),
      ["drawingLoaded"] = doc != null,
      ["operationInProgress"] = status.OperationInProgress,
      ["currentOperation"] = status.CurrentOperation,
      ["queueDepth"] = status.QueueDepth,
      ["queueCapacity"] = status.QueueCapacity,
      ["currentOperationStartedAtUnixMs"] = status.CurrentOperationStartedAtUnixMs,
      ["currentRequestId"] = status.CurrentRequestId,
      ["activeCommands"] = CommandLineMonitor.ActiveCommandNames(),
      ["currentOperationDurationMs"] = status.CurrentOperationStartedAtUnixMs is long startedAt
        ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt)
        : null,
      ["memoryUsageMb"] = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
      ["logFilePath"] = PluginLog.LogFilePath,
      ["fileLoggingHealthy"] = PluginLog.IsFileLoggingHealthy,
      ["fileLoggingError"] = PluginLog.LastFileError,
      ["jobs"] = new Dictionary<string, object?>
      {
        ["total"] = jobs.Total,
        ["running"] = jobs.Running,
        ["completed"] = jobs.Completed,
        ["failed"] = jobs.Failed,
        ["cancelled"] = jobs.Cancelled,
        ["capacity"] = jobs.Capacity,
        ["terminalRetentionMinutes"] = jobs.TerminalRetentionMinutes,
      },
    };

    return Task.FromResult<object?>(response);
  }

  /// <summary>The active drawing's file, units, coordinate system code, unsaved-changes flag and object counts (see <see cref="BuildDrawingInfo"/>).</summary>
  public static Task<object?> GetDrawingInfoAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildDrawingInfo(doc, civilDoc, database, transaction));
  }

  /// <summary>
  /// Everything civil3d_orchestrate needs in one round trip: the drawing info, the object type list and the objects
  /// the user has selected (at most <c>selectedObjectLimit</c>, default 25, clamped to 1-100). Read only.
  /// </summary>
  public static Task<object?> GetProjectContextAsync(JsonObject? parameters)
  {
    var requestedLimit = PluginRuntime.GetOptionalInt(parameters, "selectedObjectLimit") ?? 25;
    var selectedObjectLimit = Math.Clamp(requestedLimit, 1, 100);

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      new Dictionary<string, object?>
      {
        ["drawingInfo"] = BuildDrawingInfo(doc, civilDoc, database, transaction),
        ["objectTypes"] = BuildCivilObjectTypes(),
        ["selectedObjects"] = BuildSelectedCivilObjects(doc, transaction, selectedObjectLimit),
      });
  }

  /// <summary>
  /// Drawing settings: coordinate system code, its category and datum, the current layer and the first style of
  /// each kind (surface, alignment, profile, pipe network; corridor is always null). Read only.
  /// </summary>
  // NOTE: "scaleFactor" is the DIMSCALE system variable (dimension scale), not the coordinate system's grid scale
  // factor, and "coordinateZone" is the coordinate system's category; "elevationReference" is always null.
  public static Task<object?> GetDrawingSettingsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var currentLayer = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, database.Clayer, OpenMode.ForRead);
      var coords = ReadCoordinateSystemFields(civilDoc);

      return new Dictionary<string, object?>
      {
        ["coordinateSystem"] = coords.code,
        ["coordinateZone"] = coords.zone,
        ["datum"] = coords.datum,
        ["scaleFactor"] = Convert.ToDouble(App.GetSystemVariable("DIMSCALE") ?? 1d),
        ["elevationReference"] = coords.verticalDatum,
        ["defaultLayer"] = currentLayer.Name,
        ["defaultStyles"] = new Dictionary<string, object?>
        {
          ["surface"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.SurfaceStyles, transaction),
          ["alignment"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.AlignmentStyles, transaction),
          ["profile"] = LookupUtils.GetFirstStyleName(civilDoc.Styles.ProfileStyles, transaction),
          ["corridor"] = null,
          ["pipeNetwork"] = PipeNetworkCommands.GetFirstPipeNetworkStyleName(civilDoc, transaction),
        },
      };
    });
  }

  /// <summary>
  /// Saves the active drawing to its own file, or with <c>saveAs</c> to a new <c>.dwg</c> path inside the allowed
  /// export folders (an existing file only with <c>overwrite: true</c>). A drawing never saved needs <c>saveAs</c>
  /// (CIVIL3D.INVALID_INPUT otherwise). Saves in the current DWG version; the approval policy treats it as a write
  /// that needs approval.
  /// </summary>
  public static Task<object?> SaveDrawingAsync(JsonObject? parameters)
  {
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var saveAs = PluginRuntime.GetOptionalString(parameters, "saveAs");
      var targetPath = string.IsNullOrWhiteSpace(saveAs) ? database.Filename : saveAs;
      if (string.IsNullOrWhiteSpace(targetPath))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "saveDrawing requires 'saveAs' when the drawing has not been saved yet.");
      }

      // Saving the active drawing is not an export conflict: its current file
      // necessarily exists. Boundary/overwrite checks apply only to Save As.
      targetPath = string.IsNullOrWhiteSpace(saveAs)
        ? Path.GetFullPath(targetPath)
        : FileBoundary.ResolveExportPath(targetPath, overwrite, ".dwg");
      database.SaveAs(targetPath, true, DwgVersion.Current, database.SecurityParameters);

      return new Dictionary<string, object?>
      {
        ["saved"] = true,
        ["filePath"] = targetPath,
      };
    });
  }

  /// <summary>
  /// Opens a new drawing tab, from <c>templatePath</c> (a <c>.dwt</c> or <c>.dwg</c> in the folders allowed for
  /// reference files) or from AutoCAD's default template, and makes it the active drawing, so later requests act on
  /// it. Returns its name and (empty until saved) file path.
  /// </summary>
  public static async Task<object?> NewDrawingAsync(JsonObject? parameters)
  {
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    if (!string.IsNullOrWhiteSpace(templatePath))
    {
      templatePath = FileBoundary.ResolveReferencePath(templatePath, ".dwt", ".dwg");
    }

    return await CivilExecution.ExecuteInCommandContextAsync<object?>(() =>
    {
      var createdDocument = string.IsNullOrWhiteSpace(templatePath)
        ? App.DocumentManager.Add(string.Empty)
        : App.DocumentManager.Add(templatePath);

      App.DocumentManager.MdiActiveDocument = createdDocument;
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["drawingName"] = createdDocument.Name,
        ["filePath"] = createdDocument.Database.Filename,
        ["templatePath"] = templatePath,
      });
    });
  }

  /// <summary>Opens a drawing from disk in a new tab, or makes it the active tab when it is open already. It is opened
  /// read-only when asked, or when someone else has it open: AutoCAD keeps the file in use and writes a .dwl lock file
  /// next to it that names who (shown as lockedBy). The path must be inside the allowed folders (CIVIL3D_FILE_ROOTS /
  /// CIVIL3D_IMPORT_ROOTS, default Documents), like every file the plugin reads.</summary>
  public static async Task<object?> OpenDrawingAsync(JsonObject? parameters)
  {
    var readOnly = PluginRuntime.GetOptionalBool(parameters, "readOnly") ?? false;
    // Opened read-only, a drawing is only looked at: one of the user's standards in User Knowledge is fine then.
    var rawPath = PluginRuntime.GetRequiredString(parameters, "path");
    var path = readOnly
      ? FileBoundary.ResolveReferencePath(rawPath, ".dwg", ".dxf", ".dwt")
      : FileBoundary.ResolveImportPath(rawPath, ".dwg", ".dxf", ".dwt");
    return await CivilExecution.ExecuteInCommandContextAsync<object?>(() =>
    {
      var manager = App.DocumentManager;
      foreach (Document open in manager)
      {
        if (string.Equals(open.Name, path, StringComparison.OrdinalIgnoreCase))
        {
          manager.MdiActiveDocument = open;
          return Task.FromResult<object?>(new Dictionary<string, object?>
          {
            ["drawingName"] = Path.GetFileName(open.Name),
            ["filePath"] = open.Name,
            ["alreadyOpen"] = true,
            ["readOnly"] = open.IsReadOnly,
          });
        }
      }

      string? lockedBy = null;
      if (!readOnly && InUseElsewhere(path, out lockedBy))
      {
        readOnly = true;
      }

      var document = manager.Open(path, readOnly);
      manager.MdiActiveDocument = document;
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["drawingName"] = Path.GetFileName(document.Name),
        ["filePath"] = document.Name,
        ["alreadyOpen"] = false,
        ["readOnly"] = readOnly,
        ["lockedBy"] = lockedBy,
      });
    });
  }

  /// <summary>True when another program (another person's AutoCAD) has the drawing open for writing; lockedBy is the
  /// user and computer named in its .dwl lock file, when there is one.</summary>
  private static bool InUseElsewhere(string path, out string? lockedBy)
  {
    lockedBy = null;
    try
    {
      // Read access that refuses to share writing: fails while another program (AutoCAD) has it open for writing.
      using var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      return false;
    }
    catch (IOException)
    {
      var dwl = Path.ChangeExtension(path, ".dwl");
      try
      {
        if (File.Exists(dwl))
        {
          var lines = File.ReadAllLines(dwl).Select(line => line.Trim()).Where(line => line.Length > 0).Take(2).ToArray();
          lockedBy = lines.Length > 0 ? string.Join(" on ", lines) : null;
        }
      }
      catch (IOException)
      {
      }

      return true;
    }
    catch (UnauthorizedAccessException)
    {
      return true;
    }
  }

  /// <summary>Drawings open in this Civil 3D window, and which one is active.</summary>
  public static Task<object?> ListOpenDrawingsAsync()
  {
    return CivilExecution.ExecuteReadOnlyAsync<object?>(() => Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["drawings"] = ReferenceDrawing.ListOpen().Select(info => new Dictionary<string, object?>
      {
        ["name"] = info.Name,
        ["path"] = info.Path,
        ["active"] = info.Active,
      }).ToList(),
    }));
  }

  /// <summary>Makes another open drawing the active tab (by file name, with or without .dwg, or full path).</summary>
  public static Task<object?> ActivateDrawingAsync(JsonObject? parameters)
  {
    var wanted = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    return CivilExecution.ExecuteReadOnlyAsync<object?>(() =>
    {
      var manager = App.DocumentManager;
      var matches = new List<Document>();
      foreach (Document document in manager)
      {
        var name = Path.GetFileName(document.Name);
        if (string.Equals(document.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(wanted), StringComparison.OrdinalIgnoreCase))
        {
          matches.Add(document);
        }
      }

      if (matches.Count != 1)
      {
        var open = string.Join(", ", ReferenceDrawing.ListOpen().Select(info => info.Name));
        throw new JsonRpcDispatchException(
          matches.Count == 0 ? "CIVIL3D.OBJECT_NOT_FOUND" : "CIVIL3D.INVALID_INPUT",
          (matches.Count == 0 ? $"No open drawing named '{wanted}'." : $"'{wanted}' matches {matches.Count} open drawings; use the full path.") +
          $" Open drawings: {open}.");
      }

      var target = matches[0];
      var previous = manager.MdiActiveDocument;
      if (target != previous)
      {
        if (!manager.DocumentActivationEnabled)
        {
          throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", "Civil 3D isn't allowing drawing switches right now (a command or dialog may be active). Finish it and try again.");
        }

        manager.MdiActiveDocument = target;
        if (manager.MdiActiveDocument != target)
        {
          throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", $"Civil 3D didn't switch to '{Path.GetFileName(target.Name)}'. Finish any active command and try again.");
        }
      }

      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["active"] = Path.GetFileName(target.Name),
        ["path"] = target.Name,
        ["previous"] = previous == null ? null : Path.GetFileName(previous.Name),
        ["switched"] = target != previous,
      });
    });
  }

  /// <summary>
  /// Undoes the last <c>steps</c> (default 1) undo steps of the active drawing by sending <c>UNDO n</c> to the command
  /// line. Refuses with CIVIL3D.CONFLICT when the user has run commands since the AI's last change (they would be
  /// undone first), unless <c>force</c> is true because the user agreed. The command is queued, so it runs just after
  /// this answer is sent rather than before it.
  /// </summary>
  public static Task<object?> UndoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    var force = parameters?["force"]?.GetValue<bool>() ?? false;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // UNDO takes back the most recent steps, whoever made them. If the user has worked in the
      // drawing since the AI's last change, undoing would revert the user's work instead.
      var userWork = CommandLineMonitor.UserCommandsSinceLastAiChange(Path.GetFileName(doc.Name));
      if (userWork.Count > 0 && !force)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT",
          $"Not undoing: the user ran {string.Join(", ", userWork)} after the AI's last change, so UNDO would undo the user's work first. " +
          "Ask the user, or pass force=true if they agree.");
      }

      doc.SendStringToExecute($"_.UNDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "undo",
      };
    });
  }

  /// <summary>Redoes <c>steps</c> (default 1) undone steps by queuing <c>REDO n</c> on the command line, like undo.</summary>
  public static Task<object?> RedoDrawingAsync(JsonObject? parameters)
  {
    var steps = PluginRuntime.GetOptionalInt(parameters, "steps") ?? 1;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      doc.SendStringToExecute($"_.REDO {steps} ", true, false, false);
      return new Dictionary<string, object?>
      {
        ["steps"] = steps,
        ["action"] = "redo",
      };
    });
  }

  /// <summary>The fixed list of Civil 3D object types the tools know about; it doesn't look at the drawing.</summary>
  public static Task<object?> ListCivilObjectTypesAsync()
  {
    return Task.FromResult<object?>(BuildCivilObjectTypes());
  }

  /// <summary>
  /// The objects in the user's current (pickfirst) selection, at most <c>limit</c> (default 100): handle, .NET type
  /// name, name and description. An empty list when nothing is selected. Read only; the selection is left as it is.
  /// </summary>
  public static Task<object?> GetSelectedCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      BuildSelectedCivilObjects(doc, transaction, limit));
  }

  /// <summary>
  /// The getDrawingInfo answer: file name and path, coordinate system code, linear and angular units, whether there
  /// are unsaved changes (DBMOD) and counts of surfaces, alignments, profiles, corridors, pipe networks, COGO points
  /// and parcels. <c>projectName</c> is always null.
  /// </summary>
  private static Dictionary<string, object?> BuildDrawingInfo(
    Document doc,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction)
  {
    var unsavedChanges = Convert.ToInt32(App.GetSystemVariable("DBMOD") ?? 0) != 0;
    var angularUnits = CivilObjectUtils.AngularUnits(Convert.ToInt16(App.GetSystemVariable("AUNITS") ?? 0));
    var coordinateSystemCode = ReadCoordinateSystemCode(civilDoc);

    return new Dictionary<string, object?>
    {
      ["fileName"] = Path.GetFileName(database.Filename),
      ["filePath"] = database.Filename,
      ["coordinateSystem"] = coordinateSystemCode,
      ["linearUnits"] = CivilObjectUtils.LinearUnits(database),
      ["angularUnits"] = angularUnits,
      ["unsavedChanges"] = unsavedChanges,
      ["objectCounts"] = new Dictionary<string, object?>
      {
        ["surfaces"] = civilDoc.GetSurfaceIds().Count,
        ["alignments"] = civilDoc.GetAlignmentIds().Count,
        ["profiles"] = CountProfiles(civilDoc, transaction),
        ["corridors"] = civilDoc.CorridorCollection.Count,
        ["pipeNetworks"] = PipeNetworkCommands.CountPipeNetworks(civilDoc),
        ["points"] = civilDoc.CogoPoints.Count,
        ["parcels"] = CountParcels(civilDoc, transaction),
      },
      ["drawingName"] = doc.Name,
      ["projectName"] = null,
      ["units"] = CivilObjectUtils.LinearUnits(database),
    };
  }

  private static object[] BuildCivilObjectTypes() =>
  [
    "Alignment",
    "Surface",
    "Profile",
    "Corridor",
    "CogoPoint",
    "SampleLineGroup",
    "FeatureLine",
    "Parcel",
    "PipeNetwork",
  ];

  private static List<Dictionary<string, object?>> BuildSelectedCivilObjects(
    Document doc,
    Transaction transaction,
    int limit)
  {
    var result = new List<Dictionary<string, object?>>();
    var selection = doc.Editor.SelectImplied();
    if (selection.Status != PromptStatus.OK || selection.Value == null)
    {
      return result;
    }

    foreach (var objectId in selection.Value.GetObjectIds().Take(limit))
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      if (dbObject == null)
      {
        continue;
      }

      result.Add(new Dictionary<string, object?>
      {
        ["handle"] = dbObject.Handle.ToString(),
        ["objectType"] = dbObject.GetType().Name,
        ["name"] = CivilObjectUtils.GetName(dbObject),
        ["description"] = CivilObjectUtils.GetStringProperty(dbObject, "Description"),
      });
    }

    return result;
  }

  private static int CountProfiles(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;
    foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
    {
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
      count += alignment.GetProfileIds().Count;
    }

    return count;
  }

  private static int CountParcels(CivilDocument civilDoc, Transaction transaction)
  {
    var count = 0;

    foreach (ObjectId siteId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
      count += site.GetParcelIds().Count;
    }

    return count;
  }

  // Single-field coordinate system code (for getDrawingInfo).
  private static string? ReadCoordinateSystemCode(CivilDocument civilDoc)
  {
    return ReadCoordinateSystemFields(civilDoc).code;
  }

  /// <summary>
  /// The drawing's coordinate system code from its unit and zone settings, with the category and datum Civil 3D's
  /// coordinate system library has for it; all null when the drawing has none. The vertical datum isn't read.
  /// </summary>
  private static (string? code, string? zone, string? datum, string? verticalDatum) ReadCoordinateSystemFields(CivilDocument civilDoc)
  {
    var unitZone = civilDoc.Settings.DrawingSettings.UnitZoneSettings;
    var code = unitZone.CoordinateSystemCode;
    if (string.IsNullOrWhiteSpace(code))
    {
      return (null, null, null, null);
    }

    var coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
    return (code, coordinateSystem.Category, coordinateSystem.Datum, null);
  }
}
