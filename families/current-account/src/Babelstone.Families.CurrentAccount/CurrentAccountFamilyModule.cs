using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount;

// Stryker disable all : Pure DI/registration glue — the event-type→handler binding table the engine
// dispatches, not the folds/lifecycle it wires up (mutation-testing.md "Mutation scope"). Disabled
// inline rather than via the family config's `mutate` list because this family project lives
// out-of-tree (../families/…) relative to the engine/ working dir, so Stryker's file-glob cannot
// reach it; the inline directive is path-independent. The behaviour files (folds, lifecycle) stay
// fully mutated.

/// <summary>
/// The current_account family module (ADR-PC-037): the third loaded family
/// schema (after term_deposit and personal_loan). Exports the event-type → handler bindings the engine
/// dispatches and folds. Discovered by <see cref="FamilyModuleLoader"/> via its public parameterless
/// constructor.
/// </summary>
/// <remarks>
/// <see cref="FamilyName"/> and <see cref="SchemaVersion"/> ride on the EventEnvelope via
/// AppendContext; they will match the family's CUE schema (contracts/cue/families/current-account.cue)
/// once it is authored alongside the host wiring. The family's five lifecycle events bind first;
/// then the engine-declared cross-cutting set splices in via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/> — one call so the family cannot forget a
/// binding as the set grows. Because <see cref="AccountPosition"/> is <see cref="IHoldable"/>, the
/// spliced set's ADR-PC-033 authorization holds (operations.HoldPlaced/HoldCaptured/HoldExpired) and
/// ADR-PC-041 legal holds/freezes decode and replay fail-closed on this account's stream — the folds
/// are no-ops because the active-hold set is the SPINE-owned <see cref="AccountHoldProjector"/> fold,
/// not family projection state.
/// </remarks>
public sealed class CurrentAccountFamilyModule : IFamilyModule
{
    public string FamilyName => "current_account";

    public string SchemaVersion => "current_account@2026.1";

    public IReadOnlyList<HandlerRegistration> Handlers =>
    [
        new("current_account.AccountOpened", typeof(AccountOpened),
            new DispatchableHandler<AccountPosition, AccountOpened>(new AccountOpenedHandler())),
        new("current_account.AccountOpeningFailed", typeof(AccountOpeningFailed),
            new DispatchableHandler<AccountPosition, AccountOpeningFailed>(new AccountOpeningFailedHandler())),
        new("current_account.AccountMarkedDormant", typeof(AccountMarkedDormant),
            new DispatchableHandler<AccountPosition, AccountMarkedDormant>(new AccountMarkedDormantHandler())),
        new("current_account.AccountReactivated", typeof(AccountReactivated),
            new DispatchableHandler<AccountPosition, AccountReactivated>(new AccountReactivatedHandler())),
        new("current_account.AccountClosed", typeof(AccountClosed),
            new DispatchableHandler<AccountPosition, AccountClosed>(new AccountClosedHandler())),
        // The authorize refusal fact (ADR-PC-037 §D6): a DECLINED synchronous authorize appends this
        // family event (an APPROVED one appends the cross-cutting operations.HoldPlaced spliced below).
        // Store-only — a refusal is internal audit, never on the bus — and folded as a no-op.
        new("current_account.AuthorizationDeclined", typeof(AuthorizationDeclined),
            new DispatchableHandler<AccountPosition, AuthorizationDeclined>(new AuthorizationDeclinedHandler())),
        // The overdraft-interest accrual fact (ADR-PC-037 §D5): a day's descoberto interest charged against
        // a drawn (negative) balance. Store-only (uncatalogued — an internal ledger fact, ADR-IC-017) and
        // folded as a no-op on the family position, but IMovementBearing, so the SPINE's movement ledger
        // folds the fee Movement it carries into the accounting balance (the balance is spine-owned, never
        // family state — ADR-PC-033). The fee math is command-side, never in this fold (ADR-PC-037 §P3).
        new("current_account.OverdraftInterestAccrued", typeof(OverdraftInterestAccrued),
            new DispatchableHandler<AccountPosition, OverdraftInterestAccrued>(new OverdraftInterestAccruedHandler())),
        // The settlement CREDIT-receive fact (ADR-PC-043 §2 ConfirmCredit → Credit Movement): the family's
        // FIRST money-IN event. Store-only for now (its .avsc/EventCatalog entry is bd babelstone-98mj.8) and
        // folded as a no-op on the family position, but IMovementBearing, so the SPINE's movement ledger folds
        // the Credit Movement it carries into the accounting balance (the balance is spine-owned, never family
        // state — ADR-PC-033). Admission (Active/Dormant admit, Closed/Erased reject) is decided UPSTREAM in
        // the credit-receive command, never in this fold (ADR-PC-043 §The credit-admission gate).
        new("current_account.AccountCredited", typeof(AccountCredited),
            new DispatchableHandler<AccountPosition, AccountCredited>(new AccountCreditedHandler())),
        // The settlement CAPTURE-debit fact (ADR-PC-043 §2 ConfirmDebit → HoldCaptured + Debit Movement): a
        // reservation turned into a real debit. Appended in the SAME batch as the spine-owned HoldCaptured
        // (which releases the earmark, ADR-PC-033 — bound below, not redefined here). Store-only for now (its
        // .avsc is bd babelstone-98mj.8) and folded as a no-op on the family position, but IMovementBearing,
        // so the SPINE's movement ledger folds the Debit Movement it carries into the accounting balance.
        new("current_account.AccountDebited", typeof(AccountDebited),
            new DispatchableHandler<AccountPosition, AccountDebited>(new AccountDebitedHandler())),
        // The engine-declared cross-cutting operational events (event-store §4.1), bound against this
        // family's AccountPosition. The engine owns the event records + generic handlers (they name no
        // family — ADR-PC-021 §P2); the family supplies only its TState here, splicing in every
        // cross-cutting binding in one call so it cannot forget one as the set grows: pack/schema
        // migration, operations.PersonalDataErasureRequested (GDPR erasure → AccountPosition.WithErased
        // via IErasable), the ADR-PC-041 legal holds & freezes (FundsHeld/FundsReleased/AccountFrozen/
        // AccountUnfrozen), and — load-bearing for this first transactional family — the ADR-PC-033
        // authorization holds (HoldPlaced/HoldCaptured/HoldExpired). All fold as no-ops because the
        // hold set and balances are spine-owned (ADR-PC-033); binding them is what makes them DECODE
        // (and replay fail-closed) on this IHoldable account's stream.
        .. CrossCuttingEventRegistrations.For<AccountPosition>(),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new CurrentAccountFamilyModule().Handlers);
}
