using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  // Main-thread context captured at plugin load. Read-only work is posted here so it
  // runs outside command context: every ExecuteInCommandContextAsync call is recorded
  // as an EXECUTEFUNCTION undo step, even when nothing changes.
  private static SynchronizationContext? _mainThreadContext;
  private static int _mainThreadId;

  /// <summary>
  /// Reads use command context only when CIVIL3D_READ_CONTEXT=command (the
  /// pre-2027-fork behavior, which adds an empty undo step per read).
  /// </summary>
  private static readonly bool ReadsUseCommandContext =
    string.Equals(Environment.GetEnvironmentVariable("CIVIL3D_READ_CONTEXT"), "command", StringComparison.OrdinalIgnoreCase);

  /// <summary>Call on the AutoCAD main thread (IExtensionApplication.Initialize).</summary>
  internal static void CaptureMainThreadContext()
  {
    // AutoCAD installs a plain SynchronizationContext here, whose Post runs on the
    // thread pool, so marshal through the main thread's WPF dispatcher instead.
    _mainThreadId = Environment.CurrentManagedThreadId;
    _mainThreadContext = new System.Windows.Threading.DispatcherSynchronizationContext(
      System.Windows.Threading.Dispatcher.CurrentDispatcher);
    PluginLog.Info("CivilExecution", _mainThreadContext == null
      ? "No main-thread synchronization context; reads will use command context (adds undo steps)."
      : $"Read-only requests run outside command context via {_mainThreadContext.GetType().Name}.");
  }

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    Task<T> Run()
    {
      var (doc, civilDoc) = GetVerifiedActiveDocuments();
      var database = doc.Database;
      T result;

      // Reads take no lock: opening objects for read doesn't need one, and a lock
      // taken outside command context is recorded as a blank undo step.
      using var documentLock = write ? doc.LockDocument() : null;
      using var changes = ViewFollower.Track(database);
      using (var transaction = database.TransactionManager.StartTransaction())
      {
        result = action(doc, civilDoc, database, transaction);

        if (write)
        {
          transaction.Commit();
        }
      }

      if (write)
      {
        ViewFollower.Follow(doc, changes);
      }

      return Task.FromResult(result);
    }

    return write ? await ExecuteInCommandContextAsync(Run) : await ExecuteReadOnlyAsync(Run);
  }

  /// <summary>How long a request waits for the user's (or an earlier) command to finish (CIVIL3D_IDLE_WAIT_SECONDS, default 30).</summary>
  private static TimeSpan IdleWait
  {
    get
    {
      var raw = Environment.GetEnvironmentVariable("CIVIL3D_IDLE_WAIT_SECONDS");
      return int.TryParse(raw, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);
    }
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      // ExecuteInCommandContextAsync queued while another command is active can stay pending
      // forever and block every later request, so start only when Civil 3D is idle.
      await WaitForIdleAsync();
      return await RunInCommandContextAsync(action);
    });
  }

  /// <summary>
  /// Runs work that must not modify the drawing on the AutoCAD main thread in
  /// application context, so it leaves no undo step. While a command is active it
  /// retries until the command ends (never queuing into command context, which can
  /// hang), and fails with CIVIL3D.HOST_BUSY after the idle wait.
  /// </summary>
  /// <summary>Runs <paramref name="action"/> on AutoCAD's main thread, outside any command or transaction: where a
  /// window may be shown (PointCreationGate asks the user there). Runs it in place when already on that thread, or
  /// when the main thread's context was never captured.</summary>
  public static Task<T> OnMainThreadAsync<T>(Func<Task<T>> action)
  {
    var context = _mainThreadContext;
    if (context == null || Environment.CurrentManagedThreadId == _mainThreadId)
    {
      return action();
    }

    return PostToMainThreadAsync(context, action);
  }

  public static async Task<T> ExecuteReadOnlyAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      var context = _mainThreadContext;
      if (context == null || ReadsUseCommandContext)
      {
        await WaitForIdleAsync();
        return await RunInCommandContextAsync(action);
      }

      var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
      var deadline = DateTime.UtcNow + IdleWait;
      while (true)
      {
        var (ran, value) = await PostToMainThreadAsync(context, async () =>
        {
          if (Environment.CurrentManagedThreadId != _mainThreadId || IsCommandActive())
          {
            return (false, default(T)!);
          }

          return (true, await action());
        });

        if (ran)
        {
          return value;
        }

        if (DateTime.UtcNow > deadline)
        {
          throw BusyError();
        }

        await Task.Delay(200, cancellationToken);
      }
    });
  }

  /// <summary>Waits (off the main thread) until no AutoCAD command is active.</summary>
  private static async Task WaitForIdleAsync()
  {
    var context = _mainThreadContext;
    if (context == null)
    {
      return;
    }

    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    var deadline = DateTime.UtcNow + IdleWait;
    while (await PostToMainThreadAsync(context, () => Task.FromResult(IsCommandActive())))
    {
      if (DateTime.UtcNow > deadline)
      {
        throw BusyError();
      }

      await Task.Delay(200, cancellationToken);
    }
  }

  private static JsonRpcDispatchException BusyError()
  {
    var names = CommandLineMonitor.ActiveCommandNames();
    return new JsonRpcDispatchException(
      "CIVIL3D.HOST_BUSY",
      $"Civil 3D has had a command running for {IdleWait.TotalSeconds:0}s" + (names.Length > 0 ? $" ({names})" : string.Empty) +
      ". Finish or cancel it (Esc) and try again. Use civil3d_drawing action=command_line to see what it is waiting for.");
  }

  private static async Task<T> RunInCommandContextAsync<T>(Func<Task<T>> action)
  {
    var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    const int Pending = 0, Running = 1, Abandoned = 2;
    var state = Pending;

    async Task Callback(object? _)
    {
      // Never run late: whichever of "start" and "give up" happens first wins, atomically.
      if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending)
      {
        PluginLog.Warn("CivilExecution", "Command-context work started after its request was abandoned; skipped.");
        return;
      }

      started.TrySetResult(true);
      try
      {
        completion.TrySetResult(await action());
      }
      catch (Exception ex)
      {
        completion.TrySetException(ex);
      }
    }

    // DocumentManager.ExecuteInCommandContextAsync must be called on the main thread in
    // application context; from a background thread the work can stay queued forever.
    var context = _mainThreadContext;
    if (context != null)
    {
      await PostToMainThreadAsync(context, () =>
      {
        _ = App.DocumentManager.ExecuteInCommandContextAsync(Callback, null);
        return Task.FromResult(true);
      });
    }
    else
    {
      _ = App.DocumentManager.ExecuteInCommandContextAsync(Callback, null);
    }

    PluginLog.Debug("CivilExecution", "Queued command-context work.");
    if (await Task.WhenAny(started.Task, Task.Delay(IdleWait)) != started.Task &&
        Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending)
    {
      PluginLog.Warn("CivilExecution", $"Command-context work did not start within {IdleWait.TotalSeconds:0}s; abandoned (it will not run).");
      throw BusyError();
    }

    return await completion.Task;
  }

  /// <summary>Runs a small read on the main thread outside the request queue, with a timeout.</summary>
  internal static async Task<T> OnMainThreadAsync<T>(Func<T> action, TimeSpan timeout)
  {
    var context = _mainThreadContext ?? throw new TimeoutException("No main-thread context.");
    var task = PostToMainThreadAsync(context, () => Task.FromResult(action()));
    if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
    {
      throw new TimeoutException();
    }

    return await task;
  }

  private static Task<T> PostToMainThreadAsync<T>(SynchronizationContext context, Func<Task<T>> action)
  {
    var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Flow the request's AsyncLocals (drawing identity, cancellation) explicitly.
    var executionContext = ExecutionContext.Capture();
    context.Post(_ =>
    {
      void Start(object? __)
      {
        _ = RunAsync();
      }

      async Task RunAsync()
      {
        try
        {
          completion.TrySetResult(await action());
        }
        catch (Exception ex)
        {
          completion.TrySetException(ex);
        }
      }

      if (executionContext != null)
      {
        ExecutionContext.Run(executionContext, Start, null);
      }
      else
      {
        Start(null);
      }
    }, null);
    return completion.Task;
  }

  private static bool IsCommandActive()
  {
    try
    {
      return Convert.ToInt32(App.GetSystemVariable("CMDACTIVE")) != 0;
    }
    catch (Exception)
    {
      return true;
    }
  }

  /// <summary>
  /// Returns the active AutoCAD and Civil 3D documents, failing if no drawing is
  /// open or if the active drawing changed while the request was queued.
  /// Must be called on the AutoCAD main thread (inside command context).
  /// </summary>
  internal static (Document Document, CivilDocument CivilDocument) GetVerifiedActiveDocuments()
  {
    var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
    var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
    if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
        !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
    }
    var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
    return (doc, civilDoc);
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      return await action();
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }
}
