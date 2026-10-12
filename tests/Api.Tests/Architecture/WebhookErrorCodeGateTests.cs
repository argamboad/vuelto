using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// v4 T39 (JOBS-1, R89): a tenant-visible error column never holds an exception's text. The webhook delivery log
/// used to save <c>ex.Message</c> straight into <c>WebhookDelivery.Error</c> and return it from the API — resolved
/// addresses, DNS errors and the SSRF guard's verdict included. This scans the sources for that shape.
/// </summary>
public class WebhookErrorCodeGateTests
{
    [Fact]
    public void NoErrorColumn_IsAssignedFromAnExceptionMessage()
    {
        var shape = new Regex(@"\b(?:Error|transportError|LastError)\s*=\s*(?:\w+\.)?(?:ex|e|exception|cause)\.Message\b");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            // The outbox's own LastError is operator-only (never returned by an API) and keeps the raw text on purpose.
            if (file.EndsWith("OutboxProcessor.cs", StringComparison.Ordinal))
                continue;
            foreach (Match m in shape.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {m.Value}");
        }
        Assert.True(offenders.Count == 0,
            "A tenant-visible Error must be a reason code (WebhookFailure.Reason), never exception text; found:\n" + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
