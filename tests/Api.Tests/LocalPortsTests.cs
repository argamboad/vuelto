using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// Local Dev Alignment L1/L2/L4/L5: the three apps (perezosoft-platform, y-el-vuelto, jigger-jot) run side by side on
/// one machine, each on its own block of ports. A repo states its ports in many places — compose defaults, launch
/// profiles, <c>appsettings.Development.json</c>, <c>.env.example</c>, the E2E defaults, the README — and before this
/// gate they had drifted: two compose files defaulted to another app's database port, and one app's dev SMTP and E2E
/// Mailpit pointed at the platform's Mailpit, so its codes landed in the wrong inbox. The block is read from the compose
/// defaults and the launch profiles; every other source must agree with it. This file is identical in all three repos.
/// </summary>
public class LocalPortsTests
{
    [Fact]
    public void EveryLocalPortSource_AgreesWithTheRepoBlock()
    {
        var block = Block();
        var failures = new List<string>();
        void Expect(string where, string? actual, string expected)
        {
            if (actual != expected) failures.Add($"{where}: {actual ?? "(missing)"}, expected {expected}");
        }

        var env = Read(".env.example");
        Expect(".env.example DB_PORT", EnvValue(env, "DB_PORT"), block.Db);
        Expect(".env.example MAIL_SMTP_PORT", EnvValue(env, "MAIL_SMTP_PORT"), block.Smtp);
        Expect(".env.example MAIL_UI_PORT", EnvValue(env, "MAIL_UI_PORT"), block.MailUi);
        Expect(".env.example APP_PORT", EnvValue(env, "APP_PORT"), block.App);
        Expect(".env.example connection string Port",
            Regex.Match(env, @"(?m)^ConnectionStrings__DefaultConnection=.*?Port=(\d+)") is { Success: true } c ? c.Groups[1].Value : null, block.Db);

        using (var dev = JsonDocument.Parse(Read("src/Api/appsettings.Development.json")))
        {
            Expect("appsettings.Development Email:Smtp:Port", Path(dev.RootElement, "Email", "Smtp", "Port"), block.Smtp);
            Expect("appsettings.Development Auth:AppBaseUrl", Path(dev.RootElement, "Auth", "AppBaseUrl"), $"https://localhost:{block.WebHttps}");
        }

        var e2e = string.Join("\n", Directory.EnumerateFiles(Full("tests/E2E.Tests"), "*.cs").Select(File.ReadAllText));
        Expect("E2E default PLAYWRIGHT_BASE_URL", EnvDefault(e2e, "PLAYWRIGHT_BASE_URL"), $"https://localhost:{block.WebHttps}");
        Expect("E2E default MAILPIT_BASE_URL", EnvDefault(e2e, "MAILPIT_BASE_URL"), $"http://localhost:{block.MailUi}");
        Expect("E2E default E2E_API_BASE_URL", EnvDefault(e2e, "E2E_API_BASE_URL"), $"https://localhost:{block.ApiHttps}");
        foreach (Match m in Regex.Matches(Read("tests/E2E.Tests/playwright.runsettings"), @"localhost:(\d+)"))
            Expect("playwright.runsettings", m.Groups[1].Value, block.WebHttps);
        foreach (Match m in Regex.Matches(Read("tests/E2E.Tests/README.md"), @"localhost:(\d+)"))
            if (!block.All.Contains(m.Groups[1].Value)) failures.Add($"tests/E2E.Tests/README.md: localhost:{m.Groups[1].Value} is not one of this repo's ports");
        // tools/e2e.ps1 reads the block from the same sources at run time, so it is the same file in all three repos.
        foreach (Match m in Regex.Matches(Read("tools/e2e.ps1"), @"localhost:(\d+)"))
            failures.Add($"tools/e2e.ps1: hardcodes localhost:{m.Groups[1].Value} - derive it from compose / launchSettings");
        // .env.example's comments name ports too (the RLS connection, the Stripe hint, the Mailpit default); the shared
        // Aspire Dashboard's OTLP 4317 and UI 18888 are the machine's, not any one app's.
        foreach (Match m in Regex.Matches(env, @"localhost:(\d+)"))
            if (!block.All.Contains(m.Groups[1].Value) && m.Groups[1].Value is not ("4317" or "18888"))
                failures.Add($".env.example: localhost:{m.Groups[1].Value} is not one of this repo's ports");

        var row = ReadmeRows().SingleOrDefault(r => r.Name.Contains("(this repo)", StringComparison.Ordinal));
        if (row is null) failures.Add("README.md: the Local ports table has no row marked (this repo)");
        else
        {
            string[] expected = [block.Db, block.Smtp, block.MailUi, block.ApiHttps, block.ApiHttp, block.WebHttps, block.WebHttp, block.App];
            Expect("README.md Local ports (this repo)", string.Join(" ", row.Ports), string.Join(" ", expected));
        }

        Assert.True(failures.Count == 0, "Local ports disagree with this repo's block (compose defaults + launch profiles):\n  "
            + string.Join("\n  ", failures));
    }

    [Fact]
    public void TheThreeAppsShareNoLocalPort()
    {
        // Every README carries the same table for all three apps, so each repo can check the whole plan: a port two apps
        // claim is two stacks fighting for one socket the first time both run.
        var rows = ReadmeRows();
        Assert.True(rows.Count == 3, $"README.md Local ports should list the three apps, found {rows.Count}");
        var clashes = rows.SelectMany(r => r.Ports.Select(p => (p, r.Name))).GroupBy(x => x.p).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Name))}").ToList();
        Assert.True(clashes.Count == 0, "Two apps claim the same local port: " + string.Join("; ", clashes));
    }

    [Fact]
    public void CiMailpit_IsWhereTheApiSendsAndTheSuiteReads()
    {
        // The CI API runs as Development, so it sends to appsettings.Development's SMTP port (this repo's block), and the
        // suite reads Mailpit at MAILPIT_BASE_URL. GitHub publishes the service's `ports:` mapping, so the mapping must
        // put Mailpit on the block's ports. A repo whose block moved off 1025 without this got every E2E journey timing
        // out on "No OTP email" (y-el-vuelto, 2026-09-24).
        var block = Block();
        var failures = new List<string>();
        foreach (var (file, hostNetworking) in new[] { (".github/workflows/ci.yml", false) })
        {
            var ci = Read(file);
            var services = Regex.Matches(ci, @"image: axllent/mailpit[^\n]*\n\s*ports: (\[[^\]]*\])");
            if (services.Count == 0) failures.Add($"{file}: no Mailpit service with a ports: line");
            foreach (Match m in services)
                if (m.Groups[1].Value != $"[\"{block.Smtp}:1025\", \"{block.MailUi}:8025\"]")
                    failures.Add($"{file}: Mailpit ports {m.Groups[1].Value}, expected [\"{block.Smtp}:1025\", \"{block.MailUi}:8025\"]");
            var readsAt = hostNetworking ? "8025" : block.MailUi;
            foreach (Match m in Regex.Matches(ci, @"MAILPIT_BASE_URL[:=] *""?http://localhost:(\d+)"))
                if (m.Groups[1].Value != readsAt) failures.Add($"{file}: MAILPIT_BASE_URL on {m.Groups[1].Value}, Mailpit answers on {readsAt} there");
            var pins = Regex.Matches(ci, @"Email__Smtp__Port: ""(\d+)""").Select(m => m.Groups[1].Value).ToList();
            if (pins.Any(p => p != "1025")) failures.Add($"{file}: Email__Smtp__Port pinned to {string.Join(", ", pins)}, Mailpit takes 1025");
            if (hostNetworking && block.Smtp != "1025" && pins.Count != services.Count)
                failures.Add($"{file}: {services.Count} Mailpit job(s) but {pins.Count} Email__Smtp__Port: \"1025\" pin(s) - host networking ignores ports:, so the API must be sent to 1025");
        }
        Assert.True(failures.Count == 0, "CI mail wiring disagrees with this repo's block:\n  " + string.Join("\n  ", failures));
    }

    // --- the block ---

    private sealed record PortBlock(string Db, string Smtp, string MailUi, string App, string ApiHttps, string ApiHttp, string WebHttps, string WebHttp)
    {
        public HashSet<string> All => [Db, Smtp, MailUi, App, ApiHttps, ApiHttp, WebHttps, WebHttp];
    }

    private static PortBlock Block()
    {
        var compose = Read("docker-compose.yml");
        string Default(string var) => Regex.Match(compose, @"\$\{" + var + @":-(\d+)\}") is { Success: true } m
            ? m.Groups[1].Value : throw new InvalidOperationException($"docker-compose.yml has no ${{{var}:-<port>}} default");
        var (apiHttps, apiHttp) = LaunchPorts("src/Api/Properties/launchSettings.json");
        var (webHttps, webHttp) = LaunchPorts("src/Web/Properties/launchSettings.json");
        return new PortBlock(Default("DB_PORT"), Default("MAIL_SMTP_PORT"), Default("MAIL_UI_PORT"), Default("APP_PORT"),
            apiHttps, apiHttp, webHttps, webHttp);
    }

    private static (string Https, string Http) LaunchPorts(string file)
    {
        var urls = Regex.Matches(Read(file), @"(https?)://localhost:(\d+)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        string One(string scheme) => urls.Where(u => u.Item1 == scheme).Select(u => u.Item2).Distinct().SingleOrDefault()
            ?? throw new InvalidOperationException($"{file} should state exactly one {scheme} port");
        return (One("https"), One("http"));
    }

    // --- sources ---

    private sealed record ReadmeRow(string Name, string[] Ports);

    private static List<ReadmeRow> ReadmeRows()
    {
        var readme = Read("README.md");
        var at = readme.IndexOf("## Local ports", StringComparison.Ordinal);
        Assert.True(at >= 0, "README.md has no \"## Local ports\" section");
        var next = readme.IndexOf("\n## ", at + 3, StringComparison.Ordinal);
        var section = next < 0 ? readme[at..] : readme[at..next];
        return [.. section.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('|') && Regex.IsMatch(l, @"\|\s*\d{4}\s*\|"))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Select(c => new ReadmeRow(c[0], c[1..]))];
    }

    private static string? EnvValue(string env, string key) =>
        Regex.Match(env, @"(?m)^" + key + @"=(\d+)") is { Success: true } m ? m.Groups[1].Value : null;

    private static string? EnvDefault(string source, string variable) =>
        Regex.Match(source, variable + @"""\)\s*\?\?\s*""([^""]+)""") is { Success: true } m ? m.Groups[1].Value : null;

    private static string? Path(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(k, out root)) return null;
        return root.ValueKind == JsonValueKind.Number ? root.GetRawText() : root.GetString();
    }

    private static string Read(string relative) => File.ReadAllText(Full(relative)).ReplaceLineEndings("\n");

    private static string Full(string relative) => System.IO.Path.Combine(RepoRoot(), relative);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
