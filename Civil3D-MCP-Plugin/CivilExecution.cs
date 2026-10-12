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

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(() => RunInCommandContextAsync(action));
  }

  /// <summary>
  /// Runs work that must not modify the drawing on the AutoCAD main thread in
  /// application context, so it leaves no undo step. Falls back to command context
  /// when a command is active (to avoid interleaving with the user's command) or no
  /// main-thread context was captured.
  /// </summary>
  public static async Task<T> ExecuteReadOnlyAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      var context = _mainThreadContext;
      if (context != null && !ReadsUseCommandContext)
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
      }

      return await RunInCommandContextAsync(action);
    });
  }

  private static async Task<T> RunInCommandContextAsync<T>(Func<Task<T>> action)
  {
    T? result = default;
    Exception? capturedException = null;

    await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
    {
      try
      {
        result = await action();
      }
      catch (Exception ex)
      {
        capturedException = ex;
      }
    }, null);

    if (capturedException != null)
    {
      throw capturedException;
    }

    return result!;
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
