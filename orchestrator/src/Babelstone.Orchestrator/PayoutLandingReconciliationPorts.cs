using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator;

/// <summary>
/// The read port the scheduled reconciler pass pulls its inputs from (bd babelstone-qa92.2; ADR-PC-043) — the
/// source-side Originated payouts and the CA-side AccountCredited/AccountDebited landings, both as-of a given
/// date. In plain terms: the pass needs "every payout the source recorded" and "every landing the CA recorded"
/// to pair them; this interface is where those two reads come from, so the pass stays a deterministic function
/// of what it reads and a test supplies a fake.
/// </summary>
/// <remarks>
/// <para>
/// <b>As-of, never the clock.</b> The caller (the clock-owning worker loop) supplies the <c>asOf</c> date —
/// the port reads the world as-of that date and the classifier stays clock-free (ADR-PC-023 §6). A live
/// implementation range-scans the movement ledger / CA read model as of that date; it is the impure shell, so
/// it may touch the clock for connection bookkeeping but the DATE it reads by is the injected one.
/// </para>
/// <para>
/// <b>Structural, no PII (ADR-PC-004 §P2).</b> Both returned record shapes carry only opaque intent
/// references, integer cents, dates, and a closed direction — never a depositor name, NIF, or IBAN. The live
/// reader resolves nothing about the subject; that boundary stays closed, exactly as the projection
/// reconciler's does.
/// </para>
/// <para>
/// The live Postgres/read-model implementation of this port needs a running stack (the movement ledger + the
/// CA landing read model) and is a human bring-up follow-up (bd babelstone-qa92.2 §Scope); the pass, its
/// signal emission, and its idempotency are exercised in CI against an in-memory fake.
/// </para>
/// </remarks>
public interface IPayoutLandingSource
{
    /// <summary>
    /// Read the source-side Originated payouts as-of <paramref name="asOf"/> — one per economic occurrence,
    /// carrying the intent id, the amount the source paid, and the value date the source recorded.
    /// </summary>
    /// <param name="asOf">Today, supplied by the clock-owning worker loop (ADR-PC-023 §6).</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task<IReadOnlyList<SourcePayout>> ReadSourcePayoutsAsync(DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// Read the CA-side AccountCredited/AccountDebited landings as-of <paramref name="asOf"/> — one per applied
    /// credit/debit, carrying the intent reference, the amount that landed, and the direction.
    /// </summary>
    /// <param name="asOf">Today, supplied by the clock-owning worker loop (ADR-PC-023 §6).</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task<IReadOnlyList<CaLanding>> ReadCaLandingsAsync(DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// The durable sink the reconciler pass surfaces a non-matched <see cref="ReconciliationSignal"/> to (bd
/// babelstone-qa92.2; ADR-PC-043) — where a signal reaches a human. In plain terms: when the reconciler finds
/// a drop, a double, or a wrong-amount landing, it hands the signal here, and this is what makes it visible.
/// The reconciler NEVER moves money; a signal is advisory (ADR-PC-043 reconcile-signals-only), so a sink
/// SURFACES it — it never corrects it.
/// </summary>
/// <remarks>
/// The default live sink (<see cref="OperatorReconciliationSignalSink"/>) is a Prometheus counter
/// (<see cref="PayoutReconciliationMetrics"/>) + a structured log line (ADR-IC-007 Layer 1). It is a seam so a
/// test can assert the exact signals a pass emitted, and so a deployment could add a spine operational event
/// without touching the pass (ADR-PC-043 names that as optional).
/// </remarks>
public interface IReconciliationSignalSink
{
    /// <summary>Surface one non-matched reconciliation signal for an operator. Idempotent by construction on
    /// the caller's side — a re-run over the same world re-derives the same signals, and a counter/log is safe
    /// to re-emit (it is a rate an operator reads, not a state a re-emit corrupts).</summary>
    /// <param name="signal">The non-matched signal to surface (never null; a matched pair carries no signal).</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task EmitAsync(ReconciliationSignal signal, CancellationToken ct = default);
}

/// <summary>
/// The default operator-facing reconciliation signal sink (bd babelstone-qa92.2; ADR-PC-043 / ADR-IC-007
/// Layer 1): it increments the per-<c>ReconciliationClass</c> Prometheus counter
/// (<see cref="PayoutReconciliationMetrics.RecordSignal"/>) so the <c>payout-landing-reconciliation</c> alert
/// group can fire, AND writes ONE structured log line so the discrepancy is human-readable in the log stream.
/// It moves no money — it SURFACES the fact (ADR-PC-043 reconcile-signals-only).
/// </summary>
/// <remarks>
/// The log carries only structural fields — the intent id (an opaque reference), the class, and the detail
/// string the reconciler composed (integer cents, dates; no PII, ADR-PC-004 §P2). The counter and the log are
/// the two halves of the ADR-IC-007 operator surface: the counter is the aggregable, alertable rate; the log
/// is the per-occurrence context an operator reads when triaging (the runbook's DROP/DOUBLE/WRONG-AMOUNT
/// procedures key off exactly these fields).
/// </remarks>
public sealed class OperatorReconciliationSignalSink(ILogger<OperatorReconciliationSignalSink>? logger = null)
    : IReconciliationSignalSink
{
    /// <inheritdoc />
    public Task EmitAsync(ReconciliationSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // The alertable, aggregable rate (ADR-IC-007 Layer 1 — the metric an operator alarms on), tagged by
        // the reconciliation class so the alert group fires per class (DROP/DOUBLE/WRONG-AMOUNT).
        PayoutReconciliationMetrics.RecordSignal(signal.Classification);

        // The per-occurrence context (ADR-IC-007 Layer 1 — the structured log an operator reads when
        // triaging). Structural fields only: the opaque intent id, the class, and the reconciler's detail
        // (integer cents / dates). Warning level: every one of these is a discrepancy that needs a human.
        logger?.LogWarning(
            "Payout-landing reconciliation signal ({Classification}) for intent {IntentId}: {Detail} " +
            "(ADR-PC-043 — signal only, no Movement invented).",
            signal.Classification, signal.IntentId, signal.Detail);

        return Task.CompletedTask;
    }
}
