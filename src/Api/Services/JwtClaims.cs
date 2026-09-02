namespace Perezosoft.Api.Services;

/// <summary>
/// Custom JWT claim names issued by <see cref="JwtTokenService"/> and read server-side
/// (e.g. by <c>HttpCurrentTenant</c>). These mirror
/// <c>Perezosoft.Shared.Ui.Auth.AppClaims</c> — the client reads the same names over the
/// wire, so keep the two in sync.
/// </summary>
public static class JwtClaims
{
    public const string Provider = "provider";
    public const string TenantName = "tenant_name";
    public const string TenantId = "tenant_id";
    public const string Locale = "locale";
    public const string Theme = "theme";

    /// <summary>Present only on an impersonation token (ADMIN-2, ADR-014): the staff user id acting as this user.</summary>
    public const string ImpersonatedBy = "impersonated_by";
}

/// <summary>HTTP header names used by the auth flow.</summary>
public static class AuthHeaders
{
    /// <summary>Set by native (MAUI) clients to select body-token transport over cookies.
    /// Must match the literal set in <c>Perezosoft.Maui.MauiProgram</c>.</summary>
    public const string NativeClient = "X-Native-Client";
    public const string NativeClientValue = "true";
}
