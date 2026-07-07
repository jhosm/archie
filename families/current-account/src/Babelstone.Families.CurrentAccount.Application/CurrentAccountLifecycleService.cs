using Babelstone.Engine;
using Babelstone.Packs;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure orchestration around the pure <see cref="CurrentAccountLifecycleDecider"/> (ADR-PC-021):
/// it rehydrates the account's <see cref="AccountPosition"/>, calls the static decider to produce the
/// family event, and appends it through the runtime. In plain English: this is the thin command-side
/// shell that does the I/O — load the current position, ask the pure decider what event (if any) is
/// legal, and write it — so the decision itself stays clock-free and Docker-testable.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>PersonalLoanConstitutionService</c> over <see cref="AccountPosition"/>, minus the
/// rate-sheet resolution a demand account's lifecycle has no need of (opening carries a product code and
/// a currency, not a priced rate). It depends only on generic engine ports
/// (<see cref="AggregateRuntime{TState}"/>) plus the pinned <see cref="VerifiedPack"/> — the dependency
/// arrow is family→engine, never the reverse (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// <para>
/// This class owns only the account's OPEN / DORMANT / REACTIVATE / CLOSE lifecycle plus the shared
/// GDPR-erasure fact. The synchronous AUTHORIZE decision is a separate service on the ADR-PC-034
/// technique, not this one.
/// </para>
/// </remarks>
public sealed class CurrentAccountLifecycleService(AggregateRuntime<AccountPosition> runtime, VerifiedPack pack)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    /// <summary>
    /// Open a new demand account: decide <see cref="AccountOpened"/> from the seed Pending state and
    /// append it as the stream's first event. The decider rejects a re-open (the stream is no longer
    /// Pending) or an empty product code / currency with <see cref="DomainRejectedException"/> before any
    /// append. <paramref name="validTime"/> is the envelope's valid time; the command's own
    /// <c>OpenedOn</c> is the domain value-date the decider folds.
    /// </summary>
    /// <returns>The new stream's head version — the read-your-writes token (ADR-IC-005).</returns>
    public async Task<long> OpenAsync(
        OpenAccountCommand command, string actor, DateTimeOffset validTime,
        Guid? commandId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Load the current position to answer the decider's legality question (open-once from Pending).
        // A fresh stream folds to AccountPosition.Empty (Pending), so the append below is a new stream
        // (expectedVersion -1); an existing account folds non-Pending and DecideOpen rejects before append.
        var current = (await runtime.LoadAsync(command.AccountId, ct)).State;
        var opened = CurrentAccountLifecycleDecider.DecideOpen(current, command);

        return await runtime.AppendAsync(
            command.AccountId, expectedVersion: -1, [opened],
            Context(actor, validTime, commandId), ct);
    }

    /// <summary>
    /// Mark a live account dormant after an inactivity horizon (→ <see cref="AccountMarkedDormant"/>).
    /// Legal only from Active; Dormant is the reversible non-terminal state
    /// (<see cref="ReactivateAsync"/> is its reverse leg).
    /// </summary>
    public Task<long> MarkDormantAsync(
        MarkAccountDormantCommand command, string actor, DateTimeOffset validTime,
        Guid? commandId = null, CancellationToken ct = default)
        => LoadDecideAppendAsync(
            command?.AccountId ?? throw new ArgumentNullException(nameof(command)),
            current => CurrentAccountLifecycleDecider.DecideMarkDormant(current, command),
            actor, validTime, commandId, ct);

    /// <summary>
    /// Reactivate a dormant account on use (→ <see cref="AccountReactivated"/>). Legal only from Dormant —
    /// the reverse leg of the reversible Dormant ⇄ Active pair.
    /// </summary>
    public Task<long> ReactivateAsync(
        ReactivateAccountCommand command, string actor, DateTimeOffset validTime,
        Guid? commandId = null, CancellationToken ct = default)
        => LoadDecideAppendAsync(
            command?.AccountId ?? throw new ArgumentNullException(nameof(command)),
            current => CurrentAccountLifecycleDecider.DecideReactivate(current, command),
            actor, validTime, commandId, ct);

    /// <summary>
    /// Close a live account (→ <see cref="AccountClosed"/>, a business terminal). Legal only from Active
    /// (ADR-PC-037). GDPR erasure remains legal from Closed — a closed account still holds the subject's
    /// PII until erased.
    /// </summary>
    public Task<long> CloseAsync(
        CloseAccountCommand command, string actor, DateTimeOffset validTime,
        Guid? commandId = null, CancellationToken ct = default)
        => LoadDecideAppendAsync(
            command?.AccountId ?? throw new ArgumentNullException(nameof(command)),
            current => CurrentAccountLifecycleDecider.DecideClose(current, command),
            actor, validTime, commandId, ct);

    /// <summary>
    /// Record the GDPR Article 17 erasure fact (ADR-PC-004): append the engine-declared cross-cutting
    /// <see cref="PersonalDataErasureRequested"/> so the account folds to Erased via
    /// <see cref="AccountPosition.WithErased"/>. The host has already crypto-shredded the subject's key
    /// before calling here, so this layer only writes the structural audit fact — the pseudonym is an
    /// opaque one-way reference, never the raw subject id. Erasure is legal from any state that still
    /// holds PII (Active / Dormant / Failed / Closed) and rejected from Pending or an already-Erased
    /// account, which is also the double-erase idempotency guard (<see cref="LifecycleTransitions"/>).
    /// </summary>
    public async Task<long> ErasePersonalDataAsync(
        Guid accountId, string subjectPseudonym, string erasureReason, string actor,
        DateTimeOffset validTime, Guid? commandId = null, CancellationToken ct = default)
    {
        var hydrated = await runtime.LoadAsync(accountId, ct);

        // The legality gate lives in the family lifecycle table, not the generic engine erasure fold
        // (the fold is family-agnostic and always marks erased). Consult it here so an erasure on a
        // Pending or already-Erased account is a DomainRejectedException, never a silent no-op append.
        if (!LifecycleTransitions.IsLegal(hydrated.State.Lifecycle, LifecycleTransitions.Transition.Erase))
        {
            throw new DomainRejectedException(
                $"current_account {accountId} is {hydrated.State.Lifecycle}; cannot erase personal data "
                + $"(illegal transition {LifecycleTransitions.Transition.Erase}).");
        }

        var erased = new PersonalDataErasureRequested(
            accountId, subjectPseudonym, DateOnly.FromDateTime(validTime.UtcDateTime), erasureReason);

        return await runtime.AppendAsync(
            accountId, hydrated.Version, [erased], Context(actor, validTime, commandId), ct);
    }

    // The shared load-then-decide-then-append choreography for the operating lifecycle commands
    // (mark-dormant / reactivate / close): rehydrate at the live head, let the pure decider both check
    // the legality table and produce the event (an illegal transition throws DomainRejectedException
    // before any append), and commit at the loaded version under optimistic concurrency. The command id,
    // when supplied, makes the append idempotent (ADR-PC-029).
    private async Task<long> LoadDecideAppendAsync(
        Guid accountId, Func<AccountPosition, DomainEvent> decide,
        string actor, DateTimeOffset validTime, Guid? commandId, CancellationToken ct)
    {
        var hydrated = await runtime.LoadAsync(accountId, ct);
        var @event = decide(hydrated.State);
        return await runtime.AppendAsync(
            accountId, hydrated.Version, [@event], Context(actor, validTime, commandId), ct);
    }

    // The family / pack / schema pins ride the EventEnvelope via AppendContext, never on the event record
    // (ADR-PC-009). commandId is the optional command-ingress idempotency key (ADR-PC-029).
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid? commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
