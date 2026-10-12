using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Mail;

// EMAIL-2/3 (port slice P9b): the mail-ingestion vocabulary and small value objects.

/// <summary>OAuth providers an inbox can be connected through — the same names as the login providers.</summary>
public static class EmailProviders
{
    public const string Microsoft = "microsoft";
    public const string Google = "google";

    public static bool IsValid(string? provider) => provider is Microsoft or Google;

    public static string? Normalize(string? provider)
    {
        var p = provider?.Trim().ToLowerInvariant();
        return IsValid(p) ? p : null;
    }
}

/// <summary>Connection lifecycle states surfaced in the UI.</summary>
public static class EmailConnectionStatuses
{
    public const string Active = "active";

    /// <summary>A token refresh failed — the user must reconnect.</summary>
    public const string NeedsReconsent = "needs_reconsent";
}

/// <summary>A mailbox folder (Graph) or label (Gmail) the user can choose to scan.</summary>
public record MailFolder(string Id, string Name);

/// <summary>Result of a folder listing; <see cref="NeedsReconsent"/> mirrors the fetch path.</summary>
public record EmailFoldersResult(IReadOnlyList<MailFolder> Folders, bool NeedsReconsent)
{
    public static EmailFoldersResult Ok(IReadOnlyList<MailFolder> folders) => new(folders, false);
    public static readonly EmailFoldersResult Reconsent = new([], true);
}

/// <summary>
/// Outcome of a fetch. <see cref="NeedsReconsent"/>: the token could not be refreshed — flag and skip,
/// don't retry this cycle. <see cref="Saturated"/>: the reader hit its page cap and more matching mail
/// may exist — the cursor must not jump past the fetched window (WU-3 A5).
/// </summary>
public record EmailFetchResult(IReadOnlyList<VoucherMessage> Messages, bool NeedsReconsent, bool Saturated = false)
{
    public static EmailFetchResult Ok(IReadOnlyList<VoucherMessage> messages, bool saturated = false) => new(messages, false, saturated);
    public static readonly EmailFetchResult Reconsent = new([], true);
}

/// <summary>Tokens (plaintext) freshly acquired from a consent exchange or refresh.</summary>
public record MailConsentTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? AccountEmail);

/// <summary>Per-provider OAuth client config — read from the platform's <c>Authentication:*</c> section (the login apps are reused).</summary>
public record MailConsentSettings
{
    public string MicrosoftClientId { get; init; } = "";
    public string MicrosoftClientSecret { get; init; } = "";
    public string MicrosoftTenant { get; init; } = "consumers";
    public string GoogleClientId { get; init; } = "";
    public string GoogleClientSecret { get; init; } = "";

    public bool IsConfigured(string provider) => provider switch
    {
        EmailProviders.Microsoft => !string.IsNullOrWhiteSpace(MicrosoftClientId),
        EmailProviders.Google => !string.IsNullOrWhiteSpace(GoogleClientId),
        _ => false,
    };
}
