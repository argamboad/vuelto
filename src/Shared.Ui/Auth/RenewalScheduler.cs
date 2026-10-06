namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// The keep-alive timer of an open session (ADR-002 addendum, 2026-09-22), pulled out of
/// <see cref="AuthService"/> (v4 audit T57): one pending wake-up at a time, on the injected clock, and the
/// growing pause between attempts while the server stays unreachable. It only keeps time — what a wake-up
/// does (renew, re-arm, give up) is the session's decision, handed in as <c>onDue</c>.
/// </summary>
internal sealed class RenewalScheduler(TimeProvider time, Func<Task> onDue)
{
    /// <summary>
    /// After a renewal that couldn't reach the server, how long until the next attempt — the FIRST pause; each
    /// further consecutive failure doubles it, up to <see cref="RenewRetryCap"/>, and a renewal resets it. Also the
    /// floor of every wait: a device clock running ahead of the server's would otherwise see every fresh token
    /// as already due, and renew in a tight loop.
    /// </summary>
    public static readonly TimeSpan RenewRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>The longest pause between renewal attempts while the server stays unreachable.</summary>
    public static readonly TimeSpan RenewRetryCap = TimeSpan.FromMinutes(5);

    /// <summary>Timers can't wait longer than ~49 days; a far-off expiry just wakes up early and looks again.</summary>
    public static readonly TimeSpan MaxRenewalWait = TimeSpan.FromDays(1);

    // Its own lock: on native the timer fires on a thread-pool thread while the UI thread re-arms it.
    private readonly object _gate = new();
    private ITimer? _timer;

    // Consecutive renewals that couldn't reach the server; reset by a renewal. Drives the retry pause.
    private int _failures;

    /// <summary>Arms the one wake-up, replacing any pending one; <paramref name="due"/> is clamped to [RenewRetryDelay, MaxRenewalWait].</summary>
    public void Schedule(TimeSpan due)
    {
        if (due < RenewRetryDelay)
            due = RenewRetryDelay;
        if (due > MaxRenewalWait)
            due = MaxRenewalWait;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = time.CreateTimer(_ => _ = onDue(), null, due, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops the pending wake-up, if any.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>The server is back: the next pause starts from the base again.</summary>
    public void ResetFailures() => _failures = 0;

    /// <summary>
    /// Arms the wake-up after one more attempt that couldn't reach the server: the base pause, doubled per
    /// consecutive failure, capped — so a background app doesn't wake twice a minute for as long as the server
    /// stays down (v4 UX-12).
    /// </summary>
    public void ScheduleRetry() => Schedule(RetryPause(++_failures));

    private static TimeSpan RetryPause(int failures) =>
        TimeSpan.FromTicks(Math.Min(RenewRetryDelay.Ticks << Math.Clamp(failures - 1, 0, 16), RenewRetryCap.Ticks));
}
