namespace Vuelto.E2E.Tests;

// The testable core of BlazorBoot (v4 audit T55, R109): the retry decision, with the browser behind three
// small delegates so it runs under plain unit tests without Playwright. BlazorBoot.cs (the Playwright
// adapter) is the only caller in the E2E suite; tests/Api.Tests/E2E/BlazorBootTests.cs links this file and
// drives it with a scripted page. No Playwright or NUnit types in here — that is what keeps it linkable.

/// <summary>What the core needs from a browser page: the first navigation, a reload of wherever it landed, and a poll.</summary>
public interface IBootPage
{
    /// <summary>The original navigation — issued ONCE. A retry never repeats it (UX-8: a single-use URL would be spent twice).</summary>
    Task NavigateAsync();

    /// <summary>A full reload of the page the browser landed on — what a retry does.</summary>
    Task ReloadAsync();

    /// <summary><c>ok</c> once the app rendered its root, <c>loading</c> while the loader is up, <c>banner at NN%</c> when Blazor showed its error UI.</summary>
    Task<string> OutcomeAsync();
}

/// <summary>
/// The browser console, kept per page: console messages, page errors and failed requests, trimmed to the last
/// <see cref="Keep"/> lines. Lines are numbered as they arrive, so "everything since this navigation" is a
/// position, not a count — the old <c>Skip(count)</c> skipped too many once the queue had been trimmed, and a
/// genuine failed framework fetch could hide behind the trim (LB-DEP-6b).
/// </summary>
public sealed class BootConsole
{
    public const int Keep = 300;
    private readonly Queue<string> _lines = new();
    private long _total;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            _total++;
            while (_lines.Count > Keep) _lines.Dequeue();
        }
    }

    /// <summary>A position: pass it back to <see cref="Since"/> for the lines added after it.</summary>
    public long Mark()
    {
        lock (_lines) return _total;
    }

    /// <summary>The lines added after <paramref name="mark"/> that are still retained.</summary>
    public IReadOnlyList<string> Since(long mark)
    {
        lock (_lines)
        {
            var wanted = (int)Math.Min(_total - mark, _lines.Count);
            return wanted <= 0 ? [] : _lines.Skip(_lines.Count - wanted).ToArray();
        }
    }

    public override string ToString()
    {
        lock (_lines) return _lines.Count == 0 ? "(browser console empty)" : string.Join("\n", _lines);
    }
}

/// <summary>
/// The run's allowance of dead boots (UX-9): one per process, so a test process (a CI shard) that keeps losing
/// boots stops instead of quietly retrying its way to green — an app that crashes one boot in three would
/// otherwise pass every run. Counted on the dead boots themselves, not on the lines they print.
/// </summary>
public sealed class BootBudget(int limit)
{
    private int _spent;

    public int Limit => limit;
    public int Spent => _spent;

    /// <summary>Charges one dead boot; false when the allowance is already spent.</summary>
    public bool TryCharge() => Interlocked.Increment(ref _spent) <= limit;

    public void Reset() => Interlocked.Exchange(ref _spent, 0);
}

public static class BlazorBootCore
{
    /// <summary>Boots per navigation before giving up on it.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Dead boots per run (per test process — one CI shard) before the next one fails outright, whatever its
    /// reload would have done. The "Slowest journeys" step in both CI workflow copies reads the same number from
    /// the run's .trx and fails past it (R109); EnforcementGateTests holds the three together.
    /// </summary>
    public const int DeadBootsPerRun = 3;

    public static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Navigates once, then reloads the landed page while the boot dies of a NETWORK cause, within
    /// <see cref="MaxAttempts"/> and the run's <paramref name="budget"/>. Blazor's own error banner with no failed
    /// framework fetch behind it is the app's startup exception, not the network: it fails at once, unretried.
    /// Every dead boot is reported through <paramref name="note"/> as one <c>[blazor-boot] attempt n/N</c> line
    /// (CI counts those), and the exception that ends the boot carries the console.
    /// </summary>
    public static async Task BootAsync(IBootPage page, BootConsole console, BootBudget budget, Action<string> note, string what,
        TimeProvider? clock = null, Func<Task>? poll = null)
    {
        clock ??= TimeProvider.System;
        poll ??= () => Task.Delay(250);
        for (var attempt = 1; ; attempt++)
        {
            var mark = console.Mark(); // console lines before this navigation don't count against it
            if (attempt == 1) await page.NavigateAsync();
            else await page.ReloadAsync(); // the landed URL — never the original navigation again
            var verdict = await WatchAsync(page, console, mark, clock, poll);
            if (verdict == "ok") return;

            var line = $"[blazor-boot] attempt {attempt}/{MaxAttempts} of {what}: Blazor's loader died ({verdict})";
            note(line);
            if (!IsNetworkDeath(verdict))
                throw new InvalidOperationException($"{line} — the app's own startup failure, not the network; not retried.\nBrowser console:\n{console}");
            if (attempt == MaxAttempts)
                throw new InvalidOperationException($"{line} — giving up.\nBrowser console:\n{console}");
            if (!budget.TryCharge())
                throw new InvalidOperationException($"{line} — and the run's allowance of {budget.Limit} dead boots is spent; the environment is unhealthy, not this journey.\nBrowser console:\n{console}");
        }
    }

    /// <summary>A verdict a reload can cure: a framework fetch that failed, or a loader that never finished.</summary>
    public static bool IsNetworkDeath(string verdict) =>
        verdict.StartsWith("fetch failed: ", StringComparison.Ordinal)
        || verdict.StartsWith("still loading after ", StringComparison.Ordinal)
        || verdict.Contains(" after a failed fetch: ", StringComparison.Ordinal);

    // Polls the page and the console until the app is up, the boot is visibly dead, or the timeout passes.
    private static async Task<string> WatchAsync(IBootPage page, BootConsole console, long mark, TimeProvider clock, Func<Task> poll)
    {
        var started = clock.GetTimestamp();
        while (true)
        {
            var outcome = await page.OutcomeAsync();
            if (outcome == "ok") return outcome;

            // The silent death: a framework fetch failed (ERR_NETWORK_CHANGED, connection reset…) while the
            // loader is still up. Blazor never shows its banner for this one; the loader just sits at NN%.
            // ERR_ABORTED is not that: it is the browser cancelling the previous page's lazy downloads when
            // this navigation started, and it lands in the log just after the mark (9 needless reloads in
            // one shard before this filter).
            var dead = console.Since(mark).FirstOrDefault(IsDeadFrameworkFetch);
            if (outcome != "loading")
            {
                // Blazor's banner. Behind a failed framework fetch it is the network's doing and a reload
                // cures it; on its own it is our startup exception, and retrying would hide it (UX-9).
                return dead is null ? outcome : $"{outcome} after a failed fetch: {dead[(dead.IndexOf(' ') + 1)..]}";
            }
            if (dead is not null) return "fetch failed: " + dead[(dead.IndexOf(' ') + 1)..];

            if (clock.GetElapsedTime(started) > BootTimeout) return $"still loading after {BootTimeout.TotalSeconds:0} s";
            await poll();
        }
    }

    private static bool IsDeadFrameworkFetch(string line) =>
        (line.StartsWith("requestfailed: ", StringComparison.Ordinal) && line.Contains("/_framework/", StringComparison.Ordinal)
            && !line.EndsWith("net::ERR_ABORTED", StringComparison.Ordinal))
        || line.StartsWith("pageerror: Error: download ", StringComparison.Ordinal);
}
