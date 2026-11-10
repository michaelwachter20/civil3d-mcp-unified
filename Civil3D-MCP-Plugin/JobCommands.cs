// Background jobs for a few long operations, and status/cancel for any job.
// JSON-RPC methods (CommandDispatcher): startJob, getJobStatus, cancelJob. Called by src/tools/domains/jobDomain.ts
// (civil3d_job). startJob re-enters CommandDispatcher with the job's plugin method on a worker task, so the work still
// goes through CivilExecution (host queue, drawing check, one undo step for a write) and runs only on the drawing that
// was active when the job started. getJobStatus/cancelJob only read JobRegistry: no host queue, no drawing access.
// Any files a job writes (PDFs) go through FileBoundary inside the called method.
using System.Collections;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin methods behind civil3d_job (src/tools/domains/jobDomain.ts): <c>startJob</c> runs one of a few long
/// operations in the background, and <c>getJobStatus</c> / <c>cancelJob</c> look at or stop any job in JobRegistry,
/// including corridor rebuilds started by civil3d_corridor. Jobs exist so long work doesn't hit the server's command
/// timeout: the call answers at once with a job id and the caller polls.
/// </summary>
public static class JobCommands
{
  /// <summary>The operations startJob accepts, mapped to the plugin method each one runs.</summary>
  private static readonly IReadOnlyDictionary<string, string> LongRunningOperations =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["publish_sheet_pdf"] = "publishSheetPdf",
      ["surface_dem_import"] = "createSurfaceFromDem",
      ["bulk_qc_report"] = "qcReportGenerate",
    };

  /// <summary>
  /// Starts <c>operation</c> (publish_sheet_pdf, surface_dem_import or bulk_qc_report; anything else is
  /// CIVIL3D.INVALID_INPUT) as a background job with <c>parameters</c> passed to its plugin method, and returns the
  /// job at once. The work goes through <c>CommandDispatcher</c> like a normal request, so it takes its turn in the
  /// host queue and runs only on the drawing that was active when the job started. It waits 250 ms first so an
  /// immediate cancel still stops it; a cancel that arrives once the Civil 3D work has begun can't undo it, and the
  /// job is then reported completed with a warning saying so. Errors in the work become a failed job, never a thrown
  /// error here; only an unknown operation or a full registry (CIVIL3D.HOST_BUSY) is thrown.
  /// </summary>
  public static Task<object?> StartJobAsync(JsonObject? parameters)
  {
    var operation = PluginRuntime.GetRequiredString(parameters, "operation");
    if (!LongRunningOperations.TryGetValue(operation, out var pluginMethod))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Unsupported job operation '{operation}'. Supported operations: {string.Join(", ", LongRunningOperations.Keys)}. Corridor rebuilds already return jobs through civil3d_corridor.");
    }

    var operationParameters = parameters?["parameters"] as JsonObject;
    operationParameters = operationParameters?.DeepClone() as JsonObject ?? new JsonObject();
    var requestId = PluginRuntime.GetCurrentRequestId();
    var job = JobRegistry.Create(
      $"Queued {operation}",
      operation,
      requestId,
      PluginRuntime.GetActiveDrawingIdentity());
    job.CancellationSource = new CancellationTokenSource();
    var cancellationToken = job.CancellationSource.Token;

    _ = Task.Run(async () =>
    {
      try
      {
        await PluginRuntime.RunWithRequestContextAsync(
          pluginMethod,
          $"{requestId ?? "job"}:job:{job.JobId}",
          cancellationToken,
          job.DrawingIdentity,
          async () =>
          {
            // Keep the queued state observable long enough for a caller to
            // issue an immediate cooperative cancellation after StartJob.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            JobRegistry.Progress(job.JobId, 10, $"Starting {operation}", null);
            PluginLog.Info("Job", $"Starting {operation} (job {job.JobId}, request {requestId ?? "none"}).");
            var result = await CommandDispatcher.DispatchAsync(pluginMethod, operationParameters, cancellationToken);
            var warnings = ExtractWarnings(result).ToList();
            if (cancellationToken.IsCancellationRequested)
            {
              warnings.Add("Cancellation was requested after non-interruptible Civil 3D host work began; the committed result is reported as completed.");
            }
            JobRegistry.Complete(job.JobId, result, warnings);
            PluginLog.Info("Job", $"Completed {operation} (job {job.JobId}).");
            return result;
          });
      }
      catch (OperationCanceledException)
      {
        JobRegistry.AcknowledgeCancellation(job.JobId);
        PluginLog.Info("Job", $"Cancelled {operation} (job {job.JobId}).");
      }
      catch (JsonRpcDispatchException ex)
      {
        JobRegistry.Fail(job.JobId, ex.Message, ex.Code);
        PluginLog.Info("Job", $"Failed {operation} (job {job.JobId}) [{ex.Code}]: {ex.Message}");
      }
      catch (Exception ex)
      {
        JobRegistry.Fail(job.JobId, ex.Message, ex.GetType().Name);
        PluginLog.Error("Job", $"Unhandled failure in {operation} (job {job.JobId}).", ex);
      }
      finally
      {
        try
        {
          job.CancellationSource?.Dispose();
        }
        catch (ObjectDisposedException)
        {
          // Worker and cancellation raced; the source is already released.
        }
        job.CancellationSource = null;
      }
    });

    return Task.FromResult<object?>(ToResponse(job));
  }

  /// <summary>The job <c>jobId</c> as it stands now (see <see cref="ToResponse"/>); CIVIL3D.OBJECT_NOT_FOUND for an unknown or expired id. Doesn't use the host queue.</summary>
  public static object? GetJobStatus(JsonObject? parameters)
  {
    var jobId = PluginRuntime.GetRequiredString(parameters, "jobId");
    return ToResponse(JobRegistry.Get(jobId));
  }

  /// <summary>Asks job <c>jobId</c> to stop (<see cref="JobRegistry.Cancel"/>) and returns it; it may still be "running" until its worker stops.</summary>
  public static object? CancelJob(JsonObject? parameters)
  {
    var jobId = PluginRuntime.GetRequiredString(parameters, "jobId");
    return ToResponse(JobRegistry.Cancel(jobId));
  }

  /// <summary>The non-blank entries of a dictionary result's "warnings" list, if it has one, to copy onto the job.</summary>
  private static IEnumerable<string> ExtractWarnings(object? result)
  {
    if (result is not IDictionary<string, object?> dictionary
      || !dictionary.TryGetValue("warnings", out var warningValue)
      || warningValue is not IEnumerable warnings
      || warningValue is string)
    {
      return [];
    }

    return warnings.Cast<object?>()
      .Select(value => value?.ToString())
      .Where(value => !string.IsNullOrWhiteSpace(value))
      .Cast<string>()
      .ToArray();
  }

  /// <summary>The job answer every job method returns: the job's fields, its result once finished, and the registry's
  /// counts. The fields are read under the record's SyncRoot (as JobRegistry writes them), so a job finishing on its
  /// worker meanwhile can't give a torn answer (state "completed" without its result, or a Warnings list changing while
  /// it is copied). The registry's counts are read first, outside that lock.</summary>
  private static Dictionary<string, object?> ToResponse(JobRecord job)
  {
    var stats = JobRegistry.GetStats();
    lock (job.SyncRoot)
    {
      return new Dictionary<string, object?>
      {
        ["jobId"] = job.JobId,
        ["state"] = job.State,
        ["operation"] = job.Operation,
        ["progressPercent"] = job.ProgressPercent,
        ["currentPhase"] = job.CurrentPhase,
        ["estimatedRemainingSeconds"] = job.EstimatedRemainingSeconds,
        ["result"] = job.Result,
        ["createdAt"] = job.CreatedAt,
        ["completedAt"] = job.CompletedAt,
        ["durationMs"] = job.DurationMs,
        ["requestId"] = job.RequestId,
        ["drawingIdentity"] = job.DrawingIdentity,
        ["failureCategory"] = job.FailureCategory,
        ["cancellationRequested"] = job.CancellationRequested,
        ["warnings"] = job.Warnings.ToArray(),
        ["registry"] = new Dictionary<string, object?>
        {
          ["total"] = stats.Total,
          ["running"] = stats.Running,
          ["capacity"] = stats.Capacity,
          ["terminalRetentionMinutes"] = stats.TerminalRetentionMinutes,
        },
      };
    }
  }
}
