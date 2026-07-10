using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Tests for <see cref="PayoutLandingReconciler"/> — the engine↔engine-CA payout-landing reconciler
/// (ADR-PC-043; bd babelstone-98mj.7). In plain English: this is the safety net that catches the rare cases
/// prevention cannot — a payout that never landed, landed twice, or landed at the wrong amount. The tests pin
/// <b>XFAMILY_PAYOUT_LANDING_RECONCILED</b>: each source payout occurrence pairs to exactly one CA landing by
/// the economic-intent id; a matched pair raises NO signal; and DROP (source-paid-not-landed past the interim
/// SLA), DOUBLE, WRONG-AMOUNT, and an orphan landing each raise an operational signal — and the reconciler
/// NEVER invents or auto-corrects a Movement (a signal is advisory). The interim DROP SLA is recorded here
/// (Q-AG calibration noted pending). Pure, no I/O — default CI lane.
/// </summary>
public sealed class PayoutLandingReconciliationTests
{
    private static readonly Guid SourceA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SourceB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateOnly AsOf = new(2026, 7, 10);

    // Interim DROP SLA (bd babelstone-98mj.7): a source payout with no landing is IN_FLIGHT until older than
    // this horizon, then a DROP. Q-AG calibration of the real horizon is PENDING — this cross-checks the
    // documented interim placeholder, not a settled value.
    private const int InterimDropSlaDays = PayoutLandingReconciler.DefaultDropSlaDays;

    [Fact]
    public void Each_source_payout_pairs_to_exactly_one_matching_ca_landing_no_signal()
    {
        // Happy path: one source payout, one CA landing of the same amount for the same intent — MATCHED,
        // no operator signal. Proves the intent-keyed pairing (the reconciler recovers the intent from the
        // landing's reference the same way the writer derived it).
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var outcomes = PayoutLandingReconciler.Reconcile(
            sourcePayouts: [new SourcePayout(intent, 150_00, new DateOnly(2026, 7, 9))],
            caLandings: [Landing(intent, 150_00, "Credit")],
            asOf: AsOf);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(intent, outcome.IntentId);
        Assert.Equal(ReconciliationClass.Matched, outcome.Classification);
        Assert.Null(outcome.Signal);
    }

    [Fact]
    public void A_source_paid_payout_that_never_landed_past_the_sla_is_a_drop_signal()
    {
        // Source paid, nothing landed, and older than the interim SLA — a DROP that raises a signal. The
        // reconciler surfaces it; it never re-pays (the source holds the funds via the payout-pending marker).
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var paidOn = AsOf.AddDays(-(InterimDropSlaDays + 1));

        var outcome = Assert.Single(PayoutLandingReconciler.Reconcile(
            sourcePayouts: [new SourcePayout(intent, 150_00, paidOn)],
            caLandings: [],
            asOf: AsOf));

        Assert.Equal(ReconciliationClass.Drop, outcome.Classification);
        Assert.NotNull(outcome.Signal);
        Assert.Equal(ReconciliationClass.Drop, outcome.Signal!.Classification);
        Assert.Equal(intent, outcome.Signal.IntentId);
    }

    [Fact]
    public void A_source_paid_payout_within_the_sla_is_in_flight_not_yet_a_drop()
    {
        // Same source-paid-not-landed shape but WITHIN the interim SLA — IN_FLIGHT, no DROP signal yet (the
        // landing may still be in transit). This is what makes the SLA horizon load-bearing.
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var paidRecently = AsOf.AddDays(-(InterimDropSlaDays - 1));

        var outcome = Assert.Single(PayoutLandingReconciler.Reconcile(
            sourcePayouts: [new SourcePayout(intent, 150_00, paidRecently)],
            caLandings: [],
            asOf: AsOf));

        Assert.Equal(ReconciliationClass.InFlight, outcome.Classification);
        Assert.Null(outcome.Signal);
    }

    [Fact]
    public void Two_landings_for_one_source_payout_are_a_double_signal()
    {
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");

        var outcome = Assert.Single(PayoutLandingReconciler.Reconcile(
            sourcePayouts: [new SourcePayout(intent, 150_00, new DateOnly(2026, 7, 9))],
            caLandings: [Landing(intent, 150_00, "Credit"), Landing(intent, 150_00, "Credit")],
            asOf: AsOf));

        Assert.Equal(ReconciliationClass.Double, outcome.Classification);
        Assert.NotNull(outcome.Signal);
        Assert.Equal(ReconciliationClass.Double, outcome.Signal!.Classification);
    }

    [Fact]
    public void A_landing_of_a_different_amount_is_a_wrong_amount_signal()
    {
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");

        var outcome = Assert.Single(PayoutLandingReconciler.Reconcile(
            sourcePayouts: [new SourcePayout(intent, 150_00, new DateOnly(2026, 7, 9))],
            caLandings: [Landing(intent, 149_99, "Credit")],
            asOf: AsOf));

        Assert.Equal(ReconciliationClass.WrongAmount, outcome.Classification);
        Assert.NotNull(outcome.Signal);
        Assert.Contains("150", outcome.Signal!.Detail);
        Assert.Contains("14999", outcome.Signal.Detail);
    }

    [Fact]
    public void A_landing_with_no_source_payout_is_an_orphan_landing_signal()
    {
        // Money landed on the CA for an intent the engine never sourced — surfaced as its own signal, never
        // silently absorbed.
        var orphanIntent = SettlementReferences.DeriveIntentId(SourceB, "installment-3");

        var outcome = Assert.Single(PayoutLandingReconciler.Reconcile(
            sourcePayouts: [],
            caLandings: [Landing(orphanIntent, 42_00, "Credit")],
            asOf: AsOf));

        Assert.Equal(ReconciliationClass.OrphanLanding, outcome.Classification);
        Assert.NotNull(outcome.Signal);
        Assert.Equal(orphanIntent, outcome.IntentId);
    }

    [Fact]
    public void A_mixed_batch_classifies_every_intent_and_signals_only_the_non_matched()
    {
        // A representative batch: one matched, one dropped, one double, one wrong-amount. The reconciler
        // classifies all four and raises a signal for exactly the three non-matched — never inventing a
        // corrective Movement for any of them.
        var matched = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var dropped = SettlementReferences.DeriveIntentId(SourceA, "coupon-1");
        var doubled = SettlementReferences.DeriveIntentId(SourceB, "maturity");
        var wrong = SettlementReferences.DeriveIntentId(SourceB, "installment-2");

        var outcomes = PayoutLandingReconciler.Reconcile(
            sourcePayouts:
            [
                new SourcePayout(matched, 100_00, new DateOnly(2026, 7, 9)),
                new SourcePayout(dropped, 200_00, AsOf.AddDays(-10)),
                new SourcePayout(doubled, 300_00, new DateOnly(2026, 7, 9)),
                new SourcePayout(wrong, 400_00, new DateOnly(2026, 7, 9)),
            ],
            caLandings:
            [
                Landing(matched, 100_00, "Credit"),
                Landing(doubled, 300_00, "Credit"),
                Landing(doubled, 300_00, "Credit"),
                Landing(wrong, 399_00, "Credit"),
            ],
            asOf: AsOf);

        Assert.Equal(ReconciliationClass.Matched, ClassOf(outcomes, matched));
        Assert.Equal(ReconciliationClass.Drop, ClassOf(outcomes, dropped));
        Assert.Equal(ReconciliationClass.Double, ClassOf(outcomes, doubled));
        Assert.Equal(ReconciliationClass.WrongAmount, ClassOf(outcomes, wrong));

        // Exactly the three non-matched intents carry a signal; the matched one does not.
        var signalled = outcomes.Where(o => o.Signal is not null).Select(o => o.IntentId).ToHashSet();
        Assert.Equal(3, signalled.Count);
        Assert.Contains(dropped, signalled);
        Assert.Contains(doubled, signalled);
        Assert.Contains(wrong, signalled);
        Assert.DoesNotContain(matched, signalled);
    }

    // --- helpers ---

    private static CaLanding Landing(string intentId, long amountCents, string direction) =>
        new(SettlementReferences.DeriveFromIntent(SettlementReferences.CreditPrefix, intentId), amountCents, direction);

    private static ReconciliationClass ClassOf(IReadOnlyList<ReconciliationOutcome> outcomes, string intentId) =>
        outcomes.Single(o => o.IntentId == intentId).Classification;
}
