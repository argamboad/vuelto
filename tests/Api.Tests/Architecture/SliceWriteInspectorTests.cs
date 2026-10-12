namespace Vuelto.Api.Tests.Architecture;

/// <summary>Self-test of <see cref="SliceWriteInspector"/> (Arch A8): what counts as a write, what stays a read.</summary>
public class SliceWriteInspectorTests
{
    [Fact]
    public void AReadingHandler_WritesNothing()
    {
        const string dashboard = """
            public sealed class DashboardHandler(IRepository<Transaction> transactions, IRepository<Envelope> envelopes, ICurrentTenant tenant)
            {
                public async Task<Summary> GetAsync(CancellationToken ct) =>
                    new(await transactions.Query().Where(t => t.MonthId == id).SumAsync(t => t.Amount, ct),
                        await envelopes.Query().CountAsync(ct)); // envelopes.Remove(x) in a comment is not a write
            }
            """;
        Assert.Empty(SliceWriteInspector.Writes(dashboard));
    }

    [Fact]
    public void TheWritingMembers_AndASetBasedWriteChainedFromAQuery_Count()
    {
        const string ledger = """
            public sealed class TransactionHandler(IRepository<Transaction> transactions, IRepository<Week> weeks, IRepository<Month> months)
            {
                private readonly IRepository<Refund> _refunds;
                public TransactionHandler(IRepository<Refund> refunds) { _refunds = refunds; }
                public async Task CreateAsync(Transaction t, CancellationToken ct) { await transactions.AddAsync(t, ct); await transactions.SaveChangesAsync(ct); }
                public void Forget(Refund r) => _refunds.Remove(r);
                public Task Rebalance(Guid id, CancellationToken ct) => weeks.Query().Where(w => w.MonthId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Closed, true), ct);
                public Task<int> Count(CancellationToken ct) => months.Query().CountAsync(ct);
            }
            """;
        var writes = SliceWriteInspector.Writes(ledger);
        Assert.Equal(["Refund", "Transaction", "Week"], writes.Select(w => w.Entity).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(("Week", "ExecuteUpdateAsync"), writes);
        Assert.Contains(("Refund", "Remove"), writes);
        Assert.DoesNotContain(writes, w => w.Entity == "Month"); // counted, never written
    }

    [Fact]
    public void AContributorsWipe_IsAWrite_OfItsOwnEntity()
    {
        const string contributor = """
            public sealed class LedgerDataContributor(IRepository<Transaction> transactions) : ITenantDataContributor
            {
                public async Task WipeAsync(Guid tenantId, CancellationToken ct) =>
                    await transactions.QueryAllTenants().Where(t => t.TenantId == tenantId).ExecuteDeleteAsync(ct);
            }
            """;
        Assert.Equal([("Transaction", "ExecuteDeleteAsync")], SliceWriteInspector.Writes(contributor));
    }

    [Fact]
    public void TwoSubclassesInOneFile_EachKeepTheirEntity_UnderTheSameName()
    {
        // vuelto's expense handlers: one file, two subclasses, each injecting its own `lines`. The last declaration used to
        // win, so the fixed-expense writes were credited to VariableExpense alone.
        var handlers = """
            public abstract class LineHandler<TLine>(IRepository<TLine> lines) { public void Save(TLine l) => lines.Update(l); }
            public sealed class FixedHandler(IRepository<FixedExpense> lines) : LineHandler<FixedExpense>(lines) { }
            public sealed class VariableHandler(IRepository<VariableExpense> lines) : LineHandler<VariableExpense>(lines) { }
            """;
        Assert.Equal(["FixedExpense", "TLine", "VariableExpense"], SliceWriteInspector.Writes(handlers).Select(w => w.Entity).Order());
    }

    [Fact]
    public void ANameThatContainsARepositoryName_IsNotThatRepository()
    {
        const string source = """
            public sealed class H(IRepository<Widget> widgets, IWidgetIndex allwidgets)
            {
                public void Go() { allwidgets.Add("x"); }
            }
            """;
        Assert.Empty(SliceWriteInspector.Writes(source)); // `allwidgets.Add` is not `widgets.Add`
    }
}
