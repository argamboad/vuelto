using System.Text.Json.Serialization;

namespace Vuelto.Api.Models;

public record MfaStatusResponse
{
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
}

public record MfaEnrollResponse
{
    /// <summary>The <c>otpauth://</c> URI to render as a QR (also carries the secret for manual entry).</summary>
    [JsonPropertyName("provisioning_uri")] public required string ProvisioningUri { get; init; }

    /// <summary>The Base32 secret, for manual entry into an authenticator app.</summary>
    [JsonPropertyName("secret")] public required string Secret { get; init; }
}

public record MfaCodeRequest
{
    [JsonPropertyName("code")] public string? Code { get; init; }
}

public record MfaRecoveryCodesResponse
{
    /// <summary>One-time recovery codes — shown once. Store them somewhere safe.</summary>
    [JsonPropertyName("recovery_codes")] public required IReadOnlyList<string> RecoveryCodes { get; init; }
}

/// <summary>Returned by a login when MFA is enabled — the client must complete the step-up.</summary>
public record MfaRequiredResponse
{
    [JsonPropertyName("mfa_required")] public bool MfaRequired => true;

    /// <summary>The signed, short-lived challenge to post back to <c>/api/auth/mfa/verify</c>.</summary>
    [JsonPropertyName("challenge")] public required string Challenge { get; init; }
}

public record MfaVerifyRequest
{
    [JsonPropertyName("challenge")] public string? Challenge { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}
