using System.Diagnostics;

namespace BFCrewSync.Services;

public record PerfSample(double CpuPercent, double SystemRamPercent, long SelfWorkingSetBytes);

/// <summary>
/// Lightweight perf sampler for the overlay. Uses a PerformanceCounter for
/// system-wide CPU% (cheap, no per-process WMI polling) and
/// GlobalMemoryStatusEx for system RAM load. Avoids allocating per tick.
/// </summary>
public class PerformanceMonitorService : IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private bool _warmedUp;

    public PerformanceMonitorService()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
        // First call always returns 0 — prime it once at construction.
        _cpuCounter.NextValue();
    }

    public PerfSample Sample()
    {
        if (!_warmedUp)
        {
            _warmedUp = true;
        }

        float cpu = _cpuCounter.NextValue();

        var mem = NativeMethods.MEMORYSTATUSEX.Create();
        double ramPercent = 0;
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            ramPercent = mem.dwMemoryLoad;
        }

        long selfWs = Process.GetCurrentProcess().WorkingSet64;

        return new PerfSample(Math.Clamp(cpu, 0, 100), ramPercent, selfWs);
    }

    public void Dispose() => _cpuCounter.Dispose();
}
