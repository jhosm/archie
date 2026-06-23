using System.Reflection;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// B.10 mutation backstop for <see cref="DepositPosition"/>'s HAND-ROLLED value equality. The record
/// overrides <c>Equals</c>/<c>GetHashCode</c> on purpose: the compiler-generated equality would
/// compare the one collection field (<c>PrincipalTimeline</c>) by REFERENCE, which would make two
/// independently-folded-but-identical positions unequal and break the byte-identical replay-determinism
/// contract (ADR-PC-010 §P5) the engine relies on. That contract is exercised end-to-end by the
/// <c>.Application</c> determinism/parity suites — a SEPARATE mutation leg — so within THIS family leg
/// the per-field equality is otherwise unpinned and every <c>&amp;&amp;</c>/<c>hash.Add</c> mutant
/// survives. These tests close that gap and, more importantly, are the executable form of the invariant
/// the source comment names: "a new field added here MUST be added to BOTH members." Each field, changed
/// alone, must make the position UNEQUAL and (for a distinct value) hash differently; the collection
/// field must compare ELEMENT-WISE, not by reference.
/// </summary>
public class DepositPositionEqualityTests
{
    // A fully-populated baseline: every field carries a distinct, non-default value so that any
    // single-field change is observable in both Equals and GetHashCode.
    private static DepositPosition Baseline() => DepositPosition.Empty with
    {
        DepositId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
        Principal = new Money(1_000_000L),
        TanBasisPoints = 300,
        RateSheetVersionId = "rs-1",
        TermDays = 365,
        StartDate = new DateOnly(2026, 1, 1),
        MaturityDate = new DateOnly(2027, 1, 1),
        InterestVariant = "AT_MATURITY",
        AutoRenewalPolicy = "NONE",
        PaymentPeriodMonths = 3,
        ProductCode = "dpz_pt_12m",
        Role = "standard",
        FundingAccount = "acct-1",
        MinWithdrawalCents = 10_000L,
        MinRemainingBalanceCents = 50_000L,
        CarenciaDays = 30,
        PrincipalTimeline = [new PrincipalSegment(new DateOnly(2026, 1, 1), new Money(1_000_000L))],
        AccruedGrossInterest = new Money(15_001L),
        WithholdingToDate = new Money(4_200L),
        NetInterest = new Money(10_801L),
        TotalPayout = new Money(1_021_900L),
        RemainingPrincipal = new Money(800_000L),
        SettlementAmount = new Money(1_018_400L),
        CorrectionCount = 2,
        CouponsPaid = 2,
        Lifecycle = DepositLifecycle.Active,
    };

    // One variant per field, each differing from the baseline in EXACTLY that field.
    public static TheoryData<string> FieldNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in SingleFieldVariants(Baseline()).Keys)
            data.Add(name);
        return data;
    }

    private static IReadOnlyDictionary<string, DepositPosition> SingleFieldVariants(DepositPosition b) =>
        new Dictionary<string, DepositPosition>
        {
            [nameof(b.DepositId)] = b with { DepositId = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7") },
            [nameof(b.Principal)] = b with { Principal = new Money(1_000_001L) },
            [nameof(b.TanBasisPoints)] = b with { TanBasisPoints = 301 },
            [nameof(b.RateSheetVersionId)] = b with { RateSheetVersionId = "rs-2" },
            [nameof(b.TermDays)] = b with { TermDays = 366 },
            [nameof(b.StartDate)] = b with { StartDate = new DateOnly(2026, 1, 2) },
            [nameof(b.MaturityDate)] = b with { MaturityDate = new DateOnly(2027, 1, 2) },
            [nameof(b.InterestVariant)] = b with { InterestVariant = "ADVANCE" },
            [nameof(b.AutoRenewalPolicy)] = b with { AutoRenewalPolicy = "AUTO" },
            [nameof(b.PaymentPeriodMonths)] = b with { PaymentPeriodMonths = 1 },
            [nameof(b.ProductCode)] = b with { ProductCode = "dpz_pt_24m" },
            [nameof(b.Role)] = b with { Role = "premium" },
            [nameof(b.FundingAccount)] = b with { FundingAccount = "acct-2" },
            [nameof(b.MinWithdrawalCents)] = b with { MinWithdrawalCents = 20_000L },
            [nameof(b.MinRemainingBalanceCents)] = b with { MinRemainingBalanceCents = 60_000L },
            [nameof(b.CarenciaDays)] = b with { CarenciaDays = 31 },
            [nameof(b.PrincipalTimeline)] = b with { PrincipalTimeline = [new PrincipalSegment(new DateOnly(2026, 1, 1), new Money(900_000L))] },
            [nameof(b.AccruedGrossInterest)] = b with { AccruedGrossInterest = new Money(15_002L) },
            [nameof(b.WithholdingToDate)] = b with { WithholdingToDate = new Money(4_201L) },
            [nameof(b.NetInterest)] = b with { NetInterest = new Money(10_802L) },
            [nameof(b.TotalPayout)] = b with { TotalPayout = new Money(1_021_901L) },
            [nameof(b.RemainingPrincipal)] = b with { RemainingPrincipal = new Money(800_001L) },
            [nameof(b.SettlementAmount)] = b with { SettlementAmount = new Money(1_018_401L) },
            [nameof(b.CorrectionCount)] = b with { CorrectionCount = 3 },
            [nameof(b.CouponsPaid)] = b with { CouponsPaid = 3 },
            [nameof(b.Lifecycle)] = b with { Lifecycle = DepositLifecycle.Matured },
        };

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Changing_any_single_field_breaks_equality_and_the_hash(string field)
    {
        var baseline = Baseline();
        var variant = SingleFieldVariants(baseline)[field];

        // Equality must consider this field (kills the && → || mutant guarding it).
        Assert.NotEqual(baseline, variant);
        Assert.False(baseline.Equals(variant), $"{field} ignored by Equals");
        // The hash must mix this field (kills the hash.Add(field) statement-removal mutant).
        Assert.NotEqual(baseline.GetHashCode(), variant.GetHashCode());
    }

    [Fact]
    public void Confirms_every_record_field_is_covered_by_a_single_field_variant()
    {
        // Guards the test itself against drift: the variant set must cover EXACTLY the record's public
        // fields. Reflecting over the record (not a hand-maintained literal on both sides) means ADDING a
        // field to DepositPosition without a matching variant fails HERE — caught now, not as a fresh
        // survivor in the next weekly run.
        var recordFieldCount = typeof(DepositPosition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
        Assert.Equal(26, recordFieldCount); // the 26 positional record parameters
        Assert.Equal(recordFieldCount, SingleFieldVariants(Baseline()).Count);
    }

    [Fact]
    public void A_position_equals_itself_and_a_structural_copy()
    {
        var baseline = Baseline();
        var copy = baseline with { }; // a fresh instance, structurally identical

        Assert.True(baseline.Equals(baseline));
        Assert.True(baseline.Equals(copy));
        Assert.Equal(baseline, copy);
        Assert.Equal(baseline.GetHashCode(), copy.GetHashCode());
    }

    [Fact]
    public void A_position_never_equals_null()
    {
        var baseline = Baseline();

        // Kills the `other is not null` mutation: a null other must short-circuit to false, never
        // dereference (a mutated guard would throw a NullReferenceException here).
        Assert.False(baseline.Equals(null));
        Assert.False(baseline.Equals((object?)null));
    }

    [Fact]
    public void Principal_timeline_compares_element_wise_not_by_reference()
    {
        // Two positions whose timelines are SEPARATE list instances but element-wise identical MUST be
        // equal — this is the whole reason equality is hand-rolled (the determinism contract). A
        // reference comparison (the compiler default) would make these unequal.
        var a = Baseline() with { PrincipalTimeline = [new PrincipalSegment(new DateOnly(2026, 1, 1), new Money(1_000_000L))] };
        var b = Baseline() with { PrincipalTimeline = [new PrincipalSegment(new DateOnly(2026, 1, 1), new Money(1_000_000L))] };

        Assert.NotSame(a.PrincipalTimeline, b.PrincipalTimeline);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // …and a differing element must still break equality.
        var c = Baseline() with { PrincipalTimeline = [new PrincipalSegment(new DateOnly(2026, 6, 1), new Money(1_000_000L))] };
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Principal_timeline_equality_is_order_and_length_sensitive()
    {
        // SequenceEqual is order- AND length-sensitive; a single-element timeline cannot demonstrate
        // that, so pin it with a two-segment timeline (the F.12 partial-withdrawal shape).
        static PrincipalSegment[] Two() =>
        [
            new PrincipalSegment(new DateOnly(2026, 1, 1), new Money(1_000_000L)),
            new PrincipalSegment(new DateOnly(2026, 6, 1), new Money(800_000L)),
        ];
        var a = Baseline() with { PrincipalTimeline = Two() };
        var b = Baseline() with { PrincipalTimeline = Two() };

        Assert.NotSame(a.PrincipalTimeline, b.PrincipalTimeline);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // The same two segments in the opposite order are NOT equal (order-sensitive).
        var swapped = Baseline() with { PrincipalTimeline = [Two()[1], Two()[0]] };
        Assert.NotEqual(a, swapped);

        // A shorter timeline is NOT equal (length-sensitive).
        var shorter = Baseline() with { PrincipalTimeline = [Two()[0]] };
        Assert.NotEqual(a, shorter);
    }
}
