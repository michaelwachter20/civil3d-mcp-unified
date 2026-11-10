// Data shortcuts: list, publish, reference (by published name or by source drawing path), synchronize, promote.
// JSON-RPC methods (CommandDispatcher): listDataShortcuts, createDataShortcut, referenceDataShortcut,
// createDataShortcutReference, syncDataShortcuts, promoteDataShortcut. Called by src/tools/domains/projectDomain.ts
// (civil3d_project) and reused by WorkflowCommands' data shortcut workflows.
// listDataShortcuts is a CivilExecution.ReadAsync, but it reloads the current shortcut project (sets the working
// folder) as a side effect outside the drawing. Publish and reference are CivilExecution.WriteAsync (one undo step);
// publishing writes Civil 3D's shortcut files into the project folder through DataShortcutManager. Sync only queues
// SynchronizeReferences, which runs after the request. Source drawing paths go through FileBoundary.ResolveReferencePath.
// Reference-state properties that differ between releases are read late-bound through CivilObjectUtils.
using System.Collections;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using Shortcuts = Autodesk.Civil.DataShortcuts.DataShortcuts;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind civil3d_project (data shortcuts; src/tools/domains/projectDomain.ts) and the data shortcut
/// workflows. Publishing and referencing go through Civil 3D's DataShortcutManager, so they finish here with no window:
/// a drawing's objects are selected and saved into the current project, and a published item becomes a reference with
/// CreateReference. Sync launches the SynchronizeReferences command. Promote answers "manual_step_required" (or
/// "unsupported" for corridors) with what the user must do in Toolspace; pass that on instead of reporting it as done.
/// </summary>
public static class DataShortcutCommands
{
  /// <summary>Surfaces, alignments and other shortcut-able objects split into incoming references (data shortcuts
  /// already in this drawing) and exportable objects (this drawing's own, which could be published), plus the working
  /// folder, current project and what that project has published. listDataShortcuts; no parameters. Doesn't change
  /// the drawing (it does reload the current shortcut project).</summary>
  public static Task<object?> ListDataShortcutsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var incoming = new List<Dictionary<string, object?>>();
      var exportable = new List<Dictionary<string, object?>>();

      foreach (ObjectId objectId in civilDoc.GetSurfaceIds())
      {
        var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, objectId, OpenMode.ForRead);
        RegisterShortcutCandidate(surface, "surface", incoming, exportable);
      }

      foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
        RegisterShortcutCandidate(alignment, "alignment", incoming, exportable);

        foreach (ObjectId profileId in alignment.GetProfileIds())
        {
          var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);
          RegisterShortcutCandidate(profile, "profile", incoming, exportable);
        }
      }

      foreach (var objectId in GetPipeNetworkIds(civilDoc))
      {
        var network = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, OpenMode.ForRead);
        RegisterShortcutCandidate(network, "pipe_network", incoming, exportable);
      }

      foreach (var objectId in GetPressureNetworkIds(civilDoc))
      {
        var network = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, OpenMode.ForRead);
        RegisterShortcutCandidate(network, "pressure_network", incoming, exportable);
      }

      foreach (ObjectId objectId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, objectId, OpenMode.ForRead);
        RegisterShortcutCandidate(corridor, "corridor", incoming, exportable);
      }

      var (current, _) = Projects();
      return new Dictionary<string, object?>
      {
        ["incoming"] = incoming,
        ["exportable"] = exportable,
        ["workingFolder"] = Shortcuts.GetWorkingFolder(),
        ["currentProject"] = current,
        ["published"] = PublishedItems(),
      };
    });
  }

  /// <summary>The current shortcut project, and the working folder's other ones.</summary>
  private static (string Current, List<string> Others) Projects()
  {
    string current = string.Empty;
    var others = new List<string>();
    Shortcuts.GetAllProjectFolders(ref current, ref others);
    return (current ?? string.Empty, others ?? new List<string>());
  }

  /// <summary>
  /// Makes a project current: <paramref name="project"/> is a name in the working folder, or a full path, which also
  /// moves the working folder to the project's parent. <paramref name="create"/> makes one that isn't there.
  /// Without <paramref name="create"/>, an unknown project is CIVIL3D.OBJECT_NOT_FOUND listing the ones there are.
  /// </summary>
  private static void UseProject(string? project, bool create)
  {
    if (string.IsNullOrWhiteSpace(project))
    {
      return;
    }

    var name = project.TrimEnd('\\', '/');
    if (Path.IsPathRooted(name))
    {
      Shortcuts.SetWorkingFolder(Path.GetDirectoryName(name)!);
      name = Path.GetFileName(name);
    }

    var (current, others) = Projects();
    if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    if (others.Contains(name, StringComparer.OrdinalIgnoreCase))
    {
      Shortcuts.SetCurrentProjectFolder(name);
    }
    else if (create)
    {
      Shortcuts.CreateProjectFolder(name, string.Empty, true);
    }
    else
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"No shortcut project '{name}' in '{Shortcuts.GetWorkingFolder()}'. It has: {string.Join(", ", others.Prepend(current).Where(item => item.Length > 0))}.");
    }
  }

  /// <summary>Refuses when no project is current (CIVIL3D.INVALID_INPUT), otherwise loads it. Civil 3D's own commands
  /// answer a missing project with a window that stops every request.</summary>
  internal static void RequireShortcutFolder()
  {
    var (current, others) = Projects();
    if (current.Length > 0)
    {
      LoadProject(current);
      return;
    }

    var choices = others.Count > 0 ? $" Its projects are: {string.Join(", ", others)}." : string.Empty;
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"Nothing was started: no shortcut project is current in '{Shortcuts.GetWorkingFolder()}'.{choices} Name one with projectFolder; publishing makes it if it's new.");
  }

  /// <summary>
  /// Civil 3D remembers the project across sessions but doesn't load it after a start: the manager then sees nothing,
  /// and publishing fails with "Unknown error". Setting the project doesn't load it; setting the working folder does.
  /// </summary>
  private static void LoadProject(string current)
  {
    Shortcuts.SetWorkingFolder(Shortcuts.GetWorkingFolder());
    Shortcuts.SetCurrentProjectFolder(current);
  }

  /// <summary>What the current project has published. The manager throws when it has nothing yet, rather than giving 0.</summary>
  private static List<Dictionary<string, object?>> PublishedItems()
  {
    var (current, _) = Projects();
    var items = new List<Dictionary<string, object?>>();
    if (current.Length == 0)
    {
      return items;
    }

    LoadProject(current);
    var valid = false;
    var manager = Shortcuts.CreateDataShortcutManager(ref valid);
    try
    {
      for (var index = 0; index < PublishedCount(manager); index++)
      {
        var item = manager.GetPublishedItemAt(index);
        items.Add(new Dictionary<string, object?> { ["name"] = item.Name, ["type"] = item.DSEntityType.ToString(), ["broken"] = item.IsBroken });
      }
    }
    finally
    {
      manager.Dispose();
    }

    return items;
  }

  /// <summary>The manager's published item count, 0 when it throws because the project has none yet.</summary>
  private static int PublishedCount(Shortcuts.DataShortcutManager manager)
  {
    try
    {
      return manager.GetPublishedItemsCount();
    }
    catch (InvalidOperationException)
    {
      return 0;
    }
  }

  /// <summary>Whether Civil 3D's entity type is the tools' <c>pipe_network</c>, <c>surface</c> and so on.</summary>
  private static bool IsType(object entityType, string objectType)
  {
    var entity = entityType.ToString()?.ToLowerInvariant() ?? string.Empty;
    return objectType.ToLowerInvariant() switch
    {
      "pressure_network" => entity.StartsWith("pressure", StringComparison.Ordinal),
      "section_view_group" => entity.Contains("sampleline", StringComparison.Ordinal),
      var other => entity == other.Replace("_", string.Empty),
    };
  }

  /// <summary>
  /// Publishes object <c>objectName</c> to the current project, or to <c>projectFolder</c>, which is made if it's new.
  /// Civil 3D also publishes what the object depends on: an alignment brings its profiles and sample lines.
  /// createDataShortcut; reads <c>objectType</c>, <c>objectName</c> (required) and <c>projectFolder</c> (optional).
  /// Writes shortcut files to the project; status "published" or "already_published". Throws CIVIL3D.INVALID_INPUT
  /// for an unsaved drawing or no current project, CIVIL3D.OBJECT_NOT_FOUND when the drawing has no such exportable
  /// object (message lists what it has).
  /// </summary>
  public static Task<object?> CreateDataShortcutAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");
    var projectFolder = PluginRuntime.GetOptionalString(parameters, "projectFolder");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (!Path.IsPathRooted(doc.Name))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Save the drawing first: a data shortcut points at a saved file.");
      }

      UseProject(projectFolder, create: true);
      RequireShortcutFolder();

      var valid = false;
      var manager = Shortcuts.CreateDataShortcutManager(ref valid);
      try
      {
        var found = -1;
        var names = new List<string>();
        for (var index = 0; index < manager.GetExportableItemsCount(); index++)
        {
          var item = manager.GetExportableItemAt(index);
          names.Add(item.Name);
          if (found < 0 && IsType(item.DSEntityType, objectType) && string.Equals(item.Name, objectName, StringComparison.OrdinalIgnoreCase))
          {
            found = index;
          }
        }

        if (found < 0)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"No {objectType} '{objectName}' can be published from this drawing. It has: {string.Join(", ", names.Take(30))}.");
        }

        var already = manager.IsItemAtIndexAlreadyPublished(found);
        if (!already)
        {
          manager.SetSelectItemAtIndex(found, true);
          Shortcuts.SaveDataShortcutManager(ref manager);
        }

        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType,
          ["objectName"] = objectName,
          ["project"] = Projects().Current,
          ["workingFolder"] = Shortcuts.GetWorkingFolder(),
          ["status"] = already ? "already_published" : "published",
        };
      }
      finally
      {
        manager.Dispose();
      }
    });
  }

  /// <summary>
  /// referenceDataShortcut: makes published item <c>shortcutName</c> of type <c>shortcutType</c> in project
  /// <c>projectFolder</c> (all required) a reference in the active drawing, optionally on <c>layer</c>. Changes the
  /// drawing; returns the new objects' handles. Throws CIVIL3D.OBJECT_NOT_FOUND for an unknown project or item,
  /// CIVIL3D.CONFLICT for a broken shortcut, CIVIL3D.INVALID_INPUT when no project is current.
  /// </summary>
  public static Task<object?> ReferenceDataShortcutAsync(JsonObject? parameters)
  {
    var projectFolder = PluginRuntime.GetRequiredString(parameters, "projectFolder");
    var shortcutName = PluginRuntime.GetRequiredString(parameters, "shortcutName");
    var shortcutType = PluginRuntime.GetRequiredString(parameters, "shortcutType");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      UseProject(projectFolder, create: false);
      RequireShortcutFolder();

      var valid = false;
      var manager = Shortcuts.CreateDataShortcutManager(ref valid);
      try
      {
        var found = -1;
        var names = new List<string>();
        for (var index = 0; index < PublishedCount(manager); index++)
        {
          var item = manager.GetPublishedItemAt(index);
          names.Add(item.Name);
          if (found < 0 && IsType(item.DSEntityType, shortcutType) && string.Equals(item.Name, shortcutName, StringComparison.OrdinalIgnoreCase))
          {
            found = index;
          }
        }

        if (found < 0)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"No {shortcutType} '{shortcutName}' is published in this project. It has: {string.Join(", ", names)}.");
        }

        if (manager.GetPublishedItemAt(found).IsBroken)
        {
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Shortcut '{shortcutName}' is broken: its source can't be found, so nothing was referenced.");
        }

        var handles = new List<string>();
        foreach (ObjectId id in manager.CreateReference(found, database))
        {
          var created = transaction.GetObject(id, OpenMode.ForWrite);
          handles.Add(CivilObjectUtils.GetHandle(created));
          if (!string.IsNullOrWhiteSpace(layer) && created is Autodesk.AutoCAD.DatabaseServices.Entity entity)
          {
            entity.Layer = layer;
          }
        }

        return new Dictionary<string, object?>
        {
          ["projectFolder"] = projectFolder,
          ["shortcutName"] = shortcutName,
          ["shortcutType"] = shortcutType,
          ["layer"] = layer,
          ["handles"] = handles,
          ["status"] = "referenced",
        };
      }
      finally
      {
        manager.Dispose();
      }
    });
  }

  /// <summary>
  /// Lists the references that look out of date and, unless <c>dryRun</c>, launches SynchronizeReferences. The command
  /// is queued with SendStringToExecute, so it runs after this request returns (status "initiated"), and it covers
  /// every reference in the drawing: <c>shortcutNames</c> and <c>projectFolder</c> only filter what is reported.
  /// Nothing stale answers "up_to_date" without sending anything. The command itself runs outside the host queue and
  /// changes the drawing on its own. CIVIL3D.COMMAND_FAILED when the command can't be sent.
  /// </summary>
  public static async Task<object?> SyncDataShortcutsAsync(JsonObject? parameters)
  {
    var projectFolder = PluginRuntime.GetOptionalString(parameters, "projectFolder");
    var shortcutNames = ParseStringArray(parameters, "shortcutNames");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    var inventory = await RequireDictionary(ListDataShortcutsAsync(), "listDataShortcuts");
    var incoming = inventory["incoming"] as List<Dictionary<string, object?>> ?? new List<Dictionary<string, object?>>();
    var staleReferences = incoming
      .Where(item => !(item["isSynced"] as bool? ?? false))
      .Where(item => shortcutNames.Count == 0 || shortcutNames.Contains(item["objectName"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
      .ToList();

    if (dryRun)
    {
      return new Dictionary<string, object?>
      {
        ["projectFolder"] = projectFolder,
        ["shortcutNames"] = shortcutNames,
        ["dryRun"] = true,
        ["staleReferenceCount"] = staleReferences.Count,
        ["wouldSynchronize"] = staleReferences,
      };
    }

    if (incoming.Count == 0 || staleReferences.Count == 0)
    {
      return new Dictionary<string, object?>
      {
        ["projectFolder"] = projectFolder,
        ["shortcutNames"] = shortcutNames,
        ["dryRun"] = false,
        ["staleReferenceCount"] = 0,
        ["status"] = "up_to_date",
      };
    }

    return await CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      SendCommand(doc, "SynchronizeReferences");

      var notes = new List<string>
      {
        "The SynchronizeReferences command was launched in Civil 3D.",
      };

      if (!string.IsNullOrWhiteSpace(projectFolder))
      {
        notes.Add($"The requested project folder was '{projectFolder}'. Civil 3D sync scope still depends on the active Data Shortcuts project.");
      }

      if (shortcutNames.Count > 0)
      {
        notes.Add("Civil 3D's manual synchronize command applies to the active drawing's references; shortcutNames were treated as a filter for reporting, not as a guaranteed command-line scope.");
      }

      return new Dictionary<string, object?>
      {
        ["projectFolder"] = projectFolder,
        ["shortcutNames"] = shortcutNames,
        ["dryRun"] = false,
        ["staleReferenceCount"] = staleReferences.Count,
        ["targetReferences"] = staleReferences,
        ["status"] = "initiated",
        ["dialogRequired"] = false,
        ["command"] = "SynchronizeReferences",
        ["notes"] = notes,
      };
    });
  }

  /// <summary>
  /// References an object of a saved drawing by that file's path. The object must already be published to the current
  /// project, because Civil 3D looks it up there. A second call changes nothing and answers with the same reference.
  /// createDataShortcutReference; reads <c>sourceFilePath</c> (.dwg, through FileBoundary.ResolveReferencePath),
  /// <c>objectName</c> and <c>objectType</c> (all required). Changes the drawing. Throws CIVIL3D.INVALID_INPUT for the
  /// drawing itself, an unknown type, no current project or an unpublished object, and FileBoundary's path errors.
  /// </summary>
  public static Task<object?> CreateDataShortcutReferenceAsync(JsonObject? parameters)
  {
    var sourceFilePath = FileBoundary.ResolveReferencePath(PluginRuntime.GetRequiredString(parameters, "sourceFilePath"), ".dwg");
    var objectName = PluginRuntime.GetRequiredString(parameters, "objectName");
    var objectType = PluginRuntime.GetRequiredString(parameters, "objectType");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (SheetSetCom.SameFile(doc.Name, sourceFilePath))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A drawing can't reference itself: open the drawing that needs the reference.");
      }

      var types = Enum.GetValues<Autodesk.Civil.DataShortcuts.DataShortcutEntityType>().Where(type => IsType(type, objectType)).ToList();
      if (types.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{objectType}' isn't a kind of object data shortcuts carry.");
      }

      RequireShortcutFolder();
      if (!PublishedItems().Any(item => string.Equals((string?)item["name"], objectName, StringComparison.OrdinalIgnoreCase)))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{objectName}' isn't published to project '{Projects().Current}': publish it from its drawing with data_shortcut_create first.");
      }

      var made = Shortcuts.CreateReference(database, sourceFilePath, objectName, types[0]);
      return new Dictionary<string, object?>
      {
        ["status"] = "referenced",
        ["sourceFilePath"] = sourceFilePath,
        ["objectName"] = objectName,
        ["objectType"] = objectType,
        ["created"] = made.Cast<ObjectId>().Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead))).ToList(),
      };
    });
  }

  /// <summary>Civil 3D has no call for this: corridors can't be promoted at all, and other types answer
  /// "manual_step_required" (promote from Toolspace). promoteDataShortcut reads <c>shortcutName</c>,
  /// <c>shortcutType</c> (required) and <c>newName</c>, and never touches the drawing.</summary>
  public static Task<object?> PromoteDataShortcutAsync(JsonObject? parameters)
  {
    var shortcutName = PluginRuntime.GetRequiredString(parameters, "shortcutName");
    var shortcutType = PluginRuntime.GetRequiredString(parameters, "shortcutType");
    var newName = PluginRuntime.GetOptionalString(parameters, "newName");

    if (shortcutType.Equals("corridor", StringComparison.OrdinalIgnoreCase))
    {
      return Task.FromResult<object?>(new Dictionary<string, object?>
      {
        ["shortcutName"] = shortcutName,
        ["shortcutType"] = shortcutType,
        ["newName"] = newName,
        ["status"] = "unsupported",
        ["notes"] = new List<string>
        {
          "Corridor references cannot be promoted in Civil 3D.",
        },
      });
    }

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["shortcutName"] = shortcutName,
      ["shortcutType"] = shortcutType,
      ["newName"] = newName,
      ["status"] = "manual_step_required",
      ["dialogRequired"] = true,
      ["notes"] = new List<string>
      {
        "Promotion currently requires selecting the reference in Toolspace and using Promote.",
        "The native plugin reports the request and current limits rather than pretending promotion was completed automatically.",
      },
    });
  }

  /// <summary>
  /// Files an object as incoming (a data shortcut reference of a supported type, with source path, sync and validity
  /// state) or exportable (with whether it is already published). Reference state is read late-bound under several
  /// release-dependent property names; an unreadable one counts as synced and valid.
  /// </summary>
  private static void RegisterShortcutCandidate(AcDbObject dbObject, string objectType, List<Dictionary<string, object?>> incoming, List<Dictionary<string, object?>> exportable)
  {
    var isReference = GetAnyBool(dbObject, "IsReferenceObject", "IsReference", "IsDrefObject") ?? false;
    var name = CivilObjectUtils.GetName(dbObject) ?? CivilObjectUtils.GetHandle(dbObject);

    if (isReference && IsIncomingSupportedType(objectType))
    {
      var sourceFilePath = GetAnyString(dbObject, "SourceFilePath", "ReferencePath", "SourceDrawingPath", "SourceDrawing", "ShortcutSourceFile") ?? string.Empty;
      var isValid = GetAnyBool(dbObject, "IsReferenceValid", "ReferenceIsValid", "IsValid") ?? true;
      var isStale = GetAnyBool(dbObject, "IsReferenceStale", "ReferenceIsStale", "IsOutOfDate", "IsReferenceOutOfDate") ?? false;

      incoming.Add(new Dictionary<string, object?>
      {
        ["objectName"] = name,
        ["objectType"] = objectType,
        ["sourceFilePath"] = sourceFilePath,
        ["isSynced"] = !isStale,
        ["isValid"] = isValid,
      });
      return;
    }

    var isExported = GetAnyBool(dbObject, "IsExportedToDataShortcuts", "IsPublishedAsDataShortcut", "IsSharedAsShortcut") ?? false;
    exportable.Add(new Dictionary<string, object?>
    {
      ["objectName"] = name,
      ["objectType"] = objectType,
      ["isExported"] = isExported,
    });
  }

  private static bool IsIncomingSupportedType(string objectType)
  {
    return objectType is "surface" or "alignment" or "profile" or "pipe_network";
  }

  private static IEnumerable<ObjectId> GetPipeNetworkIds(CivilDocument civilDoc)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId != ObjectId.Null)
      {
        yield return objectId;
      }
    }
  }

  private static IEnumerable<ObjectId> GetPressureNetworkIds(CivilDocument civilDoc)
  {
    foreach (ObjectId objectId in civilDoc.GetPressurePipeNetworkIds())
    {
      if (objectId != ObjectId.Null)
      {
        yield return objectId;
      }
    }
  }

  /// <summary>The first of <paramref name="propertyNames"/> that reads as a bool (late-bound), else null.</summary>
  private static bool? GetAnyBool(object value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetBoolProperty(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  /// <summary>The first of <paramref name="propertyNames"/> that reads as non-empty text (late-bound), else null.</summary>
  private static string? GetAnyString(object value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(propertyValue))
      {
        return propertyValue;
      }
    }

    return null;
  }

  private static List<string> ParseStringArray(JsonObject? parameters, string name)
  {
    if (PluginRuntime.GetParameter(parameters, name) is not JsonArray array)
    {
      return new List<string>();
    }

    return array
      .Select(item => item?.GetValue<string>())
      .Where(value => !string.IsNullOrWhiteSpace(value))
      .Cast<string>()
      .ToList();
  }

  /// <summary>
  /// Queues <paramref name="commandName"/> on the document's command line (SendStringToExecute); it runs after the
  /// current request. CIVIL3D.COMMAND_FAILED when queuing throws.
  /// </summary>
  private static void SendCommand(Document doc, string commandName)
  {
    try
    {
      doc.SendStringToExecute($"{commandName}\n", true, false, false);
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.COMMAND_FAILED", $"Failed to execute {commandName}: {ex.Message}");
    }
  }

  /// <summary>Awaits a handler's result and requires it to be a dictionary (CIVIL3D.TRANSACTION_FAILED otherwise).</summary>
  private static async Task<Dictionary<string, object?>> RequireDictionary(Task<object?> task, string context)
  {
    var value = await task;
    if (value is Dictionary<string, object?> dictionary)
    {
      return dictionary;
    }

    throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"Expected '{context}' to return an object result.");
  }
}
