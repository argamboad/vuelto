using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// v4 T34 (LB-AUTH-6/7 ≡ LB-BILL-20/25, R127): whether an invitation is still valid is ONE rule, on the entity
/// (<c>TenantInvitation.ValidAt</c> / <c>IsValidAt</c>), and every reader that needs validity — the signup gate,
/// the accept, the seat count — asks it. Three hand-written rules used to disagree at the expiry instant and
/// one of them (the seat count) had no expiry at all, so lapsed invites reserved seats forever. This scans the
/// sources: no expiry comparison on an invitation outside the entity, and every remaining status-only read is
/// a named, reasoned site — a new one must be added here with its reason, or it fails.
/// </summary>
public class InvitationValidityGateTests
{
    // Status-only reads that are RIGHT as status-only, with why. Count = occurrences of the literal in that file.
    private static readonly Dictionary<string, (int Count, string Why)> StatusOnlySites = new()
    {
        ["src/Infrastructure/Repositories/TenantInvitationRepository.cs"] = (3,
            "the owner's pending list (lapsed invites stay visible to revoke or regenerate), the refresh-an-existing-" +
            "invite lookup (expiry-blind on purpose, so a lapsed invite is refreshed rather than duplicated), and the " +
            "accept's conditional flip (validity was decided by the predicate one call earlier; the flip only races a " +
            "second accept)"),
        ["src/Api/Services/TenantInvitationService.cs"] = (2,
            "regenerate (a lapsed invite may be regenerated) and revoke (a lapsed invite may be revoked)"),
    };

    [Fact]
    public void InvitationExpiry_IsDecidedOnlyByTheEntityPredicate()
    {
        // Any `ExpiresAt <op> ...` on an invitation outside the entity is a second rule.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            if (file.EndsWith(Path.Combine("Entities", "TenantInvitation.cs"), StringComparison.Ordinal))
                continue;
            var source = File.ReadAllText(file);
            if (!source.Contains("TenantInvitation"))
                continue;
            foreach (Match m in Regex.Matches(source, @"\bExpiresAt\s*(<=|>=|<|>)\s*\w+"))
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {m.Value}");
        }
        Assert.True(offenders.Count == 0,
            "Invitation validity must go through TenantInvitation.ValidAt/IsValidAt (R127); found:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void StatusOnlyInvitationReads_AreTheNamedSites()
    {
        var literal = new Regex(@"Status\s*[!=]=\s*InvitationStatuses\.Pending");
        var found = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            if (file.EndsWith(Path.Combine("Entities", "TenantInvitation.cs"), StringComparison.Ordinal))
                continue;
            var count = literal.Matches(File.ReadAllText(file)).Count;
            if (count > 0)
                found[Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/')] = count;
        }

        var unexpected = found.Where(f => !StatusOnlySites.TryGetValue(f.Key, out var site) || site.Count != f.Value)
            .Select(f => $"{f.Key} ×{f.Value}").ToList();
        var missing = StatusOnlySites.Keys.Where(k => !found.ContainsKey(k)).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Status-only invitation reads changed. Validity (expiry included) belongs to TenantInvitation.ValidAt; a status-only " +
            "read needs a line in StatusOnlySites with its reason.\nUnexpected: " + string.Join(", ", unexpected) +
            "\nMissing: " + string.Join(", ", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
