namespace Vuelto.Infrastructure.Scheduling;

/// <summary>Tuning for <c>ScheduledJobsHost</c>. Defaults suit the in-process baseline.</summary>
public sealed class ScheduledJobsOptions
{
    /// <summary>
    /// How often the host wakes to check which jobs are due. This is the scheduling granularity — a
    /// job still runs no more often than its own <c>Interval</c>, so keep this ≤ the smallest job
    /// interval.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);
}
