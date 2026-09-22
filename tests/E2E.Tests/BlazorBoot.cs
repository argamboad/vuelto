using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Every full navigation boots the Blazor WebAssembly app from scratch: the loader fetches the runtime
/// and ~100 assemblies, shows "NN%", then hands over to the app. On the CI machine that boot has died in
/// two ways, neither of them ours: Blazor's own banner ("NN% An unhandled error has occurred."), and a
/// silent one where every `_framework/*` fetch fails with <c>net::ERR_NETWORK_CHANGED</c> — Chromium
/// aborting in-flight requests because a network interface appeared or vanished in its namespace (a
/// container starting or stopping on a Docker daemon the job shares). Either way the journey would wait
/// its whole timeout for an element that will never render. This helper does what a person does: reloads.
/// It also keeps the browser console per page, so a boot that fails for good, and any failed journey,
/// reports what the browser saw — which is how the second failure mode was found.
/// </summary>
public static class BlazorBoot
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(60);
    private static readonly ConditionalWeakTable<IPage, ConcurrentQueue<string>> Consoles = new();
    private static readonly Lock Gate = new();

    // 'ok' once the app rendered its root (the loader is gone); 'banner …' when Blazor showed its error UI.
    private const string BootOutcome = """
        () => {
          const banner = document.getElementById('blazor-error-ui');
          if (banner && getComputedStyle(banner).display !== 'none') {
            const pct = getComputedStyle(document.documentElement).getPropertyValue('--blazor-load-percentage-text');
            return 'banner at ' + (pct.trim() || '?');
          }
          return document.querySelector('#app .loading-progress-text') ? 'loading' : 'ok';
        }
        """;

    /// <summary>Records console messages, page errors and failed requests for <paramref name="page"/> (idempotent).</summary>
    public static ConcurrentQueue<string> Watch(IPage page)
    {
        lock (Gate)
        {
            if (Consoles.TryGetValue(page, out var existing)) return existing;
            var log = new ConcurrentQueue<string>();
            Consoles.Add(page, log);
            page.Console += (_, m) => Add(log, $"console.{m.Type}: {m.Text}");
            page.PageError += (_, e) => Add(log, $"pageerror: {e}");
            page.RequestFailed += (_, r) => Add(log, $"requestfailed: {r.Method} {r.Url} — {r.Failure}");
            return log;
        }
    }

    public static string ConsoleOf(IPage page) =>
        Consoles.TryGetValue(page, out var log) && !log.IsEmpty ? string.Join("\n", log) : "(browser console empty)";

    /// <summary>Full navigation to <paramref name="url"/>, reloaded while Blazor's boot fails.</summary>
    public static Task GotoAsync(IPage page, string url) => BootAsync(page, () => page.GotoAsync(url), url);

    /// <summary>Full reload of the current page, repeated while Blazor's boot fails.</summary>
    public static Task ReloadAsync(IPage page) => BootAsync(page, () => page.ReloadAsync(), "reload of " + page.Url);

    private static async Task BootAsync(IPage page, Func<Task<IResponse?>> navigate, string what)
    {
        var log = Watch(page);
        for (var attempt = 1; ; attempt++)
        {
            var seen = log.Count; // console lines before this navigation don't count against it
            await navigate();
            var verdict = await WatchBootAsync(page, log, seen);
            if (verdict == "ok") return;

            // One line per dead boot, on the live console and in the test's own output (the .trx keeps
            // the latter, and CI's "Slowest journeys" step counts these lines).
            var note = $"[blazor-boot] attempt {attempt}/{MaxAttempts} of {what}: Blazor's loader died ({verdict})";
            TestContext.Progress.WriteLine(note);
            TestContext.Out.WriteLine(note);
            if (attempt == MaxAttempts)
                throw new InvalidOperationException($"{note} — giving up.\nBrowser console:\n{ConsoleOf(page)}");
        }
    }

    // Polls the page and the console until the app is up, the boot is visibly dead, or the timeout passes.
    private static async Task<string> WatchBootAsync(IPage page, ConcurrentQueue<string> log, int seen)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var outcome = await page.EvaluateAsync<string>(BootOutcome);
            if (outcome == "ok") return outcome;
            if (outcome != "loading") return outcome;

            // The silent death: a framework fetch failed (ERR_NETWORK_CHANGED, connection reset…) while the
            // loader is still up. Blazor never shows its banner for this one; the loader just sits at NN%.
            var dead = log.Skip(seen).FirstOrDefault(l =>
                (l.StartsWith("requestfailed: ", StringComparison.Ordinal) && l.Contains("/_framework/", StringComparison.Ordinal))
                || l.StartsWith("pageerror: Error: download ", StringComparison.Ordinal));
            if (dead is not null) return "fetch failed: " + dead[(dead.IndexOf(' ') + 1)..];

            if (clock.Elapsed > BootTimeout) return $"still loading after {BootTimeout.TotalSeconds:0} s";
            await Task.Delay(250);
        }
    }

    private static void Add(ConcurrentQueue<string> log, string line)
    {
        log.Enqueue(line);
        while (log.Count > 300 && log.TryDequeue(out _)) { }
    }
}
