// Pressure pipe networks (water, force mains): list, inspect, create, delete, assign a parts list, add pipes,
// fittings and appurtenances, swap a pipe's size, and export a network's parts as JSON data.
// JSON-RPC methods (CommandDispatcher.cs): listPressureNetworks, getPressureNetworkInfo, createPressureNetwork,
// deletePressureNetwork, assignPressurePartsList, setPressureNetworkCover, validatePressureNetwork,
// exportPressureNetwork, connectPressureNetworks, addPressurePipe, getPressurePipeProperties, resizePressurePipe,
// addPressureFitting, getPressureFittingProperties, addPressureAppurtenance. All are the *_pressure_* actions of
// civil3d_pipe (src/tools/domains/pipeDomain.ts), plus civil3d_pressure_* alias tools when aliases are enabled.
// Reads use CivilExecution.ReadAsync (main thread, no undo step); create, delete, assign and add/resize use WriteAsync
// (command context, document lock, one transaction, one undo step). setPressureNetworkCover, validatePressureNetwork
// and connectPressureNetworks always throw CIVIL3D.API_ERROR without touching the drawing. No files are written.
// Late-bound reads go through CivilObjectUtils helpers and Civil3DCompatibility (GetNamedMember); no reflection here.
using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for pressure network MCP tools.
///
/// The writes use the typed API from AeccDbMgd.dll: PressurePipeNetwork (Create, AddLinePipe, AddFitting,
/// AddAppurtenance, PartsListId), PressurePipe.SwapPartSize, and PressurePartList / PressurePartSize to find a part
/// by its description (ignoring case). The reads (names, sizes, points, child ids) are late-bound through CivilObjectUtils and
/// try several member names, so a property a release lacks reads as null rather than failing.
/// Networks, pipes, fittings and appurtenances are found by name, case-insensitively.
/// </summary>
public static class PressureNetworkCommands
{
  // -------------------------------------------------------------------------
  // listPressureNetworks
  // -------------------------------------------------------------------------

  /// <summary>Lists every pressure network: name, handle, pipe, fitting and appurtenance counts, and parts list name.
  /// Takes no parameters. Read-only. Returns <c>{ networks: [...] }</c>.</summary>
  public static Task<object?> ListPressureNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networks = EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(net => ToPressureNetworkSummary(net, transaction))
        .ToList();

      return new Dictionary<string, object?> { ["networks"] = networks };
    });
  }

  // -------------------------------------------------------------------------
  // getPressureNetworkInfo
  // -------------------------------------------------------------------------

  /// <summary>
  /// Details of pressure network <c>name</c> (required): name, handle, parts list, and every pipe, fitting and
  /// appurtenance with its data (see ToPressurePipeData and ToPressureFittingData). Read-only. Throws
  /// CIVIL3D.INVALID_INPUT without <c>name</c> and CIVIL3D.OBJECT_NOT_FOUND for an unknown network.
  /// </summary>
  public static Task<object?> GetPressureNetworkInfoAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);

      var pipes = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes")
        .Select(id => ToPressurePipeData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      var fittings = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings")
        .Select(id => ToPressureFittingData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      var appurtenances = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances")
        .Select(id => ToPressureAppurtenanceData(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead)))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
        ["pipes"] = pipes,
        ["fittings"] = fittings,
        ["appurtenances"] = appurtenances,
      };
    });
  }

  // -------------------------------------------------------------------------
  // createPressureNetwork
  // -------------------------------------------------------------------------

  /// <summary>
  /// Creates pressure network <c>name</c> (required) with pressure parts list <c>partsList</c> (required), and
  /// optionally sets its <c>layer</c>, <c>referenceSurface</c> and <c>referenceAlignment</c> (by name). Changes the
  /// drawing: one WriteAsync transaction, one undo step, so a failure part-way leaves nothing behind. Returns name,
  /// handle, <c>partsList</c> and <c>created</c>. Throws CIVIL3D.INVALID_INPUT for a missing name and
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown parts list, surface or alignment.
  /// </summary>
  public static Task<object?> CreatePressureNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkId = CreatePressureNetwork(database, name);
      var network = CivilObjectUtils.GetRequiredObject<PressurePipeNetwork>(transaction, networkId, OpenMode.ForWrite);

      // Layer
      var layer = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layer))
      {
        network.Layer = layer;
      }

      // Parts list
      var partsListId = FindPressurePartsListId(civilDoc, transaction, partsListName);
      network.PartsListId = partsListId;

      // Reference surface
      var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
      if (!string.IsNullOrWhiteSpace(referenceSurface))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurface!, OpenMode.ForRead);
        network.ReferenceSurfaceId = surface.ObjectId;
      }

      // Reference alignment
      var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, referenceAlignment!);
        network.ReferenceAlignmentId = alignment.ObjectId;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = partsListName,
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // deletePressureNetwork
  // -------------------------------------------------------------------------

  /// <summary>Erases pressure network <c>name</c> (required). Changes the drawing (one undo step). Returns
  /// <c>deleted</c> and <c>name</c>. Throws CIVIL3D.INVALID_INPUT without <c>name</c> and CIVIL3D.OBJECT_NOT_FOUND for
  /// an unknown network.</summary>
  public static Task<object?> DeletePressureNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, name, OpenMode.ForWrite);
      network.Erase();
      return new Dictionary<string, object?> { ["deleted"] = true, ["name"] = name };
    });
  }

  // -------------------------------------------------------------------------
  // assignPressurePartsList
  // -------------------------------------------------------------------------

  /// <summary>Sets the pressure parts list <c>partsList</c> (required) on network <c>networkName</c> (required).
  /// Changes the drawing (one undo step). Returns <c>networkName</c>,
  /// <c>partsList</c> and <c>assigned</c>. Throws CIVIL3D.INVALID_INPUT for a missing name and
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown network or parts list.</summary>
  public static Task<object?> AssignPressurePartsListAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var partsListId = FindPressurePartsListId(civilDoc, transaction, partsListName);
      ((PressurePipeNetwork)network).PartsListId = partsListId;

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["partsList"] = partsListName,
        ["assigned"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setPressureNetworkCover
  // -------------------------------------------------------------------------

  /// <summary>
  /// Not supported: PressurePipeNetwork has no network-level cover setters. Reads <c>networkName</c> and
  /// <c>minCoverDepth</c> (required, CIVIL3D.INVALID_INPUT without them) and <c>maxCoverDepth</c> (optional), then
  /// always throws CIVIL3D.API_ERROR. The drawing isn't touched.
  /// </summary>
  public static Task<object?> SetPressureNetworkCoverAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var minCover = PluginRuntime.GetRequiredDouble(parameters, "minCoverDepth");
    var maxCover = PluginRuntime.GetOptionalDouble(parameters, "maxCoverDepth");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Civil 3D PressurePipeNetwork does not expose network-level minimum or maximum cover setters. No pipe elevations were changed; apply reviewed cover criteria through a pipe-run workflow.");
  }

  // -------------------------------------------------------------------------
  // validatePressureNetwork
  // -------------------------------------------------------------------------

  /// <summary>Not supported: a validation needs explicit cover criteria and typed connection checks, and a guessed
  /// "valid" would mislead. Reads <c>networkName</c> (required) and always throws CIVIL3D.API_ERROR.</summary>
  public static Task<object?> ValidatePressureNetworkAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "networkName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Pressure-network validation requires explicit cover criteria and typed connection checks. The previous implementation treated missing criteria as zero and could return a false valid result; no validation result was produced.");
  }

  // -------------------------------------------------------------------------
  // exportPressureNetwork
  // -------------------------------------------------------------------------

  /// <summary>
  /// Returns network <c>networkName</c> (required) as JSON data: handle, parts list, pipes, fittings, appurtenances
  /// and an <c>exportedAt</c> UTC time. <c>includeCoordinates</c> (optional, default true) false drops the points and
  /// positions. No file is written; read-only. Throws CIVIL3D.INVALID_INPUT without <c>networkName</c> or for an
  /// <c>includeCoordinates</c> that isn't true or false, and CIVIL3D.OBJECT_NOT_FOUND for an unknown network.
  /// </summary>
  public static Task<object?> ExportPressureNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var includeCoordinates = PluginRuntime.GetOptionalBool(parameters, "includeCoordinates") ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);

      var pipes = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes")
        .Select(id => ExportPressurePipe(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      var fittings = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings")
        .Select(id => ExportPressureFitting(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      var appurtenances = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances")
        .Select(id => ExportPressureAppurtenance(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead), includeCoordinates))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
        ["pipes"] = pipes,
        ["fittings"] = fittings,
        ["appurtenances"] = appurtenances,
        ["exportedAt"] = DateTime.UtcNow.ToString("O"),
      };
    });
  }

  // -------------------------------------------------------------------------
  // connectPressureNetworks
  // -------------------------------------------------------------------------

  /// <summary>Not supported (see the error message: AddPipe would list a part in both networks). Reads
  /// <c>targetNetwork</c> and <c>sourceNetwork</c> (both required) and always throws CIVIL3D.API_ERROR; neither network
  /// is changed.</summary>
  public static Task<object?> ConnectPressureNetworksAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "targetNetwork");
    _ = PluginRuntime.GetRequiredString(parameters, "sourceNetwork");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Pressure networks can't be merged from here: PressurePipeNetwork.AddPipe doesn't move a part, it leaves it listed in both networks while it still belongs to the first. Use Civil 3D's Connect command. Neither network was changed.");
  }

  // -------------------------------------------------------------------------
  // addPressurePipe
  // -------------------------------------------------------------------------

  /// <summary>
  /// Adds a straight pressure pipe to network <c>networkName</c> from <c>startPoint</c> to <c>endPoint</c> (both
  /// required <c>{x, y, z}</c> objects), using the pipe part whose description matches <c>partName</c> (required,
  /// ignoring case) in the network's parts list. <c>diameter</c> (optional) is only checked against the part, not applied. Changes the
  /// drawing (one undo step). Returns <c>networkName</c>, the new <c>pipe</c>'s data and <c>added</c>.
  /// Throws CIVIL3D.INVALID_INPUT for a missing name or point, a missing coordinate, equal points, or a part whose
  /// diameter doesn't match <c>diameter</c> (the transaction then isn't committed), and CIVIL3D.OBJECT_NOT_FOUND for
  /// an unknown network or part, or a network without a parts list.
  /// </summary>
  public static Task<object?> AddPressurePipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var startPoint = ReadPoint(parameters, "startPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startPoint is required; an origin point will not be assumed.");
    var endPoint = ReadPoint(parameters, "endPoint")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endPoint is required; a unit-length pipe will not be fabricated.");
    var diameter = PluginRuntime.GetOptionalDouble(parameters, "diameter");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      if (startPoint.IsEqualTo(endPoint))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startPoint and endPoint are the same: a pipe needs a length.");
      }

      var createdId = AddPressurePipeToNetwork(network, transaction, partName, startPoint, endPoint, diameter);
      var pipe = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipe"] = ToPressurePipeData(pipe),
        ["added"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getPressurePipeProperties
  // -------------------------------------------------------------------------

  /// <summary>Data of pipe <c>pipeName</c> in network <c>networkName</c> (both required), as ToPressurePipeData gives
  /// it. Read-only. Throws CIVIL3D.INVALID_INPUT for a missing name and CIVIL3D.OBJECT_NOT_FOUND for an unknown network
  /// or pipe.</summary>
  public static Task<object?> GetPressurePipePropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPressureComponentByName(network, transaction, pipeName, "GetPipeIds", "PipeIds", "Pipes");
      return ToPressurePipeData(pipe);
    });
  }

  // -------------------------------------------------------------------------
  // resizePressurePipe
  // -------------------------------------------------------------------------

  /// <summary>
  /// Swaps pipe <c>pipeName</c> in network <c>networkName</c> to another pipe part of the network's parts list, with
  /// PressurePipe.SwapPartSize. Without <c>newDiameter</c> the part is the one described as <c>newPartName</c>. With
  /// <c>newDiameter</c> (an inner diameter in drawing units, as the pipe reports it) the part is the pipe part of that
  /// inner diameter: <c>newPartName</c>'s own part when it has that size, else the one of that size in
  /// <c>newPartName</c>'s family, else the only part of that size in the list (see SelectPipePartBySize). The pipe's
  /// diameter is checked after the swap, and a mismatch undoes it. Changes the drawing (one undo step). Returns the
  /// names, <c>partUsed</c>, <c>previousDiameter</c>, <c>newDiameter</c> and <c>resized</c>. Throws
  /// CIVIL3D.INVALID_INPUT for a missing name, a non-positive <c>newDiameter</c>, several parts of that size to choose
  /// from or a swap that didn't give that size, and CIVIL3D.OBJECT_NOT_FOUND for an unknown network, pipe or part, no
  /// part of that size, or a network without a parts list; every error lists the parts to choose from.
  /// </summary>
  public static Task<object?> ResizePressurePipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetRequiredString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");
    if (newDiameter is <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"newDiameter must be a positive inner diameter in drawing units, got {newDiameter}.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = (PressurePipe)FindPressureComponentByName(network, transaction, pipeName, "GetPipeIds", "PipeIds", "Pipes");
      var partsListId = ((PressurePipeNetwork)network).PartsListId;
      var part = newDiameter.HasValue
        ? SelectPipePartBySize(transaction, partsListId, pipe, newPartName, newDiameter.Value)
        : FindPressurePart(transaction, partsListId, newPartName, PressurePartType.PressurePipe);
      var previousDiameter = pipe.InnerDiameter;

      pipe.UpgradeOpen();
      pipe.SwapPartSize(part);

      // The catalog's sizes were scaled to drawing units from the pipe's own part; the swapped pipe has the last word.
      if (newDiameter.HasValue && !SameDiameter(pipe.InnerDiameter, newDiameter.Value))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Part '{part.Description}' gave inner diameter {pipe.InnerDiameter:0.####}, not {newDiameter.Value}; the swap was undone. " +
          $"Pipe parts in the list: {DescribePipeParts(transaction, partsListId)}.");
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipeName"] = pipeName,
        ["newPartName"] = newPartName,
        ["partUsed"] = part.Description,
        ["previousDiameter"] = previousDiameter,
        ["newDiameter"] = pipe.InnerDiameter,
        ["resized"] = true,
      };
    });
  }

  /// <summary>Whether two inner diameters in drawing units are the same size (within 0.1 %, since catalog sizes are
  /// converted).</summary>
  private static bool SameDiameter(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-6, 1e-3 * Math.Abs(b));

  /// <summary>A part size's catalog inside diameter, or null when the catalog doesn't give one.</summary>
  private static double? CatalogInsideDiameter(PressurePartSize part)
  {
    try
    {
      var value = part.GetProperty(PressurePartContextType.PipeDiamterInside);
      return value == null ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception)
    {
      return null;
    }
  }

  /// <summary>A part size's family name from the catalog, or null.</summary>
  private static string? CatalogFamily(PressurePartSize part)
  {
    try { return part.GetProperty(PressurePartContextType.PartFamilyName)?.ToString(); }
    catch (Exception) { return null; }
  }

  /// <summary>
  /// The pipe part of inner diameter <paramref name="diameter"/> (drawing units) in the parts list. Catalog diameters
  /// are in the catalog's units, so they are scaled by the ratio between the pipe's current inner diameter and its own
  /// part's catalog value (1 when that can't be read). Preference: the part described as <paramref name="partName"/>
  /// when it has that size; else the one of that size in that part's family; else the only part of that size in the
  /// list. Several to choose from is CIVIL3D.INVALID_INPUT and none is CIVIL3D.OBJECT_NOT_FOUND, both listing the
  /// parts.
  /// </summary>
  private static PressurePartSize SelectPipePartBySize(Transaction transaction, ObjectId partsListId, PressurePipe pipe, string partName, double diameter)
  {
    if (partsListId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The pressure network does not have a pressure parts list assigned.");
    var partsList = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, partsListId, OpenMode.ForRead);
    var parts = partsList.GetParts(PressurePartType.PressurePipe).Where(part => part.IsValid).ToList();

    PressurePartSize? current = null;
    try { current = pipe.GetPartSize(); } catch (Exception) { }
    var currentCatalog = current == null ? null : CatalogInsideDiameter(current);
    var scale = currentCatalog is > 0 && pipe.InnerDiameter > 0 ? pipe.InnerDiameter / currentCatalog.Value : 1.0;

    var sized = parts.Where(part => CatalogInsideDiameter(part) is { } inside && SameDiameter(inside * scale, diameter)).ToList();
    var named = parts.FirstOrDefault(part => string.Equals(part.Description, partName, StringComparison.OrdinalIgnoreCase));
    if (named != null && sized.Contains(named)) return named;

    var choices = sized;
    if (named != null && CatalogFamily(named) is { } family)
    {
      var sameFamily = sized.Where(part => string.Equals(CatalogFamily(part), family, StringComparison.OrdinalIgnoreCase)).ToList();
      if (sameFamily.Count > 0) choices = sameFamily;
    }

    if (choices.Count == 1) return choices[0];

    if (choices.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"No pipe part in parts list '{partsList.Name}' has inner diameter {diameter}. It has: {DescribePipeParts(transaction, partsListId)}.");
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"Several pipe parts have inner diameter {diameter}: {string.Join(", ", choices.Select(part => part.Description).Take(12))}. Send one of them as newPartName.");
  }

  /// <summary>Up to 12 valid pipe part descriptions of the parts list, for an error message.</summary>
  private static string DescribePipeParts(Transaction transaction, ObjectId partsListId)
  {
    var partsList = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, partsListId, OpenMode.ForRead);
    return string.Join(", ", partsList.GetParts(PressurePartType.PressurePipe).Where(part => part.IsValid).Select(part => part.Description).Take(12));
  }

  // -------------------------------------------------------------------------
  // addPressureFitting
  // -------------------------------------------------------------------------

  /// <summary>
  /// Adds a fitting (elbow, tee, wye, cross, cap, coupling, plug or reducer part described as <c>partName</c>) to
  /// network <c>networkName</c> at <c>position</c> (required <c>{x, y, z}</c>). <c>rotation</c> must be absent or 0:
  /// AddFitting takes none, so any other value throws CIVIL3D.API_ERROR before the drawing is touched. Changes the
  /// drawing (one undo step). Returns <c>networkName</c>, the new <c>fitting</c>'s data and <c>added</c>. Throws
  /// CIVIL3D.INVALID_INPUT for a missing name, position or coordinate, and CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// network or part, or a network without a parts list.
  /// </summary>
  public static Task<object?> AddPressureFittingAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var position = ReadPoint(parameters, "position")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "position is required; drawing origin will not be assumed.");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    if (Math.Abs(rotation) > 1.0e-12)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D AddFitting does not accept a rotation. No fitting was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var createdId = AddPressureComponentToNetwork(network, transaction, partName, position, rotation, isFitting: true);
      var fitting = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["fitting"] = ToPressureFittingData(fitting),
        ["added"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getPressureFittingProperties
  // -------------------------------------------------------------------------

  /// <summary>Data of fitting <c>fittingName</c> in network <c>networkName</c> (both required), as
  /// ToPressureFittingData gives it. Read-only. Throws CIVIL3D.INVALID_INPUT for a missing name and
  /// CIVIL3D.OBJECT_NOT_FOUND for an unknown network or fitting.</summary>
  public static Task<object?> GetPressureFittingPropertiesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var fittingName = PluginRuntime.GetRequiredString(parameters, "fittingName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var fitting = FindPressureComponentByName(network, transaction, fittingName, "GetFittingIds", "FittingIds", "Fittings");
      return ToPressureFittingData(fitting);
    });
  }

  // -------------------------------------------------------------------------
  // addPressureAppurtenance
  // -------------------------------------------------------------------------

  /// <summary>
  /// Adds an appurtenance (valve, pump or hydrant part described as <c>partName</c>) to network <c>networkName</c> at
  /// <c>position</c> (required <c>{x, y, z}</c>). With <c>onPipeName</c> (optional) the position moves to that pipe's
  /// midpoint; a pipe that isn't in the network is CIVIL3D.INVALID_INPUT and one whose end points can't be read is
  /// CIVIL3D.API_ERROR, never a silent fallback to <c>position</c>.
  /// <c>rotation</c> must be absent or 0 (CIVIL3D.API_ERROR otherwise, before the drawing is touched). Changes the
  /// drawing (one undo step). Returns <c>networkName</c>, the new <c>appurtenance</c>'s data and <c>added</c>. Throws
  /// CIVIL3D.INVALID_INPUT for a missing name, position or coordinate, and CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// network or part, or a network without a parts list.
  /// </summary>
  public static Task<object?> AddPressureAppurtenanceAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var position = ReadPoint(parameters, "position")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "position is required unless a future pipe-insertion workflow explicitly derives it.");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    if (Math.Abs(rotation) > 1.0e-12)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Civil 3D AddAppurtenance does not accept a rotation. No appurtenance was created.");
    var onPipeName = PluginRuntime.GetOptionalString(parameters, "onPipeName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPressureNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);

      // With onPipeName the appurtenance goes at that pipe's midpoint. A pipe that isn't there is the caller's mistake,
      // so it's refused rather than quietly placing the part at position instead.
      if (!string.IsNullOrWhiteSpace(onPipeName))
      {
        AcDbObject pipe;
        try
        {
          pipe = FindPressureComponentByName(network, transaction, onPipeName!, "GetPipeIds", "PipeIds", "Pipes");
        }
        catch (JsonRpcDispatchException exception) when (exception.Code == "CIVIL3D.OBJECT_NOT_FOUND")
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"onPipeName '{onPipeName}' is not a pipe of pressure network '{networkName}'. Send an existing pipe name, or leave onPipeName out to place the appurtenance at position. Nothing was added.");
        }

        var sp = GetPointProperty(pipe, "StartPoint", "PointAtStart");
        var ep = GetPointProperty(pipe, "EndPoint", "PointAtEnd");
        if (!sp.HasValue || !ep.HasValue)
        {
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            $"The end points of pipe '{onPipeName}' can't be read, so its midpoint isn't known. Leave onPipeName out to place the appurtenance at position. Nothing was added.");
        }

        position = new Point3d(
          (sp.Value.X + ep.Value.X) / 2,
          (sp.Value.Y + ep.Value.Y) / 2,
          (sp.Value.Z + ep.Value.Z) / 2
        );
      }

      var createdId = AddPressureComponentToNetwork(network, transaction, partName, position, rotation, isFitting: false);
      var app = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, createdId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["appurtenance"] = ToPressureAppurtenanceData(app),
        ["added"] = true,
      };
    });
  }

  // =========================================================================
  // Private helpers
  // =========================================================================

  /// <summary>Every pressure network in the drawing, opened with <paramref name="openMode"/>.</summary>
  private static IEnumerable<AcDbObject> EnumeratePressureNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (var objectId in GetPressureNetworkIds(civilDoc))
    {
      yield return CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, openMode);
    }
  }

  /// <summary>Pressure network ids from CivilDocument.GetPressurePipeNetworkIds. The late-bound fallbacks after it only
  /// run when <paramref name="civilDoc"/> is not a CivilDocument, which the handlers here never pass.</summary>
  private static IEnumerable<ObjectId> GetPressureNetworkIds(object civilDoc)
  {
    if (civilDoc is CivilDocument typedDocument)
    {
      foreach (ObjectId objectId in typedDocument.GetPressurePipeNetworkIds())
      {
        if (objectId != ObjectId.Null)
          yield return objectId;
      }
      yield break;
    }

    var candidates = new object?[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPressureNetworkIds"),
      GetNamedMember(civilDoc, "PressureNetworkCollection"),
      GetNamedMember(civilDoc, "PressureNetworks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIdsFlexible(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  /// <summary>The pressure network named <paramref name="name"/> (case-insensitive), reopened for write when asked.
  /// Throws CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static AcDbObject FindPressureNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (var network in EnumeratePressureNetworks(civilDoc, transaction, OpenMode.ForRead))
    {
      if (!string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (openMode == OpenMode.ForWrite)
      {
        return CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, network.ObjectId, OpenMode.ForWrite);
      }

      return network;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure network '{name}' was not found.");
  }

  /// <summary>The pipe, fitting or appurtenance of <paramref name="network"/> named <paramref name="componentName"/>
  /// (case-insensitive), opened for read; <paramref name="idMethods"/> picks which kind. Throws
  /// CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static AcDbObject FindPressureComponentByName(
    AcDbObject network,
    Transaction transaction,
    string componentName,
    params string[] idMethods)
  {
    foreach (var objectId in GetChildObjectIds(network, idMethods))
    {
      var component = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(component), componentName, StringComparison.OrdinalIgnoreCase))
      {
        return component;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"Component '{componentName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  /// <summary>A network's list entry: name, handle, part counts and parts list name.</summary>
  private static Dictionary<string, object?> ToPressureNetworkSummary(AcDbObject network, Transaction transaction)
  {
    var pipeIds = GetChildObjectIds(network, "GetPipeIds", "PipeIds", "Pipes").ToList();
    var fittingIds = GetChildObjectIds(network, "GetFittingIds", "FittingIds", "Fittings").ToList();
    var appIds = GetChildObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds", "Appurtenances").ToList();

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(network) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["pipeCount"] = pipeIds.Count,
      ["fittingCount"] = fittingIds.Count,
      ["appurtenanceCount"] = appIds.Count,
      ["partsList"] = ResolveObjectName(transaction, GetAnyObjectId(network, "PartsListId", "CatalogId")),
    };
  }

  /// <summary>A pressure pipe as JSON data: name, handle, diameter, center-to-center length, material, start and end
  /// points, cover depth. Each value is read late-bound from the first member name that answers; null when none
  /// does.</summary>
  private static Dictionary<string, object?> ToPressurePipeData(AcDbObject pipe)
  {
    var sp = GetPointProperty(pipe, "StartPoint", "PointAtStart");
    var ep = GetPointProperty(pipe, "EndPoint", "PointAtEnd");

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(pipe) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      ["diameter"] = GetAnyDouble(pipe, "InnerDiameter", "InnerDiameterOrWidth", "Diameter"),
      ["length"] = GetAnyDouble(pipe, "Length3DCenterToCenter", "Length2DCenterToCenter"),
      ["material"] = GetAnyString(pipe, "Material", "PartFamilyName", "PartDescription") ?? string.Empty,
      ["startPoint"] = sp.HasValue ? new Dictionary<string, object?> { ["x"] = sp.Value.X, ["y"] = sp.Value.Y, ["z"] = sp.Value.Z } : null,
      ["endPoint"] = ep.HasValue ? new Dictionary<string, object?> { ["x"] = ep.Value.X, ["y"] = ep.Value.Y, ["z"] = ep.Value.Z } : null,
      ["coverDepth"] = GetAnyDouble(pipe, "CoverDepth", "Cover", "MinCover"),
    };
  }

  /// <summary>A pressure fitting as JSON data: name, handle, type (falls back to the .NET class name), position and
  /// part size, read late-bound like ToPressurePipeData.</summary>
  private static Dictionary<string, object?> ToPressureFittingData(AcDbObject fitting)
  {
    var pos = GetPointProperty(fitting, "Position", "Location", "CenterPoint");

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(fitting) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(fitting),
      ["type"] = GetAnyString(fitting, "FittingType", "PartType", "PartDescription") ?? fitting.GetType().Name,
      ["position"] = pos.HasValue ? new Dictionary<string, object?> { ["x"] = pos.Value.X, ["y"] = pos.Value.Y, ["z"] = pos.Value.Z } : null,
      ["partSize"] = GetAnyString(fitting, "PartSize", "SizeName", "PartFamilyName"),
    };
  }

  /// <summary>A pressure appurtenance as JSON data, the same shape as ToPressureFittingData.</summary>
  private static Dictionary<string, object?> ToPressureAppurtenanceData(AcDbObject app)
  {
    var pos = GetPointProperty(app, "Position", "Location", "CenterPoint");

    return new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(app) ?? string.Empty,
      ["handle"] = CivilObjectUtils.GetHandle(app),
      ["type"] = GetAnyString(app, "AppurtenanceType", "PartType", "PartDescription") ?? app.GetType().Name,
      ["position"] = pos.HasValue ? new Dictionary<string, object?> { ["x"] = pos.Value.X, ["y"] = pos.Value.Y, ["z"] = pos.Value.Z } : null,
      ["partSize"] = GetAnyString(app, "PartSize", "SizeName", "PartFamilyName"),
    };
  }

  /// <summary>ToPressurePipeData, without the start and end points unless <paramref name="includeCoords"/>.</summary>
  private static Dictionary<string, object?> ExportPressurePipe(AcDbObject pipe, bool includeCoords)
  {
    var data = ToPressurePipeData(pipe);
    if (!includeCoords)
    {
      data.Remove("startPoint");
      data.Remove("endPoint");
    }

    return data;
  }

  /// <summary>ToPressureFittingData, without the position unless <paramref name="includeCoords"/>.</summary>
  private static Dictionary<string, object?> ExportPressureFitting(AcDbObject fitting, bool includeCoords)
  {
    var data = ToPressureFittingData(fitting);
    if (!includeCoords)
    {
      data.Remove("position");
    }

    return data;
  }

  /// <summary>ToPressureAppurtenanceData, without the position unless <paramref name="includeCoords"/>.</summary>
  private static Dictionary<string, object?> ExportPressureAppurtenance(AcDbObject app, bool includeCoords)
  {
    var data = ToPressureAppurtenanceData(app);
    if (!includeCoords)
    {
      data.Remove("position");
    }

    return data;
  }

  /// <summary>Creates an empty pressure network named <paramref name="name"/>; the caller sets its parts list.</summary>
  private static ObjectId CreatePressureNetwork(Database database, string name)
    => PressurePipeNetwork.Create(database, name);

  /// <summary>
  /// Adds a straight pipe of part <paramref name="partName"/> along the segment and returns its id. When
  /// <paramref name="diameter"/> is given, the new pipe's inner diameter must match it within 1e-6 or it throws
  /// CIVIL3D.INVALID_INPUT; the pipe already exists then, and the uncommitted transaction is what drops it.
  /// </summary>
  private static ObjectId AddPressurePipeToNetwork(AcDbObject network, Transaction transaction, string partName, Point3d startPoint, Point3d endPoint, double? diameter)
  {
    var pressureNetwork = (PressurePipeNetwork)network;
    var part = FindPressurePart(transaction, pressureNetwork.PartsListId, partName, PressurePartType.PressurePipe);
    var id = pressureNetwork.AddLinePipe(new LineSegment3d(startPoint, endPoint), part);
    if (diameter.HasValue)
    {
      var created = CivilObjectUtils.GetRequiredObject<PressurePipe>(transaction, id, OpenMode.ForRead);
      var actualDiameter = GetAnyDouble(created, "InnerDiameter", "InnerDiameterOrWidth");
      if (!actualDiameter.HasValue || Math.Abs(actualDiameter.Value - diameter.Value) > 1.0e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Catalog part '{partName}' does not match requested diameter {diameter.Value}. The pipe was not committed.");
    }
    return id;
  }

  /// <summary>Adds a fitting or an appurtenance of part <paramref name="partName"/> at <paramref name="position"/> and
  /// returns its id. The part is looked up among the fitting or appurtenance part types only. <paramref name="rotation"/>
  /// is ignored: the callers already refuse anything but 0.</summary>
  private static ObjectId AddPressureComponentToNetwork(AcDbObject network, Transaction transaction, string partName, Point3d position, double rotation, bool isFitting)
  {
    _ = rotation;
    var pressureNetwork = (PressurePipeNetwork)network;
    var candidates = isFitting
      ? new[] { PressurePartType.Elbow, PressurePartType.Tee, PressurePartType.Wye, PressurePartType.Cross, PressurePartType.Cap, PressurePartType.Coupling, PressurePartType.Plug, PressurePartType.Reducer }
      : new[] { PressurePartType.Valve, PressurePartType.Pump, PressurePartType.Hydrant };
    var part = FindPressurePart(transaction, pressureNetwork.PartsListId, partName, candidates);
    return isFitting
      ? pressureNetwork.AddFitting(position, part)
      : pressureNetwork.AddAppurtenance(position, part);
  }

  /// <summary>The id of the pressure parts list named <paramref name="partsListName"/> (case-insensitive). Throws
  /// CIVIL3D.OBJECT_NOT_FOUND when there is none.</summary>
  private static ObjectId FindPressurePartsListId(object civilDoc, Transaction transaction, string partsListName)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).Styles.GetPressurePartLists())
    {
      var list = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(list.Name, partsListName, StringComparison.OrdinalIgnoreCase))
        return objectId;
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pressure parts list '{partsListName}' was not found.");
  }

  /// <summary>
  /// The valid part of one of <paramref name="partTypes"/> whose description equals <paramref name="partName"/>
  /// (ignoring case) in the parts list. Throws CIVIL3D.OBJECT_NOT_FOUND when the network has no parts list, or when no
  /// part matches; that message lists up to 12 descriptions the list does have, so the AI can pick one.
  /// </summary>
  private static PressurePartSize FindPressurePart(
    Transaction transaction,
    ObjectId partsListId,
    string partName,
    params PressurePartType[] partTypes)
  {
    if (partsListId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The pressure network does not have a pressure parts list assigned.");
    var partsList = CivilObjectUtils.GetRequiredObject<PressurePartList>(transaction, partsListId, OpenMode.ForRead);
    foreach (var partType in partTypes)
    {
      foreach (var part in partsList.GetParts(partType))
      {
        if (part.IsValid && string.Equals(part.Description, partName, StringComparison.OrdinalIgnoreCase))
          return part;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Exact pressure part '{partName}' was not found in parts list '{partsList.Name}'. It has: {string.Join(", ", partTypes.SelectMany(type => partsList.GetParts(type).Where(part => part.IsValid).Select(part => part.Description)).Take(12))}.");
  }

  /// <summary>Child object ids of <paramref name="owner"/>: for each member name, the method's result or, when that is
  /// null, the property or field of that name. Null ids are skipped.</summary>
  private static IEnumerable<ObjectId> GetChildObjectIds(object owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      var value = CivilObjectUtils.InvokeMethod(owner, memberName) ?? GetNamedMember(owner, memberName);
      foreach (var id in ToObjectIdsFlexible(value))
      {
        if (id != ObjectId.Null) yield return id;
      }
    }
  }

  /// <summary>Object ids from a value: what CivilObjectUtils.ToObjectIds finds, or else each item of a collection that
  /// is an ObjectId or has an ObjectId/Id property.</summary>
  private static IEnumerable<ObjectId> ToObjectIdsFlexible(object? value)
  {
    var direct = CivilObjectUtils.ToObjectIds(value).ToList();
    if (direct.Count > 0)
    {
      // Walking the collection as well would give every id again: a network of 3 pipes reported 6.
      foreach (var id in direct)
      {
        yield return id;
      }

      yield break;
    }

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId oid && oid != ObjectId.Null) { yield return oid; continue; }
        var itemId = GetAnyObjectId(item, "ObjectId", "Id");
        if (itemId != ObjectId.Null) yield return itemId;
      }
    }
  }

  /// <summary>A property, or else a field, of that name, through Civil3DCompatibility (the plugin's one place for
  /// reflection). Null when neither exists.</summary>
  private static object? GetNamedMember(object? value, string memberName)
  {
    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  /// <summary>The name of the object behind an id (a parts list, say), or null for a null id or any failure.</summary>
  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null) return null;
    try { return CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead)); }
    catch { return null; }
  }

  /// <summary>The first of several ObjectId properties that is not null, or ObjectId.Null.</summary>
  private static ObjectId GetAnyObjectId(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var id = CivilObjectUtils.GetPropertyValue<ObjectId>(value, name);
      if (id != ObjectId.Null) return id;
    }

    return ObjectId.Null;
  }

  /// <summary>The first of several numeric properties that has a value, or null.</summary>
  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<double?>(value, name);
      if (v.HasValue) return v.Value;
    }

    return null;
  }

  /// <summary>The first of several string properties that is not blank, or null.</summary>
  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetStringProperty(value, name);
      if (!string.IsNullOrWhiteSpace(v)) return v;
    }

    return null;
  }

  /// <summary>The first of several Point3d properties that has a value, or null.</summary>
  private static Point3d? GetPointProperty(object? value, params string[] propertyNames)
  {
    foreach (var name in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<Point3d?>(value, name);
      if (v.HasValue) return v.Value;
    }

    return null;
  }

  /// <summary>Reads parameter <paramref name="paramName"/> as an <c>{x, y, z}</c> object. Null when it is missing or
  /// not an object (the callers then throw); throws CIVIL3D.INVALID_INPUT when x, y or z is missing or not a number,
  /// so no coordinate or elevation is ever assumed to be 0.</summary>
  private static Point3d? ReadPoint(JsonObject? parameters, string paramName)
  {
    if (PluginRuntime.GetParameter(parameters, paramName) is not JsonObject node) return null;
    double Coordinate(string axis, string missing) =>
      node[axis] is JsonValue value && value.TryGetValue<double>(out var number)
        ? number
        : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", missing);
    return new Point3d(
      Coordinate("x", $"{paramName}.x is required as a number."),
      Coordinate("y", $"{paramName}.y is required as a number."),
      Coordinate("z", $"{paramName}.z is required as a number; elevation 0 will not be assumed.")
    );
  }
}
