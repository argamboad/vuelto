using Microsoft.EntityFrameworkCore;

namespace Vuelto.Infrastructure.Persistence;

/// <summary>
/// Startup guard for the two-role RLS topology (ADR-020): verifies the app's runtime connection is
/// actually subject to row-level security. Postgres exempts superusers and <c>BYPASSRLS</c> roles
/// unconditionally, and table owners are only subject under <c>FORCE</c> — so a misconfigured
/// connection silently turns the backstop off. Config-gated (<c>Rls:EnforceRuntimeRole</c>) and
/// fail-closed when enabled, mirroring the Production Stripe-key guard: the app refuses to start
/// rather than run with a wall it believes exists but doesn't.
/// </summary>
public static class RlsPostureGuard
{
    /// <summary>Configuration key that enables the guard (default off; prod activation sets it).</summary>
    public const string EnforceConfigKey = "Rls:EnforceRuntimeRole";

    /// <summary>
    /// Throws when the current connection's role is superuser, <c>BYPASSRLS</c>, or owns the schema
    /// (probed via the always-present <c>Tenants</c> table — ownership matters even under FORCE,
    /// because an owner-run migration can drop/alter policies).
    /// </summary>
    public static async Task EnsureRuntimeRoleIsNotPrivilegedAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var privileged = await db.Database
            .SqlQueryRaw<bool>(
                """
                SELECT (r.rolsuper OR r.rolbypassrls OR c.relowner = r.oid) AS "Value"
                FROM pg_roles r
                JOIN pg_class c ON c.relname = 'Tenants' AND c.relnamespace = 'public'::regnamespace
                WHERE r.rolname = current_user
                """)
            .SingleAsync(cancellationToken);

        if (privileged)
            throw new InvalidOperationException(
                $"{EnforceConfigKey} is enabled, but the app's database role (current_user) is a "
                + "superuser, has BYPASSRLS, or owns the tables — row-level security (ADR-020) would "
                + "be silently bypassed. Connect the app as the non-privileged runtime role (see "
                + "docker/db/provision-rls-runtime-role.sql and docs/DEPLOYMENT.md), keep the "
                + "owner/migrator connection in ConnectionStrings:Migrations, or disable the guard.");
    }
}
