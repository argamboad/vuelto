using System.Net.Http.Json;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// The three cheap questions the chrome asks the server before it draws itself (v4 audit T57, pulled out of
/// <see cref="AuthService"/>): is the caller platform staff, is billing switched on, which OAuth providers are
/// configured. Each answer is cached as long as it can stay true; a failed probe is never cached.
/// </summary>
internal sealed class AuthProbes(HttpClient httpClient)
{
    // Cached for the session so nav rendering doesn't re-hit the API; forgotten whenever the identity changes
    // (impersonate/stop/logout, new tokens).
    private bool? _isStaff;

    // Cached for the app's lifetime: a deployment's gates are startup config and cannot change under a
    // running client. Nothing resets it — unlike the staff probe, this has nothing to do with identity.
    private bool? _billingEnabled;

    /// <summary>The identity changed: the staff answer belongs to whoever was signed in before.</summary>
    public void ForgetStaff() => _isStaff = null;

    /// <summary>
    /// Whether the caller is platform staff. Probes <c>GET /api/admin/me</c> (200 with <c>is_staff</c> for any
    /// authenticated user). <paramref name="eligible"/> false — signed out, or impersonating — answers false
    /// without a call, and that answer is cached; a failed call answers false and is not.
    /// </summary>
    public async Task<bool> IsStaffAsync(bool eligible, string? accessToken)
    {
        if (_isStaff is { } cached) return cached;
        if (!eligible) return (_isStaff = false).Value;
        try
        {
            // The auth client deliberately has NO Bearer handler (it would be a DI cycle — see Program.cs), so
            // the in-memory token is attached explicitly for this one authenticated probe.
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return false; // don't cache transient failures
            var res = await response.Content.ReadFromJsonAsync<StaffStatus>();
            return (_isStaff = res?.IsStaff ?? false).Value;
        }
        catch
        {
            return false; // don't cache transient failures
        }
    }

    /// <summary>
    /// The OAuth providers the server has actually configured (lowercase, e.g. "google"). Anonymous probe of
    /// <c>GET /api/auth/providers</c>. Empty on any error (fail closed to no OAuth rather than a broken button).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEnabledProvidersAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<ProvidersResponse>("/api/auth/providers");
            return res?.Providers ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Whether this deployment has the billing surface switched on (GATES-1, ADR-027), with "could not tell" kept
    /// apart from "off" (v4 audit BILL-7/UX-13): <c>null</c> when the anonymous probe of <c>GET /api/features</c>
    /// failed — the server is waking up, the network dropped. A failure is not cached: the next call asks again.
    /// </summary>
    public async Task<bool?> ProbeBillingAsync()
    {
        if (_billingEnabled is { } cached) return cached;
        try
        {
            var res = await httpClient.GetFromJsonAsync<FeaturesResponse>("/api/features");
            return _billingEnabled = res?.Billing ?? false;
        }
        catch
        {
            return null; // don't cache transient failures
        }
    }
}
