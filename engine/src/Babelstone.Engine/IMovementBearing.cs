using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// The spine-owned seam a money-moving event implements to declare that it carries
/// <see cref="Movement"/>s — the generic-discoverability contract the account-keyed movement ledger
/// folds through (ADR-PC-032 §A1). In plain English: every event that moves money already carries its
/// <see cref="Movement"/>s as ordinary payload data; this one-member interface is how the engine SPINE
/// discovers and reads those movements off an ARBITRARY family event WITHOUT opening the opaque payload
/// or naming the family. A family event opts in by implementing it; the spine's generic projector then
/// folds <see cref="Movements"/> into the <c>account_ref</c>-keyed ledger by pattern-matching on this
/// interface alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same idiom as <see cref="IErasable{TState}"/> — engine owns the generic mechanism, the family
/// supplies its binding (ADR-PC-032 §A1).</b> The spine declares the interface and the generic fold; a
/// money-moving family event declares "I bear movements" by implementing <see cref="Movements"/> to
/// return the carrier it already holds. The engine knows "this event bears movements, fold them"; the
/// family knows which of its lifecycle facts move money (the §Decision slot 6 ownership split is
/// unchanged). The seam is compiler-enforced at the family's binding site, so — unlike a reserved
/// payload key (the §A2 rejected alternative) — a family cannot silently omit or misspell its way into
/// the spine folding zero movements off a non-conforming event.
/// </para>
/// <para>
/// <b>Family-agnostic (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021 §P2).</b> The interface names NO family: it
/// exposes only <see cref="Movement"/> — the single-sided, family-agnostic money-movement atom carrying
/// an opaque <see cref="Movement.AccountRef"/> (a reference the engine resolves internally, never PII;
/// ADR-PC-004 §P2) and generic <see cref="Money"/> / <see cref="SettlementDirection"/> /
/// <see cref="MovementOperation"/> / <see cref="MovementOrigin"/>. A Movement-bearing event in ANY
/// family — or the cross-cutting <c>operations.MovementObserved</c> the spine itself declares for the
/// <see cref="MovementOrigin.Observed"/> direction (ADR-PC-032 §A4) — is foldable through this one seam
/// with no special-casing.
/// </para>
/// <para>
/// <b>The carrier is the event's existing <c>IReadOnlyList&lt;Movement&gt;</c> (ADR-PC-032 §A3).</b> An
/// event MAY bear more than one movement — a deposit renewal returns both its rollover-debit and its
/// interest-credit on one append — so <see cref="Movements"/> returns the whole list in carrier order.
/// <see cref="Movements"/> is NON-nullable: a money-free or pre-Movement event implements it to return an
/// EMPTY list, never <see langword="null"/>, so the generic projector folds it unconditionally. This is
/// the spine's READ view of the carrier; how a family stores the carrier on its record (e.g. a
/// nullable, defaulted positional parameter kept for forward-only replay, ADR-IC-002) is the family's
/// concern, mapped onto this non-null member.
/// </para>
/// </remarks>
public interface IMovementBearing
{
    /// <summary>
    /// The movements this event records, in carrier order — the slot-1 payload data the spine's
    /// account-keyed movement ledger folds (ADR-PC-032 §A1). NON-nullable: an event that moves no money
    /// returns an EMPTY list, never <see langword="null"/>, so the generic projector needs no per-event
    /// null guard. A single-leg money-mover returns one <see cref="Movement"/>; a multi-direction event
    /// (e.g. a renewal's rollover-debit + interest-credit) returns several, in declared order.
    /// </summary>
    IReadOnlyList<Movement> Movements { get; }
}
