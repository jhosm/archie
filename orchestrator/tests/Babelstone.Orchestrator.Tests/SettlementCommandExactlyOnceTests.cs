using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The ADR-PC-043 slot-4 EXACTLY-ONCE fitness functions for the engine-CA settlement contract (bd
/// babelstone-98mj.1). In plain English: every cross-family payment carries a stable "which economic event is
/// this?" id — the economic-INTENT id, <c>f(source_id, occurrence)</c> — and the current-account writer's
/// append <c>command_id</c> is derived from THAT id, NOT from the HTTP Idempotency-Key. So a settlement reissue
/// (a byte-identical command body redelivered with a fresh dispatch <c>message_id</c>) presents the SAME
/// reference and collapses to ONE landing at the CA's <c>command_dedup</c>; a re-target / retry derives its
/// resolution key from the SAME intent id, never fresh, so a late original apply and the resolution collapse to
/// one landing too. These three tests are the load-bearing gates the ADR names:
/// <list type="bullet">
///   <item><c>SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED</c> — the CA-apply command_id derives from the body's
///   intent reference, not the HTTP Idempotency-Key.</item>
///   <item><c>SETTLEMENT_CA_CASH_LEG_IDEMPOTENT</c> — a saga reissue with a FRESH dispatch message_id collapses
///   to ONE append (byte-identical body ⇒ identical intent-derived reference).</item>
///   <item><c>CREDIT_RESOLUTION_KEY_INTENT_DERIVED</c> — the resolution key is g(IntentId); a fresh-id
///   resolution fails the intent-derived check (the double-pay guard).</item>
/// </list>
/// </summary>
/// <remarks>
/// Pure, Docker-free tests of the reference-derivation + payload-assembly seams
/// (<see cref="SettlementReferences"/> / <see cref="SettlementCommandPayloadFactory"/>). No clock, no I/O, no
/// DB — the ADR-PC-043 slot-4 rule is a property of the byte-stable body derivation, and these assert it
/// directly. The amount rides as integer cents (<c>long</c>) — the substrate stays extraction-ready and does
/// not reference the engine's <c>Money</c> type (ADR-PC-019 §P2).
/// </remarks>
public sealed class SettlementCommandExactlyOnceTests
{
    // A stable source aggregate + occurrence — a matured deposit's maturity payout. The intent id is a pure
    // function of these (ADR-PC-043 §Idempotency): the SAME (source, occurrence) always yields the SAME id.
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Occurrence = "maturity";
    private const long AmountCents = 250_000L; // €2,500.00 — exactly the source Movement.Amount.

    private static string IntentId() => SettlementReferences.DeriveIntentId(SourceId, Occurrence);

    private static SettlementIntent Intent() => new(IntentId(), AmountCents);

    // ============================================================================================
    // SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED — the CA-apply command_id is derived from the body's
    // economic-intent reference f(source_id, occurrence), NEVER the HTTP Idempotency-Key.
    // ============================================================================================

    [Fact]
    public void SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED_the_confirm_credit_reference_derives_from_the_intent_id()
    {
        var intent = Intent();

        // A message_id (the HTTP Idempotency-Key the dispatcher mints per DELIVERY) that has NOTHING to do with
        // the reference — the point is the reference does NOT derive from it.
        var deliveryMessageId = Guid.NewGuid();

        var credit = (ConfirmCreditCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit,
            processId: Guid.NewGuid(),
            causationMessageId: deliveryMessageId,
            correlationId: null,
            intent)!;

        // The CA-apply reference IS the intent-derived one (the credit prefix + the exact intent id) — the
        // deterministic f(source_id, occurrence), not the delivery message_id.
        var expected = SettlementReferences.DeriveFromIntent(SettlementReferences.CreditPrefix, intent.IntentId);
        Assert.Equal(expected, credit.CreditRef);
        Assert.Contains(intent.IntentId, credit.CreditRef);
        Assert.DoesNotContain(deliveryMessageId.ToString("N"), credit.CreditRef);
        // The amount rides the body (the in-band WRONG-AMOUNT guard) — exactly the source Movement.Amount.
        Assert.Equal(AmountCents, credit.AmountCents);
    }

    [Fact]
    public void SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED_the_confirm_credit_carries_the_promoted_destination_and_engine_ca_target()
    {
        // ADR-PC-043 §D5 / CA-17: when the substrate threads a promoted destination account_ref onto the intent,
        // the engine-CA ConfirmCredit body carries THAT account_ref (never the ACCT-{processId} placeholder), the
        // exact source amount (the WRONG-AMOUNT guard), and settlement_target = engine-ca (so the dispatcher
        // routes the credit to the engine-owned CA ingress, not the legacy core). This is the fix for the
        // never-wired maturity-credit path (bd babelstone-u79p.21).
        const string destination = "acct-conta-a-ordem-0001";
        var intent = new SettlementIntent(IntentId(), AmountCents, destination);
        var processId = Guid.NewGuid();

        var credit = (ConfirmCreditCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, Guid.NewGuid(), null, intent)!;

        Assert.Equal(destination, credit.AccountRef);
        Assert.NotEqual(
            SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId), credit.AccountRef);
        Assert.Equal(AmountCents, credit.AmountCents);
        Assert.Equal(SettlementCommandRouter.EngineCaValue, credit.SettlementTarget);
    }

    [Fact]
    public void SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED_no_intent_keeps_the_placeholder_and_no_engine_ca_target()
    {
        // Without a threaded intent (the legacy-DDA path) the ConfirmCredit falls back to the ACCT-{processId}
        // placeholder and promotes NO engine-ca target — legacy routing is UNCHANGED.
        var processId = Guid.NewGuid();

        var credit = (ConfirmCreditCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, Guid.NewGuid(), null, intent: null)!;

        Assert.Equal(
            SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId), credit.AccountRef);
        Assert.Null(credit.SettlementTarget);
    }

    [Fact]
    public void SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED_the_confirm_debit_carries_the_promoted_destination_and_engine_ca_target()
    {
        // ADR-PC-043 §D5 / CA-17 for the DEBIT legs (bd babelstone-u79p.22): when the substrate threads a
        // promoted destination account_ref onto the intent, the engine-CA ConfirmDebit body carries THAT
        // account_ref (never the ACCT-{processId} placeholder), the exact source amount (the WRONG-AMOUNT
        // guard), and settlement_target = engine-ca (so the dispatcher routes the debit to the engine-owned CA
        // capture ingress, not the legacy core). The confirm fires on a LATER advance off the synthesized
        // BalanceReserved event; the promoted values reach it via the dispatcher's forward-propagation across
        // the reserve→confirm hop — this test pins the factory half (given the intent, the body is correct).
        const string destination = "acct-conta-a-ordem-0001";
        var intent = new SettlementIntent(IntentId(), AmountCents, destination);
        var processId = Guid.NewGuid();

        var debit = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit, processId, Guid.NewGuid(), null, intent)!;

        Assert.Equal(destination, debit.AccountRef);
        Assert.NotEqual(
            SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId), debit.AccountRef);
        Assert.Equal(AmountCents, debit.AmountCents);
        Assert.Equal(SettlementCommandRouter.EngineCaValue, debit.SettlementTarget);
    }

    [Fact]
    public void SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED_the_reserve_leg_carries_the_promoted_destination_amount_and_engine_ca_target()
    {
        // The DEBIT path's reversible RESERVE leg fires on the START advance, where the promoted headers are
        // directly in scope. It must carry the promoted account_ref, the amount (the engine-CA authorize
        // ingress REQUIRES a positive amount to place the reversible hold, else a 400), and settlement_target =
        // engine-ca — so the hold the reserve places is on the SAME account + amount the confirm later
        // captures. Reserve and confirm agree by construction (bd babelstone-u79p.22).
        const string destination = "acct-conta-a-ordem-0001";
        var intent = new SettlementIntent(IntentId(), AmountCents, destination);
        var processId = Guid.NewGuid();

        var reserve = (ReserveAccountBalanceCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ReserveAccountBalance, processId, Guid.NewGuid(), null, intent)!;

        Assert.Equal(destination, reserve.AccountRef);
        Assert.NotEqual(
            SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId), reserve.AccountRef);
        Assert.Equal(AmountCents, reserve.AmountCents);
        Assert.Equal(SettlementCommandRouter.EngineCaValue, reserve.SettlementTarget);
    }

    [Fact]
    public void SETTLEMENT_LEG_ACCOUNT_REF_STABLE_two_payouts_on_one_account_share_the_accountref_but_differ_in_intent_ref()
    {
        // ADR-PC-043 §Payload-shape / CA-18 (bd babelstone-u79p.22): the account-identity axis (AccountRef —
        // ONE per account, stable across many payouts) and the exactly-once economic-intent axis (the
        // IntentId-derived CoreHoldRef — ONE per payout occurrence) are INDEPENDENT. Two distinct payouts
        // collecting from the SAME conta à ordem — a loan's installment-1 and installment-2 — carry the SAME
        // promoted AccountRef while their intent-derived references DIFFER, so the promoted destination can
        // never stand in for the dedup key and vice-versa (the orthogonality the SettlementIntent record
        // documents, now pinned by one test that constructs both on ONE account).
        const string oneAccount = "acct-conta-a-ordem-0001";
        var installment1 = new SettlementIntent(
            SettlementReferences.DeriveIntentId(SourceId, "installment-1"), AmountCents, oneAccount);
        var installment2 = new SettlementIntent(
            SettlementReferences.DeriveIntentId(SourceId, "installment-2"), AmountCents, oneAccount);

        var debit1 = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit, Guid.NewGuid(), Guid.NewGuid(), null, installment1)!;
        var debit2 = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit, Guid.NewGuid(), Guid.NewGuid(), null, installment2)!;

        // SAME account — the stable account-identity axis (one AccountRef, many payouts) ...
        Assert.Equal(oneAccount, debit1.AccountRef);
        Assert.Equal(debit1.AccountRef, debit2.AccountRef);
        // ... DIFFERENT intent-derived exactly-once key — the per-occurrence economic-intent axis. installment-2
        // can never dedup against installment-1 (no double-collect), though both land on the one account.
        Assert.NotEqual(debit1.CoreHoldRef, debit2.CoreHoldRef);
        Assert.Contains(installment1.IntentId, debit1.CoreHoldRef);
        Assert.Contains(installment2.IntentId, debit2.CoreHoldRef);
    }

    [Fact]
    public void SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED_the_confirm_debit_reference_derives_from_the_intent_id()
    {
        var intent = Intent();
        var deliveryMessageId = Guid.NewGuid();

        var debit = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit,
            processId: Guid.NewGuid(),
            causationMessageId: deliveryMessageId,
            correlationId: null,
            intent)!;

        var expected = SettlementReferences.DeriveFromIntent(SettlementReferences.CoreHoldPrefix, intent.IntentId);
        Assert.Equal(expected, debit.CoreHoldRef);
        Assert.Contains(intent.IntentId, debit.CoreHoldRef);
        Assert.DoesNotContain(deliveryMessageId.ToString("N"), debit.CoreHoldRef);
        Assert.Equal(AmountCents, debit.AmountCents);
    }

    [Fact]
    public void SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED_the_intent_id_is_a_pure_function_of_source_and_occurrence()
    {
        // f(source_id, occurrence) is deterministic (ADR-PC-010 §P5): same inputs → same id; a different
        // occurrence on the same source → a DIFFERENT intent (installment-2 never dedups against maturity), and
        // a different source → a different intent.
        Assert.Equal(
            SettlementReferences.DeriveIntentId(SourceId, Occurrence),
            SettlementReferences.DeriveIntentId(SourceId, Occurrence));
        Assert.NotEqual(
            SettlementReferences.DeriveIntentId(SourceId, "maturity"),
            SettlementReferences.DeriveIntentId(SourceId, "installment-2"));
        Assert.NotEqual(
            SettlementReferences.DeriveIntentId(SourceId, Occurrence),
            SettlementReferences.DeriveIntentId(Guid.NewGuid(), Occurrence));
    }

    // ============================================================================================
    // SETTLEMENT_CA_CASH_LEG_IDEMPOTENT — a saga REISSUE with a FRESH dispatch message_id collapses
    // to ONE append: the byte-identical body derives the identical intent reference, so the CA's
    // command_dedup absorbs the second delivery.
    // ============================================================================================

    [Fact]
    public void SETTLEMENT_CA_CASH_LEG_IDEMPOTENT_a_reissue_with_a_fresh_message_id_is_byte_identical()
    {
        var intent = Intent();
        var processId = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        // First dispatch and a REISSUE — the dispatcher mints a fresh delivery message_id (the
        // causationMessageId proxy here) each time, but the LOGICAL command is the same economic intent.
        var first = SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, causationMessageId: Guid.NewGuid(), correlation, intent)!;
        var reissue = SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, causationMessageId: Guid.NewGuid(), correlation, intent)!;

        // The intent-derived CA-apply reference is IDENTICAL across the reissue (it does not carry the delivery
        // message_id), so the CA's command_dedup — keyed on the body's intent reference, not the
        // Idempotency-Key — collapses the second delivery to ONE credit append (no double-move).
        Assert.Equal(
            ((ConfirmCreditCommand)first).CreditRef,
            ((ConfirmCreditCommand)reissue).CreditRef);
        Assert.Equal(
            ((ConfirmCreditCommand)first).AmountCents,
            ((ConfirmCreditCommand)reissue).AmountCents);
    }

    [Fact]
    public void SETTLEMENT_CA_CASH_LEG_IDEMPOTENT_both_confirm_legs_are_byte_stable_across_reissue()
    {
        var intent = Intent();
        var processId = Guid.NewGuid();
        // The saga FACTS in scope — the causation + correlation references — are re-derived identically on a
        // reissue (they are pre-existing ids carried through, ADR-IC-003 §P7), so a true reissue re-assembles
        // the SAME body. The per-DELIVERY message_id (the HTTP Idempotency-Key) is minted by the outbox writer
        // as a row COLUMN, never in the body — which is exactly why a fresh delivery does not perturb the bytes.
        var causation = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        foreach (var cmd in new[] { SettlementProcess.ConfirmDebit, SettlementProcess.ConfirmCredit })
        {
            var a = SettlementCommandPayloadFactory.Build(cmd, processId, causation, correlation, intent)!;
            var b = SettlementCommandPayloadFactory.Build(cmd, processId, causation, correlation, intent)!;
            // The full body bytes are identical across the reissue — the ADR-PC-010 §P5 byte-stability the
            // no-double-move guarantee rests on, now anchored on the INTENT id rather than the process id.
            Assert.Equal(a.ToBytes(), b.ToBytes());
        }
    }

    [Fact]
    public void SETTLEMENT_CA_CASH_LEG_IDEMPOTENT_the_clearance_query_reuses_the_same_intent_reference()
    {
        var intent = Intent();
        var processId = Guid.NewGuid();

        // The RETRY_PERMITTED clearance query must target the SAME reference the indeterminate confirm used —
        // never a fresh one — so the retry resolves exactly that operation and cannot double-move.
        var confirm = (ConfirmCreditCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, Guid.NewGuid(), null, intent)!;
        var clearance = (QueryCoreCreditStatusCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.QueryCoreCreditStatus, processId, Guid.NewGuid(), null, intent)!;
        Assert.Equal(confirm.CreditRef, clearance.CreditRef);

        var confirmDebit = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit, processId, Guid.NewGuid(), null, intent)!;
        var debitClearance = (QueryCoreDebitStatusCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.QueryCoreDebitStatus, processId, Guid.NewGuid(), null, intent)!;
        Assert.Equal(confirmDebit.CoreHoldRef, debitClearance.CoreHoldRef);
    }

    // ============================================================================================
    // CREDIT_RESOLUTION_KEY_INTENT_DERIVED — the resolution/retry key is g(IntentId), derived from
    // the SAME original intent id, never fresh (the double-pay guard).
    // ============================================================================================

    [Fact]
    public void CREDIT_RESOLUTION_KEY_INTENT_DERIVED_the_resolution_key_is_a_pure_function_of_the_intent_id()
    {
        var intentId = IntentId();

        // ResolutionIntentId = g(IntentId): derived from the ORIGINAL intent id, deterministic, and it CARRIES
        // the intent id — so an operator re-target and a late original apply collapse to one landing.
        var resolution = SettlementReferences.DeriveResolutionIntentId(intentId);
        Assert.Equal(SettlementReferences.DeriveResolutionIntentId(intentId), resolution);
        Assert.Contains(intentId, resolution);
        // It is NOT the original intent id itself (a distinct leg) but a derivation OF it.
        Assert.NotEqual(intentId, resolution);
        Assert.StartsWith(SettlementReferences.ResolutionPrefix, resolution);
    }

    [Fact]
    public void CREDIT_RESOLUTION_KEY_INTENT_DERIVED_a_fresh_id_resolution_fails_the_intent_derived_check()
    {
        var intentId = IntentId();
        var resolution = SettlementReferences.DeriveResolutionIntentId(intentId);

        // The double-pay guard: a resolution derived from the RIGHT intent carries that intent id; a resolution
        // minted from a FRESH, unrelated intent id does NOT — so a fresh-id resolution is detectable (it fails
        // the "resolution key contains the original intent id" check the reconciler keys on).
        var freshUnrelatedIntent = SettlementReferences.DeriveIntentId(Guid.NewGuid(), "some-other-occurrence");
        var freshResolution = SettlementReferences.DeriveResolutionIntentId(freshUnrelatedIntent);

        Assert.Contains(intentId, resolution);
        Assert.DoesNotContain(intentId, freshResolution);
        Assert.NotEqual(resolution, freshResolution);
    }

    // ============================================================================================
    // Legacy-DDA path UNCHANGED — a leg with no threaded intent keeps the process-id-derived reference
    // (byte-identical to the pre-ADR-PC-043 shape) and carries no amount.
    // ============================================================================================

    [Fact]
    public void The_legacy_path_with_no_intent_keeps_the_process_id_derived_reference_unchanged()
    {
        var processId = Guid.NewGuid();

        // No intent threaded (the legacy-DDA path / the pre-threading platform default) → the confirm legs
        // derive their reference from the PROCESS id, exactly as before ADR-PC-043 — legacy routing UNCHANGED.
        var credit = (ConfirmCreditCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmCredit, processId, Guid.NewGuid(), null)!;
        Assert.Equal(SettlementReferences.Derive(SettlementReferences.CreditPrefix, processId), credit.CreditRef);
        Assert.Equal(0L, credit.AmountCents);

        var debit = (ConfirmDebitCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ConfirmDebit, processId, Guid.NewGuid(), null)!;
        Assert.Equal(SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId), debit.CoreHoldRef);
        Assert.Equal(0L, debit.AmountCents);
        Assert.Null(debit.SettlementTarget);

        // The reserve leg likewise stays the ACCT-{processId} placeholder with no amount and no engine-ca
        // target — its body is byte-identical to the pre-u79p.22 shape on the legacy path.
        var reserve = (ReserveAccountBalanceCommand)SettlementCommandPayloadFactory.Build(
            SettlementProcess.ReserveAccountBalance, processId, Guid.NewGuid(), null)!;
        Assert.Equal(SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId), reserve.AccountRef);
        Assert.Null(reserve.AmountCents);
        Assert.Null(reserve.SettlementTarget);
    }
}
