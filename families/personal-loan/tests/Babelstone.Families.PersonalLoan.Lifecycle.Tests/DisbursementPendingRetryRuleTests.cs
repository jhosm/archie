using Babelstone.Families.PersonalLoan.Lifecycle;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="DisbursementPendingRetryRule"/> — the personal-loan family's re-attempt rule for a
/// held disbursement (ADR-PC-043 slot 5; bd babelstone-98mj.6). In plain English: when a loan was approved but
/// its disbursement had nowhere to land, the money is HELD at source (the loan is disbursement-pending) rather
/// than lost; this rule re-fires the disbursement the moment a live destination exists, and does so exactly
/// once. The tests drive the rule over a fake held-loan reader and a fake receivability probe (no live engine
/// / no DB) and pin the two named commitments:
/// <list type="bullet">
/// <item><b>CREDIT_UNDELIVERABLE_HELD_AT_SOURCE</b> — while the destination still rejects, a
/// disbursement-pending loan is NOT re-fired: its funds stay held at source, never disgorged; and a re-attempt
/// after the destination becomes admittable lands EXACTLY ONCE (the same one-shot occurrence, so the dispatch
/// ledger + engine command_dedup + slot-4 intent key collapse it).</item>
/// <item><b>CREDIT_UNAPPLIED_IS_ATTRIBUTED</b> — the re-attempt targets the SAME loan and its OWN
/// disbursement-account reference and start date, so the held credit is attributed to a specific loan and
/// economic occurrence, never re-scoped to a fresh one.</item>
/// </list>
/// </summary>
public sealed class DisbursementPendingRetryRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    private const string EngineDisburseKind = "disburse";
    private const long OneShotOccurrence = 1;

    // ── CREDIT_UNDELIVERABLE_HELD_AT_SOURCE ─────────────────────────────────────────────────────────

    [Fact]
    public async Task While_the_destination_still_rejects_the_disbursement_is_held_at_source_not_re_fired()
    {
        var loan = new DisbursementPendingLoan(Guid.NewGuid(), "BORROWER-ACCT-1", new DateOnly(2026, 6, 1));
        var rule = new DisbursementPendingRetryRule(
            new FakePendingReader(loan),
            new FakeReceivability(receivable: false));

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    [Fact]
    public async Task A_re_attempt_after_the_destination_is_admittable_fires_the_same_one_shot_occurrence()
    {
        var id = Guid.NewGuid();
        var loan = new DisbursementPendingLoan(id, "BORROWER-ACCT-1", new DateOnly(2026, 6, 1));
        var rule = new DisbursementPendingRetryRule(
            new FakePendingReader(loan),
            new FakeReceivability(receivable: true));

        var first = await rule.EvaluateAsync(Today);
        var second = await rule.EvaluateAsync(Today);

        var decision = Assert.Single(first);
        Assert.Equal(id, decision.InstanceId);
        Assert.Equal(EngineDisburseKind, decision.CommandKind);
        Assert.Equal(OneShotOccurrence, decision.OccurrenceKey);
        Assert.Equal($"/v1/loans/{id:D}/disbursement", decision.RequestPath);

        // Same occurrence on every pass — same kind + occurrence key — so the driver's number-pinned dispatch
        // id is identical and the engine dedupes it to one landing (exactly-once).
        var again = Assert.Single(second);
        Assert.Equal(decision.CommandKind, again.CommandKind);
        Assert.Equal(decision.OccurrenceKey, again.OccurrenceKey);
        Assert.Equal(decision.InstanceId, again.InstanceId);
    }

    // ── CREDIT_UNAPPLIED_IS_ATTRIBUTED ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_re_attempt_carries_the_loans_own_account_ref_and_start_date_as_business_valid_time()
    {
        var id = Guid.NewGuid();
        var start = new DateOnly(2026, 5, 1);
        var rule = new DisbursementPendingRetryRule(
            new FakePendingReader(new DisbursementPendingLoan(id, "BORROWER-ACCT-42", start)),
            new FakeReceivability(receivable: true));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        Assert.Equal(start, decision.DueAt);
        Assert.Equal("BORROWER-ACCT-42", decision.Body["disbursement_account_ref"]);
        Assert.Equal(
            PersonalLoanLifecycleDispatch.DueInstant(start),
            Assert.IsType<DateTimeOffset>(decision.Body["disbursed_at"]));
    }

    [Fact]
    public async Task Only_receivable_held_loans_are_re_fired_the_rest_stay_held_at_source()
    {
        var receivableId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var rejectingId = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
        var rule = new DisbursementPendingRetryRule(
            new FakePendingReader(
                new DisbursementPendingLoan(receivableId, "OK-ACCT", new DateOnly(2026, 6, 1)),
                new DisbursementPendingLoan(rejectingId, "REJECT-ACCT", new DateOnly(2026, 6, 1))),
            new FakeReceivability(receivableRefs: ["OK-ACCT"]));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));
        Assert.Equal(receivableId, decision.InstanceId);
    }

    // --- helpers ---

    private sealed class FakePendingReader(params DisbursementPendingLoan[] loans) : IDisbursementPendingReader
    {
        public Task<IReadOnlyList<DisbursementPendingLoan>> ListDisbursementPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DisbursementPendingLoan>>(
                loans.OrderBy(l => l.LoanId).ToList());
    }

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
