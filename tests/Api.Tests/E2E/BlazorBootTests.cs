using Microsoft.Extensions.Time.Testing;
using Vuelto.E2E.Tests;

namespace Vuelto.Api.Tests.E2E;

/// <summary>
/// v4 audit T55 (UX-8/9, DEP-21, LB-DEP-6b; R109): the E2E boot helper's retry decision, driven with a scripted
/// page — no browser. <see cref="BlazorBootCore"/> is the E2E project's file, linked into this one: what
/// these prove is what every journey's navigation does on CI. The cases are the audit's (1)–(6).
/// </summary>
public class BlazorBootTests
{
    [Fact]
    public async Task Case1_LoadingThenOk_NavigatesOnce_NeverReloads()
    {
        var page = new ScriptedPage("loading", "loading", "ok");
        await Boot(page, new BootConsole());
        Assert.Equal(1, page.Navigations);
        Assert.Equal(0, page.Reloads);
    }

    [Fact]
    public async Task Case2_NetworkDeath_ReloadsTheLandedPage_NeverTheOriginalUrlAgain()
    {
        // UX-8: the magic-link journey navigates to a single-use verify URL. Re-issuing that navigation on a dead
        // boot spent the token again, and the journey failed with invalid_link and no explanation. A retry is a
        // RELOAD of wherever the browser landed — the navigation count stays at one.
        var console = new BootConsole();
        var page = new ScriptedPage("loading", "loading", "ok");
        page.OnOutcome(1, () => console.Add("requestfailed: GET http://web.test/_framework/dotnet.js — net::ERR_NETWORK_CHANGED"));
        var notes = new List<string>();

        await Boot(page, console, notes.Add);

        Assert.Equal(1, page.Navigations);
        Assert.Equal(1, page.Reloads);
        var note = Assert.Single(notes);
        Assert.Contains("attempt 1/3", note);
        Assert.Contains("fetch failed: GET http://web.test/_framework/dotnet.js", note);
    }

    [Fact]
    public async Task Case3_BannerWithoutAFailedFrameworkFetch_IsOurStartupException_ThrowsAtOnce()
    {
        // UX-9: Blazor's error banner with nothing failed on the network is the app's own startup exception. It
        // used to be retried like a flaky network — an app crashing one boot in three passed every run.
        var console = new BootConsole();
        console.Add("requestfailed: GET http://web.test/_framework/old.dll — net::ERR_ABORTED"); // before the navigation: not this boot's
        var page = new ScriptedPage("loading", "banner at 47%", "ok");
        var notes = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Boot(page, console, notes.Add));

        Assert.Equal(0, page.Reloads);
        Assert.Contains("banner at 47%", ex.Message);
        Assert.Contains("not retried", ex.Message);
        Assert.Contains("Browser console:", ex.Message);
        Assert.Single(notes);
    }

    [Fact]
    public async Task Case4_BannerBehindAFailedFrameworkFetch_IsTheNetwork_Reloads()
    {
        var console = new BootConsole();
        var page = new ScriptedPage("loading", "banner at 12%", "ok");
        page.OnOutcome(1, () => console.Add("pageerror: Error: download failed: dotnet.wasm"));

        await Boot(page, console);

        Assert.Equal(1, page.Navigations);
        Assert.Equal(1, page.Reloads);
    }

    [Fact]
    public async Task Case5_ThreeNetworkDeaths_GivesUp_WithTheConsole_AndOneNotePerDeadBoot()
    {
        // LB-DEP-6b: the giving-up exception repeats the third attempt's line, and the .trx stores the exception
        // too — so CI counts the notes (one per dead boot), never the exception. Three deaths = three notes.
        var console = new BootConsole();
        var page = new ScriptedPage("loading", "loading", "loading", "ok");
        for (var i = 0; i < 3; i++)
            page.OnOutcome(i, () => console.Add("requestfailed: GET http://web.test/_framework/blazor.boot.json — net::ERR_CONNECTION_RESET"));
        var notes = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Boot(page, console, notes.Add));

        Assert.Equal(1, page.Navigations);
        Assert.Equal(2, page.Reloads);
        Assert.Equal(3, notes.Count);
        Assert.Equal(1, notes.Count(n => n.Contains("attempt 3/3")));
        Assert.Contains("attempt 3/3", ex.Message);
        Assert.Contains("giving up", ex.Message);
        Assert.Contains("ERR_CONNECTION_RESET", ex.Message); // the console rides along
    }

    [Fact]
    public async Task Case6_TheRunsAllowanceOfDeadBoots_FailsTheNextOne_EvenWhenItsReloadWouldSucceed()
    {
        // UX-9's per-shard threshold: the allowance is per RUN, not per navigation. Two dead boots on the first
        // navigation and one on the second are within it; the fourth dead boot fails even though its reload would
        // have come up — a shard losing that many boots is an unhealthy environment, not a green run.
        var console = new BootConsole();
        var budget = new BootBudget(BlazorBootCore.DeadBootsPerRun);
        Assert.Equal(3, BlazorBootCore.DeadBootsPerRun);
        string Dead() => "requestfailed: GET http://web.test/_framework/dotnet.js — net::ERR_NETWORK_CHANGED";

        var first = new ScriptedPage("loading", "loading", "ok");
        first.OnOutcome(0, () => console.Add(Dead()));
        first.OnOutcome(1, () => console.Add(Dead()));
        await Boot(first, console, budget: budget);
        Assert.Equal(2, budget.Spent);

        var second = new ScriptedPage("loading", "loading", "ok");
        second.OnOutcome(0, () => console.Add(Dead()));
        second.OnOutcome(1, () => console.Add(Dead()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Boot(second, console, budget: budget));

        Assert.Contains("allowance of 3 dead boots is spent", ex.Message);
        Assert.Equal(1, second.Reloads); // the third dead boot was retried; the fourth was not
    }

    [Fact]
    public async Task StillLoadingPastTheTimeout_IsANetworkDeath_Reloads()
    {
        var clock = new FakeTimeProvider();
        var page = new ScriptedPage("loading", "loading", "ok");
        var notes = new List<string>();

        await Boot(page, new BootConsole(), notes.Add, clock: clock, poll: () => { clock.Advance(TimeSpan.FromSeconds(61)); return Task.CompletedTask; });

        Assert.Equal(1, page.Reloads);
        Assert.Contains("still loading after 60 s", Assert.Single(notes));
    }

    [Fact]
    public void Console_LinesSinceAMark_SurviveTheTrim()
    {
        // LB-DEP-6b (the trim index): after the queue trims, "since this navigation" was a Skip over a count that
        // no longer matched the retained lines, so a genuine failed fetch could be skipped. Marks are positions.
        var console = new BootConsole();
        for (var i = 0; i < BootConsole.Keep; i++) console.Add($"console.log: line {i}");
        var mark = console.Mark();
        for (var i = 0; i < 50; i++) console.Add($"console.log: noise {i}"); // trims 50 old lines
        console.Add("requestfailed: GET /_framework/dotnet.js — net::ERR_NETWORK_CHANGED");

        var since = console.Since(mark);

        Assert.Equal(51, since.Count);
        Assert.Contains(since, l => l.StartsWith("requestfailed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(since, l => l.Contains("line ")); // nothing from before the mark
        Assert.Empty(console.Since(console.Mark()));
    }

    private static Task Boot(ScriptedPage page, BootConsole console, Action<string>? note = null, BootBudget? budget = null,
        TimeProvider? clock = null, Func<Task>? poll = null) =>
        BlazorBootCore.BootAsync(page, console, budget ?? new BootBudget(BlazorBootCore.DeadBootsPerRun), note ?? (_ => { }),
            "https://localhost/verify?token=once", clock ?? new FakeTimeProvider(), poll ?? (() => Task.CompletedTask));

    /// <summary>A page whose polls answer from a script, in order; hooks run when a given poll is answered (to drop console lines).</summary>
    private sealed class ScriptedPage(params string[] outcomes) : IBootPage
    {
        private readonly Dictionary<int, Action> _hooks = [];
        private int _polls;

        public int Navigations { get; private set; }
        public int Reloads { get; private set; }

        public void OnOutcome(int poll, Action hook) => _hooks[poll] = hook;

        public Task NavigateAsync()
        {
            Navigations++;
            return Task.CompletedTask;
        }

        public Task ReloadAsync()
        {
            Reloads++;
            return Task.CompletedTask;
        }

        public Task<string> OutcomeAsync()
        {
            var i = _polls++;
            if (_hooks.TryGetValue(i, out var hook)) hook();
            return Task.FromResult(outcomes[Math.Min(i, outcomes.Length - 1)]);
        }
    }
}
