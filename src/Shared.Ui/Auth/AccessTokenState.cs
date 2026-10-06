using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// The access token in memory and what the client knows about it: who it names, what it claims and when it
/// stops being good. Pulled out of <see cref="AuthService"/> (v4 audit T57) — the session decides WHEN the token
/// changes; this only holds it and reads it.
/// <para>
/// The lifetime is the one the SERVER stated (<c>expires_in</c>), counted from receipt on the device clock —
/// never the JWT's <c>exp</c> read against that clock, which a device running slow or fast turns into 401 storms
/// or a 30 s rotation loop (v4 T32, R126). No token, or one whose lifetime is unknown, reads as expired.
/// </para>
/// </summary>
internal sealed class AccessTokenState(TimeProvider time)
{
    private DateTimeOffset? _expiresAt;
    private TimeSpan? _lifetime;

    /// <summary>The token as held — possibly past its lifetime. Null when there is none.</summary>
    public string? Token { get; private set; }

    /// <summary>The token is past the lifetime the server gave it (or has no known lifetime at all).</summary>
    public bool Expired => _expiresAt is not { } at || time.GetUtcNow() > at;

    /// <summary>There is a token and it is inside its lifetime.</summary>
    public bool Live => !string.IsNullOrEmpty(Token) && !Expired;

    /// <summary>
    /// Holds <paramref name="token"/> and records its lifetime from the server's <c>expires_in</c>, counted from
    /// now. Without one (an older server, a token handed in directly) the JWT's own exp is the fallback — the
    /// reading that device-clock skew breaks, so the API always sends expires_in. An unreadable token has no
    /// lifetime: it reads as expired, i.e. signed out, and never throws.
    /// </summary>
    public void Set(string token, int? expiresInSeconds)
    {
        Token = token;
        if (expiresInSeconds is > 0)
        {
            _lifetime = TimeSpan.FromSeconds(expiresInSeconds.Value);
            _expiresAt = time.GetUtcNow() + _lifetime;
            return;
        }
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            _expiresAt = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
            _lifetime = jwt.ValidFrom == DateTime.MinValue ? null : jwt.ValidTo - jwt.ValidFrom;
        }
        catch
        {
            _expiresAt = null;
            _lifetime = null;
        }
    }

    /// <summary>Lets go of the token and its lifetime.</summary>
    public void Clear()
    {
        Token = null;
        _expiresAt = null;
        _lifetime = null;
    }

    /// <summary>Lets go of the token only (a native store that turned out empty); the lifetime is left as it was.</summary>
    public void DropToken() => Token = null;

    /// <summary>A claim of the live token; null when there is no token, it has expired or it cannot be read.</summary>
    public string? Claim(string type)
    {
        if (!Live)
            return null;
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(Token)
                .Claims.FirstOrDefault(c => c.Type == type)?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The live token's user id (the JWT NameIdentifier claim), or null.</summary>
    public Guid? UserId => Live && Guid.TryParse(SubjectOf(Token), out var id) ? id : null;

    /// <summary>A token's subject, expiry ignored — who it names, whether or not it is still live.</summary>
    public static string? SubjectOf(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(token)
                .Claims.FirstOrDefault(c => c.Type is "nameid" or ClaimTypes.NameIdentifier or "sub")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>How long until the token expires — never negative, never longer than a timer can wait.</summary>
    public TimeSpan TimeUntilExpiry()
    {
        if (_expiresAt is not { } at)
            return TimeSpan.Zero;
        var until = at - time.GetUtcNow();
        return until < TimeSpan.Zero ? TimeSpan.Zero : until > RenewalScheduler.MaxRenewalWait ? RenewalScheduler.MaxRenewalWait : until;
    }

    /// <summary>
    /// How long from now until the token should be renewed: the server-stated expiry, less
    /// <paramref name="lead"/> — capped at a quarter of the server-stated lifetime, so a deployment with short
    /// tokens isn't renewed the moment they arrive (the cap used to come from nbf, which the API's tokens never
    /// carry). Zero or less means "now".
    /// </summary>
    public TimeSpan RenewalDue(TimeSpan lead)
    {
        if (_expiresAt is not { } expiresAt)
            return TimeSpan.Zero;
        var lifetime = _lifetime ?? lead * 4;
        var effectiveLead = lifetime / 4 < lead ? lifetime / 4 : lead;
        return expiresAt - effectiveLead - time.GetUtcNow();
    }
}
