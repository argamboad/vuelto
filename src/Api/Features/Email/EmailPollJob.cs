using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-4: the ingestion poller on the platform scheduler (<see cref="IScheduledJob"/>). Every tick it
/// stages each <c>active</c> connection that is due by its own <c>polling_interval_minutes</c> — one job
/// for all connections is simpler than a timer per connection with the same outcome. A throwing
/// connection is logged and skipped; the pass continues. A requested shutdown propagates promptly.
/// </summary>
public sealed class EmailPollJob(IRepository<EmailConnection> connections, IVoucherStagingService staging, TimeProvider clock, ILogger<EmailPollJob> logger) : IScheduledJob
{
    public string Name => "email-poll";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken cancellationToken = default) => await RunOnceAsync(cancellationToken);

    /// <summary>Stages every due connection; returns how many were polled (unit-testable without wall-clock timing).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var active = await connections.Query().Where(c => c.Status == EmailConnectionStatuses.Active).ToListAsync(cancellationToken);
        var polled = 0;

        foreach (var connection in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dueAt = (connection.LastPolledAt ?? connection.ImportFrom).AddMinutes(connection.PollingIntervalMinutes);
            if (dueAt > now) continue;

            try
            {
                await staging.StageConnectionAsync(connection, cancellationToken);
                polled++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll failed for connection {Id}; continuing", connection.Id);
            }
        }
        return polled;
    }
}
