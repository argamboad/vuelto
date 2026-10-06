using System.Text.Json.Serialization;

namespace Vuelto.Shared.Ui.Auth;

// The JSON shapes the auth client reads off the wire (snake_case). Internal to the RCL: pages never see them.

/// <summary>Mirrors the API's TokenResponse.</summary>
internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    // Present only for native clients; the web flow keeps the token in the cookie.
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    // The server's word on the token's lifetime, in seconds from receipt — what the client counts from,
    // never the JWT's exp read against the device clock (v4 T32, R126).
    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }
}

/// <summary>A native primary-auth response: either tokens, or an MFA challenge to step up (mfa_required).</summary>
internal sealed record NativeAuthResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("mfa_required")]
    public bool MfaRequired { get; init; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    // Present only on a failure body (ErrorResponse); e.g. too_many_attempts on OTP lockout.
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>GET /api/admin/me — is the caller platform staff?</summary>
internal sealed record StaffStatus
{
    [JsonPropertyName("is_staff")]
    public bool IsStaff { get; init; }
}

/// <summary>GET /api/features — the config-gated surfaces this deployment switched on.</summary>
internal sealed record FeaturesResponse
{
    [JsonPropertyName("billing")]
    public bool Billing { get; init; }
}

/// <summary>GET /api/auth/providers — the OAuth providers this deployment configured.</summary>
internal sealed record ProvidersResponse
{
    [JsonPropertyName("providers")]
    public IReadOnlyList<string>? Providers { get; init; }
}

/// <summary>The API's own ErrorResponse, as far as the client needs it: is there an error code at all.</summary>
internal sealed record ApiErrorBody([property: JsonPropertyName("error")] string? Error);
