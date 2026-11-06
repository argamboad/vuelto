namespace Vuelto.Shared.Ui.Capabilities;

/// <summary>
/// One endpoint a capability names: the platform's API group it belongs to — a controller by its class name, or a
/// minimal-API group by its prefix — and the route shown. A null group is a route of a group another capability owns.
/// </summary>
public sealed record CapabilityEndpoint(string? Group, string Route);

/// <summary>A platform capability: its strings (<c>Capability_{Key}_Name</c>, <c>_What</c>), its pages, its endpoints.</summary>
public sealed record PlatformCapability(string Key, IReadOnlyList<string> Pages, IReadOnlyList<CapabilityEndpoint> Endpoints);

/// <summary>
/// What the platform gives this app, as the seeded Home lists it (Pkg 6.3): every capability with the page that uses it
/// and the endpoints that serve it. The platform's <c>PlatformCapabilitiesTests</c> holds it to the API: every controller
/// and every endpoint group appears exactly once, and every route belongs to one.
/// </summary>
public static class PlatformCapabilities
{
    public static IReadOnlyList<PlatformCapability> All { get; } =
    [
        new("SignIn", ["/login"],
        [
            new("AuthController", "/api/auth"), new("NativeAuthController", "/api/auth/native"),
            new("AccountController", "/api/auth/me"), new(null, "/api/auth/logins"),
        ]),
        new("Mfa", ["/settings"], [new("MfaController", "/api/auth/mfa")]),
        new("Household", ["/household", "/join"],
            [new("HouseholdController", "/api/household"), new("HouseholdInvitationsController", "/api/household/invitations")]),
        new("Billing", ["/billing"], [new("BillingController", "/api/billing"), new("BillingWebhookController", "/api/billing/webhook")]),
        new("Notifications", ["/settings"], [new("NotificationsController", "/api/notifications")]),
        new("ApiKeys", [], [new("/api/apikeys", "/api/apikeys"), new("/api/public", "/api/public")]),
        new("Webhooks", [], [new("/api/webhooks", "/api/webhooks")]),
        new("Files", [], [new("FilesController", "/api/files")]),
        new("Gdpr", ["/household", "/settings"], [new(null, "/api/household/export"), new(null, "/api/auth/me")]),
        new("Admin", ["/admin"], [new("AdminController", "/api/admin")]),
        new("Theme", ["/settings"], [new(null, "/api/auth/theme")]),
        new("Locale", ["/settings"], [new(null, "/api/auth/locale")]),
        new("Deployment", [], [new("FeaturesController", "/api/features"), new("/api/version", "/api/version"), new("/health", "/health")]),
    ];
}
