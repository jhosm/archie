using Babelstone.FinancialTypes;

namespace Babelstone.FinancialMath;

/// <summary>
/// One segment of a resolved rate VECTOR (fin-math §5; v1-build-backlog F.10). A rate schedule
/// resolves a single deposit's rate into a deterministic ordered sequence of segments at
/// constitution, then folds that vector over the simple-interest accrual engine. Two shapes share
/// this segment type, distinguished by what <see cref="From"/> measures:
/// <list type="bullet">
/// <item><b>Step-up (<i>crescente</i>)</b> — the rate RISES across sub-periods of the term. Here
/// <see cref="From"/> is an ELAPSED-DAY boundary: the segment applies from day <see cref="From"/>
/// (inclusive) of the term until the next segment's boundary (or maturity for the last).</item>
/// <item><b>Amount-tiered (<i>escalonada</i>)</b> — the rate depends on the PRINCIPAL band. Here
/// <see cref="From"/> is a CENTS boundary: the segment prices the principal tranche from
/// <see cref="From"/> cents (inclusive) up to the next segment's boundary.</item>
/// </list>
/// </summary>
/// <remarks>
/// This is NOT variable/indexed rate (an Euribor-linked rate resolved per reset window) — that is
/// v3. Every segment's <see cref="RateBasisPoints"/> is a FIXED rate known at constitution; the
/// vector is purely a deterministic function of the (resolved) sheet and the deposit's term/
/// principal, so a cold replay reproduces it byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
/// <param name="From">The segment's inclusive lower boundary — an elapsed-day for a step-up
/// schedule, a principal-cents threshold for an amount-tiered schedule. The first segment is
/// always <c>0</c>.</param>
/// <param name="RateBasisPoints">The fixed TAN (basis points) this segment accrues at. May be
/// negative by design (negative-rate environments), exactly as <see cref="Accrual.SimpleInterest"/>.</param>
public readonly record struct RateSegment(long From, int RateBasisPoints);

/// <summary>
/// How a <see cref="RateSchedule"/>'s segments are indexed (fin-math §5; F.10) — the two
/// non-flat rate shapes the family schema's <c>#SteppedRate</c> models and v1 prices.
/// </summary>
public enum RateScheduleKind
{
    /// <summary>The rate is a single fixed TAN for the whole term/principal — the degenerate
    /// one-segment schedule, accrual-equivalent to <see cref="Accrual.SimpleInterest"/>.</summary>
    Flat,

    /// <summary>Step-up (<i>crescente</i>): segments are ELAPSED-DAY sub-periods of the term; the
    /// rate rises across them. Folded sub-period by sub-period over the term.</summary>
    StepUp,

    /// <summary>Amount-tiered (<i>escalonada</i>): segments are PRINCIPAL tranches; each tranche of
    /// the principal accrues at its own rate (a marginal/progressive tiering, like a tax bracket).</summary>
    AmountTiered,
}

/// <summary>
/// A resolved rate VECTOR for one deposit (fin-math §5; v1-build-backlog F.10): the deterministic
/// ordered segments resolved at constitution, plus the kind that says how they fold. The whole
/// point is to keep the rate-schedule RICHNESS engine-internal: it resolves into a deposit's
/// accrual state and folds over the existing <see cref="Accrual.SimpleInterest"/> primitive, so
/// no new bus event or contract change is needed — the gross interest the vector produces flows
/// through the SAME <c>InterestAccrued</c>/<c>InterestPaid</c> events the flat-rate path emits.
/// </summary>
/// <remarks>
/// <para><b>Purity &amp; rounding.</b> Both fold modes accumulate the whole interest in
/// <see cref="decimal"/> at full precision and cross to <see cref="Money"/> EXACTLY ONCE, at the
/// end (ADR-PC-010 §P1–§P2). They never round a per-segment sub-amount and re-sum cents — that
/// would open a rounding gap a flat-rate equivalent would not have, breaking the "one segment ⇒
/// flat" equivalence this type guarantees.</para>
/// <para><b>Flow-by-flow withholding is unaffected.</b> A rate schedule changes only how the GROSS
/// interest of a flow is computed; withholding is still applied to each emitted flow by the
/// caller (<see cref="Withholding.Withhold"/>), never rate-scaled (fin-math §5.4). For an
/// AT_MATURITY deposit the whole stepped accrual is still ONE flow at maturity; the vector folds
/// into that single gross figure.</para>
/// </remarks>
/// <param name="Kind">Whether the segments are flat, elapsed-day (step-up), or principal-tranche
/// (amount-tiered).</param>
/// <param name="Segments">The ordered segments. Ascending <see cref="RateSegment.From"/> with a
/// leading <c>0</c> segment is the well-formed shape the constructor enforces.</param>
public sealed record RateSchedule(RateScheduleKind Kind, IReadOnlyList<RateSegment> Segments)
{
    /// <summary>
    /// Build a flat schedule from a single TAN — the degenerate one-segment vector whose accrual
    /// equals <see cref="Accrual.SimpleInterest"/> over any interval. The flat path stays the common
    /// case; the vector is the generalisation that the step-up/tiered shapes specialise.
    /// </summary>
    public static RateSchedule Flat(int rateBasisPoints) =>
        new(RateScheduleKind.Flat, [new RateSegment(0, rateBasisPoints)]);

    /// <summary>
    /// Build a step-up (<i>crescente</i>) schedule from ascending elapsed-day boundaries. The first
    /// boundary MUST be 0 (the term opens at some rate); boundaries must strictly ascend (the
    /// depth-4 obligation the family schema's <c>#SteppedRate.steps</c> defers — re-asserted here so
    /// a malformed vector fails loud rather than mis-accruing).
    /// </summary>
    public static RateSchedule StepUp(IReadOnlyList<RateSegment> daySegments) =>
        Validated(RateScheduleKind.StepUp, daySegments);

    /// <summary>
    /// Build an amount-tiered (<i>escalonada</i>) schedule from ascending principal-cents tranche
    /// boundaries. The first boundary MUST be 0; boundaries must strictly ascend.
    /// </summary>
    public static RateSchedule AmountTiered(IReadOnlyList<RateSegment> trancheSegments) =>
        Validated(RateScheduleKind.AmountTiered, trancheSegments);

    private static RateSchedule Validated(RateScheduleKind kind, IReadOnlyList<RateSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("A rate schedule needs at least one segment.", nameof(segments));
        }
        if (segments[0].From != 0)
        {
            throw new ArgumentException(
                $"The first rate segment must start at 0 (got {segments[0].From}); the schedule must cover " +
                "the term/principal from its origin.", nameof(segments));
        }

        long prev = long.MinValue;
        foreach (var s in segments)
        {
            if (s.From <= prev)
            {
                throw new ArgumentException(
                    $"Rate-segment boundaries must strictly ascend (got {s.From} after {prev}); " +
                    "a non-ascending vector mis-resolves which segment covers a point.", nameof(segments));
            }
            prev = s.From;
        }
        return new RateSchedule(kind, segments);
    }

    /// <summary>
    /// The gross interest this schedule accrues for one deposit over <c>[start, end]</c> on the
    /// resolved principal and day-count (fin-math §5.1 generalised to a rate vector). Branches on
    /// <see cref="Kind"/>:
    /// <list type="bullet">
    /// <item><b>Flat</b> — one <see cref="Accrual.SimpleInterest"/> over the whole interval.</item>
    /// <item><b>StepUp</b> — sum each elapsed-day sub-period's interest, the sub-period accruing at
    /// its segment's rate; a deposit broken or matured mid-vector accrues only through <c>end</c>.</item>
    /// <item><b>AmountTiered</b> — sum each principal tranche's interest over the WHOLE interval,
    /// the tranche accruing at its segment's rate (marginal tiering).</item>
    /// </list>
    /// Accumulates in <see cref="decimal"/> and rounds ONCE at the boundary, so a one-segment vector
    /// of any kind equals the flat <see cref="Accrual.SimpleInterest"/> to the cent.
    /// </summary>
    /// <param name="principal">The principal the interest accrues on.</param>
    /// <param name="start">The accrual interval's inclusive start (the deposit's start date).</param>
    /// <param name="end">The accrual interval's exclusive end (maturity, a coupon boundary, or a
    /// termination date).</param>
    /// <param name="dayCount">The pack-resolved day-count convention.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <c>end &lt; start</c> (a reversed interval),
    /// mirroring <see cref="Accrual.SimpleInterest"/>'s guard.</exception>
    public Money AccrueGross(Money principal, DateOnly start, DateOnly end, DayCountConvention dayCount) =>
        // Accruing the WHOLE interval is the window [start, start..end] anchored at start — the
        // window form with windowStart == start is the general case.
        AccrueGrossWindow(principal, start, start, end, dayCount);

    /// <summary>
    /// The gross interest this schedule accrues over a SUB-WINDOW <c>[windowStart, windowEnd]</c> of
    /// a deposit anchored at <paramref name="depositStart"/> (fin-math §5.1; F.10 coupon support).
    /// The schedule's elapsed-day boundaries are measured from <paramref name="depositStart"/>, so a
    /// PERIODIC coupon window that opens partway through a <i>crescente</i> term is priced
    /// segment-by-segment against the rates in force across exactly that window — a later coupon
    /// earns the higher step. For a flat schedule this reduces to <see cref="Accrual.SimpleInterest"/>
    /// over <c>[windowStart, windowEnd]</c> to the cent. Amount-tiered schedules ignore
    /// <paramref name="depositStart"/> (their boundaries are principal, not time) and price the
    /// window's day count directly. Rounds ONCE at the boundary.
    /// </summary>
    /// <param name="principal">The principal the interest accrues on.</param>
    /// <param name="depositStart">The deposit's start — the anchor the elapsed-day boundaries count from.</param>
    /// <param name="windowStart">The accrual window's inclusive start (>= <paramref name="depositStart"/>).</param>
    /// <param name="windowEnd">The accrual window's exclusive end.</param>
    /// <param name="dayCount">The pack-resolved day-count convention.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <c>windowEnd &lt; windowStart</c> (reversed window).</exception>
    public Money AccrueGrossWindow(
        Money principal, DateOnly depositStart, DateOnly windowStart, DateOnly windowEnd, DayCountConvention dayCount) =>
        Kind switch
        {
            RateScheduleKind.StepUp =>
                AccrueStepUpWindow(principal, depositStart, windowStart, windowEnd, dayCount),
            RateScheduleKind.AmountTiered =>
                AccrueAmountTiered(principal, windowStart, windowEnd, dayCount),
            // Flat (and any degenerate one-segment vector) is the simple-interest path over the window.
            _ => Accrual.SimpleInterest(
                principal, Segments[0].RateBasisPoints, DayCount.Between(windowStart, windowEnd, dayCount)),
        };

    // Step-up: attribute each segment's days that fall inside the window [windowStart, windowEnd]
    // — both expressed as elapsed days from the deposit anchor — to that segment's rate, summing
    // the un-rounded amount in decimal and rounding ONCE. A flat one-segment vector reduces to
    // SimpleInterest over the window; a coupon window straddling a step boundary is split exactly.
    private Money AccrueStepUpWindow(
        Money principal, DateOnly depositStart, DateOnly windowStart, DateOnly windowEnd, DayCountConvention dayCount)
    {
        var windowFactor = DayCount.Between(windowStart, windowEnd, dayCount);
        if (windowFactor.Days < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowEnd), windowEnd, "Day count is negative (reversed interval); accrual requires start <= end.");
        }

        // Elapsed-day positions of the window edges, measured from the deposit anchor on the same
        // convention the segments are indexed by (actual elapsed days for Act/360 and Act/365).
        int winFromDay = DayCount.Between(depositStart, windowStart, dayCount).Days;
        int winToDay = DayCount.Between(depositStart, windowEnd, dayCount).Days;
        int basis = windowFactor.Basis;

        decimal accrued = 0m;
        for (int i = 0; i < Segments.Count; i++)
        {
            long segFrom = Segments[i].From;
            long segTo = i + 1 < Segments.Count ? Segments[i + 1].From : long.MaxValue;

            // Overlap of [segFrom, segTo) with the window [winFromDay, winToDay).
            long lo = Math.Max(segFrom, winFromDay);
            long hi = Math.Min(segTo, winToDay);
            if (hi <= lo)
            {
                continue; // this segment does not overlap the window
            }

            long segDays = hi - lo;
            // Same (Days/Basis) shape as SimpleInterest — accumulate un-rounded so the final
            // cross-to-Money is the single rounding boundary (ADR-PC-010 §P1–§P2).
            accrued += (decimal)principal.Cents * Segments[i].RateBasisPoints * segDays
                     / ((decimal)basis * 10_000);
        }
        return Money.FromCents(accrued);
    }

    // Amount-tiered: each principal tranche [trancheFrom, trancheTo) cents accrues over the WHOLE
    // interval at its segment rate (a progressive/marginal tiering). The top tranche is unbounded
    // (covers the principal above the last boundary). Accumulate in decimal, round once.
    private Money AccrueAmountTiered(Money principal, DateOnly start, DateOnly end, DayCountConvention dayCount)
    {
        var factor = DayCount.Between(start, end, dayCount);
        if (factor.Days < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end), end, "Day count is negative (reversed interval); accrual requires start <= end.");
        }

        decimal accrued = 0m;
        for (int i = 0; i < Segments.Count; i++)
        {
            long trancheFrom = Segments[i].From;
            if (trancheFrom >= principal.Cents)
            {
                break; // the principal does not reach this tranche
            }
            long trancheTo = i + 1 < Segments.Count ? Segments[i + 1].From : principal.Cents;
            if (trancheTo > principal.Cents)
            {
                trancheTo = principal.Cents; // clip the top in-range tranche to the actual principal
            }

            long trancheCents = trancheTo - trancheFrom;
            accrued += (decimal)trancheCents * Segments[i].RateBasisPoints * factor.Days
                     / ((decimal)factor.Basis * 10_000);
        }
        return Money.FromCents(accrued);
    }
}
