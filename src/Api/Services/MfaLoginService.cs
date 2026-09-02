using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Services;

/// <summary>A completed step-up: the issued session + whether the login was native (drives cookie vs body).</summary>
public sealed record MfaVerifyOutcome(AccessSession Session, bool Native);

/// <summary>
/// The MFA login step-up (MFA-2, ADR-012). Sits at the point where a primary-auth path has resolved a
/// user and would issue a session: if the user has MFA enabled, it returns a <b>challenge</b> instead
/// of a session; the client then posts the challenge + a code back to complete login. Users without
/// MFA are issued a session directly, exactly as before.
/// </summary>
public interface IMfaLoginService
{
    /// <summary>Session when MFA is off; otherwise a challenge (and no session).</summary>
    Task<(AccessSession? Session, string? Challenge)> CompleteOrChallengeAsync(
        User user, string provider, string ip, bool native, CancellationToken cancellationToken = default);

    /// <summary>Verifies a challenge + TOTP/recovery code and issues the session; null if either is invalid.</summary>
    Task<MfaVerifyOutcome?> VerifyChallengeAsync(string challenge, string code, string ip, CancellationToken cancellationToken = default);
}

public sealed class MfaLoginService(
    IMfaService mfa,
    IMfaChallengeService challenges,
    ISessionService sessionService,
    IUserService userService) : IMfaLoginService
{
    public async Task<(AccessSession?, string?)> CompleteOrChallengeAsync(
        User user, string provider, string ip, bool native, CancellationToken cancellationToken = default)
    {
        if (await mfa.IsEnabledAsync(user.Id, cancellationToken))
            return (null, challenges.Mint(user.Id, provider, native));

        var session = await sessionService.IssueAsync(user, provider, ip, native, cancellationToken);
        return (session, null);
    }

    public async Task<MfaVerifyOutcome?> VerifyChallengeAsync(
        string challenge, string code, string ip, CancellationToken cancellationToken = default)
    {
        if (!challenges.TryRead(challenge, out var userId, out var provider, out var native, out var challengeId))
            return null;

        // Claim the challenge BEFORE the factor check (LB-AUTH-1). VerifyAsync has side effects — it burns a
        // recovery code / advances the anti-replay step — so verifying first meant a claim that then failed
        // (replay, or the challenge evicted from the cache) spent the second factor with no session issued:
        // the user's recovery code simply gone. Claiming first means only the winner ever touches the factor.
        if (!challenges.Consume(challengeId))
            return null; // already redeemed (replay) or expired — single-use

        if (!await mfa.VerifyAsync(userId, code, cancellationToken))
        {
            // A wrong code burns nothing, so hand the claim back and let the user retry this step-up. The
            // per-user lockout (ADM-3) — not the challenge — is what caps guessing.
            challenges.Restore(challengeId);
            return null;
        }

        var user = await userService.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        var session = await sessionService.IssueAsync(user, provider, ip, native, cancellationToken);
        return new MfaVerifyOutcome(session, native);
    }
}
