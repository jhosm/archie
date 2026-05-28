using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Babelstone.FinancialTypes.Tests;

/// <summary>
/// Property-based companions to <see cref="MoneyBoundaryFixtures"/> (B.9; ADR-PC-010 §P1–§P2).
/// The sealed corpus pins named points; these properties assert the laws that must hold across
/// the whole input space — the Money invariants a mutation (B.10) would most easily break:
/// <list type="bullet">
///   <item>the Decimal→Cents boundary rounds <b>to the nearest cent, ties to even</b> (HALF_EVEN,
///   §P2) and is <b>idempotent</b> — a second crossing of an already-rounded amount is a no-op;</item>
///   <item>integer-cent arithmetic <b>conserves cents</b> across split and merge — addition and
///   subtraction never round, so partitioning a total and re-merging it loses nothing (§P1).</item>
/// </list>
/// </summary>
public class MoneyProperties
{
    // A decimal cents amount with up to four fractional digits across a wide magnitude range —
    // frac == 5000 lands exactly on a .5-cent tie, so the "to nearest" property meets the midpoints.
    private static Gen<decimal> BoundaryDecimals =>
        from whole in Gen.Choose(-100_000_000, 100_000_000)
        from frac in Gen.Choose(0, 9_999)
        select whole + frac / 10_000m;

    // Amounts sitting exactly on a half-cent tie (k + 0.5), where HALF_EVEN is the rule that bites:
    // round-half-up or round-half-away would disagree with round-half-to-even on every odd k.
    private static Gen<decimal> HalfCentTies =>
        from k in Gen.Choose(-100_000_000, 100_000_000)
        select k + 0.5m;

    // A non-empty partition: 1..64 non-negative cent parts whose integer sum stays well inside
    // Int64 (64 × 1e8 = 6.4e9), so neither the fold nor parts.Sum() can overflow.
    private static Gen<long[]> CentParts =>
        from n in Gen.Choose(1, 64)
        from parts in Gen.ArrayOf(from c in Gen.Choose(0, 100_000_000) select (long)c, n)
        select parts;

    [Property]
    public Property Rounding_to_cents_lands_within_half_a_cent()
    {
        // The "nearest" half of round-to-nearest-even: the rounded whole-cent amount is never more
        // than half a cent from the exact decimal. A truncating or floor boundary would fail this
        // for inputs near x.9 — this is what stops it passing vacuously.
        return Prop.ForAll(BoundaryDecimals.ToArbitrary(), x =>
            Math.Abs((decimal)Money.FromCents(x).Cents - x) <= 0.5m);
    }

    [Property]
    public Property Half_cent_ties_round_to_even()
    {
        // The "ties to even" half of HALF_EVEN (§P2): an amount exactly on a half-cent resolves to
        // the even neighbour. This is the assertion the corpus pins at named points; here it holds
        // across the range, and it falsifies any round-half-up/away mutation (which makes k+0.5 odd).
        return Prop.ForAll(HalfCentTies.ToArbitrary(), x =>
            Money.FromCents(x).Cents % 2 == 0);
    }

    [Property]
    public Property Rounding_to_cents_is_idempotent()
    {
        // Round-once must be well-defined: re-crossing the boundary on an already-rounded amount is
        // a no-op, so a pipeline that re-wraps an intermediate Money cannot drift. (This is the
        // idempotence law itself; the HALF_EVEN value behaviour is pinned by the two properties above
        // and the named corpus — here the first crossing's tie has already been resolved.)
        return Prop.ForAll(BoundaryDecimals.ToArbitrary(), x =>
        {
            Money once = Money.FromCents(x);
            Money twice = Money.FromCents(once.Cents); // long widens to decimal: an exact integer
            return once == twice;
        });
    }

    [Property]
    public Property Adding_then_subtracting_round_trips()
    {
        var gen = from a in Gen.Choose(-1_000_000_000, 1_000_000_000)
                  from b in Gen.Choose(-1_000_000_000, 1_000_000_000)
                  select ((long)a, (long)b);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (a, b) = t;
            Money ma = new(a), mb = new(b);
            return (ma + mb) - mb == ma && (ma - mb) + mb == ma;
        });
    }

    [Property]
    public Property Merging_split_parts_conserves_cents()
    {
        // Merge: folding the parts with Money's '+' yields exactly the integer sum of their cents —
        // money-money arithmetic never rounds, so no cent is created or destroyed in the join.
        return Prop.ForAll(CentParts.ToArbitrary(), parts =>
        {
            Money merged = parts.Aggregate(Money.Zero, (acc, c) => acc + new Money(c));
            return merged.Cents == parts.Sum();
        });
    }

    [Property]
    public Property Splitting_a_total_recovers_each_part_exactly()
    {
        // Split is the inverse of merge: subtracting every part but the first from the merged total
        // must return precisely the first part — the conservation law read the other direction.
        return Prop.ForAll(CentParts.ToArbitrary(), parts =>
        {
            Money total = parts.Aggregate(Money.Zero, (acc, c) => acc + new Money(c));
            Money remainder = total;
            for (int i = 1; i < parts.Length; i++)
                remainder -= new Money(parts[i]);
            return remainder == new Money(parts[0]);
        });
    }
}
