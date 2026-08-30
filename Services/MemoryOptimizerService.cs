using System.Diagnostics;

namespace BFCrewSync.Services;

public record OptimizeResult(int ProcessesTrimmed, long EstimatedBytesFreed, TimeSpan Elapsed);

/// <summary>
/// RAM/CPU optimizer. Trims working sets via SetProcessWorkingSetSize(-1,-1) +
/// EmptyWorkingSet, and can drop background Roblox client priority so the
/// foreground client stays responsive during a multi-instance session.
/// </summary>
public class MemoryOptimizerService
{
    /// <summary>
    /// Trims this app's own working set. Cheap, safe to call on a timer.
    /// </summary>
    public void TrimSelf()
    {
        var handle = NativeMethods.GetCurrentProcess();
        NativeMethods.SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
        NativeMethods.EmptyWorkingSet(handle);
    }

    /// <summary>
    /// "Optimize Now" — trims this process plus every RobloxPlayerBeta.exe
    /// instance found, and (optionally) drops trimmed processes to
    /// Below Normal priority so background clients yield CPU to the active one.
    /// </summary>
    public OptimizeResult OptimizeNow(bool alsoLowerPriority = true, string targetProcessName = "RobloxPlayerBeta")
    {
        var sw = Stopwatch.StartNew();
        int trimmed = 0;
        long freed = 0;

        // Self first.
        long before = Process.GetCurrentProcess().WorkingSet64;
        TrimSelf();
        Process.GetCurrentProcess().Refresh();
        freed += Math.Max(0, before - Process.GetCurrentProcess().WorkingSet64);
        trimmed++;

        foreach (var proc in Process.GetProcessesByName(targetProcessName))
        {
            try
            {
                long beforeBytes = proc.WorkingSet64;

                IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TRIM_ACCESS, false, (uint)proc.Id);
                if (handle == IntPtr.Zero)
                    continue;

                try
                {
                    NativeMethods.SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
                    NativeMethods.EmptyWorkingSet(handle);

                    if (alsoLowerPriority)
                        NativeMethods.SetPriorityClass(handle, NativeMethods.BELOW_NORMAL_PRIORITY_CLASS);

                    trimmed++;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }

                proc.Refresh();
                freed += Math.Max(0, beforeBytes - proc.WorkingSet64);
            }
            catch
            {
                // Process may have exited mid-loop, or access denied — skip and continue.
            }
            finally
            {
                proc.Dispose();
            }
        }

        sw.Stop();
        return new OptimizeResult(trimmed, freed, sw.Elapsed);
    }

    /// <summary>
    /// Restores a set of Roblox processes to Normal priority — call this on the
    /// window the user actually wants responsive right now, or on exit.
    /// </summary>
    public void RestorePriority(string targetProcessName = "RobloxPlayerBeta")
    {
        foreach (var proc in Process.GetProcessesByName(targetProcessName))
        {
            try
            {
                IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TRIM_ACCESS, false, (uint)proc.Id);
                if (handle == IntPtr.Zero) continue;
                try { NativeMethods.SetPriorityClass(handle, NativeMethods.NORMAL_PRIORITY_CLASS); }
                finally { NativeMethods.CloseHandle(handle); }
            }
            catch { /* ignore */ }
            finally { proc.Dispose(); }
        }
    }
}
