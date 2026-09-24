using Microsoft.Extensions.Configuration;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// Tuning for the outbox dispatcher. Defaults are sensible for the in-process baseline; only
/// <see cref="RetentionDays"/> is read from configuration (<see cref="FromConfiguration"/>).
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration key for <see cref="RetentionDays"/> (<c>Outbox__RetentionDays</c> in the environment).</summary>
    public const string RetentionDaysKey = "Outbox:RetentionDays";

    /// <summary>
    /// Days a finished row (sent or dead, payload already cleared) is kept before <c>OutboxRetentionJob</c> deletes
    /// it (v4 audit H7, decision #6). Long enough to answer "did that email go out?", short enough that the table
    /// does not grow forever.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Delivery attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base retry backoff; the nth retry waits <c>Base * 2^(n-1)</c> (exponential).</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum messages claimed per processing pass.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Idle delay between polls when nothing is due.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The defaults, with <see cref="RetentionDays"/> from configuration. Fails at startup below 1 day —
    /// 0 would delete a row the moment it finished, which is never what a typo meant.</summary>
    public static OutboxOptions FromConfiguration(IConfiguration configuration)
    {
        var days = configuration.GetValue<int?>(RetentionDaysKey) ?? 30;
        if (days < 1)
            throw new InvalidOperationException($"{RetentionDaysKey} must be at least 1 (got {days}).");
        return new OutboxOptions { RetentionDays = days };
    }
}
