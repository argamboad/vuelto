namespace Vuelto.Shared.Ui.Auth;

public static class AppClaims
{
    public const string TenantName = "tenant_name";
    public const string Locale = "locale";
    public const string Theme = "theme";

    // Server-issued; read API-side to scope tenant queries. Listed here so the claim
    // names stay in one inventory (see Vuelto.Api.Services.JwtTokenService).
    public const string TenantId = "tenant_id";

    // Present only on an admin "sign in as" token (ADR-014): the staff user id behind
    // the impersonation. Its presence is how the UI knows it's in an impersonated session.
    public const string ImpersonatedBy = "impersonated_by";
}
