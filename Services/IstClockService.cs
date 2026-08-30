namespace BFCrewSync.Services;

public record ClockTick(DateTime IstNow, string Formatted);

/// <summary>
/// IST (UTC+5:30) is fixed-offset with no DST, so this needs no TimeZoneInfo
/// database lookups — just DateTimeOffset math, which keeps the sampling
/// tick cheap enough to run every ~30ms without spiking CPU.
/// </summary>
public static class IstClockService
{
    public static readonly TimeSpan IstOffset = new(5, 30, 0);

    public static DateTime NowIst() => DateTimeOffset.UtcNow.ToOffset(IstOffset).DateTime;

    public static string FormatPrecise(DateTime istTime) => istTime.ToString("HH:mm:ss.fff");

    /// <summary>
    /// Parses a user-entered "HH:mm:ss" (or "HH:mm") IST target time and
    /// resolves it to the next occurrence of that wall-clock time in IST —
    /// today if it hasn't passed yet, tomorrow otherwise — then returns the
    /// equivalent Unix epoch seconds (UTC-based, so DST/offset-proof).
    /// </summary>
    public static bool TryResolveTargetEpoch(string hhmmss, out long targetEpochSeconds, out DateTime resolvedIst)
    {
        targetEpochSeconds = 0;
        resolvedIst = default;

        if (!TimeSpan.TryParse(hhmmss, out var timeOfDay))
            return false;

        var nowIst = NowIst();
        var candidate = nowIst.Date + timeOfDay;
        if (candidate <= nowIst)
            candidate = candidate.AddDays(1);

        resolvedIst = candidate;

        // Convert the IST wall-clock candidate back to a true UTC instant.
        var asUtc = DateTime.SpecifyKind(candidate - IstOffset, DateTimeKind.Utc);
        targetEpochSeconds = new DateTimeOffset(asUtc).ToUnixTimeSeconds();
        return true;
    }

    /// <summary>
    /// Resolves a target as "N minutes from now" instead of a wall-clock time.
    /// </summary>
    public static (long targetEpochSeconds, DateTime resolvedIst) ResolveTargetFromOffset(int minutesFromNow)
    {
        var target = DateTimeOffset.UtcNow.AddMinutes(minutesFromNow);
        return (target.ToUnixTimeSeconds(), target.ToOffset(IstOffset).DateTime);
    }

    public static TimeSpan CountdownTo(long targetEpochSeconds)
    {
        var target = DateTimeOffset.FromUnixTimeSeconds(targetEpochSeconds);
        var remaining = target - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "00:00:00.000";
        return remaining.ToString(remaining.TotalHours >= 1 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff");
    }
}
