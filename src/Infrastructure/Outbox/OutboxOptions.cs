namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// Tuning for the outbox dispatcher. Defaults are sensible for the in-process baseline; bind from
/// configuration if/when these need to vary per environment.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Delivery attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base retry backoff; the nth retry waits <c>Base * 2^(n-1)</c> (exponential).</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum messages claimed per processing pass.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Idle delay between polls when nothing is due.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
