using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

/// <summary>One withholding flow as it was RECORDED on the stream — the AT_MATURITY single
/// <c>WithholdingApplied</c> split, or the withholding leg of one PERIODIC/ADVANCE
/// <c>InterestPaid</c> coupon.</summary>
/// <remarks>
/// This entry is the ledger's atom and the reason the projection is correct: Portuguese
/// withholding is applied FLOW-BY-FLOW to each interest payment as it accrues, NEVER by scaling
/// the rate (financial-math §5.4 — <c>TANL = TANB × (1 − 0.28)</c> is exact only for a single
/// at-maturity flow; multi-period deposits must withhold each flow). Each entry therefore records
/// one flow's own (gross, tax, net) exactly as the command-side kernel stamped it
/// (<c>Withholding.Withhold</c>, E.3); the fold conserves <c>Gross = Tax + Net</c> per flow and
/// never derives tax from a rate. Every value is event-derived, so a cold rebuild reproduces the
/// ledger byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
/// <param name="Gross">The flow's gross interest, as recorded (cents, <see cref="Money"/>).</param>
/// <param name="Tax">The flow's withholding tax, as recorded — never a rate-scaled re-derivation.</param>
/// <param name="Net">The flow's net interest, as recorded; <c>Gross = Tax + Net</c> by construction.</param>
/// <param name="Source">Which event produced the entry — <c>"withholding"</c> for
/// <c>WithholdingApplied</c> (the AT_MATURITY split), <c>"coupon"</c> for an <c>InterestPaid</c> leg.</param>
/// <param name="WithheldOn">The DATE the flow was withheld on — the <c>WithholdingApplied.WithheldOn</c>
/// (AT_MATURITY / early termination), the <c>InterestPaid.PaidOn</c> (coupon), or, for a pre-field
/// <c>WithholdingApplied</c> that carries no date of its own, the paired <c>InterestAccrued.AsOf</c>
/// (see <see cref="PendingAccrual"/>) — so a downstream reader can slice the ledger per tax year
/// (ADR-PC-027 read surface). Event-derived in every case, so a cold rebuild reproduces it exactly
/// (ADR-PC-010).</param>
public sealed record WithholdingEntry(Money Gross, Money Tax, Money Net, string Source, DateOnly WithheldOn);

/// <summary>The most recent <c>InterestAccrued</c> flow awaiting its withholding leg — the deterministic
/// date source for a <c>WithholdingApplied</c> recorded before the <c>WithheldOn</c> field existed.</summary>
/// <remarks>
/// The decider emits every <c>WithholdingApplied</c> immediately after the <c>InterestAccrued</c> it
/// withholds, both stamped with the SAME date (the maturity or termination date — the invariant each
/// decider-path test asserts as <c>WithheldOn == AsOf</c>), and the withheld legs conserve the accrual's
/// gross (<c>Gross = Tax + Net</c>, <c>Withholding.Withhold</c>). A pre-field withholding flow — one whose
/// <c>WithheldOn</c> replays as <c>default(DateOnly)</c> — therefore recovers its date from this slot:
/// event-derived only, no clock (ADR-PC-010; the byte-identity across rebuilds is pinned by
/// <c>Runner_rebuild_reproduces_a_byte_identical_belief</c>). The slot is CONSUME-ONCE and settle-aware:
/// a <c>WithholdingApplied</c> reads it (only when its own gross matches the accrual's — the conservation
/// cross-check) and clears it either way, and an <c>InterestPaid</c> coupon clears it UNREAD (a coupon's
/// flow is self-contained), so a stale date can never leak onto a later, unrelated flow. Re-arming an
/// already-armed slot POISONS it: consecutive accruals with no settling flow between them are a stream
/// shape the decider never emits, so the withholding flow that follows refuses attribution (it stays
/// un-dated and surfaces loud downstream) rather than guessing which accrual is its pair.
/// </remarks>
/// <param name="Gross">The accrued gross the paired withholding must conserve (<c>Tax + Net</c>).</param>
/// <param name="AsOf">The accrual date — the date the paired withholding flow was withheld on.</param>
/// <param name="Poisoned">Whether the slot was re-armed while already armed (an unknown stream shape);
/// a poisoned slot never dates a flow.</param>
public sealed record PendingAccrual(Money Gross, DateOnly AsOf, bool Poisoned = false);

/// <summary>
/// The withholding-ledger projection (F.6, babelstone-3kjl): the per-stream record of every
/// withholding flow on a term deposit, folded from the family's withholding-bearing events. Kind
/// <c>term_deposit.withholding_ledger</c>; <see cref="ProjectionMode.Async"/> like every v1
/// projection. Modelled as a state record holding a COLLECTION so it fits the existing store shape
/// unchanged — one current-belief row per <c>(stream_id, projection_kind)</c> carrying the whole
/// ledger (no per-row schema change; ADR-PC-002 §P1).
/// </summary>
/// <remarks>
/// <para>
/// The fold is pure and accumulating: each withholding-bearing event APPENDS a
/// <see cref="WithholdingEntry"/> (recorded as stamped) and adds to the running gross/tax/net
/// totals via <see cref="Money"/>'s checked <c>+</c> — no mid-step rounding, no <c>decimal</c>
/// state (ADR-PC-010 §P1, BMNY002). Crucially, the running totals are a SUM OF PER-FLOW NETS, not
/// a rate applied to a gross total — the flow-by-flow §5.4 rule holds at the ledger level too, so
/// the realised net equals the sum of each flow's net even when per-flow roundings differ from a
/// single bulk withhold.
/// </para>
/// <para>
/// The accumulating shape is replay-safe because the runner's <c>source_sequence</c> guard skips a
/// re-delivered event (ProjectionRunner remarks); no flow is counted twice. The ledger folds the
/// SAME tax/net the <see cref="DepositPosition"/> fold sums, so its totals reconcile with the
/// position's <c>WithholdingToDate</c>/<c>NetInterest</c> by construction (D.5's reconciliation
/// drill).
/// </para>
/// </remarks>
/// <param name="Entries">The withholding flows in fold order (append-only) — the recorded ledger.</param>
/// <param name="TotalGross">Running sum of every flow's gross interest, conserved to the cent.</param>
/// <param name="TotalTax">Running sum of every flow's withholding tax (sum of per-flow taxes — NOT rate-scaled).</param>
/// <param name="TotalNet">Running sum of every flow's net interest; <c>TotalGross = TotalTax + TotalNet</c>.</param>
/// <param name="PendingAccrual">The accrual flow awaiting its withholding leg — the pre-field date
/// source (see <see cref="Babelstone.Families.TermDeposit.PendingAccrual"/>). Null-defaulted so a belief
/// payload stored before this field existed (no such JSON member) deserializes to a null slot — the
/// missing-member tolerance the pre-field belief fixture test pins. Declared LAST so positional
/// construction sites keep their meaning and the serialized order of the pre-existing properties is
/// unchanged.</param>
public sealed record WithholdingLedger(
    IReadOnlyList<WithholdingEntry> Entries,
    Money TotalGross,
    Money TotalTax,
    Money TotalNet,
    PendingAccrual? PendingAccrual = null)
{
    /// <summary>The seed state a fold starts from (before any withholding event).</summary>
    public static WithholdingLedger Empty { get; } = new([], Money.Zero, Money.Zero, Money.Zero);
}

// Pure folds (state, event) → state for the withholding-ledger projection. No clock, no I/O,
// no randomness (BENG001/002/003); each body is a single `state with { … }`. The two flow handlers
// APPEND the recorded flow and accumulate per-flow totals via Money's checked + operator; the
// accrual handler only arms the pending-accrual date slot. The fold records the (gross, tax, net)
// the event already carries — it NEVER re-derives tax from a rate, honouring the financial-math
// §5.4 flow-by-flow withholding rule.

public sealed class WithholdingLedgerWithholdingAppliedHandler : IEventHandler<WithholdingLedger, WithholdingApplied>
{
    public HandlerResult<WithholdingLedger> Apply(WithholdingLedger state, WithholdingApplied @event)
    {
        // The AT_MATURITY split carries Tax and Net; the flow's gross is Net + Tax (conserved to
        // the cent by the command-side Withhold). Reconstructing gross by adding the two recorded
        // legs is exact integer arithmetic — not a rate re-derivation.
        var gross = @event.Net + @event.Tax;

        // A pre-field flow (WithheldOn replays as default 0001-01-01) recovers its date from the paired
        // InterestAccrued the decider emitted just before it, only when the slot is unpoisoned AND the
        // gross conservation cross-check holds (see PendingAccrual). A dated flow keeps its own stamp
        // verbatim; an unpairable flow keeps the default so the statement read surface still surfaces it
        // loud — never a guessed year.
        var withheldOn = @event.WithheldOn != default
            ? @event.WithheldOn
            : state.PendingAccrual is { Poisoned: false } accrual && accrual.Gross == gross ? accrual.AsOf : default;

        return HandlerResult<WithholdingLedger>.From(state with
        {
            Entries = [.. state.Entries, new WithholdingEntry(gross, @event.Tax, @event.Net, "withholding", withheldOn)],
            TotalGross = state.TotalGross + gross,
            TotalTax = state.TotalTax + @event.Tax,
            TotalNet = state.TotalNet + @event.Net,
            // Consume-once: matched or not, the slot never carries past a withholding flow.
            PendingAccrual = null,
        });
    }
}

public sealed class WithholdingLedgerInterestAccruedHandler : IEventHandler<WithholdingLedger, InterestAccrued>
{
    public HandlerResult<WithholdingLedger> Apply(WithholdingLedger state, InterestAccrued @event)
        // An accrual is NOT a withholding flow: it appends no entry and moves no total. It only ARMS the
        // pending-accrual slot the next WithholdingApplied reads its pre-field date from. Arming an
        // ALREADY-ARMED slot poisons it instead — the stream shape is unknown, so no flow gets a guessed
        // date (see PendingAccrual).
        => HandlerResult<WithholdingLedger>.From(state with
        {
            PendingAccrual = state.PendingAccrual is null
                ? new PendingAccrual(@event.GrossInterest, @event.AsOf)
                : new PendingAccrual(@event.GrossInterest, @event.AsOf, Poisoned: true),
        });
}

public sealed class WithholdingLedgerInterestPaidHandler : IEventHandler<WithholdingLedger, InterestPaid>
{
    public HandlerResult<WithholdingLedger> Apply(WithholdingLedger state, InterestPaid @event)
        => HandlerResult<WithholdingLedger>.From(state with
        {
            // A PERIODIC/ADVANCE coupon carries its own gross/withholding/net flow; record it as
            // stamped (gross = tax + net, conserved command-side) and dated on its PaidOn so the ledger
            // can be sliced per tax year. Withholding each coupon as it is paid is the §5.4 multi-period rule.
            Entries = [.. state.Entries, new WithholdingEntry(@event.GrossInterest, @event.WithholdingTax, @event.NetInterest, "coupon", @event.PaidOn)],
            TotalGross = state.TotalGross + @event.GrossInterest,
            TotalTax = state.TotalTax + @event.WithholdingTax,
            TotalNet = state.TotalNet + @event.NetInterest,
            // A coupon SETTLES whatever accrual was armed: an armed slot surviving a coupon could date a
            // later, unrelated pre-field flow with a stale accrual's date — silently, since equal grosses
            // are realistic (same principal/rate/window). Clear it unread (see PendingAccrual).
            PendingAccrual = null,
        });
}
