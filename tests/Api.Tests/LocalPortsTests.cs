using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vuelto.Api.Tests;

/// <summary>
/// Local Dev Alignment L1/L2/L4/L5, and Arch A10 (#369): the three apps (perezosoft-platform, y-el-vuelto, jigger-jot)
/// run side by side on one machine, each on its own block of ports. The block is stated ONCE, in
/// <c>local-ports.props</c>. Code reads it through the generated <c>LocalPorts</c> class (Directory.Build.props); the
/// files that cannot read MSBuild — compose defaults, launch profiles, appsettings, <c>.env.example</c>, the Postman
/// environment, the README row — are rewritten by <c>pwsh tools/ports.ps1 -Apply</c> and held here. Before the one
/// source, the block was written into a dozen files by hand and drifted: two compose files defaulted to another app's
/// database port, and one app's dev SMTP and E2E Mailpit pointed at the platform's Mailpit, so its codes landed in the
/// wrong inbox. This file is identical in all three repos.
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

        // The files tools/ports.ps1 rewrites. Compose and the launch profiles used to BE the source; now they derive.
        var compose = Read("docker-compose.yml");
        string ComposeDefault(string var) => Regex.Match(compose, @"\$\{" + var + @":-(\d+)\}") is { Success: true } m ? m.Groups[1].Value : "(missing)";
        Expect("docker-compose.yml DB_PORT default", ComposeDefault("DB_PORT"), block.Db);
        Expect("docker-compose.yml MAIL_SMTP_PORT default", ComposeDefault("MAIL_SMTP_PORT"), block.Smtp);
        Expect("docker-compose.yml MAIL_UI_PORT default", ComposeDefault("MAIL_UI_PORT"), block.MailUi);
        Expect("docker-compose.yml APP_PORT default", ComposeDefault("APP_PORT"), block.App);

        var (apiHttps, apiHttp) = LaunchPorts("src/Api/Properties/launchSettings.json");
        Expect("src/Api launchSettings https", apiHttps, block.ApiHttps);
        Expect("src/Api launchSettings http", apiHttp, block.ApiHttp);
        var (webHttps, webHttp) = LaunchPorts("src/Web/Properties/launchSettings.json");
        Expect("src/Web launchSettings https", webHttps, block.WebHttps);
        Expect("src/Web launchSettings http", webHttp, block.WebHttp);

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
        using (var web = JsonDocument.Parse(Read("src/Web/wwwroot/appsettings.json")))
            Expect("src/Web appsettings ApiBaseUrl", Path(web.RootElement, "ApiBaseUrl"), $"https://localhost:{block.ApiHttps}");

        foreach (var postman in Directory.EnumerateFiles(Full("docs/postman"), "*.local.postman_environment.json"))
        {
            var text = File.ReadAllText(postman);
            Expect($"{System.IO.Path.GetFileName(postman)} baseUrl", Regex.Match(text, @"""baseUrl"",\s*""value"":\s*""([^""]+)""").Groups[1].Value, $"https://localhost:{block.ApiHttps}");
            Expect($"{System.IO.Path.GetFileName(postman)} mailpitUrl", Regex.Match(text, @"""mailpitUrl"",\s*""value"":\s*""([^""]+)""").Groups[1].Value, $"http://localhost:{block.MailUi}");
        }

        foreach (Match m in Regex.Matches(Read("tests/E2E.Tests/playwright.runsettings"), @"localhost:(\d+)"))
            Expect("playwright.runsettings", m.Groups[1].Value, block.WebHttps);
        foreach (Match m in Regex.Matches(Read("tests/E2E.Tests/README.md"), @"localhost:(\d+)"))
            if (!block.All.Contains(m.Groups[1].Value)) failures.Add($"tests/E2E.Tests/README.md: localhost:{m.Groups[1].Value} is not one of this repo's ports");
        // .env.example's comments name ports too (the RLS connection, the Stripe hint, the Mailpit default); the shared
        // Aspire Dashboard's OTLP 4317 and UI 18888 are the machine's, not any one app's.
        foreach (Match m in Regex.Matches(env, @"localhost:(\d+)"))
            if (!block.All.Contains(m.Groups[1].Value) && !MachinePorts.Contains(m.Groups[1].Value))
                failures.Add($".env.example: localhost:{m.Groups[1].Value} is not one of this repo's ports");

        var row = ReadmeRows().SingleOrDefault(r => r.Name.Contains("(this repo)", StringComparison.Ordinal));
        if (row is null) failures.Add("README.md: the Local ports table has no row marked (this repo)");
        else
        {
            string[] expected = [block.Db, block.Smtp, block.MailUi, block.ApiHttps, block.ApiHttp, block.WebHttps, block.WebHttp, block.App];
            Expect("README.md Local ports (this repo)", string.Join(" ", row.Ports), string.Join(" ", expected));
        }

        Assert.True(failures.Count == 0, "Local ports disagree with local-ports.props (run `pwsh tools/ports.ps1 -Apply`):\n  "
            + string.Join("\n  ", failures));
    }

    [Fact]
    public void CodeToolsAndTests_StateNoLocalPort_OutsideTheProps() // Arch A10 (#369)
    {
        // A port in code is a port the next app forgets: the API's dev fallback, the MAUI Debug base, the E2E defaults,
        // the harness URL and a dozen comments each carried the platform's numbers downstream. Code names LocalPorts.*;
        // comments say which port, not which number; tools derive the block at run time (e2e.ps1) or from the props
        // (ports.ps1). Test data that needs some loopback origin uses a port outside the block, or a made-up host.
        var block = Block();
        var root = RepoRoot();
        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(System.IO.Path.Combine(root, "tools"), "*.ps1"))
            .Concat(Directory.EnumerateFiles(System.IO.Path.Combine(root, "tests"), "*.*", SearchOption.AllDirectories))
            .Where(f => Regex.IsMatch(f, @"\.(cs|razor|csproj|props|ps1|js|xml)$"))
            .Where(f => !Regex.IsMatch(f.Replace('\\', '/'), @"/(obj|bin|node_modules|wwwroot/lib)/"))
            .Where(f => System.IO.Path.GetFileName(f) is not ("LocalPortsTests.cs" or "local-ports.props"));
        foreach (var f in files)
        {
            var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
            var inSourceOrTools = rel.StartsWith("src/", StringComparison.Ordinal) || rel.StartsWith("tools/", StringComparison.Ordinal);
            foreach (var line in File.ReadLines(f).Select((text, i) => (text, n: i + 1)))
            {
                foreach (Match m in Regex.Matches(line.text, @"(?:localhost|127\.0\.0\.1|10\.0\.2\.2|tcp):(\d{4,5})\b"))
                {
                    var port = m.Groups[1].Value;
                    if (MachinePorts.Contains(port)) continue;                 // the shared Aspire dashboard
                    if (block.All.Contains(port) || inSourceOrTools)            // a block number anywhere; any number in code or tools
                        offenders.Add($"{rel}:{line.n}: {m.Value}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "local ports stated outside local-ports.props (use LocalPorts.*, $(Local*Port) in MSBuild, or a made-up host in test data):\n  "
            + string.Join("\n  ", offenders));
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

    [Fact]
    public void TheGeneratedConstants_AreTheProps()
    {
        // The compile-time half of the one source: this project opts into GenerateLocalPorts, so LocalPorts.* is what
        // the API, the MAUI app and the E2E suite compiled against. If the generation ever drifted from the props, every
        // "no number in code" above would be holding code to the wrong block.
        var block = Block();
        Assert.Equal(LocalPorts.Db.ToString(), block.Db);
        Assert.Equal(LocalPorts.Smtp.ToString(), block.Smtp);
        Assert.Equal(LocalPorts.MailUi.ToString(), block.MailUi);
        Assert.Equal(LocalPorts.App.ToString(), block.App);
        Assert.Equal(LocalPorts.ApiHttpsUrl, $"https://localhost:{block.ApiHttps}");
        Assert.Equal(LocalPorts.ApiHttpUrl, $"http://localhost:{block.ApiHttp}");
        Assert.Equal(LocalPorts.WebHttpsUrl, $"https://localhost:{block.WebHttps}");
        Assert.Equal(LocalPorts.WebHttpUrl, $"http://localhost:{block.WebHttp}");
        Assert.Equal(LocalPorts.MailUiUrl, $"http://localhost:{block.MailUi}");
    }

    // --- the block ---

    // The machine's shared Aspire Dashboard (tools/telemetry.ps1): OTLP and its UI are one container for every repo.
    private static readonly HashSet<string> MachinePorts = ["4317", "4318", "18888"];

    private sealed record PortBlock(string Db, string Smtp, string MailUi, string App, string ApiHttps, string ApiHttp, string WebHttps, string WebHttp)
    {
        public HashSet<string> All => [Db, Smtp, MailUi, App, ApiHttps, ApiHttp, WebHttps, WebHttp];
    }

    private static PortBlock Block()
    {
        var props = XDocument.Load(Full("local-ports.props"));
        string P(string name) => props.Descendants(name).SingleOrDefault()?.Value.Trim()
            ?? throw new InvalidOperationException($"local-ports.props has no <{name}>");
        return new PortBlock(P("LocalDbPort"), P("LocalSmtpPort"), P("LocalMailUiPort"), P("LocalAppPort"),
            P("LocalApiHttpsPort"), P("LocalApiHttpPort"), P("LocalWebHttpsPort"), P("LocalWebHttpPort"));
    }

    private static (string Https, string Http) LaunchPorts(string file)
    {
        var urls = Regex.Matches(Read(file), @"(https?)://localhost:(\d+)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        string One(string scheme) => urls.Where(u => u.Item1 == scheme).Select(u => u.Item2).Distinct().SingleOrDefault()
            ?? $"(none or several {scheme} ports)";
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
