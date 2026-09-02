using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Perezosoft.Infrastructure.Outbox;

/// <summary>
/// Drains the outbox on an interval. Resolves a fresh DI scope per pass (the processor and its
/// <c>AppDbContext</c> are scoped). A single in-process poller is the platform baseline; the
/// processor's <c>FOR UPDATE SKIP LOCKED</c> claim keeps it correct if ever run multi-instance,
/// and a real scheduler/broker is a documented swap-in (ADR-007).
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxOptions options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                var processed = await processor.ProcessDueAsync(stoppingToken);

                // Nothing was due — idle until the next poll. After a non-empty pass, loop straight
                // back to drain any remaining backlog without waiting.
                if (processed == 0)
                    await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatcher pass failed; backing off before retry");
                await Task.Delay(options.PollInterval, stoppingToken);
            }
        }
    }
}
