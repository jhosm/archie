namespace Babelstone.Money.Tests;

/// <summary>
/// The sealed MONEY_BOUNDARY_FIXTURES corpus (ADR-PC-010 §P2; commitment-catalogue #2):
/// (input decimal cents, expected long cents) pairs that pin "HALF_EVEN rounds once at
/// the Decimal→Cents boundary". Three classes per §P2: midpoints, small (sub-cent
/// daily accruals), and large magnitudes (€1e8+ over multi-year terms). Independent
/// external-anchor cases (B.8) extend this corpus; they do not replace it.
/// </summary>
public static class MoneyBoundaryFixtures
{
    public readonly record struct Case(string Name, decimal InputCents, long ExpectedCents);

    public static readonly IReadOnlyList<Case> Cases = new[]
    {
        // Midpoints — round half to even (banker's rounding), including negatives.
        new Case("midpoint 0.5 -> 0", 0.5m, 0L),
        new Case("midpoint 1.5 -> 2", 1.5m, 2L),
        new Case("midpoint 2.5 -> 2", 2.5m, 2L),
        new Case("midpoint 99.5 -> 100", 99.5m, 100L),
        new Case("midpoint 100.5 -> 100", 100.5m, 100L),
        new Case("midpoint 101.5 -> 102", 101.5m, 102L),
        new Case("midpoint -0.5 -> 0", -0.5m, 0L),
        new Case("midpoint -1.5 -> -2", -1.5m, -2L),
        new Case("midpoint -2.5 -> -2", -2.5m, -2L),

        // Small magnitudes — sub-cent daily accruals round to nearest, not at midpoint.
        new Case("small 0.4 -> 0", 0.4m, 0L),
        new Case("small 0.6 -> 1", 0.6m, 1L),
        new Case("small 0.49999 -> 0", 0.49999m, 0L),
        new Case("small 0.50001 -> 1", 0.50001m, 1L),

        // Large magnitudes — €1e8 = 10,000,000,000 cents; rounds once, no overflow.
        new Case("large exact 1e10", 10_000_000_000m, 10_000_000_000L),
        new Case("large 1e10 + 0.5 -> even", 10_000_000_000.5m, 10_000_000_000L),
        new Case("large 1e10 + 1.5 -> even", 10_000_000_001.5m, 10_000_000_002L),
    };
}
