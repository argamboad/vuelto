using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Vuelto.Core.Mail;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// EMAIL-2: the incremental, read-only mail consent flow on the platform's own OAuth apps
/// (<c>Authentication:Microsoft</c> / <c>Authentication:Google</c>). Scopes: <c>openid email</c> so the
/// token response carries an id_token with the account email, <c>offline_access</c> /
/// <c>access_type=offline</c> for a refresh token, and the read-only mail scope only. The round-trip
/// state is a <b>time-limited Data Protection payload</b> (ADR-V016) — tamper-proof, expiring, no HMAC
/// secret. Outbound calls go to the two fixed provider token hosts only (R76 allowlist).
/// </summary>
public sealed class MailConsentService(MailConsentSettings settings, HttpClient http, IDataProtectionProvider dataProtection, TimeProvider clock) : IMailConsentService
{
    private const string MicrosoftScope = "openid email offline_access https://graph.microsoft.com/Mail.Read";
    private const string GoogleAuthorize = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleToken = "https://oauth2.googleapis.com/token";
    private const string GoogleScope = "openid email https://www.googleapis.com/auth/gmail.readonly";
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _state = dataProtection.CreateProtector("Vuelto.Mail.ConsentState.v1").ToTimeLimitedDataProtector();

    public string BuildAuthorizationUrl(string provider, string redirectUri, string state, string? loginHint = null)
    {
        var p = EmailProviders.Normalize(provider) ?? throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        var (clientId, scope) = p switch
        {
            EmailProviders.Microsoft => (settings.MicrosoftClientId, MicrosoftScope),
            _ => (settings.GoogleClientId, GoogleScope),
        };
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException($"No OAuth client id configured for mail provider '{p}' — set Authentication:{(p == EmailProviders.Microsoft ? "Microsoft" : "Google")}:ClientId.");

        var q = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
        };
        if (p == EmailProviders.Google)
        {
            q["access_type"] = "offline";
            q["prompt"] = "consent"; // force a refresh_token on re-consent
        }
        if (!string.IsNullOrWhiteSpace(loginHint)) q["login_hint"] = loginHint; // bias to the signed-in account

        var authorizeBase = p == EmailProviders.Microsoft
            ? $"https://login.microsoftonline.com/{settings.MicrosoftTenant}/oauth2/v2.0/authorize"
            : GoogleAuthorize;
        var query = string.Join("&", q.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return $"{authorizeBase}?{query}";
    }

    public string ProtectState(Guid userId, string provider)
    {
        var p = EmailProviders.Normalize(provider) ?? throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        return _state.Protect($"{userId:N}|{p}", StateLifetime);
    }

    public bool TryReadState(string? state, out Guid userId, out string provider)
    {
        userId = Guid.Empty;
        provider = "";
        if (string.IsNullOrWhiteSpace(state)) return false;
        try
        {
            var fields = _state.Unprotect(state).Split('|');
            if (fields.Length != 2 || !Guid.TryParseExact(fields[0], "N", out userId)) return false;
            provider = fields[1];
            return EmailProviders.IsValid(provider);
        }
        catch
        {
            return false; // tampered, expired, other key ring
        }
    }

    public async Task<MailConsentTokens> ExchangeCodeAsync(string provider, string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        var (endpoint, clientId, clientSecret) = ProviderToken(provider);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code",
        });
        var (access, refresh, expiresAt, email) = await PostTokenAsync(endpoint, content, cancellationToken);
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("Provider did not return a refresh token (offline access not granted).");
        return new MailConsentTokens(access, refresh, expiresAt, email);
    }

    public async Task<MailConsentTokens> RefreshAsync(string provider, string refreshToken, CancellationToken cancellationToken = default)
    {
        var (endpoint, clientId, clientSecret) = ProviderToken(provider);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token",
        });
        var (access, newRefresh, expiresAt, email) = await PostTokenAsync(endpoint, content, cancellationToken);
        return new MailConsentTokens(access, newRefresh ?? refreshToken, expiresAt, email);
    }

    private (string Endpoint, string ClientId, string ClientSecret) ProviderToken(string provider) =>
        EmailProviders.Normalize(provider) switch
        {
            EmailProviders.Microsoft => ($"https://login.microsoftonline.com/{settings.MicrosoftTenant}/oauth2/v2.0/token", settings.MicrosoftClientId, settings.MicrosoftClientSecret),
            EmailProviders.Google => (GoogleToken, settings.GoogleClientId, settings.GoogleClientSecret),
            _ => throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider)),
        };

    private async Task<(string Access, string? Refresh, DateTimeOffset ExpiresAt, string? Email)> PostTokenAsync(string endpoint, FormUrlEncodedContent content, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds) ? seconds : 3600;
        var email = root.TryGetProperty("id_token", out var idt) ? EmailFromIdToken(idt.GetString()) : null;
        return (access, refresh, clock.GetUtcNow().AddSeconds(expiresIn), email);
    }

    private static string? EmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            using var doc = JsonDocument.Parse(FromBase64Url(parts[1]));
            if (doc.RootElement.TryGetProperty("email", out var e)) return e.GetString();
            if (doc.RootElement.TryGetProperty("preferred_username", out var u)) return u.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}
