using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the pure <see cref="UndeliverableCreditsEndpoints.Shape"/> projection — the operator
/// IOU-listing read's field mapping and AGE arithmetic (ADR-PC-043 slot 5). In plain English: these
/// prove the endpoint surfaces each outstanding IOU with its beneficiary, amount, reason, unapplied
/// date and how old it is, and that age is whole days from the credit's unapplied date to the
/// operator-supplied <c>as_of</c> horizon (an input, never a clock — ADR-PC-023), so the same rows
/// against the same horizon always shape identically. No HTTP stack, no database.
/// </summary>
public sealed class UndeliverableCreditsEndpointShapeTests
{
    private static UndeliverableCreditRow Outstanding(
        string intentId, DateOnly unappliedAt, string beneficiary = "acct-1", long cents = 5_000,
        string reason = "BENEFICIARY_ACCOUNT_CLOSED") =>
        new(
            IntentId: intentId,
            BeneficiaryRef: beneficiary,
            AmountCents: cents,
            Reason: reason,
            UnappliedAt: unappliedAt,
            State: "OUTSTANDING",
            UnappliedStreamId: Guid.NewGuid(),
            UnappliedSequence: 0);

    [Fact]
    public void Each_outstanding_iou_surfaces_beneficiary_amount_reason_date_and_age()
    {
        var unappliedAt = new DateOnly(2026, 6, 1);
        var asOf = new DateOnly(2026, 6, 21); // 20 days later

        var response = UndeliverableCreditsEndpoints.Shape(
            [Outstanding("INTENT-a", unappliedAt, "acct-a", 12_345, "BENEFICIARY_ACCOUNT_NOT_FOUND")],
            asOf);

        Assert.Equal(asOf, response.AsOf);
        Assert.Equal(1, response.OutstandingCount);
        var view = Assert.Single(response.Credits);
        Assert.Equal("INTENT-a", view.IntentId);
        Assert.Equal("acct-a", view.BeneficiaryRef);
        Assert.Equal(12_345, view.AmountCents);
        Assert.Equal("BENEFICIARY_ACCOUNT_NOT_FOUND", view.Reason);
        Assert.Equal(unappliedAt, view.UnappliedAt);
        Assert.Equal(20, view.AgeDays); // 2026-06-21 − 2026-06-01
    }

    [Fact]
    public void Age_is_zero_on_the_unapplied_date_and_never_negative_before_it()
    {
        var unappliedAt = new DateOnly(2026, 6, 25);

        // Same-day: zero.
        var sameDay = Assert.Single(
            UndeliverableCreditsEndpoints.Shape([Outstanding("INTENT-a", unappliedAt)], unappliedAt).Credits);
        Assert.Equal(0, sameDay.AgeDays);

        // A horizon BEFORE the unapplied date (an operator pinning an earlier snapshot) never yields a
        // negative age — it clamps to zero, so the IOU simply reads as "not yet aged".
        var earlier = Assert.Single(
            UndeliverableCreditsEndpoints.Shape(
                [Outstanding("INTENT-a", unappliedAt)], unappliedAt.AddDays(-5)).Credits);
        Assert.Equal(0, earlier.AgeDays);
    }

    [Fact]
    public void The_same_rows_against_the_same_horizon_shape_identically()
    {
        // Deterministic (ADR-PC-023): the horizon is an input, so re-shaping the same rows against the
        // same as_of yields the identical response — no clock leaks into the projection.
        var rows = new[]
        {
            Outstanding("INTENT-a", new DateOnly(2026, 6, 1), "acct-a", 1_000),
            Outstanding("INTENT-b", new DateOnly(2026, 6, 10), "acct-b", 2_000),
        };
        var asOf = new DateOnly(2026, 6, 20);

        var first = UndeliverableCreditsEndpoints.Shape(rows, asOf);
        var second = UndeliverableCreditsEndpoints.Shape(rows, asOf);

        // The views are value records, so element-wise equality proves the projection is deterministic
        // (the response wrapper holds a fresh list each call, so its record equality is reference-based).
        Assert.Equal(first.AsOf, second.AsOf);
        Assert.Equal(first.OutstandingCount, second.OutstandingCount);
        Assert.Equal(first.Credits, second.Credits);
    }

    [Fact]
    public void An_empty_outstanding_set_shapes_to_zero_credits()
    {
        var response = UndeliverableCreditsEndpoints.Shape([], new DateOnly(2026, 6, 20));

        Assert.Equal(0, response.OutstandingCount);
        Assert.Empty(response.Credits);
    }
}
