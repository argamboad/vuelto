using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// The harness's database-fault seam (v4 T54, R7). Attached to a context by
/// <see cref="PostgresFixture.CreateTestContext(Vuelto.Core.Abstractions.ICurrentTenant, DbFaultInjector?)"/>, it
/// sees every command and transaction the context runs and can make a CHOSEN one fail — the Nth command whose SQL
/// matches, or the Nth commit — with the same <see cref="NpgsqlException"/> a real disconnect raises, so the code
/// under test takes the path it would take in production. Until now every fault-path spec (a stamp that fails after
/// the notification, the outbox's bookkeeping write during a disconnect, a commit that fails after the handler
/// succeeded) either wrapped a repository in a throwing double or could not be written at all; this is the seam
/// v3 named and never built.
/// <para>
/// It also records what ran, in order (<see cref="Trace"/>: <c>BEGIN</c>, <c>COMMIT</c>, <c>ROLLBACK</c> and each
/// command's SQL), so a test can prove the SHAPE of a flow — "the notification insert and the stamp update sit
/// between one BEGIN and one COMMIT" — not only its outcome. And <see cref="BeforeCommand"/> runs a hook right
/// before a chosen command: an interleaving seam, for the race a concurrent writer wins by landing between two
/// statements of the flow under test.
/// </para>
/// </summary>
public sealed class DbFaultInjector : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly List<string> _trace = [];
    private readonly List<Rule> _rules = [];
    private int _commits;

    /// <summary>Everything the context ran, in order: <c>BEGIN</c>, <c>COMMIT</c>, <c>ROLLBACK</c>, or a command's SQL.</summary>
    public IReadOnlyList<string> Trace
    {
        get { lock (_trace) return _trace.ToArray(); }
    }

    /// <summary>How many injected faults fired so far.</summary>
    public int Fired { get; private set; }

    /// <summary>The <paramref name="occurrence"/>th command (1-based) whose SQL satisfies <paramref name="match"/> fails.</summary>
    public DbFaultInjector FailCommand(Func<string, bool> match, int occurrence = 1, string? reason = null)
    {
        _rules.Add(new Rule(match, occurrence, Fault: reason ?? "injected database fault"));
        return this;
    }

    /// <summary>The <paramref name="occurrence"/>th COMMIT (1-based) fails — the transaction is not committed.</summary>
    public DbFaultInjector FailCommit(int occurrence = 1, string? reason = null)
    {
        _rules.Add(new Rule(null, occurrence, Fault: reason ?? "injected commit fault", OnCommit: true));
        return this;
    }

    /// <summary>
    /// Runs <paramref name="action"/> right before the <paramref name="occurrence"/>th command whose SQL satisfies
    /// <paramref name="match"/> — while the context's transaction, if any, is still open. The interleaving seam:
    /// the action typically runs a competing write on ANOTHER connection.
    /// </summary>
    public DbFaultInjector BeforeCommand(Func<string, bool> match, Func<Task> action, int occurrence = 1)
    {
        _rules.Add(new Rule(match, occurrence, Fault: null, Hook: action));
        return this;
    }

    /// <summary>Runs <paramref name="action"/> right before the <paramref name="occurrence"/>th COMMIT — every write of the transaction is in, none is visible yet.</summary>
    public DbFaultInjector BeforeCommit(Func<Task> action, int occurrence = 1)
    {
        _rules.Add(new Rule(null, occurrence, Fault: null, OnCommit: true, Hook: action));
        return this;
    }

    /// <summary>Forgets every rule; the trace stays.</summary>
    public void Disarm() => _rules.Clear();

    // ── commands ────────────────────────────────────────────────────────────────────────────────────────────

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await OnCommandAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await OnCommandAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await OnCommandAsync(command);
        return result;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        OnCommandAsync(command).GetAwaiter().GetResult();
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        OnCommandAsync(command).GetAwaiter().GetResult();
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        OnCommandAsync(command).GetAwaiter().GetResult();
        return result;
    }

    private async Task OnCommandAsync(DbCommand command)
    {
        var sql = command.CommandText;
        Record(sql);
        foreach (var rule in _rules.Where(r => !r.OnCommit && r.Match!(sql)).ToArray())
        {
            if (++rule.Seen != rule.Occurrence) continue;
            if (rule.Hook is not null) await rule.Hook();
            if (rule.Fault is not null) Throw(rule.Fault, sql);
        }
    }

    // ── transactions ────────────────────────────────────────────────────────────────────────────────────────

    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Record("BEGIN");
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        Record("BEGIN");
        return ValueTask.FromResult(result);
    }

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        OnCommitAsync().GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await OnCommitAsync();
        return result;
    }

    public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Record("ROLLBACK");
        return result;
    }

    public ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Record("ROLLBACK");
        return ValueTask.FromResult(result);
    }

    private async Task OnCommitAsync()
    {
        var n = ++_commits;
        Record("COMMIT");
        foreach (var rule in _rules.Where(r => r.OnCommit && r.Occurrence == n).ToArray())
        {
            if (rule.Hook is not null) await rule.Hook();
            if (rule.Fault is not null) Throw(rule.Fault, "COMMIT");
        }
    }

    private void Throw(string fault, string statement)
    {
        Fired++;
        // What a real disconnect raises (Npgsql's own type), so nothing under test can tell the difference.
        throw new NpgsqlException($"{fault} (on: {Head(statement)})");
    }

    private void Record(string entry)
    {
        lock (_trace) _trace.Add(entry);
    }

    private static string Head(string sql)
    {
        var line = sql.Replace("\r", "").Replace("\n", " ").Trim();
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    private sealed record Rule(Func<string, bool>? Match, int Occurrence, string? Fault, bool OnCommit = false, Func<Task>? Hook = null)
    {
        public int Seen;
    }
}
