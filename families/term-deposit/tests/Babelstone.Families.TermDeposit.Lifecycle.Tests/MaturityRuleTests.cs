using Babelstone.Engine.Hosting;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Lifecycle;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.TermDeposit.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="MaturityRule"/> — the term-deposit family's one-shot lifecycle-command rule
/// (ADR-PC-036 §Decision 2/6; bd babelstone-6cpq.8). They drive the rule over a fake deposit read-model store
/// (no live engine / no DB) and assert the maturity-specific decisions:
/// <list type="bullet">
/// <item>a deposit at/after maturity is matured EXACTLY ONCE (the dispatch ledger absorbs the re-tick), under
/// the canonical server-derived key the engine maturity endpoint also derives — <c>("mature", occurrence 1)</c>;</item>
/// <item>a backfilled (past) maturity is re-derived under the SAME number-pinned id, carrying its OWN maturity
/// date as the business valid_time so a late firing records the correct date;</item>
/// <item>the rule fires a deposit maturing TODAY but NEVER one maturing tomorrow — the on/after-maturity
/// ordering invariant the rule owns (ADR-PC-036 §Residual risks);</item>
/// <item>an already-matured (non-Active) deposit is never fired again.</item>
/// </list>
/// </summary>
public sealed class MaturityRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    // The stable command-kind the ENGINE maturity endpoint derives its idempotency key under
    // (DepositsEndpoints.MatureCommandKind). The driver must converge on it, so the key cross-checks use this
    // literal rather than the rule's own constant (which would make the assertion circular).
    private const string EngineMatureKind = "mature";

    [Fact]
    public async Task A_deposit_at_or_after_maturity_is_matured_exactly_once()
    {
        var deposit = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new MaturityRule(new FakeDepositStore(Deposit(deposit, Today, "Active"))));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second);

        // POSTed once, under the canonical server-derived key the engine also derives — ("mature", 1).
        Assert.Single(sink.Dispatched);
        Assert.Equal("mature", first[0].CommandKind);
        Assert.Equal(1, first[0].OccurrenceKey);
        Assert.Equal(deposit, first[0].InstanceId);
        Assert.Equal(LifecycleCommandKey.Derive(deposit, EngineMatureKind, 1), first[0].CommandId);
    }

    [Fact]
    public async Task A_backfilled_past_maturity_is_rederived_under_the_same_number_pinned_key()
    {
        var deposit = Guid.NewGuid();
        var maturedOn = Today.AddDays(-10); // matured while the driver was down
        var rule = new MaturityRule(new FakeDepositStore(Deposit(deposit, maturedOn, "Active")));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        // The occurrence key is the constant 1, so a backfill re-derives the SAME id a first firing would —
        // the engine's command_dedup swallows the repeat (one-shot, number-pinned).
        Assert.Equal(1, decision.OccurrenceKey);
        Assert.Equal(LifecycleCommandKey.Derive(deposit, EngineMatureKind, 1),
            LifecycleDispatchId.Of(decision));

        // The due date rides as the business valid_time: the deposit's OWN (past) maturity date, not today, so
        // a late firing records the correct business date (ADR-PC-036 §Context; ADR-PC-002).
        Assert.Equal(maturedOn, decision.DueAt);
        Assert.Equal(
            new DateTimeOffset(maturedOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Assert.IsType<DateTimeOffset>(decision.Body["matured_at"]));
    }

    [Fact]
    public async Task The_rule_fires_a_maturity_today_but_never_one_due_tomorrow()
    {
        var maturingToday = Guid.NewGuid();
        var maturingTomorrow = Guid.NewGuid();
        var rule = new MaturityRule(new FakeDepositStore(
            Deposit(maturingToday, Today, "Active"),
            Deposit(maturingTomorrow, Today.AddDays(1), "Active")));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        // Today's maturity fires (the boundary is inclusive of today); tomorrow's is excluded — the rule fires
        // on/after the maturity date and NEVER before it (the one ordering invariant it owns).
        Assert.Equal(maturingToday, decision.InstanceId);
        Assert.Equal($"/v1/deposits/{maturingToday:D}/maturity", decision.RequestPath);
        // The deposit money-mover route authorises the non-interactive driver by the scoped SCA principal.
        Assert.Equal(MaturityRule.DepositMoneyMoverScope, decision.ServicePrincipalScope);
    }

    [Fact]
    public async Task An_already_matured_deposit_is_not_fired_again()
    {
        // A non-Active deposit (maturity already happened) is filtered out, so the driver never re-POSTs a
        // maturity the engine would reject — command_dedup is the backstop regardless.
        var deposit = Guid.NewGuid();
        var rule = new MaturityRule(new FakeDepositStore(Deposit(deposit, Today.AddDays(-1), "Matured")));

        Assert.Empty(await rule.EvaluateAsync(Today));
    }

    // --- helpers ---

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new InMemoryLifecycleDispatchLedger(), sink);

    private static DepositReadModelRow Deposit(Guid id, DateOnly maturity, string lifecycle) =>
        new(
            StreamId: id,
            Sor: "engine",
            PrincipalCents: 0,
            TanBasisPoints: 0,
            RateSheetVersionId: string.Empty,
            ProductCode: string.Empty,
            TermDays: 0,
            StartDate: maturity.AddDays(-365),
            MaturityDate: maturity,
            InterestVariant: string.Empty,
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            Lifecycle: lifecycle,
            AccruedGrossInterestCents: 0,
            WithholdingToDateCents: 0,
            NetInterestCents: 0,
            TotalPayoutCents: 0,
            CouponsPaid: 0,
            Detail: ReadOnlyMemory<byte>.Empty,
            LastSequence: 1,
            LastUpdated: default);

    /// <summary>A fake deposit read-model store that honours the half-open [from, to) maturity window the rule
    /// scans (so the on/after-maturity ordering is genuinely exercised), throwing for the reads the rule never
    /// makes.</summary>
    private sealed class FakeDepositStore(params DepositReadModelRow[] rows) : IDepositReadModelStore
    {
        public Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepositReadModelRow>>(
                rows.Where(r => r.MaturityDate >= fromInclusive && r.MaturityDate < toExclusive)
                    .OrderBy(r => r.MaturityDate).ThenBy(r => r.StreamId).ToList());

        public Task<IReadOnlyList<DepositReadModelRow>> ListWithWithholdingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListActiveStreamIdsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DepositReadModelRow>> ListPayoutPendingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Records every command the pass POSTs through it (the decision + the derived command id).</summary>
    private sealed class RecordingSink : ILifecycleCommandSink
    {
        private readonly List<(LifecycleCommandDecision Decision, Guid CommandId)> _dispatched = [];

        public IReadOnlyList<(LifecycleCommandDecision Decision, Guid CommandId)> Dispatched => _dispatched;

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            _dispatched.Add((decision, commandId));
            return Task.CompletedTask;
        }
    }
}
