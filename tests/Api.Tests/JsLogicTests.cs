using System.Diagnostics;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit T53 (NAT-19, LB-NAT-3 — R142, the unit half of R110): the RCL's <c>wwwroot/js</c> bootstraps run
/// before Blazor exists and no C# test executes them; until now the only checks were "the string is in the
/// file". <c>tests/js-logic/</c> runs each file for real inside a stub browser with <c>node --test</c> — no
/// packages, no DOM library. This class is how that suite gets into the gate CI already runs, and what holds
/// every bootstrap file to having one.
/// </summary>
public class JsLogicTests
{
    [Fact]
    public void EveryJsBootstrap_HasANodeTest()
    {
        var root = RepoRoot();
        var scripts = Directory.EnumerateFiles(Path.Combine(root, "src", "Shared.Ui", "wwwroot", "js"), "*.js")
            .Select(Path.GetFileName).Where(f => !f!.EndsWith(".min.js", StringComparison.Ordinal)).ToList(); // vendored libraries are not ours to test
        Assert.True(scripts.Count >= 3, "probe: wwwroot/js moved");

        var missing = scripts.Where(f => !File.Exists(Path.Combine(root, "tests", "js-logic", Path.ChangeExtension(f!, ".test.js")))).ToList();
        Assert.True(missing.Count == 0, "wwwroot/js files with no tests/js-logic/<name>.test.js: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task JsLogic_NodeTestsPass()
    {
        var root = RepoRoot();
        var tests = Directory.EnumerateFiles(Path.Combine(root, "tests", "js-logic"), "*.test.js").Order().ToList();
        Assert.True(tests.Count >= 4, "probe: tests/js-logic moved");

        // Files by name, not the directory: `node --test <dir>` means different things across Node majors.
        var run = new ProcessStartInfo("node") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        run.ArgumentList.Add("--test");
        foreach (var test in tests) run.ArgumentList.Add(test);

        using var process = Process.Start(run) ?? throw new InvalidOperationException("node is not on PATH — tests/js-logic needs Node.js");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"tests/js-logic failed:\n{await stdout}\n{await stderr}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
