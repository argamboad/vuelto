using System.Text.RegularExpressions;

namespace Perezosoft.Api.Tests;

/// <summary>
/// v3 audit TR-8 (T56, R75): the DataProtection identity strings are FROZEN. The application name and
/// every CreateProtector purpose participate in key derivation — renaming one (an innocent-looking
/// refactor) silently invalidates every payload it ever protected: encrypted MFA secrets stop
/// decrypting (users locked out of 2FA), webhook secrets become unusable, in-flight MFA challenges and
/// file-download tokens die. This test pins the exact set; changing it must be a CONSCIOUS act with a
/// data-migration story, not a rename that slips through review.
/// </summary>
public class DataProtectionIdentityTests
{
    [Fact]
    public void ProtectorPurposes_AndApplicationName_AreFrozen()
    {
        var frozen = new HashSet<string>(StringComparer.Ordinal)
        {
            "Template.Mfa.Challenge.v1",   // MfaChallengeService — step-up challenge tokens
            "Template.Mfa.Secret.v1",      // MfaService — encrypted per-user TOTP secrets (AT REST)
            "Template.Files.Download.v1",  // FileDownloadTokenizer — signed download URLs
            "Template.Webhook.Secret.v1",  // WebhookSecretProtector — encrypted webhook secrets (AT REST)
            "template",                    // SetApplicationName — part of EVERY derivation above
        };

        var purpose = new Regex(@"(?:CreateProtector|SetApplicationName)\s*\(\s*""([^""]+)""\s*\)");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles())
            foreach (Match m in purpose.Matches(File.ReadAllText(f)))
                found.Add(m.Groups[1].Value);

        var renamedOrRemoved = frozen.Except(found).ToList();
        Assert.True(renamedOrRemoved.Count == 0,
            "DataProtection identity strings renamed/removed — every payload they protected is now "
            + $"undecryptable (MFA secrets, webhook secrets, tokens). Restore or ship a re-protection migration: {string.Join(", ", renamedOrRemoved)}");

        var added = found.Except(frozen).ToList();
        Assert.True(added.Count == 0,
            "New DataProtection identity string(s) — add to the frozen list here (with a comment saying "
            + $"what they protect) so future renames are caught: {string.Join(", ", added)}");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
