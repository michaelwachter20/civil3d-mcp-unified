using System.Runtime.InteropServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// What keeps a long call or low memory from turning into a lost session. A call runs on Civil 3D's main thread, and
/// nothing can stop it there: native Civil 3D calls can't be interrupted. So this says what's happening rather than let
/// requests pile up behind it, and refuses new work while the PC can't afford it.
/// </summary>
internal static class HostGuard
{
  /// <summary>Past this a call is treated as a long one: new requests are refused at once and the log says it's running.</summary>
  public const long LongCallMs = 60_000;

  /// <summary>
  /// Refuse new work below this much commit charge (memory plus page file) left. Running out is what took Civil 3D and
  /// the PC down on the 7 GB reference machine. New work could take the rest; a refusal costs a retry.
  /// </summary>
  public const long MinimumFreeCommitMb = 300;

  /// <summary>How many times, a second apart, to read a low figure again before believing it.</summary>
  private const int MemoryRechecks = 6;

  /// <summary>What to tell a new request while a long call has the main thread, or null when it can go ahead.</summary>
  public static string? BusyRefusal(string? operation, long runningMs)
  {
    if (operation == null || runningMs < LongCallMs) return null;

    return $"Civil 3D is still working on {operation}, {runningMs / 1000} s so far, and can't do anything else until it finishes. "
      + "It may show as Not Responding: that isn't a crash. Wait, and don't run this again or end Civil 3D.";
  }

  /// <summary>What to tell a new request when memory is nearly out, or null when there's enough.</summary>
  public static string? MemoryRefusal(long freeCommitMb)
  {
    if (freeCommitMb < 0 || freeCommitMb >= MinimumFreeCommitMb) return null;

    return $"Only {freeCommitMb} MB of memory is left on this PC, so nothing new was started. "
      + "Save your work and close other programs, or restart Civil 3D.";
  }

  /// <summary>
  /// The refusal for new work now, or null. A low reading alone proves nothing: Windows grows an automatic page file
  /// when it runs short, which took 417 MB left to 1,650 MB in 8 s with nothing released. So it has to stay low.
  /// </summary>
  public static string? CheckMemory(Func<long> freeCommitMb, Action wait)
  {
    var refusal = MemoryRefusal(freeCommitMb());
    for (var check = 0; refusal != null && check < MemoryRechecks; check++)
    {
      wait();
      refusal = MemoryRefusal(freeCommitMb());
    }

    return refusal;
  }

  /// <summary>Commit charge left in MB, or -1 when Windows won't say.</summary>
  public static long FreeCommitMb()
  {
    var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
    return GlobalMemoryStatusEx(ref status) ? (long)(status.AvailablePageFile / (1024 * 1024)) : -1;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MemoryStatus
  {
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
