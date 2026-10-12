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
/// <para>
/// The decision itself lives in <see cref="BlazorBootCore"/> (v4 T55, R109), unit-tested without a browser:
/// a retry RELOADS the page the browser landed on rather than repeating the navigation (a single-use
/// magic-link URL was being spent twice); only a network death is retried (Blazor's banner with no failed
/// framework fetch behind it is our own startup exception, and used to be retried like a flaky network);
/// and the run has an allowance of dead boots, so a shard that keeps losing boots fails instead of retrying
/// its way to green. This file is the only place in the suite that calls <c>IPage.GotoAsync</c> or
/// <c>IPage.ReloadAsync</c> (grep gate).
/// </para>
/// </summary>
public static class BlazorBoot
{
    private static readonly ConditionalWeakTable<IPage, BootConsole> Consoles = new();
    private static readonly Lock Gate = new();
    private static readonly BootBudget Budget = new(BlazorBootCore.DeadBootsPerRun); // one per test process = per CI shard

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
    public static BootConsole Watch(IPage page)
    {
        lock (Gate)
        {
            if (Consoles.TryGetValue(page, out var existing)) return existing;
            var log = new BootConsole();
            Consoles.Add(page, log);
            page.Console += (_, m) => log.Add($"console.{m.Type}: {m.Text}");
            page.PageError += (_, e) => log.Add($"pageerror: {e}");
            page.RequestFailed += (_, r) => log.Add($"requestfailed: {r.Method} {r.Url} — {r.Failure}");
            return log;
        }
    }

    public static string ConsoleOf(IPage page) =>
        Consoles.TryGetValue(page, out var log) ? log.ToString() : "(browser console empty)";

    /// <summary>Full navigation to <paramref name="url"/>, reloaded while Blazor's boot dies of a network cause.</summary>
    public static Task GotoAsync(IPage page, string url) =>
        BlazorBootCore.BootAsync(new PlaywrightPage(page, () => page.GotoAsync(url)), Watch(page), Budget, Note, url);

    /// <summary>Full reload of the current page, repeated while Blazor's boot dies of a network cause.</summary>
    public static Task ReloadAsync(IPage page) =>
        BlazorBootCore.BootAsync(new PlaywrightPage(page, () => page.ReloadAsync()), Watch(page), Budget, Note, "reload of " + page.Url);

    // One line per dead boot, on the live console and in the test's own output (the .trx keeps the latter,
    // and CI's "Slowest journeys" step counts these lines against the run's allowance).
    private static void Note(string line)
    {
        TestContext.Progress.WriteLine(line);
        TestContext.Out.WriteLine(line);
    }

    private sealed class PlaywrightPage(IPage page, Func<Task<IResponse?>> navigate) : IBootPage
    {
        public Task NavigateAsync() => navigate();
        public Task ReloadAsync() => page.ReloadAsync();
        public async Task<string> OutcomeAsync()
        {
            try
            {
                return await page.EvaluateAsync<string>(BootOutcome);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.Ordinal))
            {
                // The page navigated while the question was in flight (a locale reconcile reloads it, a sign-in
                // redirects): there is no answer from the document that was replaced, and the next one is still
                // loading. Ask again on the next poll instead of failing the journey (CI run 37355…, 2026-10-05).
                return "loading";
            }
        }
    }
}
