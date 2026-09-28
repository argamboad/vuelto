using System.Linq.Expressions;

namespace Vuelto.Core.Entities;

/// <summary>
/// An invitation to join a tenant. Created by the tenant owner, addressed to an
/// email, redeemed by whoever is signed in and presents the single-use token
/// (identity is the login, not the bare email). Only the SHA-256 hash of the
/// token is stored — the raw token is revealed once at creation.
/// </summary>
public class TenantInvitation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>Normalized lower-case email the invite was addressed to.</summary>
    public required string InvitedEmail { get; set; }

    public Guid InvitedByUserId { get; set; }

    /// <summary>One of <see cref="InvitationStatuses"/>.</summary>
    public string Status { get; set; } = InvitationStatuses.Pending;

    /// <summary>SHA-256 hash of the single-use token. Raw token revealed once at creation.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // Derived — computed, never stored. THE validity rule (v4 T34, R127), written ONCE: pending and not yet at
    // its expiry instant. It is an expression so EF translates it for the signup gate's and the seat count's
    // queries, and compiled once for the in-memory reads (the accept); nothing else may compare ExpiresAt.
    // Three hand-written copies used to disagree at the expiry instant, and the seat count had no expiry at all.
    // The *At(now) forms are the deterministic core (an explicit clock); the parameterless properties delegate
    // to them at ambient time.
    private static readonly Expression<Func<TenantInvitation, DateTimeOffset, bool>> Validity =
        (invitation, now) => invitation.Status == InvitationStatuses.Pending && invitation.ExpiresAt > now;

    private static readonly Func<TenantInvitation, DateTimeOffset, bool> IsValidCompiled = Validity.Compile();

    /// <summary>The validity rule as a query predicate over <paramref name="now"/>, for EF.</summary>
    public static Expression<Func<TenantInvitation, bool>> ValidAt(DateTimeOffset now)
    {
        var invitation = Validity.Parameters[0];
        var body = new ReplaceParameter(Validity.Parameters[1], Expression.Constant(now)).Visit(Validity.Body);
        return Expression.Lambda<Func<TenantInvitation, bool>>(body, invitation);
    }

    public bool IsValidAt(DateTimeOffset now) => IsValidCompiled(this, now);
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsExpired => IsExpiredAt(DateTimeOffset.UtcNow);
    public bool IsValid => IsValidAt(DateTimeOffset.UtcNow);

    private sealed class ReplaceParameter(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>Status values for <see cref="TenantInvitation.Status"/>.</summary>
public static class InvitationStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
}
