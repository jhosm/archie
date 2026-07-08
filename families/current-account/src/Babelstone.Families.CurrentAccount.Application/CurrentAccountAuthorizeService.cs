using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;
using Babelstone.Packs;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure command shell around the pure <see cref="CurrentAccountAuthorizeDecider"/> (ADR-PC-034
/// synchronous technique on the ADR-PC-029 command surface). In plain English: it does the I/O a
/// real-time debit authorization needs — make sure prior earmarks are visible, read what is spendable
/// and whether the account is frozen, ask the pure decider "authorized or declined?", and write the
/// answer — while the decision itself stays clock-free and Docker-testable.
/// </summary>
/// <remarks>
/// <para>
/// A SEPARATE service from <see cref="CurrentAccountLifecycleService"/> (ADR-PC-037 topology): the
/// lifecycle commands relabel state and move no money; authorize earmarks funds on the payment hot path.
/// It depends only on generic engine ports (the runtime, the spine balance/freeze readers, the spine
/// projection drainer, the store codec) plus the pinned pack — the dependency arrow is family→engine,
/// never the reverse (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// <para>
/// <b>Drain before decide (read-your-writes, ADR-PC-033).</b> <see cref="AccountBalanceReader"/> answers
/// from the DRAINED read model, not the append-instant log, so a just-placed hold from a prior authorize
/// is visible only after the spine projection drive folds it. This shell drains explicitly before it
/// reads the balance, so two back-to-back authorizes cannot both spend the same stale funds.
/// </para>
/// <para>
/// <b>Idempotent on the command id (ADR-PC-029 slot 4).</b> The append threads the caller's
/// <c>CommandId</c>, so a replay returns the ORIGINAL head with no second append. Because BOTH outcomes
/// append exactly one event (a <c>HoldPlaced</c> or an <see cref="AuthorizationDeclined"/>), a replayed
/// decline dedupes exactly like a replayed approval; <see cref="ReconstructVerdictAsync"/> reproduces the
/// original verdict from the single appended event, so the AUTHORIZATION_SYNC_IDEMPOTENT contract holds
/// for both.
/// </para>
/// </remarks>
public sealed class CurrentAccountAuthorizeService(
    AggregateRuntime<AccountPosition> runtime,
    AccountBalanceReader balances,
    AccountFreezeReader freezes,
    SpineProjectionDrainer drainer,
    VerifiedPack pack,
    IEventStore store,
    IEventSerializer serializer,
    IPiiProtector protector,
    CurrentAccountProductConfigStore productConfigs)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    /// <summary>
    /// Decide one authorize attempt synchronously and append its outcome. Loads the account, drains the
    /// spine read model so prior earmarks are visible, reads the available balance and any active freeze,
    /// resolves the pack rules, and hands all of it to the pure decider — then appends the resulting
    /// <c>HoldPlaced</c> (authorized) or <see cref="AuthorizationDeclined"/> (declined) idempotently on
    /// the command id and returns the verdict. Throws <see cref="DomainRejectedException"/> for a
    /// non-positive amount, and propagates <see cref="ConcurrencyException"/> /
    /// <see cref="DuplicateCommandException"/> for the endpoint to map.
    /// </summary>
    public async Task<AuthorizeResponse> AuthorizeAsync(
        AuthorizeAccountCommand command, DateTimeOffset validTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Structural gate: a non-positive debit is not an authorization at all. Reject before any read or
        // append (the endpoint surfaces it as a 400), so this never becomes a business decline code.
        if (command.AmountCents <= 0)
        {
            throw new DomainRejectedException("authorize requires a positive amount in integer cents (ADR-PC-010).");
        }

        var hydrated = await runtime.LoadAsync(command.AccountId, ct);
        var position = hydrated.State;
        var accountRef = position.AccountRef;

        // Drain-before-decide (ADR-PC-033 read-your-writes): fold any pending holds into the read model
        // BEFORE reading the balance, so a prior authorize's earmark lowers the available balance this
        // decision sees — the safety that lets concurrent authorization run without locking.
        await drainer.DrainOnceAsync(ct);

        var availableBalanceCents = await balances.GetAvailableBalanceCentsAsync(accountRef, ct);
        var activeFreeze = await freezes.GetActiveFreezeAsync(command.AccountId, ct);
        var rules = ResolveRules(position);

        // The hold's lifecycle key, deterministic per authorization (ADR-PC-033): derived from the command
        // id so a replay would earmark the SAME hold at most once even if the command-dedup guard were
        // bypassed (the account_holds INSERT is ON CONFLICT (hold_id) DO NOTHING). No clock, no randomness.
        var holdId = $"hold-{command.CommandId:N}";
        var request = new AuthorizationRequest(
            command.AccountId, accountRef, holdId, new Money(command.AmountCents), command.ValueDate);

        var @event = CurrentAccountAuthorizeDecider.Decide(
            position, request, availableBalanceCents, rules, activeFreeze);

        var commitSequence = await runtime.AppendAsync(
            command.AccountId, hydrated.Version, [@event],
            Context(command.Actor, validTime, command.CommandId), ct);

        return @event switch
        {
            HoldPlaced hold => Authorized(command.AccountId, hold.HoldId, commitSequence),
            AuthorizationDeclined declined => Declined(command.AccountId, declined.DeclinedReason, commitSequence),
            _ => throw new InvalidOperationException($"Unexpected authorize event '{@event.GetType().Name}'."),
        };
    }

    /// <summary>
    /// Reproduce the ORIGINAL verdict of an already-applied authorize command (ADR-PC-029 idempotent
    /// replay). The command's one appended event sits at <paramref name="commitSequence"/> on the account
    /// stream (the append returns the new head, and a replayed authorize appends nothing more), so reading
    /// that single event and branching on its type recovers the exact verdict — the same <c>hold_id</c> on
    /// an approval, the same declined code on a refusal — without re-deciding against a since-changed
    /// balance. Called on the endpoint's replay branches (a command-log pre-check hit or a
    /// <see cref="DuplicateCommandException"/>).
    /// </summary>
    public async Task<AuthorizeResponse> ReconstructVerdictAsync(
        Guid accountId, long commitSequence, CancellationToken ct = default)
    {
        await foreach (var envelope in store.LoadAsync(accountId, commitSequence, ct))
        {
            // The store streams in sequence order, so the first event at-or-after the commit sequence IS
            // this command's appended event. Decode + unprotect exactly as the fold path does (both these
            // events are PII-free, so unprotect is a no-op, but mirroring the fold keeps it correct if a
            // field ever changes). Then branch on the event to rebuild the verdict.
            var decoded = await protector.UnprotectAsync(
                serializer.Decode(envelope.Payload, EventClrType(envelope.EventType)), ct);

            return decoded switch
            {
                HoldPlaced hold => Authorized(accountId, hold.HoldId, commitSequence),
                AuthorizationDeclined declined => Declined(accountId, declined.DeclinedReason, commitSequence),
                _ => throw new InvalidOperationException(
                    $"Event at authorize commit sequence {commitSequence} is '{envelope.EventType}', not an authorize outcome."),
            };
        }

        throw new InvalidOperationException(
            $"No event found at authorize commit sequence {commitSequence} on account {accountId}.");
    }

    // Resolve the stage-4 rule inputs the pure decider applies from the account's product config
    // (ADR-PC-037 §D5, ARRANGED_OVERDRAFT_PACK_BOUNDED): the family's own product-config store maps the
    // account's product_code to its arranged-overdraft headroom + per-transaction cap, which the decider
    // reads to authorize a within-limit overdraft and refuse an ultrapassagem (OVERDRAFT_LIMIT_EXCEEDED) or
    // an over-cap debit (LIMIT_EXCEEDED). A product code the store holds no config for resolves to the
    // zero-overdraft degenerate (no headroom, no ceiling — a debit past the balance is a plain
    // INSUFFICIENT_AVAILABLE_BALANCE), the conservative gate rather than refusing a live account over a
    // config gap. Velocity (daily/monthly) is declared in the config but not enforced here yet — it needs a
    // windowed-spend projection (a documented follow-up).
    private AuthorizationRules ResolveRules(AccountPosition position) =>
        productConfigs.Resolve(position.ProductCode)?.ToAuthorizationRules() ?? CurrentAccountProductConfig.None;

    private static Type EventClrType(string eventType) => eventType switch
    {
        "operations.HoldPlaced" => typeof(HoldPlaced),
        "current_account.AuthorizationDeclined" => typeof(AuthorizationDeclined),
        _ => throw new InvalidOperationException($"'{eventType}' is not an authorize-outcome event_type."),
    };

    private static AuthorizeResponse Authorized(Guid accountId, string holdId, long commitSequence) =>
        new(accountId, AuthorizeOutcomes.Authorized, holdId, DeclinedReason: null, commitSequence);

    private static AuthorizeResponse Declined(Guid accountId, string declinedReason, long commitSequence) =>
        new(accountId, AuthorizeOutcomes.Declined, HoldId: null, declinedReason, commitSequence);

    // The family / pack / schema pins ride the EventEnvelope via AppendContext (ADR-PC-009). The command
    // id is the ADR-PC-029 idempotency key that makes a replay return the original head, no second append.
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
