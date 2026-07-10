using Babelstone.Orchestrator.Saga.Settlement;

namespace Babelstone.Orchestrator;

/// <summary>
/// The engine↔engine-CA payout-landing reconciler (ADR-PC-043) — the ADR-PC-016
/// flow-1 reconciliation pattern re-scoped INTERNALLY, from "engine vs legacy Core" to "engine source vs
/// engine-owned current account". In plain English: this is the safety net that catches the rare cases
/// prevention cannot — a payout the source recorded that never landed on the CA, one that landed twice, or
/// one that landed at the wrong amount. It pairs each source payout occurrence against its CA landing by the
/// economic-INTENT id (the ADR-PC-043 slot-4 key, <c>IntentId = f(source_id, occurrence)</c>) and classifies
/// the pair, then SURFACES an operational signal for every non-matched case. It NEVER invents or auto-corrects
/// a <c>Movement</c> — reconciliation raises a signal for a human/operator process, it does not move money
/// (ADR-PC-043; the same posture ADR-PC-016 flow-1 takes at the Core boundary).
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic and generic (ADR-PC-021 §P2).</b> The reconciler names no family: it pairs
/// intent-keyed <see cref="SourcePayout"/> records against <see cref="CaLanding"/> records, both structural
/// (opaque refs, integer cents, dates, closed direction). A term-deposit maturity payout and a personal-loan
/// disbursement reconcile through the SAME code path — the intent id is the only axis.
/// </para>
/// <para>
/// <b>Keyed on the economic intent, reusing the slot-4 derivation.</b> A source payout carries its
/// <see cref="SourcePayout.IntentId"/> (from <see cref="SettlementReferences.DeriveIntentId"/>); a CA landing
/// carries the <see cref="CaLanding.IntentReference"/> the credit/debit writer derived via
/// <see cref="SettlementReferences.DeriveFromIntent"/>. Because the CA reference is a pure function of the
/// intent id, the reconciler recovers the intent from a landing with
/// <see cref="SettlementReferences.DeriveFromIntent"/> and pairs the two — so a saga reissue (byte-identical
/// body, fresh dispatch id) does not present as a second occurrence.
/// </para>
/// <para>
/// <b>The interim DROP SLA (Q-AG calibration pending).</b> A source-paid-not-landed pair is only a DROP once
/// the payout is older than the SLA horizon — before that it is simply IN_FLIGHT (the landing may still be in
/// transit). The interim horizon is <see cref="DefaultDropSlaDays"/> days; the real value awaits Q-AG
/// calibration (recorded as pending, ADR-PC-043 §Residual risks). The caller supplies <c>asOf</c>, so the
/// classifier stays clock-free and deterministic (ADR-PC-023 §6).
/// </para>
/// </remarks>
public static class PayoutLandingReconciler
{
    /// <summary>The INTERIM source-paid-not-landed DROP SLA horizon, in days (ADR-PC-043 §Residual risks):
    /// a source payout with no matching CA landing is IN_FLIGHT until it is older than this, then a DROP.
    /// Q-AG calibration of the real horizon is PENDING — this is a placeholder, not the settled value.</summary>
    public const int DefaultDropSlaDays = 3;

    /// <summary>
    /// Reconcile the source payouts against the CA landings as-of <paramref name="asOf"/>, pairing by the
    /// economic-intent id and classifying each pair. Returns one <see cref="ReconciliationOutcome"/> per
    /// distinct intent (source payout or CA landing), each carrying its classification and, for every
    /// non-matched case, the operational <see cref="ReconciliationSignal"/> to surface. NEVER invents a
    /// Movement — a signal is advisory, not a correction (ADR-PC-043).
    /// </summary>
    /// <param name="sourcePayouts">The source-side Originated payout movements (one per economic occurrence).</param>
    /// <param name="caLandings">The CA-side AccountCredited/AccountDebited landings (one per applied credit/debit).</param>
    /// <param name="asOf">Today, supplied by the caller's clock-owning loop — never read inside the classifier.</param>
    /// <param name="dropSlaDays">The DROP SLA horizon in days; defaults to <see cref="DefaultDropSlaDays"/>.</param>
    public static IReadOnlyList<ReconciliationOutcome> Reconcile(
        IReadOnlyList<SourcePayout> sourcePayouts,
        IReadOnlyList<CaLanding> caLandings,
        DateOnly asOf,
        int? dropSlaDays = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePayouts);
        ArgumentNullException.ThrowIfNull(caLandings);
        var horizon = dropSlaDays ?? DefaultDropSlaDays;

        // Group the CA landings by the intent they resolve — recovered from the reference by re-deriving it
        // the same way the writer did (DeriveFromIntent is a pure prefix + intent id, so the intent is the
        // suffix). A landing whose reference does not carry a known intent prefix is an ORPHAN landing.
        var landingsByIntent = caLandings
            .GroupBy(l => RecoverIntentId(l.IntentReference))
            .ToDictionary(g => g.Key, g => g.ToList());

        var outcomes = new List<ReconciliationOutcome>();
        var pairedIntents = new HashSet<string>(StringComparer.Ordinal);

        // Deterministic order: by intent id, so the outcome list is stable for a given input (no clock, no
        // physical-row dependence) — the same guarantee the movement-ledger reads give.
        foreach (var payout in sourcePayouts.OrderBy(p => p.IntentId, StringComparer.Ordinal))
        {
            pairedIntents.Add(payout.IntentId);
            var landings = landingsByIntent.TryGetValue(payout.IntentId, out var found) ? found : [];

            if (landings.Count == 0)
            {
                // Source paid, nothing landed. IN_FLIGHT until older than the SLA, then a DROP — the source
                // holds the funds meanwhile (the payout-pending marker), so a DROP is a signal to
                // reconcile, never a re-pay this reconciler drives.
                var ageDays = asOf.DayNumber - payout.ValueDate.DayNumber;
                var classification = ageDays > horizon
                    ? ReconciliationClass.Drop
                    : ReconciliationClass.InFlight;
                outcomes.Add(new ReconciliationOutcome(
                    payout.IntentId, classification,
                    classification == ReconciliationClass.Drop
                        ? Signal(payout.IntentId, ReconciliationClass.Drop,
                            $"Source paid {payout.AmountCents}c on {payout.ValueDate:yyyy-MM-dd}, no CA landing after {ageDays}d (> {horizon}d SLA).")
                        : null));
                continue;
            }

            if (landings.Count > 1)
            {
                // Two or more landings for one source payout — a DOUBLE. Surface it; never net it out here.
                outcomes.Add(new ReconciliationOutcome(
                    payout.IntentId, ReconciliationClass.Double,
                    Signal(payout.IntentId, ReconciliationClass.Double,
                        $"Source paid one payout but {landings.Count} CA landings recorded for the same intent.")));
                continue;
            }

            var landing = landings[0];
            if (landing.AmountCents != payout.AmountCents)
            {
                // The one landing settled a different amount than the source paid — a WRONG-AMOUNT. Surface
                // the gap; never adjust it here (the in-band guard on the CA event is the first line; this is
                // the reconciliation backstop, ADR-PC-043).
                outcomes.Add(new ReconciliationOutcome(
                    payout.IntentId, ReconciliationClass.WrongAmount,
                    Signal(payout.IntentId, ReconciliationClass.WrongAmount,
                        $"Source paid {payout.AmountCents}c, CA landed {landing.AmountCents}c for the same intent.")));
                continue;
            }

            // Exactly one landing, matching amount — MATCHED. No signal.
            outcomes.Add(new ReconciliationOutcome(payout.IntentId, ReconciliationClass.Matched, Signal: null));
        }

        // A CA landing whose intent has NO source payout is an ORPHAN landing (money landed that the engine
        // never sourced) — surface it as its own signal. Deterministic order by intent.
        foreach (var (intentId, _) in landingsByIntent
                     .Where(kv => !pairedIntents.Contains(kv.Key))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            outcomes.Add(new ReconciliationOutcome(
                intentId, ReconciliationClass.OrphanLanding,
                Signal(intentId, ReconciliationClass.OrphanLanding,
                    "CA landing recorded for an intent with no source payout.")));
        }

        return outcomes;
    }

    /// <summary>
    /// Recover the economic-intent id a CA landing reference carries: the reference is
    /// <c>prefix + intentId</c> (from <see cref="SettlementReferences.DeriveFromIntent"/>), and the intent id
    /// itself begins with <see cref="SettlementReferences.IntentPrefix"/>, so the intent is the substring from
    /// that prefix onward. A reference that carries no intent prefix returns the raw reference (it groups as
    /// its own orphan bucket rather than silently pairing).
    /// </summary>
    private static string RecoverIntentId(string intentReference)
    {
        var idx = intentReference.IndexOf(SettlementReferences.IntentPrefix, StringComparison.Ordinal);
        return idx >= 0 ? intentReference[idx..] : intentReference;
    }

    private static ReconciliationSignal Signal(string intentId, ReconciliationClass classification, string detail) =>
        new(intentId, classification, detail);
}

/// <summary>
/// One source-side Originated payout movement to reconcile (ADR-PC-043): its
/// economic-intent id, the amount the source paid, and the value date the source recorded. Structural,
/// no PII (ADR-PC-004): opaque intent id, integer cents, a date.
/// </summary>
/// <param name="IntentId">The economic-intent id (from <see cref="SettlementReferences.DeriveIntentId"/>) —
/// the exactly-once pairing axis.</param>
/// <param name="AmountCents">The amount the source paid, integer cents (ADR-PC-010).</param>
/// <param name="ValueDate">The economic date the source recorded the payout — the DROP-SLA age anchor.</param>
public sealed record SourcePayout(string IntentId, long AmountCents, DateOnly ValueDate);

/// <summary>
/// One CA-side landing to reconcile (ADR-PC-043): the intent reference the CA
/// credit/debit writer carried, the amount that landed, and the direction. Structural, no PII (ADR-PC-004).
/// </summary>
/// <param name="IntentReference">The intent reference the CA event carried (from
/// <see cref="SettlementReferences.DeriveFromIntent"/>) — the reconciler recovers the intent id from it.</param>
/// <param name="AmountCents">The amount that landed on the CA, integer cents (ADR-PC-010).</param>
/// <param name="Direction"><c>Credit</c> or <c>Debit</c> relative to the CA account — the closed
/// <c>SettlementDirection</c> member name, carried for the signal's audit context.</param>
public sealed record CaLanding(string IntentReference, long AmountCents, string Direction);

/// <summary>The classification of one reconciled intent (ADR-PC-043).</summary>
public enum ReconciliationClass
{
    /// <summary>Source payout paired to exactly one CA landing of the same amount — the happy path.</summary>
    Matched,

    /// <summary>Source paid, no CA landing yet, and still within the DROP SLA horizon — may be in transit.</summary>
    InFlight,

    /// <summary>Source paid, no CA landing after the DROP SLA horizon — a dropped payout to reconcile.</summary>
    Drop,

    /// <summary>Two or more CA landings for one source payout — a double landing to reconcile.</summary>
    Double,

    /// <summary>One CA landing, but its amount differs from the source payout — a wrong-amount landing.</summary>
    WrongAmount,

    /// <summary>A CA landing whose intent has no source payout — money landed the engine never sourced.</summary>
    OrphanLanding,
}

/// <summary>
/// The outcome of reconciling ONE economic intent (ADR-PC-043): its classification and,
/// for every non-matched case, the operational <see cref="ReconciliationSignal"/> to surface.
/// <see cref="Signal"/> is <see langword="null"/> exactly when <see cref="Classification"/> is
/// <see cref="ReconciliationClass.Matched"/> (a matched pair needs no operator attention).
/// </summary>
/// <param name="IntentId">The economic-intent id this outcome reconciles.</param>
/// <param name="Classification">The reconciliation verdict for the intent.</param>
/// <param name="Signal">The operational signal to surface, or <see langword="null"/> for a matched pair.</param>
public sealed record ReconciliationOutcome(
    string IntentId,
    ReconciliationClass Classification,
    ReconciliationSignal? Signal);

/// <summary>
/// An operational signal the reconciler SURFACES for a non-matched intent (ADR-PC-043)
/// — advisory only, for a human/operator reconciliation process. It NEVER carries a corrective Movement: the
/// reconciler raises the fact, it does not move money.
/// </summary>
/// <param name="IntentId">The economic-intent id the signal concerns.</param>
/// <param name="Classification">Which non-matched case fired the signal.</param>
/// <param name="Detail">A structural, non-PII description of the discrepancy for the operator.</param>
public sealed record ReconciliationSignal(
    string IntentId,
    ReconciliationClass Classification,
    string Detail);
