using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// The platform/app boundary as pure functions (Arch A2, R160), so <c>PlatformOwnershipTests</c> can hold them on
/// fixtures and every repo runs the same code: the ownership map (<c>platform-ownership.json</c>, ordered globs, first
/// match wins), the brand-normalised hash that lets a renamed copy be compared with the platform's file, and the
/// downstream verdict over a manifest, a stamp and the divergence allowlist. <c>tools/port-platform.ps1</c> computes
/// the same hash in PowerShell when it writes the manifest; <c>Hash_AgreesWithThePortTool</c> pins the two together.
/// </summary>
public static class PlatformOwnership
{
    public enum Class { Platform, Adapts, App, Sample }

    public sealed record Rule(string Glob, Class Class, Regex Pattern);

    /// <summary>Parses the map's rules in order. A class outside the four is a malformed map.</summary>
    public static IReadOnlyList<Rule> ParseRules(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("rules").EnumerateArray()
            .Select(r =>
            {
                var glob = r.GetProperty("glob").GetString()!;
                var cls = r.GetProperty("class").GetString() switch
                {
                    "platform" => Class.Platform, "adapts" => Class.Adapts, "app" => Class.App, "sample" => Class.Sample,
                    var other => throw new InvalidOperationException($"platform-ownership.json: rule '{glob}' has unknown class '{other}'"),
                };
                return new Rule(glob, cls, GlobToRegex(glob));
            })
            .ToList();
    }

    /// <summary>First matching rule's class; null when no rule matches (the map is incomplete).</summary>
    public static Class? Classify(string path, IReadOnlyList<Rule> rules) =>
        rules.FirstOrDefault(r => r.Pattern.IsMatch(path))?.Class;

    /// <summary>Git-style glob: <c>**</c> spans folders (<c>**/</c> may match nothing), <c>*</c> and <c>?</c> stay inside one segment.</summary>
    public static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder().Append('^'); // (not StringBuilder("^"): the image-pin gate reads every `new *Builder("…")` in tests as an image)
        for (var i = 0; i < glob.Length; i++)
        {
            if (string.CompareOrdinal(glob, i, "**/", 0, 3) == 0) { sb.Append("(?:.*/)?"); i += 2; continue; }
            if (string.CompareOrdinal(glob, i, "**", 0, 2) == 0) { sb.Append(".*"); i += 1; continue; }
            sb.Append(glob[i] switch { '*' => "[^/]*", '?' => "[^/]", var c => Regex.Escape(c.ToString()) });
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.Compiled);
    }

    /// <summary>
    /// The content hash both sides compare: BOM dropped, CRLF→LF, each brand token replaced by a placeholder (longest
    /// token first; <c>{BRAND}</c> for a capitalised token, <c>{brand}</c> otherwise), trailing whitespace trimmed per
    /// line and at the end, SHA-256 of the UTF-8 bytes, lowercase hex. A binary (NUL in its first 8000 bytes) hashes raw.
    /// </summary>
    public static string NormalizedHash(byte[] content, IReadOnlyList<string> brandTokens)
    {
        if (content.Take(8000).Contains((byte)0))
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var text = Normalize(content, brandTokens);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static string Normalize(byte[] content, IReadOnlyList<string> brandTokens)
    {
        var start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(content, start, content.Length - start).Replace("\r\n", "\n");
        foreach (var token in brandTokens.OrderByDescending(t => t.Length))
            text = text.Replace(token, char.IsUpper(token[0]) ? "{BRAND}" : "{brand}", StringComparison.Ordinal);
        var lines = text.Split('\n').Select(l => l.TrimEnd(' ', '\t'));
        return string.Join('\n', lines).TrimEnd('\n', ' ', '\t');
    }

    /// <summary>A platform path spelled in this repo: each platform token replaced by the repo's (longest first).</summary>
    public static string LocalPath(string platformPath, IReadOnlyDictionary<string, string> brand)
    {
        foreach (var (from, to) in brand.OrderByDescending(kv => kv.Key.Length))
            platformPath = platformPath.Replace(from, to, StringComparison.Ordinal);
        return platformPath;
    }

    public sealed record ManifestEntry(string Path, Class Class, string Hash);

    public sealed record Verdict(IReadOnlyList<string> Failures, IReadOnlyList<string> AdaptsDrift, int PlatformFilesChecked);

    /// <summary>
    /// The downstream gate as a function. <paramref name="readLocal"/> returns the repo's bytes for a local path, or null
    /// when the file is absent. Platform-class entries must hash equal, or be in <paramref name="divergences"/> with a
    /// reason (an entry whose file is equal again is itself a failure: the list cannot rot; an entry for a file the
    /// manifest does not know is a failure too). Adapts-class drift is only reported. Sample and app entries are ignored.
    /// </summary>
    public static Verdict Evaluate(
        IReadOnlyList<ManifestEntry> manifest, string manifestCommit, string? stampCommit,
        IReadOnlyDictionary<string, string> divergences, IReadOnlyDictionary<string, string> brand,
        Func<string, byte[]?> readLocal)
    {
        var failures = new List<string>();
        var drift = new List<string>();
        if (stampCommit != manifestCommit)
            failures.Add($"platform-stamp.json says {stampCommit ?? "(null)"} but the manifest was written at {manifestCommit}: a port must update both");
        var appTokens = brand.Values.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var checked_ = 0;
        foreach (var entry in manifest.Where(e => e.Class is Class.Platform or Class.Adapts))
        {
            var local = LocalPath(entry.Path, brand);
            seen.Add(local);
            var bytes = readLocal(local);
            var same = bytes is not null && NormalizedHash(bytes, appTokens) == entry.Hash;
            if (entry.Class == Class.Adapts)
            {
                if (!same) drift.Add(bytes is null ? $"{local} (absent)" : local);
                continue;
            }
            checked_++;
            var allowlisted = divergences.TryGetValue(local, out var reason) && !string.IsNullOrWhiteSpace(reason);
            if (same && allowlisted)
                failures.Add($"{local}: listed in PlatformDivergences.json but identical to the platform again — drop the entry");
            else if (!same && !allowlisted)
                failures.Add(bytes is null
                    ? $"{local}: a platform file this repo does not have — port it, or list it in PlatformDivergences.json with a reason"
                    : $"{local}: differs from the platform at {manifestCommit} — port the platform's change, or list it in PlatformDivergences.json with a reason");
        }
        foreach (var path in divergences.Keys.Where(p => !seen.Contains(p)).Order())
            failures.Add($"{path}: PlatformDivergences.json lists a file the manifest does not know (app-class, renamed, or gone)");
        return new Verdict(failures, drift, checked_);
    }

    // ── file formats ──

    public static (string Commit, IReadOnlyList<ManifestEntry> Entries) ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var commit = doc.RootElement.GetProperty("commit").GetString()!;
        var entries = doc.RootElement.GetProperty("files").EnumerateArray()
            .Select(e => new ManifestEntry(e.GetProperty("path").GetString()!,
                Enum.Parse<Class>(e.GetProperty("class").GetString()!, ignoreCase: true), e.GetProperty("hash").GetString()!))
            .ToList();
        return (commit, entries);
    }

    public static (string? Commit, IReadOnlyDictionary<string, string> Brand) ParseStamp(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var commit = doc.RootElement.GetProperty("commit").ValueKind == JsonValueKind.Null ? null : doc.RootElement.GetProperty("commit").GetString();
        var brand = doc.RootElement.GetProperty("brand").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        return (commit, brand);
    }

    public static IReadOnlyDictionary<string, string> ParseDivergences(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("divergences").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }
}
