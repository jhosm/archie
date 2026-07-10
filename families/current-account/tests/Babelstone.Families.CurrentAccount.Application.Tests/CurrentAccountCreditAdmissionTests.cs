using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The credit-ADMISSION gate commitments (ADR-PC-043): a current account can only
/// RECEIVE money if it is open (or dormant, which reactivates); a closed or erased account refuses the credit
/// BY CONSTRUCTION. In plain English: these pin that admissibility is decided on the account's own folded
/// lifecycle BEFORE anything is recorded — an Active/Dormant account admits (a Dormant one reactivates + credits
/// in one atomic batch), a Closed one rejects <c>ACCOUNT_CLOSED</c> and an Erased one <c>ACCOUNT_ERASED</c> — so
/// no credit ever folds into an account that cannot receive it.
/// </summary>
/// <remarks>
/// <para>
/// Docker-free (the pure-decider lane, like <see cref="CurrentAccountLifecycleDeciderTests"/>): the admission
/// decider is a pure function of (folded lifecycle, command), so the whole gate is exercised deterministically
/// with no event store. The load→admit→append-at-loaded-version OCC CHOREOGRAPHY the impure shell wraps around
/// it (<c>CurrentAccountCreditReceiveService</c>) — the reload-and-redecide retry — is the Testcontainers
/// integration tier; here we pin the DECISION the shell reloads and re-runs.
/// </para>
/// <para>
/// <b>The own-stream OCC seam at the decision boundary (ADR-PC-043; its fitness anchor still planned).</b> The shell reads lifecycle from the
/// synchronous own-stream fold and appends on the SAME stream at the loaded version, so a concurrent
/// CloseAccount is either seen on reload or loses the per-stream OCC race → reload-and-redecide. The
/// re-decision is exactly this decider run against the now-Closed folded position, which these tests pin
/// rejects <c>ACCOUNT_CLOSED</c> — so "credit-receive vs CloseAccount at the same version yields exactly one
/// commit + an ACCOUNT_CLOSED reject on retry" reduces to: the credit admits from Active, and the reload's
/// re-decision from Closed rejects. The from-Closed reject is the retry half; the exactly-one-commit half is
/// the store's stale-head OCC seam (the same one authorize rides), integration-pinned.
/// </para>
/// </remarks>
public sealed class CurrentAccountCreditAdmissionTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly string AccountRef = AccountId.ToString();
    private static readonly DateOnly ValueDate = new(2026, 3, 5);
    private static readonly Guid CommandId = Guid.NewGuid();

    private static AccountPosition InState(AccountLifecycle lifecycle) =>
        AccountPosition.Empty with { AccountId = AccountId, Lifecycle = lifecycle };

    private static ReceiveCreditCommand Command(long amountCents = 10_000) =>
        new(AccountId, amountCents, ValueDate, "INTENT-abc|maturity", "svc:settlement-dispatch", CommandId);

    // --- admit from Active ---

    [Fact]
    public void An_active_account_admits_a_credit_yielding_a_single_AccountCredited_with_a_Credit_Movement()
    {
        var events = CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Active), Command());

        var credited = Assert.IsType<AccountCredited>(Assert.Single(events));
        Assert.Equal(AccountId, credited.AccountId);
        Assert.Equal(AccountRef, credited.AccountRef);
        Assert.Equal(10_000, credited.Amount.Cents);
        Assert.Equal("INTENT-abc|maturity", credited.IntentReference);
        Assert.Equal(ValueDate, credited.ValueDate);

        // The credit lands as EXACTLY ONE Observed Credit Movement (a Credit ADDS to the accounting balance),
        // carrying the same amount and the append command id — the spine folds it into the balance.
        var movement = Assert.Single(((IMovementBearing)credited).Movements);
        Assert.Equal(AccountRef, movement.AccountRef);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);
        Assert.Equal(10_000, movement.Amount.Cents);
        Assert.Equal(MovementOperation.PayMaturity, movement.Operation); // generic money-IN verb (a dedicated CA verb is a later change)
        Assert.Equal(MovementOrigin.Observed, movement.Origin); // engine-internal-already-effected loop-breaker
        Assert.Equal(CommandId, movement.CommandId);
    }

    // --- CREDIT_REACTIVATE_CREDIT_ATOMIC_BATCH ---

    [Fact]
    public void CREDIT_REACTIVATE_CREDIT_ATOMIC_BATCH_a_dormant_account_admits_with_reactivate_then_credit_in_one_batch()
    {
        // The load-bearing invariant (ADR-PC-043): a Dormant account is used again
        // by this credit, so it reactivates AND credits in ONE atomic append batch — the decider returns BOTH
        // events, reactivate FIRST, so the shell appends them together and a Close cannot wedge between them.
        var events = CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Dormant), Command());

        Assert.Equal(2, events.Count);
        var reactivated = Assert.IsType<AccountReactivated>(events[0]);
        Assert.Equal(AccountId, reactivated.AccountId);
        Assert.Equal(ValueDate, reactivated.ReactivatedOn);

        var credited = Assert.IsType<AccountCredited>(events[1]);
        Assert.Equal(10_000, credited.Amount.Cents);
        Assert.Single(((IMovementBearing)credited).Movements);
    }

    // --- CREDIT_ADMISSION_OWN_STREAM_OCC (the decision half) ---

    [Fact]
    public void CREDIT_ADMISSION_OWN_STREAM_OCC_a_closed_account_rejects_ACCOUNT_CLOSED()
    {
        // The retry half of the OCC race: after a concurrent CloseAccount commits, the shell reloads and
        // re-runs THIS decider against the now-Closed position — which rejects ACCOUNT_CLOSED before any
        // append, so the credit never folds into the closed account.
        var ex = Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Closed), Command()));
        Assert.Contains(CreditRejectedReason.AccountClosed, ex.Message);
    }

    [Fact]
    public void CREDIT_ADMISSION_OWN_STREAM_OCC_an_erased_account_rejects_ACCOUNT_ERASED()
    {
        var ex = Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Erased), Command()));
        Assert.Contains(CreditRejectedReason.AccountErased, ex.Message);
    }

    [Theory]
    [InlineData(AccountLifecycle.Pending)]
    [InlineData(AccountLifecycle.Failed)]
    public void A_never_opened_account_rejects_ACCOUNT_NOT_OPEN(AccountLifecycle lifecycle)
    {
        // Pending (never opened) / Failed (open rejected): there is no account to credit — refuse rather than
        // silently open one.
        var ex = Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(InState(lifecycle), Command()));
        Assert.Contains(CreditRejectedReason.AccountNotOpen, ex.Message);
    }

    [Fact]
    public void A_non_positive_credit_is_rejected_before_admission()
    {
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Active), Command(amountCents: 0)));
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Active), Command(amountCents: -1)));
    }

    // --- CREDIT_ADMISSION_UPSTREAM_OF_FOLD (folding admitted events reproduces the lifecycle) ---

    [Fact]
    public void CREDIT_ADMISSION_UPSTREAM_OF_FOLD_no_credit_ever_folds_into_a_closed_or_erased_account()
    {
        // Prove the admitted events, folded through the REAL family registry, never land a credit on a
        // Closed/Erased account: from Active the credit admits and folds leaving the account Active; a Close
        // then makes a re-decision reject, so no AccountCredited is ever produced for the Closed stream — the
        // fold only ever sees admitted credits (the generic movement-ledger fold is lifecycle-blind, so this
        // upstream gate is the ONLY thing keeping a credit out of a closed account).
        var runtime = new SimulationRuntime<AccountPosition>(
            store: null!, CurrentAccountFamilyModule.Registry(), serializer: null!, () => AccountPosition.Empty);

        // Active → admit + fold → still Active (the credit is a no-op on the family position; the balance moves
        // spine-side).
        var admittedFromActive = CurrentAccountCreditAdmissionDecider.Decide(InState(AccountLifecycle.Active), Command());
        var afterCredit = runtime.ProjectFromScratch(
            [Opened(), .. admittedFromActive]);
        Assert.Equal(AccountLifecycle.Active, afterCredit.Lifecycle);

        // Close the account, then re-decide the SAME credit against the now-Closed fold: it REJECTS, so no
        // credit event is ever appended to (or folded into) the closed account.
        var closed = runtime.ProjectFromScratch(
            [Opened(), new AccountClosed(AccountId, ValueDate, "CUSTOMER_REQUEST")]);
        Assert.Equal(AccountLifecycle.Closed, closed.Lifecycle);
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCreditAdmissionDecider.Decide(closed, Command()));
    }

    private static AccountOpened Opened() => new(AccountId, "ca_pt_standard", "EUR", new DateOnly(2026, 1, 1));
}
