using Babelstone.EventStore;
using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// The family-declared Account seam (ADR-PC-033 slot 1): a family projection state implements this
/// to declare "my state IS an account" — the role, not a base class. In plain English: every
/// engine-owned aggregate (a deposit, a loan, a current account) is an account, meaning a stream of
/// <see cref="Movement"/>s keyed by an <c>account_ref</c> whose accounting balance is the fold
/// <c>Σ(Credit) − Σ(Debit)</c> over the movements posted to it. The engine owns that generic fold
/// (the movement ledger + <see cref="AccountBalanceReader"/>); the family supplies only WHICH of its
/// states plays the role and what its <see cref="AccountRef"/> is — the same one-member-seam idiom
/// as <see cref="IErasable{TState}"/> and <see cref="IMovementBearing"/>.
/// </summary>
/// <remarks>
/// A shared cross-family <c>Account</c> BASE CLASS was rejected (ADR-PC-033): it would put a
/// family-aware shape and a shared application layer into the spine (ADR-PC-021 forbids both).
/// The seam names no family — it exposes one opaque string — so ENGINE_FAMILY_AGNOSTIC holds and
/// the <c>family → engine</c> arrow stays one-way. Term deposit and personal loan are DEGENERATE
/// accounts (one balance, no holds): implementing this seam reclassifies them, it changes nothing
/// (the conforming implementations are a tracked sibling follow-up, not part of the seam).
/// </remarks>
public interface IAccount
{
    /// <summary>
    /// The opaque account reference this state's balances fold under — the <c>account_ref</c> the
    /// movement ledger and active-hold set are keyed by. A reference the engine resolves
    /// internally, NEVER PII (ADR-PC-004).
    /// </summary>
    string AccountRef { get; }
}

/// <summary>
/// The transactional-account refinement of the Account seam (ADR-PC-033 slot 1): a family state
/// implements this — instead of bare <see cref="IAccount"/> — to declare its account carries the
/// accounting/available-balance SPLIT, i.e. approved-but-unsettled authorizations earmark funds as
/// holds and its available balance is <c>accounting balance − Σ(active holds)</c>.
/// </summary>
/// <remarks>
/// Deliberately a marker refinement with no extra member: the split is UNIFORM (ADR-PC-033 slot 1 —
/// the same fold with an empty hold set for a non-transactional account), so the seam adds no
/// state; what it declares is which accounts the funds-and-rules authorization path
/// (<see cref="FundsAndRulesDecider"/>) and the hold lifecycle apply to. The hold set itself is
/// engine-owned (the <see cref="AccountHoldProjector"/> fold) — the family never tracks holds on
/// its own state ("the engine knows this account has an active hold of N; the family knows what
/// authorization placed it").
/// </remarks>
public interface IHoldable : IAccount;

/// <summary>
/// The lifecycle state of a <see cref="Hold"/> — a CLOSED set of exactly three members mirroring the
/// three pure transitions <c>HoldPlaced → HoldCaptured | HoldExpired</c> (ADR-PC-033 slot 2).
/// </summary>
public enum HoldState
{
    /// <summary>Placed and unreleased: the earmark reduces the account's available balance.</summary>
    Active,

    /// <summary>The matching settlement arrived: the hold left the active set and a posting
    /// <see cref="Movement"/> carries the money (the accounting balance moves; the earmark ends).</summary>
    Captured,

    /// <summary>Timed out before capture: the hold left the active set with NO posting — the
    /// earmark is released and no money moved (ADR-PC-023: expiry is projection-derived).</summary>
    Expired,
}

/// <summary>
/// A hold — the spine value object for funds earmarked by an approved-but-unsettled authorization
/// (ADR-PC-033 slot 1). In plain English: when an authorization is approved, N cents on the account
/// are set aside so a second concurrent authorization cannot spend them; the hold is captured when
/// the settlement arrives or expires if it times out. While <see cref="State"/> is
/// <see cref="HoldState.Active"/> the hold lowers the account's available balance.
/// </summary>
/// <remarks>
/// Carries NO family-typed shape and NO PII (ENGINE_FAMILY_AGNOSTIC / ADR-PC-004): an opaque
/// <see cref="HoldId"/> + <see cref="AccountRef"/>, integer-cents <see cref="Money"/>, a date, and
/// a closed-set state. It is a READ shape over the rebuildable active-hold fold (never a stored
/// source of truth — ADR-PC-033 rejected every stored mutable balance/hold number).
/// </remarks>
/// <param name="HoldId">The dedup/correlation key every lifecycle event of this hold carries (slot 4).</param>
/// <param name="AccountRef">The opaque account the earmark applies to — never PII (ADR-PC-004).</param>
/// <param name="Amount">The earmarked amount (integer cents, ADR-PC-010).</param>
/// <param name="ValueDate">The economic date the hold took effect — the expiry-horizon axis (ADR-PC-023).</param>
/// <param name="State">Where in the three-transition lifecycle this hold is (slot 2).</param>
public sealed record Hold(
    string HoldId,
    string AccountRef,
    Money Amount,
    DateOnly ValueDate,
    HoldState State);

/// <summary>
/// The spine-owned generic balance fold reads (ADR-PC-033 slot 1): the ACCOUNTING balance is the
/// movement ledger's signed sum over the account's posted <see cref="Movement"/>s (ADR-PC-030 §95),
/// and the AVAILABLE balance is that same fold minus Σ(active holds). Neither is ever a stored
/// mutable number — both are reads over the two rebuildable read models (movement_ledger 0019 +
/// account_holds 0020), so a discard-and-rebuild reproduces them identically
/// (ACCOUNT_BALANCE_IS_A_FOLD).
/// </summary>
/// <remarks>
/// Family-agnostic by construction (ADR-PC-021): both stores are keyed by the opaque
/// <c>account_ref</c> the <see cref="IAccount"/> seam exposes, so one reader serves every family.
/// The split is UNIFORM: a non-transactional (degenerate) account simply has an empty active-hold
/// set, so its available balance trivially equals its accounting balance — the same fold, no
/// special case. This reader is the stage-3 input of the funds-and-rules authorization path
/// (ADR-PC-030: the decider reads available balance BEFORE its append; the fold itself is never
/// gated).
/// </remarks>
public sealed class AccountBalanceReader(IMovementLedgerStore movements, IAccountHoldStore holds)
{
    /// <summary>
    /// The account's accounting balance in integer cents: what has POSTED — the signed sum of the
    /// movements recorded against <paramref name="accountRef"/> (Credit adds, Debit subtracts).
    /// </summary>
    public Task<long> GetAccountingBalanceCentsAsync(string accountRef, CancellationToken ct = default)
        => movements.GetBalanceCentsAsync(accountRef, ct);

    /// <summary>
    /// The account's available balance in integer cents: what is SPENDABLE now —
    /// <c>accounting balance − Σ(active holds)</c> (ADR-PC-033 slot 1). Computed, never stored.
    /// </summary>
    public async Task<long> GetAvailableBalanceCentsAsync(string accountRef, CancellationToken ct = default)
        => await movements.GetBalanceCentsAsync(accountRef, ct)
           - await holds.GetActiveHoldCentsAsync(accountRef, ct);

    /// <summary>The account's currently-active holds, as the spine <see cref="Hold"/> value object.</summary>
    public async Task<IReadOnlyList<Hold>> GetActiveHoldsAsync(string accountRef, CancellationToken ct = default)
    {
        var rows = await holds.GetActiveHoldsAsync(accountRef, ct);
        return rows.Select(ToHold).ToList();
    }

    /// <summary>
    /// The projection-derived expiry-horizon read (ADR-PC-023): every ACTIVE hold whose value date
    /// is at or before <paramref name="valueDateHorizon"/>, across all accounts — what an
    /// operator/command shell reads to decide which <c>HoldExpired</c> facts to append. The horizon
    /// is an input, never a clock read, so the fold stays replay-deterministic.
    /// </summary>
    public async Task<IReadOnlyList<Hold>> GetExpiryCandidatesAsync(
        DateOnly valueDateHorizon, CancellationToken ct = default)
    {
        var rows = await holds.GetActiveHoldsWithValueDateAtOrBeforeAsync(valueDateHorizon, ct);
        return rows.Select(ToHold).ToList();
    }

    // Rows out of the store are ACTIVE by query; the closed-set parse is still total (fail-loud on
    // a state outside the migration-0020 CHECK set rather than a silent default).
    private static Hold ToHold(AccountHoldRow row) => new(
        HoldId: row.HoldId,
        AccountRef: row.AccountRef,
        Amount: new Money(row.AmountCents),
        ValueDate: row.ValueDate,
        State: Enum.Parse<HoldState>(row.State, ignoreCase: true));
}
