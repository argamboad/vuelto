using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Scheduling;

/// <summary>
/// Runs registered <see cref="IScheduledJob"/>s on their own intervals (ADR-007). Wakes every
/// <see cref="ScheduledJobsOptions.TickInterval"/>, resolves a fresh DI scope (so jobs get scoped
/// services like a new <c>AppDbContext</c>), and runs each job whose interval has elapsed. One job's
/// failure is logged and isolated — it never stops the other jobs or the host.
/// <para>
/// In-process, single-instance for the platform; a distributed scheduler (Hangfire/Quartz) is the
/// documented swap-in when multi-node arrives (ADR-007). <see cref="RunDueJobsAsync"/> is separated
/// from the timer loop so it can be unit-tested without waiting on real time.
/// </para>
/// </summary>
public sealed class ScheduledJobsHost(
    IServiceScopeFactory scopeFactory,
    ScheduledJobsOptions options,
    TimeProvider clock,
    ILogger<ScheduledJobsHost> logger) : BackgroundService
{
    // Last run per job Name. The host is a singleton, so this persists across ticks.
    private readonly Dictionary<string, DateTimeOffset> _lastRun = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled-jobs tick failed; continuing");
            }

            try { await Task.Delay(options.TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs each registered job whose <see cref="IScheduledJob.Interval"/> has elapsed since its last
    /// run. A job that throws is logged and skipped — its failure never affects the others.
    /// </summary>
    public async Task RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var now = clock.GetUtcNow();

        foreach (var job in scope.ServiceProvider.GetServices<IScheduledJob>())
        {
            if (_lastRun.TryGetValue(job.Name, out var last) && now - last < job.Interval)
                continue; // not due yet

            try
            {
                await job.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown — let ExecuteAsync stop the host
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled job {Job} failed; will retry next interval", job.Name);
            }
            finally
            {
                // Advance even on failure so a broken job retries on its interval, not in a hot loop.
                _lastRun[job.Name] = now;
            }
        }
    }
}
