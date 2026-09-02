using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure.Scheduling;

namespace Perezosoft.Api.Tests.Scheduling;

/// <summary>
/// Drives JOBS-3 (ADR-007): the host runs due jobs, isolates a failing job from the others, and
/// doesn't re-run a job before its interval elapses. Pure unit tests over a real minimal DI container
/// (no DB) — the host resolves jobs through a scope, so a real <c>IServiceScopeFactory</c> is used.
/// </summary>
public class ScheduledJobsHostTests
{
    [Fact]
    public async Task RunDueJobs_RunsARegisteredJob()
    {
        var job = new RecordingJob("a", TimeSpan.Zero);
        await NewHost(job).RunDueJobsAsync();
        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task RunDueJobs_OneJobFailing_DoesNotStopOthers()
    {
        var throwing = new ThrowingJob("boom", TimeSpan.Zero);
        var recording = new RecordingJob("ok", TimeSpan.Zero);

        // Must not throw, and the healthy job must still run.
        await NewHost(throwing, recording).RunDueJobsAsync();

        Assert.Equal(1, recording.Runs);
    }

    [Fact]
    public async Task RunDueJobs_DoesNotRerunBeforeInterval()
    {
        var job = new RecordingJob("a", TimeSpan.FromHours(1));
        var host = NewHost(job);

        await host.RunDueJobsAsync();
        await host.RunDueJobsAsync(); // same wall-clock; interval not elapsed

        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task RunDueJobs_RerunsAfterTheIntervalElapses()
    {
        // v3 TB-BILL backfill (T45b): the other half of the interval contract — the job DOES run
        // again once its interval has passed (provable only with an injected clock).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var job = new RecordingJob("a", TimeSpan.FromHours(1));
        var host = NewHost(clock, job);

        await host.RunDueJobsAsync();
        clock.Advance(TimeSpan.FromMinutes(59));
        await host.RunDueJobsAsync(); // not yet
        clock.Advance(TimeSpan.FromMinutes(2));
        await host.RunDueJobsAsync(); // interval elapsed → runs again

        Assert.Equal(2, job.Runs);
    }

    private static ScheduledJobsHost NewHost(params IScheduledJob[] jobs) => NewHost(TimeProvider.System, jobs);

    private static ScheduledJobsHost NewHost(TimeProvider clock, params IScheduledJob[] jobs)
    {
        var services = new ServiceCollection();
        foreach (var j in jobs) services.AddSingleton<IScheduledJob>(j);
        var provider = services.BuildServiceProvider();

        return new ScheduledJobsHost(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ScheduledJobsOptions(),
            clock,
            NullLogger<ScheduledJobsHost>.Instance);
    }
}

internal sealed class RecordingJob(string name, TimeSpan interval) : IScheduledJob
{
    public int Runs { get; private set; }
    public string Name => name;
    public TimeSpan Interval => interval;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Runs++;
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingJob(string name, TimeSpan interval) : IScheduledJob
{
    public string Name => name;
    public TimeSpan Interval => interval;

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("job boom");
}
