using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// Which capital an early-termination penalty applies to (02 §2.5; the CUE
/// <c>#Band.basis</c> enumeration). The PT pack restricts which bases are legally permissible
/// — that restriction is a pack-bound depth-3 check (pack-validate), not re-enforced here; the
/// decider faithfully applies whichever basis the resolved band declares.
/// </summary>
public enum PenaltyBasis
{
    /// <summary>Penalty is a share of the gross interest accrued up to the termination date.</summary>
    AccruedInterest,

    /// <summary>Penalty is a share of the principal on deposit.</summary>
    Principal,

    /// <summary>Penalty is a share of (principal + gross accrued interest).</summary>
    Both,
}

/// <summary>
/// One early-termination band (02 §2.5; the CUE <c>#Band</c>): the elapsed-term window it covers,
/// the penalty share in basis points, and the capital the share applies to. A flat policy is a
/// degenerate one-band schedule whose window is open-ended (the spec's "supported as a degenerate
/// one-band schedule").
/// </summary>
/// <param name="UpToDays">The inclusive upper bound of the elapsed-term window the band covers
/// (first-match: the engine picks the first band whose <c>up_to_days</c> is not yet reached). A
/// <c>null</c> is the open-ended tail (02 §2.5: <c>up_to_days: null</c>) — it matches any elapsed
/// term and so must be the LAST band in the schedule.</param>
/// <param name="PenaltyBasisPoints">The penalty as a share of the chosen basis, in basis points
/// (10000 = 100%). Non-negative; a 100%-of-accrued band wipes the accrued interest exactly.</param>
/// <param name="Basis">Which capital the share applies to (accrued interest, principal, or both).</param>
public sealed record EarlyTerminationBand(int? UpToDays, int PenaltyBasisPoints, PenaltyBasis Basis);

/// <summary>
/// A product's early-termination policy (02 §2.5; the CUE <c>#EarlyTermination</c>): either a flat
/// penalty (a degenerate one-band schedule) OR an ordered banded schedule evaluated first-match
/// against the elapsed term, with an optional payout floor.
/// </summary>
/// <remarks>
/// <para>
/// This is the per-PRODUCT config the bank's pricing team owns — it rides on the product config
/// (the CUE <c>#TermDeposit.early_termination</c>), not on the pack's shared primitives, so the
/// decider takes it as an explicit INPUT exactly as it takes the pack day-count and withholding
/// rate. For the walking skeleton the service holds it as engine-instance config (mirroring how the
/// pinned pack stands in for a per-deposit config registry, ADR-PC-009); a config registry resolving
/// it per deposit is later work.
/// </para>
/// <para>
/// Pure data — no clock, no I/O. Band selection (<see cref="ResolveBand"/>) takes the elapsed days as
/// an explicit input, so the policy stays deterministic and replayable (ADR-PC-010 §P5).
/// </para>
/// </remarks>
/// <param name="Bands">The ordered (window → penalty) bands, evaluated first-match against the
/// elapsed term. A flat policy is a single open-ended band. Ascending <c>UpToDays</c> with one open
/// (null) tail is the well-formed shape (a depth-4 obligation the CUE schema cannot express
/// element-wise); <see cref="ResolveBand"/> scans in order and so honours whatever order is given.</param>
/// <param name="FloorCents">Optional minimum net settlement (02 §2.5): the depositor's net payout
/// never falls below it. <c>null</c> means no floor.</param>
public sealed record EarlyTerminationPolicy(
    IReadOnlyList<EarlyTerminationBand> Bands,
    long? FloorCents)
{
    /// <summary>
    /// A flat policy (02 §2.5): one rule applied to any early termination, modelled as a degenerate
    /// open-ended one-band schedule. The optional <paramref name="floorCents"/> sets the payout floor.
    /// </summary>
    public static EarlyTerminationPolicy Flat(int penaltyBasisPoints, PenaltyBasis basis, long? floorCents = null) =>
        new([new EarlyTerminationBand(UpToDays: null, penaltyBasisPoints, basis)], floorCents);

    /// <summary>
    /// A banded policy (02 §2.5): the ordered (window → penalty) bands plus the optional floor.
    /// </summary>
    public static EarlyTerminationPolicy Banded(IReadOnlyList<EarlyTerminationBand> bands, long? floorCents = null) =>
        new(bands, floorCents);

    /// <summary>The payout floor as <see cref="Money"/>, or <c>null</c> when unset.</summary>
    public Money? Floor => FloorCents is { } cents ? new Money(cents) : null;

    /// <summary>
    /// First-match band selection (02 §2.5): the first band whose window the elapsed term has NOT yet
    /// exceeded — i.e. the first band with <c>UpToDays == null</c> (the open tail) or
    /// <c>elapsedDays &lt;= UpToDays</c>. Pure: the elapsed days are an explicit input (no clock).
    /// </summary>
    /// <param name="elapsedDays">Days elapsed from constitution to the termination date.</param>
    /// <exception cref="InvalidOperationException">If no band matches — a malformed schedule with no
    /// open tail and an elapsed term past every bounded window. Fail loud rather than settle at a
    /// silent zero penalty (mirrors the fail-loud pack-resolution discipline).</exception>
    public EarlyTerminationBand ResolveBand(int elapsedDays)
    {
        foreach (var band in Bands)
        {
            if (band.UpToDays is null || elapsedDays <= band.UpToDays.Value)
            {
                return band;
            }
        }

        throw new InvalidOperationException(
            $"Early-termination schedule has no band covering an elapsed term of {elapsedDays} days " +
            "(no open-ended tail and every bounded window exceeded); refusing to default to zero penalty.");
    }
}
