using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Vuelto.Infrastructure.Persistence;

/// <summary>
/// Carries the ambient tenant to the database for the row-level-security backstop (ADR-020) by
/// setting the tenant/bypass GUCs (<see cref="RlsDdl"/>) on the connection before commands execute.
/// Registered in <see cref="AppDbContext.OnConfiguring"/> — like the write-stamping interceptor —
/// so every context, including ones constructed directly in tests, participates.
/// <para>
/// Mechanics that matter:
/// <list type="bullet">
/// <item>The GUCs are set by a <b>separate</b> parameterized command, never by rewriting the main
/// command: a prepended statement would occupy a result position and corrupt EF's positional
/// consumption of SaveChanges batches (rows-affected checks).</item>
/// <item>Session-level <c>set_config</c> (not <c>SET LOCAL</c>) so it works for reads outside
/// transactions. Safe with pooling because the state is re-asserted per logical connection open —
/// and Npgsql resets session state when a physical connection is returned to the pool.</item>
/// <item>Per-connection state avoids a round-trip per command; it is invalidated where Postgres can
/// silently revert the GUCs: connection (re)open, transaction rollback, savepoint rollback, and a
/// FAILED commit/rollback (v3 audit RLS-5)
/// (<c>set_config</c> is transactional even at session level).</item>
/// <item>Evaluated per command (not per request) because the tenant can change mid-request
/// (<c>ITenantContext.EnterTenant</c>) and individual queries can be sanctioned cross-tenant
/// (<see cref="RlsTags.CrossTenant"/> tags, rendered by EF as a leading SQL comment).</item>
/// <item>The bypass GUC is set for the tenant-less system context (outbox dispatcher, scheduled
/// jobs, pre-auth flows) and for tagged commands — ADR-020's "explicit bypass, never
/// missing-setting = allow". Superuser/BYPASSRLS connections (local dev) bypass RLS at the server
/// regardless.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RlsSessionInterceptor : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    public static readonly RlsSessionInterceptor Instance = new();
    private RlsSessionInterceptor() { }

    private static readonly string TagComment = "-- " + RlsTags.CrossTenant;

    /// <summary>Last GUC values asserted on a connection; missing entry = unknown/unset.</summary>
    private sealed class GucState
    {
        public string? Tenant;
        public bool Bypass;
        public bool Known;
    }

    private static readonly ConditionalWeakTable<DbConnection, GucState> States = new();

    // ── Command facet: assert the desired GUCs before anything executes ───────────────────────

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureGucs(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureGucsAsync(command, eventData, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureGucs(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureGucsAsync(command, eventData, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureGucs(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureGucsAsync(command, eventData, cancellationToken);
        return result;
    }

    // ── Invalidation: points where the server-side GUCs may no longer match our record ────────

    void IDbConnectionInterceptor.ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Invalidate(connection);

    Task IDbConnectionInterceptor.ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken)
    {
        Invalidate(connection);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        InvalidateContextConnection(eventData);

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        InvalidateContextConnection(eventData);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        InvalidateContextConnection(eventData);

    Task IDbTransactionInterceptor.RolledBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        InvalidateContextConnection(eventData);
        return Task.CompletedTask;
    }

    // A FAILED commit/rollback aborts the transaction, which reverts the session-level set_config GUCs
    // (they are transactional even at session level) — so our per-connection cache must forget them, or the
    // next command on this pooled connection skips re-asserting and runs with the backstop silently off /
    // a stale tenant (RLS-5). Mirrors the rollback handlers above.
    void IDbTransactionInterceptor.TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        InvalidateContextConnection(eventData);

    Task IDbTransactionInterceptor.TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    {
        InvalidateContextConnection(eventData);
        return Task.CompletedTask;
    }

    private static void Invalidate(DbConnection connection)
    {
        if (States.TryGetValue(connection, out var state))
            state.Known = false;
    }

    private static void InvalidateContextConnection(DbContextEventData eventData)
    {
        var connection = eventData.Context?.Database.GetDbConnection();
        if (connection is not null)
            Invalidate(connection);
    }

    // ── Core ───────────────────────────────────────────────────────────────────────────────────

    private static (string Tenant, bool Bypass)? Desired(DbCommand command, CommandEventData eventData)
    {
        // Only contexts that know their tenant participate; anything else (no context, non-app
        // context, e.g. design-time tooling) is left untouched.
        if (eventData.Context is not AppDbContext db)
            return null;

        var tenantId = db.RlsTenantId;
        var bypass = tenantId is null || HasLeadingCrossTenantTag(command.CommandText);
        return (tenantId?.ToString() ?? string.Empty, bypass);
    }

    /// <summary>
    /// True when the command carries the cross-tenant sanction tag (<see cref="RlsTags.CrossTenant"/>) as
    /// one of EF's <b>leading</b> query-tag comment lines. Anchored to the leading comment block — not a
    /// substring match anywhere in the SQL — so a marker embedded elsewhere (a string literal, an echoed
    /// value, a non-sanctioned multi-tag) cannot disarm the backstop (RLS-7); and matched as an <b>exact</b>
    /// line, so only the sanctioned tag counts (not <c>-- rls:cross-tenant &lt;anything&gt;</c>).
    /// </summary>
    public static bool HasLeadingCrossTenantTag(string commandText)
    {
        foreach (var raw in commandText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;                                     // blank line between tags/SQL
            if (!line.StartsWith("--", StringComparison.Ordinal)) return false; // reached the SQL body
            if (line == TagComment) return true;                                // exact leading tag line
        }
        return false;
    }

    private static DbCommand CreateGucCommand(DbCommand command, string tenant, bool bypass)
    {
        var guc = command.Connection!.CreateCommand();
        guc.Transaction = command.Transaction;
        guc.CommandText = $"SELECT set_config('{RlsDdl.TenantGuc}', $1, false), set_config('{RlsDdl.BypassGuc}', $2, false);";
        var p1 = guc.CreateParameter();
        p1.Value = tenant;
        guc.Parameters.Add(p1);
        var p2 = guc.CreateParameter();
        p2.Value = bypass ? "on" : "off";
        guc.Parameters.Add(p2);
        return guc;
    }

    private static void EnsureGucs(DbCommand command, CommandEventData eventData)
    {
        if (Desired(command, eventData) is not var (tenant, bypass) ||
            States.GetOrCreateValue(command.Connection!) is var state
            && state.Known && state.Tenant == tenant && state.Bypass == bypass)
            return;

        using var guc = CreateGucCommand(command, tenant, bypass);
        guc.ExecuteNonQuery();
        Record(state, tenant, bypass);
    }

    private static async Task EnsureGucsAsync(DbCommand command, CommandEventData eventData, CancellationToken ct)
    {
        if (Desired(command, eventData) is not var (tenant, bypass) ||
            States.GetOrCreateValue(command.Connection!) is var state
            && state.Known && state.Tenant == tenant && state.Bypass == bypass)
            return;

        var guc = CreateGucCommand(command, tenant, bypass);
        await using (guc.ConfigureAwait(false))
            await guc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        Record(state, tenant, bypass);
    }

    private static void Record(GucState state, string tenant, bool bypass)
    {
        state.Tenant = tenant;
        state.Bypass = bypass;
        state.Known = true;
    }
}
