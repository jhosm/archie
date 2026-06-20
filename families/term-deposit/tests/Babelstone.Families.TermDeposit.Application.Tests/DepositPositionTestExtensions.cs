using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Test helper for positions built by hand with <c>DepositPosition.Empty with { … }</c> rather than by
/// folding a real <c>DepositConstituted</c> through <see cref="DepositConstitutedHandler"/>.
/// </summary>
internal static class DepositPositionTestExtensions
{
    /// <summary>
    /// Complete a hand-built Active position the way <see cref="DepositConstitutedHandler"/> folds a real
    /// freshly-constituted one (bd babelstone-emtr): <see cref="DepositPosition.RemainingPrincipal"/>
    /// starts at the full <see cref="DepositPosition.Principal"/>, and the
    /// <see cref="DepositPosition.PrincipalTimeline"/> opens with the single
    /// <c>(StartDate, Principal)</c> segment. A deposit that never partially withdraws has exactly this
    /// one-segment timeline, so the F.12 piecewise accrual reduces to the prior single-principal accrual
    /// byte-for-byte — these fixtures stay assertion-identical, they just carry the shape production has.
    /// </summary>
    public static DepositPosition AsFreshlyConstituted(this DepositPosition p) =>
        p with
        {
            RemainingPrincipal = p.Principal,
            PrincipalTimeline = [new PrincipalSegment(p.StartDate, p.Principal)],
        };
}
