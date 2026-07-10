using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// Fold conformance for the store-only settlement money-movers <c>current_account.AccountCredited</c> and
/// <c>current_account.AccountDebited</c> (ADR-PC-043 §2): each must decode on the family registry and fold
/// replay-deterministically. In plain English: a received credit's and a captured debit's effect on the
/// balance is the <see cref="Movement"/> each carries — folded by the SPINE's account-keyed movement ledger
/// (both are <see cref="IMovementBearing"/>) — so on the FAMILY position each is a pure no-op, exactly like
/// the hold + accrual events (a demand account's balance is a spine-owned fold, never family state — ADR-PC-033).
/// </summary>
/// <remarks>
/// The <c>CREDIT_ADMISSION_UPSTREAM_OF_FOLD</c> commitment (ADR-PC-043 §The credit-admission gate) has an
/// architectural half pinned HERE: the generic movement-ledger fold is lifecycle-BLIND — this fold folds an
/// <see cref="AccountCredited"/> the SAME no-op way regardless of the folded lifecycle (it never consults
/// <see cref="AccountLifecycle"/>), which is exactly WHY admission MUST be decided upstream in the command
/// (a Closed/Erased account is refused there, never here). The admission decision itself is pinned by
/// <c>CurrentAccountCreditAdmissionTests</c> in the Application.Tests project.
/// </remarks>
public sealed class SettlementMovementFoldTests
{
    private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();

    [Fact]
    public void The_settlement_credit_and_capture_events_resolve_on_the_family_registry()
    {
        // Both are registered as family events alongside the lifecycle facts (store-only events still need
        // their binding, so they decode and replay fail-closed on an account stream).
        Assert.True(
            Registry.TryResolve("current_account.AccountCredited", out _),
            "current_account.AccountCredited did not resolve on the family registry");
        Assert.True(
            Registry.TryResolve("current_account.AccountDebited", out _),
            "current_account.AccountDebited did not resolve on the family registry");
    }

    [Fact]
    public void CREDIT_ADMISSION_UPSTREAM_OF_FOLD_the_fold_is_lifecycle_blind_and_folds_a_credit_the_same_way_from_any_state()
    {
        var accountId = Guid.NewGuid();
        var credit = Credited(accountId);

        // The generic movement-ledger fold is lifecycle-BLIND (ADR-PC-043 §The credit-admission gate): folding
        // an AccountCredited leaves the family position UNCHANGED — its lifecycle is never read or moved by the
        // fold. This is the architectural reason admission MUST be gated upstream: the fold itself cannot
        // refuse a credit into a Closed/Erased account, so the command decides admissibility BEFORE the append,
        // and the fold only ever sees an already-admitted credit.
        var active = Fold(AccountPosition.Empty, Opened(accountId));
        var afterCreditFromActive = Fold(active, credit);
        Assert.Equal(active, afterCreditFromActive);

        // Even a (hypothetically) Closed position folds a credit as the SAME no-op — proving the fold cannot be
        // the admission gate. Reaching this state with a credit never happens in production (the command
        // rejects it), which is the whole point of gating upstream.
        var closed = active with { Lifecycle = AccountLifecycle.Closed };
        var afterCreditFromClosed = Fold(closed, credit);
        Assert.Equal(closed, afterCreditFromClosed);
        Assert.Equal(AccountLifecycle.Closed, afterCreditFromClosed.Lifecycle);
    }

    [Fact]
    public void A_settlement_credit_folds_as_a_no_op_and_replays_identically()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));

        var credit = Credited(accountId);
        var afterFirst = Fold(active, credit);
        var afterSecond = Fold(afterFirst, credit);

        // The family position is untouched by a credit — same record, still Active (the Credit Movement moves
        // the spine-owned accounting balance, not this state), and folding again is a deterministic no-op.
        Assert.Equal(active, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(AccountLifecycle.Active, afterSecond.Lifecycle);
    }

    [Fact]
    public void A_settlement_capture_debit_folds_as_a_no_op_and_replays_identically()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));

        var debit = Debited(accountId);
        var afterFirst = Fold(active, debit);
        var afterSecond = Fold(afterFirst, debit);

        Assert.Equal(active, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(AccountLifecycle.Active, afterSecond.Lifecycle);
    }

    // --- helpers ---

    // A representative received credit: EUR 100.00 (10 000-cent) landing as ONE Observed Credit Movement
    // (ADR-PC-043 engine-internal-already-effected) — the carrier the spine folds; the family fold ignores it.
    private static AccountCredited Credited(Guid accountId)
    {
        var accountRef = accountId.ToString();
        var movement = new Movement(
            accountRef, SettlementDirection.Credit, new Money(10_000), new DateOnly(2026, 3, 5),
            MovementOperation.PayMaturity, MovementOrigin.Observed, Guid.NewGuid());
        return new AccountCredited(
            accountId, accountRef, new Money(10_000), "INTENT-abc|maturity", new DateOnly(2026, 3, 5), [movement]);
    }

    // A representative capture debit: EUR 250.00 (25 000-cent) landing as ONE Observed Debit Movement.
    private static AccountDebited Debited(Guid accountId)
    {
        var accountRef = accountId.ToString();
        var movement = new Movement(
            accountRef, SettlementDirection.Debit, new Money(25_000), new DateOnly(2026, 3, 5),
            MovementOperation.CollectInstallment, MovementOrigin.Observed, Guid.NewGuid());
        return new AccountDebited(
            accountId, accountRef, new Money(25_000), "hold-under-test", "INTENT-abc|installment-1",
            new DateOnly(2026, 3, 5), [movement]);
    }

    private static AccountOpened Opened(Guid accountId) => new(
        AccountId: accountId,
        ProductCode: "ca_pt_standard",
        Currency: "EUR",
        OpenedOn: new DateOnly(2026, 1, 1));

    private static AccountPosition Fold(AccountPosition state, DomainEvent @event)
    {
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"current_account.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (AccountPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
