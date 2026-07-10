using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Lifecycle;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.TermDeposit.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="PayoutPendingRetryRule"/> — the term-deposit family's re-attempt rule for a held
/// maturity payout (ADR-PC-043 slot 5; bd babelstone-98mj.6). In plain English: when a deposit matured but its
/// payout had nowhere to land, the money is HELD at source (the deposit is payout-pending) rather than lost;
/// this rule re-fires the payout the moment a live destination exists, and does so exactly once. The tests
/// drive the rule over a fake read-model store and a fake receivability probe (no live engine / no DB) and
/// pin the two named commitments:
/// <list type="bullet">
/// <item><b>CREDIT_UNDELIVERABLE_HELD_AT_SOURCE</b> — while the destination still rejects, a payout-pending
/// deposit is NOT re-fired: its funds stay held at source, never disgorged into a void nor an anonymous pot;
/// and a re-attempt after the destination becomes admittable lands EXACTLY ONCE (the same one-shot occurrence,
/// so the dispatch ledger + engine command_dedup + slot-4 intent key collapse it).</item>
/// <item><b>CREDIT_UNAPPLIED_IS_ATTRIBUTED</b> — the re-attempt targets the SAME instance and re-fires the SAME
/// maturity endpoint/kind/occurrence the original payout used, so the held credit is attributed to a specific
/// deposit and economic occurrence, never re-scoped to a fresh one.</item>
/// </list>
/// </summary>
public sealed class PayoutPendingRetryRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    // The stable command-kind the ENGINE maturity endpoint derives its idempotency key under
    // (DepositsEndpoints.MatureCommandKind) — the re-attempt re-fires the SAME occurrence, so it converges on
    // this kind rather than a fresh one. Named literally so the assertion is not circular.
    private const string EngineMatureKind = "mature";
    private const long OneShotOccurrence = 1;

    // ── CREDIT_UNDELIVERABLE_HELD_AT_SOURCE ─────────────────────────────────────────────────────────

    [Fact]
    public async Task While_the_destination_still_rejects_the_payout_is_held_at_source_not_re_fired()
    {
        // The destination is NOT receivable, so the held deposit is skipped this pass — its funds stay at
        // source, never disgorged. The rule returns no decision (nothing to re-fire yet).
        var deposit = PayoutPending(Guid.NewGuid(), new DateOnly(2026, 6, 20));
        var rule = new PayoutPendingRetryRule(
            new FakePayoutPendingStore(deposit),
            new FakeReceivability(receivable: false));

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    [Fact]
    public async Task A_re_attempt_after_the_destination_is_admittable_fires_the_same_one_shot_occurrence()
    {
        // The destination is receivable again (re-opened / reactivated / re-targeted), so the rule surfaces a
        // re-attempt — re-firing the SAME maturity occurrence (kind "mature", occurrence 1, the maturity path)
        // so the driver's dedupe + engine command_dedup + the slot-4 intent key collapse a late original apply
        // and this re-attempt to EXACTLY ONE landing. Two passes surface the same decision (idempotent under
        // the ledger), never two distinct occurrences.
        var id = Guid.NewGuid();
        var maturity = new DateOnly(2026, 6, 20);
        var rule = new PayoutPendingRetryRule(
            new FakePayoutPendingStore(PayoutPending(id, maturity)),
            new FakeReceivability(receivable: true));

        var first = await rule.EvaluateAsync(Today);
        var second = await rule.EvaluateAsync(Today);

        var decision = Assert.Single(first);
        Assert.Equal(id, decision.InstanceId);
        Assert.Equal(EngineMatureKind, decision.CommandKind);
        Assert.Equal(OneShotOccurrence, decision.OccurrenceKey);
        Assert.Equal($"/v1/deposits/{id:D}/maturity", decision.RequestPath);

        // The re-attempt is the SAME occurrence on every pass — same kind + occurrence key — so the driver's
        // number-pinned dispatch id is identical and the engine dedupes it to one landing (exactly-once).
        var again = Assert.Single(second);
        Assert.Equal(decision.CommandKind, again.CommandKind);
        Assert.Equal(decision.OccurrenceKey, again.OccurrenceKey);
        Assert.Equal(decision.InstanceId, again.InstanceId);
    }

    // ── CREDIT_UNAPPLIED_IS_ATTRIBUTED ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_re_attempt_carries_the_deposits_own_maturity_date_as_the_business_valid_time()
    {
        // Attributed to a specific occurrence: the re-attempt rides the deposit's OWN maturity date as
        // matured_at, so a late re-fire records the correct business valid_time (ADR-PC-002), not "today".
        var id = Guid.NewGuid();
        var maturity = new DateOnly(2026, 5, 1);
        var rule = new PayoutPendingRetryRule(
            new FakePayoutPendingStore(PayoutPending(id, maturity)),
            new FakeReceivability(receivable: true));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        Assert.Equal(maturity, decision.DueAt);
        Assert.Equal(
            TermDepositLifecycleDispatch.DueInstant(maturity),
            Assert.IsType<DateTimeOffset>(decision.Body["matured_at"]));
    }

    [Fact]
    public async Task Only_receivable_held_deposits_are_re_fired_the_rest_stay_held_at_source()
    {
        // A mixed population: two held deposits, one whose destination is now receivable and one still
        // rejecting. Only the receivable one is re-fired; the other stays held at source (attributed, not
        // disgorged), proving the per-deposit gate.
        var receivableId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var rejectingId = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
        var rule = new PayoutPendingRetryRule(
            new FakePayoutPendingStore(
                PayoutPending(receivableId, new DateOnly(2026, 6, 1)),
                PayoutPending(rejectingId, new DateOnly(2026, 6, 1))),
            new FakeReceivability(receivableRefs: [receivableId.ToString()]));

        var decisions = await rule.EvaluateAsync(Today);

        var decision = Assert.Single(decisions);
        Assert.Equal(receivableId, decision.InstanceId);
    }

    // --- helpers ---

    private static DepositReadModelRow PayoutPending(Guid id, DateOnly maturity) =>
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
            Lifecycle: nameof(DepositLifecycle.PayoutPending),
            AccruedGrossInterestCents: 0,
            WithholdingToDateCents: 0,
            NetInterestCents: 0,
            TotalPayoutCents: 0,
            CouponsPaid: 0,
            Detail: ReadOnlyMemory<byte>.Empty,
            LastSequence: 1,
            LastUpdated: default);

    /// <summary>A fake store answering only the payout-pending scan (the one read the rule makes).</summary>
    private sealed class FakePayoutPendingStore(params DepositReadModelRow[] rows) : IDepositReadModelStore
    {
        public Task<IReadOnlyList<DepositReadModelRow>> ListPayoutPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepositReadModelRow>>(
                rows.OrderBy(r => r.StreamId).ToList());

        public Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DepositReadModelRow>> ListWithWithholdingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListActiveStreamIdsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A fake receivability probe: either a blanket verdict, or a set of receivable refs.</summary>
    private sealed class FakeReceivability : IPayoutDestinationReceivability
    {
        private readonly bool? _blanket;
        private readonly HashSet<string> _receivableRefs;

        public FakeReceivability(bool receivable)
        {
            _blanket = receivable;
            _receivableRefs = [];
        }

        public FakeReceivability(IEnumerable<string> receivableRefs)
        {
            _blanket = null;
            _receivableRefs = [.. receivableRefs];
        }

        public Task<bool> IsReceivableAsync(string beneficiaryAccountRef, DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult(_blanket ?? _receivableRefs.Contains(beneficiaryAccountRef));
    }
}
