#if MACCATALYST && DEBUG
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Auth;

/// <summary>
/// Mac Catalyst Debug-only <see cref="ISessionStore"/>. Local Debug builds are ad-hoc
/// signed, and MAUI SecureStorage uses the data-protection keychain, which needs the
/// restricted keychain-access-groups entitlement — claiming it without a provisioning
/// profile gets the app SIGKILLed at launch, omitting it fails every save with
/// MissingEntitlement. Until a real signing identity exists (a downstream app's signed
/// release — ADR-024; re-verify SecureStorage then), the refresh token lives in
/// a user-only (0600) file under Application Support — the same at-rest exposure as the
/// repo's dev .env. Signed Release/store builds keep <see cref="SecureStorageSessionStore"/>.
/// </summary>
public sealed class DebugFileSessionStore : ISessionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "vuelto-debug-session");

    public bool UsesBodyTransport => true;

    public Task<string?> GetRefreshTokenAsync()
    {
        try
        {
            return Task.FromResult<string?>(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null);
        }
        catch
        {
            // An unreadable store reads as "no session" rather than crashing (parity with the secure store).
            return Task.FromResult<string?>(null);
        }
    }

    public async Task SaveRefreshTokenAsync(string refreshToken)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        // Owner-only directory (0700) so a sibling can't even list it (v3 NAT-10).
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Create the file 0600 ATOMICALLY (v3 NAT-11): the old WriteAllText-then-chmod created the file
        // world-readable under the umask WITH the token already in it, leaving a window where another
        // local user could read the refresh token before the chmod landed. UnixCreateMode sets the mode
        // at creation, so the token is never on disk world-readable.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using var stream = new FileStream(FilePath, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(refreshToken);
    }

    public Task ClearAsync()
    {
        try { File.Delete(FilePath); } catch { /* already gone */ }
        return Task.CompletedTask;
    }
}
#endif
