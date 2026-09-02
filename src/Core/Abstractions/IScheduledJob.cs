namespace Perezosoft.Core.Abstractions;

/// <summary>
/// A recurring background job run by the <c>ScheduledJobsHost</c> on its own <see cref="Interval"/>
/// (ADR-007). Add a job by registering an <see cref="IScheduledJob"/> in DI — no host edits. Each run
/// gets a fresh DI scope, so a job may depend on scoped services (a new <c>AppDbContext</c>).
/// <para>
/// Should be resilient: a throwing run is logged and isolated (it never stops other jobs or the host),
/// and the job retries on its next interval. Long-running work must honour the cancellation token.
/// </para>
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable identifier used to track this job's last run; unique across registered jobs.</summary>
    string Name { get; }

    /// <summary>Minimum time between runs. The job runs no more often than this.</summary>
    TimeSpan Interval { get; }

    Task RunAsync(CancellationToken cancellationToken = default);
}
