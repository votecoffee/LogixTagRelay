using System.Diagnostics;

namespace LogixTagRelay;

/// <summary>Monotonic fixed cadence. Expired slots are counted/skipped, never dispatched concurrently.</summary>
internal sealed class IntervalSchedule(int intervalMs, SessionDiagnostics diagnostics, bool immediate = true, bool heartbeat = false)
{
    private readonly long period = Math.Max(1, (long)(Stopwatch.Frequency * (intervalMs / 1000d)));
    private long next = Stopwatch.GetTimestamp() + (immediate ? 0 : Math.Max(1, (long)(Stopwatch.Frequency * (intervalMs / 1000d))));
    public bool Due => Stopwatch.GetTimestamp() >= next;
    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
    public long Begin()
    {
        long began = Stopwatch.GetTimestamp();
        diagnostics.ScheduleStart(TimeSpan.FromSeconds(Math.Max(0, began - next) / (double)Stopwatch.Frequency));
        return began;
    }
    public void Complete()
    {
        next += period; long now = Stopwatch.GetTimestamp();
        if (now > next)
        {
            long skipped = (now - next) / period + 1; next += skipped * period; diagnostics.SkipIntervals(skipped, heartbeat);
        }
    }
    public static Task WaitAsync(IntervalSchedule a, IntervalSchedule? b, CancellationToken ct)
    {
        var delay = b is null || a.Remaining <= b.Remaining ? a.Remaining : b.Remaining;
        return delay > TimeSpan.Zero ? Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, ct) : Task.CompletedTask;
    }
}
