// QC checks on the open drawing: alignments, profiles, corridors, gravity pipe networks, surfaces, styles and label
// sets, and layer standards. Each check returns findings (severity, type, message) for the AI to summarize or act on.
// JSON-RPC methods (CommandDispatcher.cs): qcCheckAlignment, qcCheckProfile, qcCheckCorridor, qcCheckPipeNetwork,
// qcCheckSurface and qcReportGenerate serve civil3d_qc (src/tools/domains/qcDomain.ts); qcCheckLabels,
// qcCheckDrawingStandards and qcFixDrawingStandards serve civil3d_standards (standardsDomain.ts).
// Every check reads through CivilExecution.ReadAsync (main thread, no undo step). qcReportGenerate also writes a .txt
// file, only through FileBoundary.WriteAllTextAtomic. qcFixDrawingStandards is the one drawing write: WriteAsync
// (command context, document lock, one transaction, one undo step), with a dryRun preview.
// Unexpected errors come back as CIVIL3D.QC_ERROR; a JsonRpcDispatchException thrown inside passes through as is.
// Civil 3D properties are read late-bound through CivilObjectUtils helpers; this file does no reflection of its own.
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// QC check commands: analysis tools that inspect Civil 3D objects and return structured findings (two of them also
/// write; see the remarks). Uses reflection for API compatibility.
/// </summary>
/// <remarks>
/// Behind the civil3d_qc tool (src/tools/domains/qcDomain.ts), the drawing-standards actions of civil3d_standards
/// (standardsDomain.ts) and the QC workflows. Each check returns a list of findings, each with a <c>severity</c>
/// (error, warning, info), a machine-readable <c>type</c> and a message, so the AI can summarize or act on them.
/// Optional limits without a default (maxGrade, minCover, ...) are only checked when the caller gives them.
///
/// Not everything here is read-only: QcReportGenerateAsync writes a text file (through FileBoundary, so only inside
/// the export roots and never over an existing file without <c>overwrite</c>), and QcFixDrawingStandardsAsync renames
/// and recolors layers (a write, one undo step, with a <c>dryRun</c> preview).
///
/// Properties are read late-bound (CivilObjectUtils.GetPropertyValue), and a property a release doesn't have reads
/// as 0 or null, which can silence a check or, where 0 is a failing value, raise one.
/// </remarks>
public static class QcCommands
{
  // -------------------------------------------------------------------------
  // qcCheckAlignment
  // -------------------------------------------------------------------------

  /// <summary>
  /// Checks alignment <c>name</c> entity by entity: zero-length tangents, curves and spirals, invalid radii and spiral
  /// parameters, and, with <c>designSpeed</c>, curves below a rough minimum radius of V squared / 15 (a simplified
  /// stand-in for the AASHTO formula, not a design check). <c>checkTangents</c>, <c>checkCurves</c> and
  /// <c>checkSpirals</c> (all on by default) select the checks.
  /// <c>name</c> is required; <c>designSpeed</c> is optional (the message calls it km/h whatever the drawing units).
  /// Read-only. Returns the alignment's name, length, start and end station, <c>findings</c> and
  /// <c>totalViolations</c>. Throws CIVIL3D.INVALID_INPUT without <c>name</c>, CIVIL3D.OBJECT_NOT_FOUND for an unknown
  /// alignment, and CIVIL3D.QC_ERROR for any other failure.
  /// </summary>
  public static Task<object?> QcCheckAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var designSpeed = PluginRuntime.GetOptionalDouble(parameters, "designSpeed");
    var checkTangents = PluginRuntime.GetOptionalBool(parameters, "checkTangents") ?? true;
    var checkCurves = PluginRuntime.GetOptionalBool(parameters, "checkCurves") ?? true;
    var checkSpirals = PluginRuntime.GetOptionalBool(parameters, "checkSpirals") ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
        var findings = new List<Dictionary<string, object?>>();

        var entityCollection = CivilObjectUtils.GetPropertyValue<object>(alignment, "Entities");
        if (entityCollection is IEnumerable enumerable)
        {
          var index = 0;
          foreach (var entity in enumerable)
          {
            if (entity == null) { index++; continue; }

            var entityType = CivilObjectUtils.GetStringProperty(entity, "EntityType") ?? entity.GetType().Name;
            var length = CivilObjectUtils.EntityLength(entity);
            var startStation = CivilObjectUtils.GetPropertyValue<double?>(entity, "StartStation") ?? 0;
            var endStation = CivilObjectUtils.GetPropertyValue<double?>(entity, "EndStation") ?? 0;
            var isTangent = entityType.IndexOf("tangent", StringComparison.OrdinalIgnoreCase) >= 0
                         || entityType.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0;
            var isCurve = entityType.IndexOf("curve", StringComparison.OrdinalIgnoreCase) >= 0
                       || entityType.IndexOf("arc", StringComparison.OrdinalIgnoreCase) >= 0;
            var isSpiral = entityType.IndexOf("spiral", StringComparison.OrdinalIgnoreCase) >= 0;

            if (checkTangents && isTangent)
            {
              if (length <= 0)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "error",
                  ["type"] = "tangent_zero_length",
                  ["entityIndex"] = index,
                  ["entityType"] = entityType,
                  ["startStation"] = startStation,
                  ["endStation"] = endStation,
                  ["message"] = $"Tangent at index {index} (sta {startStation:F2}–{endStation:F2}) has zero or negative length.",
                });
              }
            }

            if (checkCurves && isCurve)
            {
              // A spiral-curve-spiral has no radius of its own: it's on the entity's Arc.
              var arc = CivilObjectUtils.GetPropertyValue<object>(entity, "Arc") ?? entity;
              var radius = CivilObjectUtils.GetPropertyValue<double?>(arc, "Radius") ?? 0;
              if (radius <= 0)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "error",
                  ["type"] = "curve_invalid_radius",
                  ["entityIndex"] = index,
                  ["entityType"] = entityType,
                  ["startStation"] = startStation,
                  ["endStation"] = endStation,
                  ["radius"] = radius,
                  ["message"] = $"Curve at index {index} (sta {startStation:F2}–{endStation:F2}) has invalid radius {radius:F2}.",
                });
              }
              else if (designSpeed.HasValue)
              {
                // AASHTO minimum radius approximation: R_min ≈ V² / (127 * (e + f))
                // Using a simplified check: R_min ≈ V² / 15 for a rough check
                var minRadius = (designSpeed.Value * designSpeed.Value) / 15.0;
                if (radius < minRadius)
                {
                  findings.Add(new Dictionary<string, object?>
                  {
                    ["severity"] = "warning",
                    ["type"] = "curve_radius_below_design_minimum",
                    ["entityIndex"] = index,
                    ["entityType"] = entityType,
                    ["startStation"] = startStation,
                    ["endStation"] = endStation,
                    ["radius"] = radius,
                    ["minimumRadius"] = minRadius,
                    ["designSpeed"] = designSpeed,
                    ["message"] = $"Curve at index {index} (sta {startStation:F2}–{endStation:F2}) has radius {radius:F2}, below minimum {minRadius:F2} for design speed {designSpeed} km/h.",
                  });
                }
              }

              if (length <= 0)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "error",
                  ["type"] = "curve_zero_length",
                  ["entityIndex"] = index,
                  ["entityType"] = entityType,
                  ["startStation"] = startStation,
                  ["endStation"] = endStation,
                  ["message"] = $"Curve at index {index} (sta {startStation:F2}–{endStation:F2}) has zero or negative length.",
                });
              }
            }

            if (checkSpirals && isSpiral)
            {
              // A spiral-curve-spiral has no A of its own: each spiral in it has one.
              var spiral = CivilObjectUtils.GetPropertyValue<object>(entity, "SpiralIn") ?? entity;
              var aValue = CivilObjectUtils.GetPropertyValue<double?>(spiral, "A")
                ?? CivilObjectUtils.GetPropertyValue<double?>(spiral, "SpiralParameter")
                ?? 0;
              if (aValue <= 0)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "warning",
                  ["type"] = "spiral_invalid_parameter",
                  ["entityIndex"] = index,
                  ["entityType"] = entityType,
                  ["startStation"] = startStation,
                  ["endStation"] = endStation,
                  ["spiralParameter"] = aValue,
                  ["message"] = $"Spiral at index {index} (sta {startStation:F2}–{endStation:F2}) has invalid A-parameter {aValue:F4}.",
                });
              }

              if (length <= 0)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "error",
                  ["type"] = "spiral_zero_length",
                  ["entityIndex"] = index,
                  ["entityType"] = entityType,
                  ["startStation"] = startStation,
                  ["endStation"] = endStation,
                  ["message"] = $"Spiral at index {index} (sta {startStation:F2}–{endStation:F2}) has zero or negative length.",
                });
              }
            }

            index++;
          }
        }

        return new Dictionary<string, object?>
        {
          ["alignmentName"] = alignment.Name,
          ["length"] = alignment.Length,
          ["startStation"] = alignment.StartingStation,
          ["endStation"] = alignment.EndingStation,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking alignment '{name}': {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckProfile
  // -------------------------------------------------------------------------

  /// <summary>One profile entity as qcCheckProfile needs it: tangent or curve, its station range, length and the grade
  /// in and out (a tangent's single grade is both).</summary>
  private readonly record struct EntityGrades(bool Tangent, double Start, double End, double Length, double GradeIn, double GradeOut);

  /// <summary>Reads the grades of a tangent or a symmetric, asymmetric or circular vertical curve. Any other entity type
  /// reads as a curve with grades 0.</summary>
  private static EntityGrades ReadGrades(ProfileEntity entity)
  {
    var (tangent, gradeIn, gradeOut) = entity switch
    {
      ProfileTangent line => (true, line.Grade, line.Grade),
      ProfileParabolaSymmetric curve => (false, curve.GradeIn, curve.GradeOut),
      ProfileParabolaAsymmetric curve => (false, curve.GradeIn, curve.GradeOut),
      ProfileCircular curve => (false, curve.GradeIn, curve.GradeOut),
      _ => (false, 0.0, 0.0),
    };
    return new EntityGrades(tangent, entity.StartStation, entity.EndStation, entity.Length, gradeIn, gradeOut);
  }

  /// <summary>
  /// Checks profile <c>profileName</c> of alignment <c>alignmentName</c>: tangents and curve ends with a grade above
  /// <c>maxGrade</c>, and vertical curves whose K value is below <c>minKValue</c>. Grades are compared as the API gives
  /// them (a fraction, e.g. 0.06 for 6 %), so pass <c>maxGrade</c> the same way.
  /// <c>alignmentName</c> and <c>profileName</c> are required; both limits are optional and unchecked when left out.
  /// Read-only. Returns the two names, <c>findings</c> and <c>totalViolations</c>. Throws CIVIL3D.INVALID_INPUT for a
  /// missing name, CIVIL3D.OBJECT_NOT_FOUND for an unknown alignment or profile, CIVIL3D.QC_ERROR for anything else.
  /// </summary>
  public static Task<object?> QcCheckProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var maxGrade = PluginRuntime.GetOptionalDouble(parameters, "maxGrade");
    var minKValue = PluginRuntime.GetOptionalDouble(parameters, "minKValue");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
        var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
        var findings = new List<Dictionary<string, object?>>();

        // By entity, not by PVI. Reading PVIs takes about 4 ms each, by index or by enumerating, so 9,662 of them on a
        // 6.5 mile ground profile took 34 s; the same profile's entities take under a second.
        // Civil 3D lists them in the order they were made, not along the road.
        var entities = profile.Entities.Cast<ProfileEntity>().Select(ReadGrades).OrderBy(entity => entity.Start).ToList();
        void CheckGrade(double grade, double station, string where)
        {
          if (!maxGrade.HasValue || Math.Abs(grade) <= maxGrade.Value) return;
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "warning",
            ["type"] = "profile_grade_exceeds_maximum",
            ["station"] = station,
            ["grade"] = grade,
            ["maxGrade"] = maxGrade,
            ["message"] = $"{where} at station {station:F2}: grade {grade:P2} exceeds maximum {maxGrade:P2}.",
          });
        }

        for (var i = 0; i < entities.Count; i++)
        {
          var entity = entities[i];
          if (entity.Tangent)
          {
            CheckGrade(entity.GradeIn, entity.Start, "Tangent");
            continue;
          }

          // Curve to curve has no tangent to carry the grade between them.
          if (i == 0 || !entities[i - 1].Tangent) CheckGrade(entity.GradeIn, entity.Start, "Curve start");
          if (i == entities.Count - 1 || !entities[i + 1].Tangent) CheckGrade(entity.GradeOut, entity.End, "Curve end");

          // K is the curve's length per percent of grade change.
          var gradeChangePercent = Math.Abs(entity.GradeOut - entity.GradeIn) * 100;
          if (minKValue.HasValue && gradeChangePercent > 0 && entity.Length / gradeChangePercent < minKValue.Value)
          {
            var kValue = entity.Length / gradeChangePercent;
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "warning",
              ["type"] = "profile_k_value_below_minimum",
              ["station"] = entity.Start,
              ["kValue"] = kValue,
              ["minKValue"] = minKValue,
              ["message"] = $"Curve at station {entity.Start:F2}: K-value {kValue:F2} is below minimum {minKValue:F2}.",
            });
          }
        }

        return new Dictionary<string, object?>
        {
          ["alignmentName"] = alignmentName,
          ["profileName"] = profileName,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking profile '{profileName}': {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckCorridor
  // -------------------------------------------------------------------------

  /// <summary>Checks corridor <c>name</c>: whether it reports itself invalid (needs a rebuild), and each baseline
  /// region for zero or inverted station ranges and a missing assembly. <c>name</c> is required. Read-only. Returns
  /// <c>corridorName</c>, <c>findings</c> and <c>totalViolations</c>. Throws CIVIL3D.INVALID_INPUT without
  /// <c>name</c>, CIVIL3D.OBJECT_NOT_FOUND for an unknown corridor, CIVIL3D.QC_ERROR for anything else.</summary>
  public static Task<object?> QcCheckCorridorAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
        var findings = new List<Dictionary<string, object?>>();

        // Check overall corridor validity via reflection
        var isValid = CivilObjectUtils.GetBoolProperty(corridor, "IsValid") ?? true;
        if (!isValid)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "error",
            ["type"] = "corridor_invalid",
            ["corridorName"] = corridor.Name,
            ["message"] = $"Corridor '{corridor.Name}' reports IsValid = false. A rebuild may be required.",
          });
        }

        // Iterate baselines and regions
        var baselines = CivilObjectUtils.GetPropertyValue<object>(corridor, "Baselines");
        if (baselines is IEnumerable baselineEnumerable)
        {
          var baselineIndex = 0;
          foreach (var baseline in baselineEnumerable)
          {
            if (baseline == null) { baselineIndex++; continue; }

            var baselineName = CivilObjectUtils.GetName(baseline) ?? $"Baseline[{baselineIndex}]";
            var regions = CivilObjectUtils.GetPropertyValue<object>(baseline, "BaselineRegions");
            if (regions is IEnumerable regionEnumerable)
            {
              var regionIndex = 0;
              foreach (var region in regionEnumerable)
              {
                if (region == null) { regionIndex++; continue; }

                var regionName = CivilObjectUtils.GetName(region) ?? $"Region[{regionIndex}]";
                var startStation = CivilObjectUtils.GetPropertyValue<double?>(region, "StartStation") ?? 0;
                var endStation = CivilObjectUtils.GetPropertyValue<double?>(region, "EndStation") ?? 0;

                // Zero-length or inverted region
                if (endStation <= startStation)
                {
                  findings.Add(new Dictionary<string, object?>
                  {
                    ["severity"] = "error",
                    ["type"] = "corridor_region_zero_length",
                    ["baseline"] = baselineName,
                    ["region"] = regionName,
                    ["startStation"] = startStation,
                    ["endStation"] = endStation,
                    ["message"] = $"Baseline '{baselineName}', region '{regionName}': end station {endStation:F2} <= start station {startStation:F2}.",
                  });
                }

                // Check assembly assignment
                var assemblyId = CivilObjectUtils.GetPropertyValue<ObjectId?>(region, "AssemblyId");
                if (assemblyId == null || assemblyId == ObjectId.Null)
                {
                  findings.Add(new Dictionary<string, object?>
                  {
                    ["severity"] = "warning",
                    ["type"] = "corridor_region_no_assembly",
                    ["baseline"] = baselineName,
                    ["region"] = regionName,
                    ["startStation"] = startStation,
                    ["endStation"] = endStation,
                    ["message"] = $"Baseline '{baselineName}', region '{regionName}': no assembly assigned.",
                  });
                }

                regionIndex++;
              }
            }

            baselineIndex++;
          }
        }

        return new Dictionary<string, object?>
        {
          ["corridorName"] = corridor.Name,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking corridor '{name}': {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckPipeNetwork
  // -------------------------------------------------------------------------

  /// <summary>
  /// Checks every pipe of gravity network <c>name</c> against the limits given: <c>minSlope</c> / <c>maxSlope</c>,
  /// <c>minCover</c>, <c>minVelocity</c> / <c>maxVelocity</c>. Cover and velocity are the values stored on the pipe;
  /// a pipe without a stored velocity reads as 0 and is not reported as too slow.
  /// <c>name</c> is required; every limit is optional and unchecked when left out. With <c>minCover</c>, a pipe with no
  /// reference surface gets a "pipe_cover_unknown" info finding instead of a cover check.
  /// Read-only. Returns <c>networkName</c>, <c>findings</c> and <c>totalViolations</c>. Throws CIVIL3D.INVALID_INPUT
  /// without <c>name</c>, CIVIL3D.OBJECT_NOT_FOUND for an unknown network, CIVIL3D.QC_ERROR for anything else.
  /// </summary>
  public static Task<object?> QcCheckPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minCover = PluginRuntime.GetOptionalDouble(parameters, "minCover");
    var maxSlope = PluginRuntime.GetOptionalDouble(parameters, "maxSlope");
    var minSlope = PluginRuntime.GetOptionalDouble(parameters, "minSlope");
    var minVelocity = PluginRuntime.GetOptionalDouble(parameters, "minVelocity");
    var maxVelocity = PluginRuntime.GetOptionalDouble(parameters, "maxVelocity");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        // Find the pipe network via reflection (same pattern as PipeNetworkCommands)
        var networkObj = FindPipeNetworkByNameReflection(civilDoc, transaction, name)
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");

        var findings = new List<Dictionary<string, object?>>();

        // Iterate pipes
        foreach (var pipeId in GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection"))
        {
          var pipe = transaction.GetObject(pipeId, OpenMode.ForRead);
          var pipeName = CivilObjectUtils.GetName(pipe) ?? pipeId.Handle.ToString();

          var slope = GetAnyDouble(pipe, "Slope", "FlowSlope") ?? 0;
          var absSlope = Math.Abs(slope);

          if (maxSlope.HasValue && absSlope > maxSlope.Value)
          {
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "warning",
              ["type"] = "pipe_slope_exceeds_maximum",
              ["pipeName"] = pipeName,
              ["slope"] = slope,
              ["maxSlope"] = maxSlope,
              ["message"] = $"Pipe '{pipeName}': slope {slope:P3} exceeds maximum {maxSlope:P3}.",
            });
          }

          if (minSlope.HasValue && absSlope < minSlope.Value)
          {
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "warning",
              ["type"] = "pipe_slope_below_minimum",
              ["pipeName"] = pipeName,
              ["slope"] = slope,
              ["minSlope"] = minSlope,
              ["message"] = $"Pipe '{pipeName}': slope {slope:P3} is below minimum {minSlope:P3}.",
            });
          }

          if (minCover.HasValue)
          {
            // Civil 3D works out cover against the pipe's reference surface, and gives 0 without one: not a real depth.
            if (pipe is not Pipe coveredPipe || HasNoReferenceSurface(coveredPipe))
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "info",
                ["type"] = "pipe_cover_unknown",
                ["pipeName"] = pipeName,
                ["message"] = $"Pipe '{pipeName}' has no reference surface, so its cover can't be checked.",
              });
              continue;
            }

            var coverDepth = Math.Min(coveredPipe.CoverOfStartPoint, coveredPipe.CoverOfEndpoint);
            if (coverDepth < minCover.Value)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "warning",
                ["type"] = "pipe_cover_below_minimum",
                ["pipeName"] = pipeName,
                ["coverDepth"] = coverDepth,
                ["minCover"] = minCover,
                ["message"] = $"Pipe '{pipeName}': cover depth {coverDepth:F3} is below minimum {minCover:F3}.",
              });
            }
          }

          if (minVelocity.HasValue || maxVelocity.HasValue)
          {
            var velocity = GetAnyDouble(pipe, "Velocity", "FlowVelocity") ?? 0;
            if (minVelocity.HasValue && velocity < minVelocity.Value && velocity > 0)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "warning",
                ["type"] = "pipe_velocity_below_minimum",
                ["pipeName"] = pipeName,
                ["velocity"] = velocity,
                ["minVelocity"] = minVelocity,
                ["message"] = $"Pipe '{pipeName}': velocity {velocity:F3} is below minimum {minVelocity:F3}.",
              });
            }
            if (maxVelocity.HasValue && velocity > maxVelocity.Value)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "warning",
                ["type"] = "pipe_velocity_exceeds_maximum",
                ["pipeName"] = pipeName,
                ["velocity"] = velocity,
                ["maxVelocity"] = maxVelocity,
                ["message"] = $"Pipe '{pipeName}': velocity {velocity:F3} exceeds maximum {maxVelocity:F3}.",
              });
            }
          }
        }

        return new Dictionary<string, object?>
        {
          ["networkName"] = name,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking pipe network '{name}': {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckSurface
  // -------------------------------------------------------------------------

  /// <summary>
  /// Sanity checks on surface <c>name</c> from its general properties: an elevation range over ten times
  /// <c>spikeThreshold</c> (default 10, so 100 units), no points, points but no triangles, a flat surface (range below
  /// <c>flatTriangleThreshold</c>), and every elevation near zero (a likely datum or units problem). The triangle count
  /// comes from a TIN surface's TIN properties, as in SurfaceCommands; a grid surface has none, so the no-triangles
  /// check doesn't apply to it.
  /// <c>name</c> is required; <c>flatTriangleThreshold</c> defaults to 0.01. Read-only. Returns the surface name, point
  /// and triangle counts, min and max elevation, <c>findings</c> and <c>totalViolations</c>; an empty surface returns a
  /// single "surface_no_points" finding. Throws CIVIL3D.INVALID_INPUT without <c>name</c>, CIVIL3D.OBJECT_NOT_FOUND for
  /// an unknown surface, CIVIL3D.QC_ERROR for anything else.
  /// </summary>
  public static Task<object?> QcCheckSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var spikeThreshold = PluginRuntime.GetOptionalDouble(parameters, "spikeThreshold") ?? 10.0;
    var flatTriangleThreshold = PluginRuntime.GetOptionalDouble(parameters, "flatTriangleThreshold") ?? 0.01;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
        var findings = new List<Dictionary<string, object?>>();

        // A surface defined but with nothing in it yet is common in real drawings, and Civil 3D throws when asked its
        // properties, so this is a finding and not a failure.
        if (SurfaceCommands.IsEmpty(surface))
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "error",
            ["type"] = "surface_no_points",
            ["surfaceName"] = name,
            ["message"] = $"Surface '{name}' has no data points.",
          });
          return new Dictionary<string, object?>
          {
            ["surfaceName"] = name,
            ["numberOfPoints"] = 0,
            ["numberOfTriangles"] = 0,
            ["minElevation"] = null,
            ["maxElevation"] = null,
            ["findings"] = findings,
            ["totalViolations"] = findings.Count,
          };
        }

        var generalProperties = CivilObjectUtils.InvokeMethod(surface, "GetGeneralProperties");
        var minElevation = CivilObjectUtils.GetPropertyValue<double?>(generalProperties, "MinimumElevation") ?? 0;
        var maxElevation = CivilObjectUtils.GetPropertyValue<double?>(generalProperties, "MaximumElevation") ?? 0;
        var numberOfPoints = CivilObjectUtils.GetPropertyValue<int?>(generalProperties, "NumberOfPoints") ?? 0;
        // A TIN surface's triangle count is in its TIN properties, not the general ones (which have none, so reading
        // it there always gave 0). Null for a grid or other surface: the no-triangles check doesn't apply to it.
        long? numberOfTriangles = surface is TinSurface tin ? Convert.ToInt64(tin.GetTinProperties().NumberOfTriangles) : null;

        var elevationRange = maxElevation - minElevation;
        if (elevationRange > spikeThreshold * 10)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "warning",
            ["type"] = "surface_large_elevation_range",
            ["surfaceName"] = name,
            ["elevationRange"] = elevationRange,
            ["minElevation"] = minElevation,
            ["maxElevation"] = maxElevation,
            ["message"] = $"Surface '{name}': elevation range {elevationRange:F2} may indicate spike data (min={minElevation:F2}, max={maxElevation:F2}).",
          });
        }

        if (numberOfPoints == 0)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "error",
            ["type"] = "surface_no_points",
            ["surfaceName"] = name,
            ["message"] = $"Surface '{name}' has no data points.",
          });
        }

        if (numberOfTriangles == 0 && numberOfPoints > 2)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "error",
            ["type"] = "surface_no_triangles",
            ["surfaceName"] = name,
            ["numberOfPoints"] = numberOfPoints,
            ["message"] = $"Surface '{name}' has {numberOfPoints} points but no triangles — surface may need rebuilding.",
          });
        }

        // Check for flat surface (zero elevation range with points present)
        if (numberOfPoints > 2 && (numberOfTriangles ?? 1) > 0 && elevationRange < flatTriangleThreshold)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "warning",
            ["type"] = "surface_flat_or_constant_elevation",
            ["surfaceName"] = name,
            ["elevationRange"] = elevationRange,
            ["flatTriangleThreshold"] = flatTriangleThreshold,
            ["message"] = $"Surface '{name}': elevation range {elevationRange:F4} is below flat-triangle threshold {flatTriangleThreshold:F4}. All triangles may be flat.",
          });
        }

        // Check for datum issues (elevations near zero when they shouldn't be)
        if (numberOfPoints > 0 && Math.Abs(minElevation) < 0.001 && Math.Abs(maxElevation) < 0.001)
        {
          findings.Add(new Dictionary<string, object?>
          {
            ["severity"] = "warning",
            ["type"] = "surface_possible_datum_issue",
            ["surfaceName"] = name,
            ["message"] = $"Surface '{name}': all elevations near zero — possible datum/units issue.",
          });
        }

        return new Dictionary<string, object?>
        {
          ["surfaceName"] = name,
          ["numberOfPoints"] = numberOfPoints,
          ["numberOfTriangles"] = numberOfTriangles,
          ["minElevation"] = minElevation,
          ["maxElevation"] = maxElevation,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking surface '{name}': {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckLabels
  // -------------------------------------------------------------------------

  /// <summary>Looks for alignments without a label set and alignments, surfaces and corridors without a style.
  /// <c>objectType</c> limits the check to one kind of object.
  /// All parameters are optional: <c>objectType</c> ("alignment", "surface" or "corridor"; any other value matches
  /// nothing), <c>checkMissing</c> (label sets, alignments only) and <c>checkStyleViolations</c> (styles), both on by
  /// default. Read-only. Returns <c>objectType</c> ("all" when not given), <c>findings</c> and <c>totalViolations</c>.
  /// Throws CIVIL3D.QC_ERROR on failure.</summary>
  public static Task<object?> QcCheckLabelsAsync(JsonObject? parameters)
  {
    var objectType = PluginRuntime.GetOptionalString(parameters, "objectType");
    var checkMissing = PluginRuntime.GetOptionalBool(parameters, "checkMissing") ?? true;
    var checkStyleViolations = PluginRuntime.GetOptionalBool(parameters, "checkStyleViolations") ?? true;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var findings = new List<Dictionary<string, object?>>();

        // Check alignments for missing labels
        if (checkMissing && (string.IsNullOrWhiteSpace(objectType) || objectType!.Equals("alignment", StringComparison.OrdinalIgnoreCase)))
        {
          foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
          {
            var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);
            var labelSetId = CivilObjectUtils.GetPropertyValue<ObjectId?>(alignment, "LabelSetId");
            if (labelSetId == null || labelSetId == ObjectId.Null)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "info",
                ["type"] = "alignment_no_label_set",
                ["objectName"] = alignment.Name,
                ["objectType"] = "alignment",
                ["message"] = $"Alignment '{alignment.Name}' has no label set assigned.",
              });
            }

            if (checkStyleViolations)
            {
              var styleId = CivilObjectUtils.GetPropertyValue<ObjectId?>(alignment, "StyleId");
              if (styleId == null || styleId == ObjectId.Null)
              {
                findings.Add(new Dictionary<string, object?>
                {
                  ["severity"] = "warning",
                  ["type"] = "alignment_no_style",
                  ["objectName"] = alignment.Name,
                  ["objectType"] = "alignment",
                  ["message"] = $"Alignment '{alignment.Name}' has no style assigned.",
                });
              }
            }
          }
        }

        // Check surfaces for missing style
        if (checkStyleViolations && (string.IsNullOrWhiteSpace(objectType) || objectType!.Equals("surface", StringComparison.OrdinalIgnoreCase)))
        {
          foreach (ObjectId surfaceId in civilDoc.GetSurfaceIds())
          {
            var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfaceId, OpenMode.ForRead);
            if (surface.StyleId == ObjectId.Null)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "warning",
                ["type"] = "surface_no_style",
                ["objectName"] = surface.Name,
                ["objectType"] = "surface",
                ["message"] = $"Surface '{surface.Name}' has no style assigned.",
              });
            }
          }
        }

        // Check corridors for missing style
        if (checkStyleViolations && (string.IsNullOrWhiteSpace(objectType) || objectType!.Equals("corridor", StringComparison.OrdinalIgnoreCase)))
        {
          foreach (ObjectId corridorId in civilDoc.CorridorCollection)
          {
            var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
            if (corridor.StyleId == ObjectId.Null)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "warning",
                ["type"] = "corridor_no_style",
                ["objectName"] = corridor.Name,
                ["objectType"] = "corridor",
                ["message"] = $"Corridor '{corridor.Name}' has no style assigned.",
              });
            }
          }
        }

        return new Dictionary<string, object?>
        {
          ["objectType"] = objectType ?? "all",
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking labels: {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcReportGenerate
  // -------------------------------------------------------------------------

  /// <summary>
  /// Runs the selected checks over every object in the drawing (alignments, corridors, pipe networks, surfaces and
  /// labels by default; profiles and drawing standards on request) and writes a plain-text report to
  /// <c>outputPath</c> (.txt, through FileBoundary.WriteAllTextAtomic: inside the export roots, no overwrite unless
  /// <c>overwrite</c>). The checks here are simpler than the single-object ones above. It runs as a read: the drawing
  /// isn't changed, only the file is written.
  /// <c>outputPath</c> is required; the include flags are optional: <c>includeAlignments</c>, <c>includeCorridors</c>,
  /// <c>includePipeNetworks</c>, <c>includeSurfaces</c>, <c>includeLabels</c> (on by default), <c>includeProfiles</c>
  /// (read but no profile section is written) and <c>includeDrawingStandards</c> (off by default).
  /// Returns the canonical <c>outputPath</c>, <c>checksRun</c>, <c>totalViolations</c>, <c>sectionsIncluded</c> and
  /// <c>reportGenerated</c>. FileBoundary's own codes pass through (CIVIL3D.PATH_NOT_ALLOWED, FILE_TYPE_NOT_ALLOWED,
  /// CONFLICT for an existing file without <c>overwrite</c>, FILE_IO_ERROR); other failures are CIVIL3D.QC_ERROR.
  /// </summary>
  public static Task<object?> QcReportGenerateAsync(JsonObject? parameters)
  {
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var includeAlignments = PluginRuntime.GetOptionalBool(parameters, "includeAlignments") ?? true;
    var includeProfiles = PluginRuntime.GetOptionalBool(parameters, "includeProfiles") ?? false;
    var includeCorridors = PluginRuntime.GetOptionalBool(parameters, "includeCorridors") ?? true;
    var includePipeNetworks = PluginRuntime.GetOptionalBool(parameters, "includePipeNetworks") ?? true;
    var includeSurfaces = PluginRuntime.GetOptionalBool(parameters, "includeSurfaces") ?? true;
    var includeLabels = PluginRuntime.GetOptionalBool(parameters, "includeLabels") ?? true;
    var includeDrawingStandards = PluginRuntime.GetOptionalBool(parameters, "includeDrawingStandards") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var sb = new StringBuilder();
        sb.AppendLine("=======================================================");
        sb.AppendLine("  CIVIL 3D QC REPORT");
        sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("=======================================================");
        sb.AppendLine();

        var totalViolations = 0;
        var checksRun = 0;
        var sectionsIncluded = new List<string>();

        // Alignments
        if (includeAlignments)
        {
          checksRun++;
          sectionsIncluded.Add("alignments");
          sb.AppendLine("--- ALIGNMENT CHECKS ---");
          foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
          {
            var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);
            var entityCollection = CivilObjectUtils.GetPropertyValue<object>(alignment, "Entities");
            var entityViolations = 0;
            if (entityCollection is IEnumerable enumerable)
            {
              var index = 0;
              foreach (var entity in enumerable)
              {
                if (entity == null) { index++; continue; }
                var length = CivilObjectUtils.EntityLength(entity);
                if (length <= 0)
                {
                  sb.AppendLine($"  [WARN] {alignment.Name} entity[{index}]: zero/negative length");
                  entityViolations++;
                }
                index++;
              }
            }
            if (entityViolations == 0)
              sb.AppendLine($"  [OK] {alignment.Name} (length={alignment.Length:F2})");
            totalViolations += entityViolations;
          }
          sb.AppendLine();
        }

        // Surfaces
        if (includeSurfaces)
        {
          checksRun++;
          sectionsIncluded.Add("surfaces");
          sb.AppendLine("--- SURFACE CHECKS ---");
          foreach (ObjectId surfId in civilDoc.GetSurfaceIds())
          {
            var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, surfId, OpenMode.ForRead);
            var gp = CivilObjectUtils.InvokeMethod(surface, "GetGeneralProperties");
            var npts = CivilObjectUtils.GetPropertyValue<int?>(gp, "NumberOfPoints") ?? 0;
            var ntri = CivilObjectUtils.GetPropertyValue<int?>(gp, "NumberOfTriangles") ?? 0;
            if (npts == 0)
            {
              sb.AppendLine($"  [ERROR] {surface.Name}: no data points");
              totalViolations++;
            }
            else if (ntri == 0)
            {
              sb.AppendLine($"  [ERROR] {surface.Name}: {npts} pts but no triangles — rebuild required");
              totalViolations++;
            }
            else
            {
              sb.AppendLine($"  [OK] {surface.Name} (pts={npts}, tri={ntri})");
            }
          }
          sb.AppendLine();
        }

        // Corridors
        if (includeCorridors)
        {
          checksRun++;
          sectionsIncluded.Add("corridors");
          sb.AppendLine("--- CORRIDOR CHECKS ---");
          foreach (ObjectId corrId in civilDoc.CorridorCollection)
          {
            var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corrId, OpenMode.ForRead);
            var isValid = CivilObjectUtils.GetBoolProperty(corridor, "IsValid") ?? true;
            if (!isValid)
            {
              sb.AppendLine($"  [ERROR] {corridor.Name}: IsValid=false — rebuild required");
              totalViolations++;
            }
            else
            {
              sb.AppendLine($"  [OK] {corridor.Name}");
            }
          }
          sb.AppendLine();
        }

        // Pipe Networks
        if (includePipeNetworks)
        {
          checksRun++;
          sectionsIncluded.Add("pipeNetworks");
          sb.AppendLine("--- PIPE NETWORK CHECKS ---");
          foreach (var networkObj in EnumerateAllPipeNetworks(civilDoc, transaction))
          {
            var networkName = CivilObjectUtils.GetName(networkObj) ?? "unknown";
            var pipeIds = GetChildObjectIds(networkObj, "GetPipeIds", "PipeIds", "Pipes", "PipeCollection").ToList();
            sb.AppendLine($"  [OK] {networkName} ({pipeIds.Count} pipes)");
          }
          sb.AppendLine();
        }

        // Labels
        if (includeLabels)
        {
          checksRun++;
          sectionsIncluded.Add("labels");
          sb.AppendLine("--- LABEL / STYLE CHECKS ---");
          foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
          {
            var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignId, OpenMode.ForRead);
            if (alignment.StyleId == ObjectId.Null)
            {
              sb.AppendLine($"  [WARN] Alignment '{alignment.Name}': no style");
              totalViolations++;
            }
          }
          sb.AppendLine();
        }

        // Drawing standards
        if (includeDrawingStandards)
        {
          checksRun++;
          sectionsIncluded.Add("drawingStandards");
          sb.AppendLine("--- DRAWING STANDARDS ---");
          var lt = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
          if (lt != null)
          {
            foreach (ObjectId layerId in lt)
            {
              var ltr = transaction.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
              if (ltr == null) continue;
              if (ltr.Name.Length > 64)
              {
                sb.AppendLine($"  [WARN] Layer '{ltr.Name}': name exceeds 64 characters");
                totalViolations++;
              }
            }
          }
          sb.AppendLine();
        }

        sb.AppendLine("=======================================================");
        sb.AppendLine($"  SUMMARY: {totalViolations} violation(s) across {checksRun} check(s)");
        sb.AppendLine("=======================================================");

        var canonicalOutputPath = FileBoundary.WriteAllTextAtomic(
          outputPath, sb.ToString(), Encoding.UTF8, overwrite, ".txt");

        return new Dictionary<string, object?>
        {
          ["outputPath"] = canonicalOutputPath,
          ["checksRun"] = checksRun,
          ["totalViolations"] = totalViolations,
          ["sectionsIncluded"] = sectionsIncluded,
          ["reportGenerated"] = true,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error generating QC report: {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // qcCheckDrawingStandards
  // -------------------------------------------------------------------------

  /// <summary>
  /// Checks every layer except 0 and Defpoints: missing <c>layerPrefix</c>, names over 64 characters or with spaces,
  /// frozen layers, and, on request, ByLayer lineweights (<c>checkLineweights</c>) and ByLayer/ByBlock colors
  /// (<c>checkColors</c>). Most findings are "info": they point at things a standard may forbid.
  /// All parameters are optional; <c>checkLineweights</c> and <c>checkColors</c> are off by default. Read-only.
  /// Returns <c>layerPrefix</c>, <c>findings</c> and <c>totalViolations</c>. Throws CIVIL3D.QC_ERROR on failure.
  /// </summary>
  public static Task<object?> QcCheckDrawingStandardsAsync(JsonObject? parameters)
  {
    var layerPrefix = PluginRuntime.GetOptionalString(parameters, "layerPrefix");
    var checkLineweights = PluginRuntime.GetOptionalBool(parameters, "checkLineweights") ?? false;
    var checkColors = PluginRuntime.GetOptionalBool(parameters, "checkColors") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var findings = new List<Dictionary<string, object?>>();

        var lt = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
        if (lt == null)
        {
          return new Dictionary<string, object?>
          {
            ["findings"] = findings,
            ["totalViolations"] = 0,
          };
        }

        foreach (ObjectId layerId in lt)
        {
          var ltr = transaction.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
          if (ltr == null) continue;

          var layerName = ltr.Name;

          // Skip "0" and "Defpoints" reserved layers
          if (layerName == "0" || layerName.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
            continue;

          // Prefix check
          if (!string.IsNullOrWhiteSpace(layerPrefix))
          {
            if (!layerName.StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase))
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "info",
                ["type"] = "layer_prefix_violation",
                ["layerName"] = layerName,
                ["expectedPrefix"] = layerPrefix,
                ["message"] = $"Layer '{layerName}' does not start with required prefix '{layerPrefix}'.",
              });
            }
          }

          // Name length check
          if (layerName.Length > 64)
          {
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "warning",
              ["type"] = "layer_name_too_long",
              ["layerName"] = layerName,
              ["nameLength"] = layerName.Length,
              ["message"] = $"Layer '{layerName}' has a name that exceeds 64 characters.",
            });
          }

          // Check for spaces in layer name (common standard violation)
          if (layerName.Contains(' '))
          {
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "info",
              ["type"] = "layer_name_contains_space",
              ["layerName"] = layerName,
              ["message"] = $"Layer '{layerName}' contains spaces — may cause compatibility issues.",
            });
          }

          // Lineweight check
          if (checkLineweights)
          {
            var lineweight = ltr.LineWeight;
            if (lineweight == LineWeight.ByLayer)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "info",
                ["type"] = "layer_lineweight_by_layer",
                ["layerName"] = layerName,
                ["message"] = $"Layer '{layerName}': lineweight set to ByLayer — consider explicit assignment for standards compliance.",
              });
            }
          }

          // Color check
          if (checkColors)
          {
            var color = ltr.Color;
            if (color.IsByLayer || color.IsByBlock)
            {
              findings.Add(new Dictionary<string, object?>
              {
                ["severity"] = "info",
                ["type"] = "layer_color_by_layer_or_block",
                ["layerName"] = layerName,
                ["colorName"] = color.ColorNameForDisplay,
                ["message"] = $"Layer '{layerName}': color is ByLayer/ByBlock — may not meet drawing standard requirements.",
              });
            }
          }

          // Frozen/off layer check
          if (ltr.IsFrozen)
          {
            findings.Add(new Dictionary<string, object?>
            {
              ["severity"] = "info",
              ["type"] = "layer_frozen",
              ["layerName"] = layerName,
              ["message"] = $"Layer '{layerName}' is frozen.",
            });
          }
        }

        return new Dictionary<string, object?>
        {
          ["layerPrefix"] = layerPrefix,
          ["findings"] = findings,
          ["totalViolations"] = findings.Count,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error checking drawing standards: {ex.Message}");
      }
    });
  }

  /// <summary>
  /// Fixes layer names to a standard (every layer except 0, Defpoints and xref layers): spaces to underscores
  /// (<c>fixSpaces</c>, on by default), <c>layerPrefix</c> added where missing, names cut to <c>maxNameLength</c>
  /// (default 64). <c>colorIndex</c> and <c>lineweight</c>, when given, are set on the layers that break the
  /// matching rule of qcCheckDrawingStandards (a color ByLayer or ByBlock, a lineweight ByLayer), or on every layer
  /// with <c>applyToAllLayers</c>; a layer that already has them isn't listed.
  /// A rename to a name that already exists is skipped and reported. With <c>dryRun</c> it only lists what would
  /// change. Approval-gated on the TypeScript side.
  /// All parameters are optional. Changes the drawing unless <c>dryRun</c> (one WriteAsync transaction, one undo step;
  /// a dry run still goes through WriteAsync but opens layers for read only). Returns <c>layerPrefix</c>,
  /// <c>applyToAllLayers</c>, <c>changedCount</c>, <c>changes</c> (per layer: <c>renamedTo</c> or
  /// <c>renameSkipped</c>/<c>targetName</c>, <c>colorIndex</c>, <c>lineweight</c>) and <c>dryRun</c>.
  /// Throws CIVIL3D.QC_ERROR when an API call fails.
  /// </summary>
  public static Task<object?> QcFixDrawingStandardsAsync(JsonObject? parameters)
  {
    var layerPrefix = PluginRuntime.GetOptionalString(parameters, "layerPrefix");
    var fixSpaces = PluginRuntime.GetOptionalBool(parameters, "fixSpaces") ?? true;
    var maxNameLength = PluginRuntime.GetOptionalInt(parameters, "maxNameLength") ?? 64;
    var colorIndex = PluginRuntime.GetOptionalInt(parameters, "colorIndex");
    var lineweight = PluginRuntime.GetOptionalInt(parameters, "lineweight");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var applyToAllLayers = PluginRuntime.GetOptionalBool(parameters, "applyToAllLayers") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      try
      {
        var changes = new List<Dictionary<string, object?>>();
        var lt = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
        if (lt == null)
        {
          return new Dictionary<string, object?>
          {
            ["changedCount"] = 0,
            ["changes"] = changes,
            ["dryRun"] = dryRun,
          };
        }

        foreach (ObjectId layerId in lt)
        {
          var layer = transaction.GetObject(layerId, dryRun ? OpenMode.ForRead : OpenMode.ForWrite) as LayerTableRecord;
          if (layer == null) continue;
          if (layer.Name == "0" || layer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
          // An xref's layer ("Base|C-ROAD") belongs to the xref: it can't be renamed here, and changes don't last.
          if (layer.IsDependent) continue;

          var originalName = layer.Name;
          var targetName = originalName;

          if (fixSpaces)
          {
            targetName = targetName.Replace(' ', '_');
          }

          if (!string.IsNullOrWhiteSpace(layerPrefix) &&
              !targetName.StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase))
          {
            targetName = $"{layerPrefix}{targetName}";
          }

          if (targetName.Length > maxNameLength)
          {
            targetName = targetName[..maxNameLength];
          }

          var layerChanges = new Dictionary<string, object?>
          {
            ["layerName"] = originalName,
          };

          if (!string.Equals(originalName, targetName, StringComparison.Ordinal))
          {
            if (!dryRun)
            {
              if (!TryRenameLayer(database, transaction, layer, targetName, out var finalName))
              {
                layerChanges["renameSkipped"] = true;
                layerChanges["targetName"] = targetName;
              }
              else
              {
                layerChanges["renamedTo"] = finalName;
              }
            }
            else
            {
              layerChanges["renamedTo"] = targetName;
            }
          }

          // The rules qcCheckDrawingStandards reports (checkColors, checkLineweights).
          var colorBreaksRule = layer.Color.IsByLayer || layer.Color.IsByBlock;
          var lineweightBreaksRule = layer.LineWeight == LineWeight.ByLayer;
          if (colorIndex.HasValue && (colorBreaksRule || applyToAllLayers)
              && !(layer.Color.ColorMethod == ColorMethod.ByAci && layer.Color.ColorIndex == colorIndex.Value))
          {
            layerChanges["colorIndex"] = colorIndex.Value;
            if (!dryRun)
            {
              layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex.Value);
            }
          }

          if (lineweight.HasValue && (lineweightBreaksRule || applyToAllLayers) && (int)layer.LineWeight != lineweight.Value)
          {
            layerChanges["lineweight"] = lineweight.Value;
            if (!dryRun)
            {
              layer.LineWeight = (LineWeight)lineweight.Value;
            }
          }

          if (layerChanges.Count > 1)
          {
            changes.Add(layerChanges);
          }
        }

        return new Dictionary<string, object?>
        {
          ["layerPrefix"] = layerPrefix,
          ["applyToAllLayers"] = applyToAllLayers,
          ["changedCount"] = changes.Count,
          ["changes"] = changes,
          ["dryRun"] = dryRun,
        };
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.QC_ERROR", $"Error fixing drawing standards: {ex.Message}");
      }
    });
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  /// <summary>The gravity pipe network named <paramref name="name"/> (case-insensitive), opened for read, or null.
  /// Despite its name it uses the typed GetPipeNetworkIds and reads the name through CivilObjectUtils.</summary>
  private static AcDbObject? FindPipeNetworkByNameReflection(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase))
        return obj;
    }

    return null;
  }

  /// <summary>Every gravity pipe network in the drawing, opened for read.</summary>
  private static IEnumerable<AcDbObject> EnumerateAllPipeNetworks(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction)
  {
    foreach (ObjectId objectId in civilDoc.GetPipeNetworkIds())
    {
      if (objectId == ObjectId.Null) continue;
      yield return transaction.GetObject(objectId, OpenMode.ForRead);
    }
  }

  /// <summary>
  /// Child object ids of <paramref name="owner"/>, trying each member name both as a method and as a property, for
  /// members named differently across releases. Null ids are skipped. Ids are not de-duplicated: a name that works as
  /// both, or two names that reach the same list, would give an id more than once.
  /// </summary>
  private static IEnumerable<ObjectId> GetChildObjectIds(AcDbObject owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      var methodResult = CivilObjectUtils.InvokeMethod(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(methodResult))
      {
        if (objectId != ObjectId.Null) yield return objectId;
      }

      var propVal = CivilObjectUtils.GetPropertyValue<object>(owner, memberName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(propVal))
      {
        if (objectId != ObjectId.Null) yield return objectId;
      }
    }
  }

  /// <summary>Renames a layer unless another layer already has the target name (returns false then, nothing changed).</summary>
  private static bool TryRenameLayer(Database database, Transaction transaction, LayerTableRecord layer, string targetName, out string finalName)
  {
    finalName = targetName;
    if (string.Equals(layer.Name, targetName, StringComparison.OrdinalIgnoreCase))
    {
      finalName = layer.Name;
      return true;
    }

    var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(targetName))
    {
      return false;
    }

    layer.Name = targetName;
    finalName = layer.Name;
    return true;
  }

  /// <summary>Whether a pipe has no reference surface: Civil 3D throws on the id when none was ever set.</summary>
  private static bool HasNoReferenceSurface(Pipe pipe)
  {
    try
    {
      return pipe.RefSurfaceId.IsNull;
    }
    catch (Exception exception) when (exception.GetType().Name == "CivilException")
    {
      return true;
    }
  }

  /// <summary>The first of several property names that has a value, for properties named differently across releases.</summary>
  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var v = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (v.HasValue) return v.Value;
    }
    return null;
  }
}
