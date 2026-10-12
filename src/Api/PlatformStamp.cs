using System.Text.Json;

namespace Vuelto.Api;

/// <summary>
/// Which platform commit this build is synced to (Arch A2, R160). <c>platform-stamp.json</c> travels with the API as
/// content (Directory.Build.props copies it beside the binaries), and <c>/api/version</c> reports it beside the app's
/// own commit, so a running app says what it is built on. On the platform itself the stamp's commit is null and the
/// answer is "platform"; a build with no stamp beside it answers "unknown".
/// </summary>
public static class PlatformStamp
{
    public static string ReadCommit(string path)
    {
        try
        {
            if (!File.Exists(path)) return "unknown";
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var commit = doc.RootElement.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return string.IsNullOrWhiteSpace(commit) ? "platform" : commit;
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}
