namespace Vuelto.Shared.Ui.Auth;

/// <summary>What became of an OAuth flow resumed after process death (NATIVE-12).</summary>
public enum OAuthResumeOutcome
{
    /// <summary>Nothing was pending (or the state was a dead half-write) — normal startup.</summary>
    None,

    /// <summary>The stashed code was exchanged and the session established.</summary>
    SignedIn,

    /// <summary>The exchange answered <c>mfa_required</c> — Login must show the step-up prompt.</summary>
    MfaRequired,

    /// <summary>The callback carried an error, the exchange was rejected, or it couldn't be reached.</summary>
    Failed,

    /// <summary>The stash outlived the one-time code's server TTL — retry with a fresh sign-in.</summary>
    Expired,

    /// <summary>A provider-link round-trip completed (the API linked at redirect time).</summary>
    LinkCompleted,

    /// <summary>A provider-link round-trip failed; <see cref="OAuthResumeResult.Error"/> has the key.</summary>
    LinkFailed,
}

/// <summary>
/// Result of <see cref="AuthService.TryCompletePendingOAuthAsync"/>. <see cref="Challenge"/>
/// is set for <see cref="OAuthResumeOutcome.MfaRequired"/>; <see cref="Error"/> carries the
/// link-error key ("in_use", "expired", …) for <see cref="OAuthResumeOutcome.LinkFailed"/>.
/// </summary>
public sealed record OAuthResumeResult(
    OAuthResumeOutcome Outcome,
    string? Provider = null,
    string? Challenge = null,
    string? Error = null)
{
    public static readonly OAuthResumeResult None = new(OAuthResumeOutcome.None);
}
