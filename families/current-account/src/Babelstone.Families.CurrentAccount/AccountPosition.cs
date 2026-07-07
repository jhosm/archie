using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount;

/// <summary>The lifecycle states the current_account aggregate folds into (ADR-PC-037). The
/// transition LEGALITY (which states may move to which) is the <see cref="LifecycleTransitions"/>
/// state machine, deliberately NOT enforced here — these handlers are pure folds that label state,
/// not guards (mirrors the term-deposit / personal-loan families' split).</summary>
public enum AccountLifecycle
{
    /// <summary>Seed state before any event has folded.</summary>
    Pending,

    /// <summary>Opened and transacting — between <see cref="AccountOpened"/> and
    /// <see cref="AccountClosed"/>. Every operating transition (authorize, mark-dormant, close) is
    /// legal only from here (ADR-PC-037).</summary>
    Active,

    /// <summary>Marked dormant after an inactivity horizon — a NON-terminal, REVERSIBLE state: an
    /// inactive account reactivates on use (<c>Dormant ⇄ Active</c>, ADR-PC-037). This is what
    /// distinguishes the demand account from the loan's good-or-closed binary.</summary>
    Dormant,

    /// <summary>Opening was rejected by a config/rule/precondition check — no account was opened
    /// (terminal, ADR-PC-037).</summary>
    Failed,

    /// <summary>The account is closed (business terminal, ADR-PC-037). Still holds the subject's
    /// PII until erased, so GDPR erasure remains legal from here.</summary>
    Closed,

    /// <summary>GDPR Article 17 right-to-be-forgotten exercised — the subject's PII key was
    /// crypto-shredded (ADR-PC-004 §P3) and only non-personal structural fields remain queryable
    /// (terminal). Reachable from any state that still holds the subject's PII (Active / Dormant /
    /// Failed / Closed), never from Pending (no account) or Erased (idempotent).</summary>
    Erased,
}

/// <summary>
/// The account-position projection: the current_account aggregate's folded state — the FIRST
/// non-degenerate <see cref="IAccount"/> in the tree (ADR-PC-037). Produced by folding the
/// family's events through the existing engine mechanism (<see cref="SimulationRuntime{TState}"/> for
/// the in-memory read, <see cref="AggregateRuntime{TState}"/> for the durable read-through), so a
/// read of the just-committed log always reflects the latest event.
/// </summary>
/// <remarks>
/// <para>
/// <b>This position carries NO balance.</b> Unlike the deposit/loan positions (which fold a single
/// balance), a transactional account's <b>accounting</b> and <b>available</b> balances and its active
/// hold set are <b>spine-owned</b> folds — the <see cref="AccountHoldProjector"/> +
/// <see cref="AccountBalanceReader"/> derive <c>available = accounting − Σ active holds</c> from the
/// movement ledger + hold set keyed by <see cref="AccountRef"/> (ADR-PC-033; the active-hold set folds
/// both authorization and ADR-PC-041 legal holds). The arranged overdraft is NOT a term in this fold —
/// it is authorization headroom the authorize decider applies as a pack rule (ADR-PC-037), not part of
/// the balance. The family supplies only its own structural/lifecycle state through the seam; no
/// balance is ever a stored mutable number on this record — both balances stay rebuildable folds (the
/// shadow-balance failure ADR-PC-033 forbids; the ACCOUNT_BALANCE_IS_A_FOLD commitment). That is the
/// whole point of the first transactional instance: the account is real, yet the family holds only its
/// lifecycle.
/// </para>
/// <para>
/// This record is all scalar/string/enum/date fields — no collection — so the compiler-synthesised
/// record equality is correct as-is (no custom <c>Equals</c> needed for the byte-identical replay
/// determinism the engine relies on, ADR-PC-010 §P5), the same posture as <c>LoanPosition</c>.
/// </para>
/// </remarks>
/// <param name="AccountId">The account stream id, folded from <see cref="AccountOpened"/>.</param>
/// <param name="ProductCode">The catalogue product code (structural, not PII), folded from opening.</param>
/// <param name="Currency">The account's ISO-4217 currency (structural), folded from opening.</param>
/// <param name="OpenedOn">The value-date the account opened, folded from opening.</param>
/// <param name="Lifecycle">The current lifecycle state (ADR-PC-037).</param>
public sealed record AccountPosition(
    Guid AccountId,
    string ProductCode,
    string Currency,
    DateOnly OpenedOn,
    AccountLifecycle Lifecycle) : IErasable<AccountPosition>, IAccount, IHoldable
{
    /// <summary>
    /// GDPR Article 17 terminal transition (ADR-PC-004 §P3 / Amendment A4): label the account
    /// <see cref="AccountLifecycle.Erased"/>. The engine's generic cross-cutting erasure fold
    /// (<c>PersonalDataErasureRequestedHandler&lt;AccountPosition&gt;</c>, bound in
    /// <see cref="CurrentAccountFamilyModule"/>) calls this; the family owns only what "erased" means
    /// on its own lifecycle. Structural fields stay intact and queryable post-erasure (the PII lived
    /// behind the OpenBao key, never in this projection). Pure — no clock, no I/O (BENG001/002/003).
    /// </summary>
    public AccountPosition WithErased() => this with { Lifecycle = AccountLifecycle.Erased };

    /// <summary>
    /// The Account seam binding (ADR-PC-033 slot 1): the current account is the FIRST TRANSACTIONAL
    /// account — it declares BOTH <see cref="IAccount"/> and the <see cref="IHoldable"/> refinement,
    /// so the spine-owned generic projector derives its accounting/available split and folds its
    /// active-hold set (ADR-PC-037). The <c>account_ref</c> is the account's own opaque stream id
    /// (<see cref="AccountId"/>): an instance identifier the engine resolves internally, never PII
    /// (ADR-PC-004 §P2). A computed read over the already-folded id — not a record positional
    /// parameter — so the compiler-synthesised record equality and replay determinism (ADR-PC-010 §P5)
    /// are untouched. Unlike the deposit/loan degenerate accounts, this account carries a real hold
    /// ledger; the holds themselves are the spine-owned <see cref="AccountHoldProjector"/> fold, never
    /// tracked on this state ("the engine knows this account has an active hold of N; the family knows
    /// what authorization placed it", ADR-PC-033).
    /// </summary>
    public string AccountRef => AccountId.ToString();

    /// <summary>The seed state a fold starts from (before <see cref="AccountOpened"/>).</summary>
    public static AccountPosition Empty { get; } = new(
        AccountId: Guid.Empty,
        ProductCode: string.Empty,
        Currency: string.Empty,
        OpenedOn: default,
        Lifecycle: AccountLifecycle.Pending);
}
