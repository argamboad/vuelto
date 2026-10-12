namespace Vuelto.Api.Configuration;

/// <summary>
/// Toggles the public API surface (PUBAPI, ADR-015). **Default off** — a platform deployment opts in
/// deliberately. When disabled, the API-key auth scheme isn't added and neither the key-management nor the
/// public routes are mapped (strong gating: they return 404, they don't merely 403). Bound from the
/// <c>PublicApi</c> config section (env <c>PublicApi__Enabled</c>).
/// </summary>
public sealed class PublicApiSettings
{
    public const string SectionName = "PublicApi";

    public bool Enabled { get; set; }
}
