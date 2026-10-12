namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// A persisted "OAuth browser round-trip in flight" marker (NATIVE-12). Written just
/// before the system browser opens; <paramref name="LinkToken"/> is non-null when the
/// round-trip links a provider to the current account rather than signing in.
/// </summary>
public sealed record OAuthFlowMarker(string Provider, string? LinkToken, DateTimeOffset StartedUtc);

/// <summary>
/// Persistence seam for resuming native OAuth across process death (NATIVE-12).
/// <para>
/// <see cref="WebAuthenticator"/>'s pending state is in-memory only: if the OS kills the
/// app while the user is on the provider's page, the callback redirect cold-starts a
/// fresh process that knows nothing about the flow and the one-time code is lost. This
/// store survives the kill: <see cref="AuthService"/> records the in-flight marker around
/// the browser flow, the platform callback activity stashes the redirect URI when it
/// arrives in a fresh process, and <see cref="AuthService.TryCompletePendingOAuthAsync"/>
/// consumes both on the next startup to finish the sign-in.
/// </para>
/// <para>
/// Only native hosts register an implementation (MAUI Preferences). The web host has no
/// process-death problem — its flow is a full-page redirect.
/// </para>
/// </summary>
public interface IOAuthResumeStore
{
    /// <summary>Persists the marker for a browser flow that is about to launch.</summary>
    void SetInFlight(OAuthFlowMarker marker);

    /// <summary>The persisted marker, or null when no flow is (or was) in flight.</summary>
    OAuthFlowMarker? GetInFlight();

    /// <summary>Removes the marker — the flow completed, failed, or was consumed by a resume.</summary>
    void ClearInFlight();

    /// <summary>Stashes the callback redirect URI a cold-started process received.</summary>
    void SetPendingCallback(string callbackUri);

    /// <summary>Returns the stashed callback URI and clears it (one-shot), or null.</summary>
    string? TakePendingCallback();
}
