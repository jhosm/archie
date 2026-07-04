using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// Account-seam conformance (ADR-PC-033 slot 1, bd babelstone-hkwp.6): the term deposit is a
/// DEGENERATE account — one balance, no holds — so <see cref="DepositPosition"/> implements the
/// spine-owned <see cref="IAccount"/> seam (the same one-member family-declared idiom as
/// <see cref="IErasable{TState}"/>). This is a RECLASSIFICATION, not a behaviour change: these
/// tests pin that the <c>account_ref</c> is the deposit's own opaque stream id (never PII —
/// ADR-PC-004 §P2), that it is stable across the fold, that the deposit is NOT
/// <see cref="IHoldable"/> (it carries no holds), and that the record's custom element-wise
/// equality — the replay-determinism backstop — is untouched by the computed property.
/// </summary>
public sealed class DepositPositionAccountSeamTests
{
    private static readonly HandlerRegistry Registry = TermDepositFamilyModule.Registry();

    [Fact]
    public void DepositPosition_implements_the_IAccount_seam_but_not_IHoldable()
    {
        // The seam declares "my state IS an account" (ADR-PC-033 slot 1). Degenerate: one balance,
        // no holds — so IAccount yes, the IHoldable transactional refinement deliberately NO (the
        // available/accounting split is trivially uniform with an empty hold set).
        Assert.IsAssignableFrom<IAccount>(DepositPosition.Empty);
        // Reflection (not an `is` pattern): the record is sealed, so the compiler would prove the
        // pattern always-false (CS0184) — which is exactly the degenerate posture we pin here.
        Assert.False(typeof(IHoldable).IsAssignableFrom(typeof(DepositPosition)),
            "a term deposit is a DEGENERATE account — it must not declare the transactional IHoldable refinement");
    }

    [Fact]
    public void AccountRef_is_the_deposits_own_stream_id_after_the_constitution_fold()
    {
        var depositId = Guid.NewGuid();
        var active = Dispatch(DepositPosition.Empty, Constituted(depositId));

        IAccount account = active;
        // The account_ref is the deposit's OWN opaque instance id — not the funding account (that
        // is the counterparty the constitution debit moved money from) and never PII (ADR-PC-004 §P2).
        Assert.Equal(depositId.ToString(), account.AccountRef);
        Assert.False(string.IsNullOrEmpty(account.AccountRef));
        Assert.NotEqual(active.FundingAccount, account.AccountRef);
    }

    [Fact]
    public void AccountRef_is_stable_across_subsequent_folds()
    {
        var depositId = Guid.NewGuid();
        var active = Dispatch(DepositPosition.Empty, Constituted(depositId));
        var refAtConstitution = ((IAccount)active).AccountRef;

        var afterAccrual = Dispatch(active, new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15)));
        var afterWithholding = Dispatch(afterAccrual, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        // The stream id never moves once folded, so the account_ref the movement ledger keys by is stable.
        Assert.Equal(refAtConstitution, ((IAccount)afterAccrual).AccountRef);
        Assert.Equal(refAtConstitution, ((IAccount)afterWithholding).AccountRef);
    }

    [Fact]
    public void The_seam_leaves_the_custom_equality_semantics_unchanged()
    {
        // AccountRef is a COMPUTED property over the already-folded DepositId, not a record field,
        // so the custom element-wise Equals/GetHashCode (the byte-identical replay-determinism
        // contract, ADR-PC-010 §P5) must behave exactly as before: two independently-folded but
        // identical positions are equal and hash identically.
        var depositId = Guid.NewGuid();
        var first = Dispatch(DepositPosition.Empty, Constituted(depositId));
        var second = Dispatch(DepositPosition.Empty, Constituted(depositId));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // And two DIFFERENT deposits stay unequal — the seam added no equality wrinkle either way.
        var other = Dispatch(DepositPosition.Empty, Constituted(Guid.NewGuid()));
        Assert.NotEqual(first, other);
    }

    // --- helpers ---

    private static DepositConstituted Constituted(Guid depositId) => new(
        DepositId: depositId,
        Principal: new Money(1_000_000),
        TanBasisPoints: 300,
        RateSheetVersionId: "rs-1",
        TermDays: 365,
        StartDate: new DateOnly(2026, 1, 15),
        MaturityDate: new DateOnly(2027, 1, 15),
        InterestVariant: "AT_MATURITY",
        AutoRenewalPolicy: "NONE",
        FundingAccount: "acct-token-1");

    private static DepositPosition Dispatch(DepositPosition state, DomainEvent @event)
    {
        // Mirrors TermDepositDispatchTests: fold through the family's OWN handler registry — the
        // same one the durable runtime folds through — so the seam is proved on real folded state.
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"term_deposit.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (DepositPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
