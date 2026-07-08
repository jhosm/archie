using Babelstone.Engine;
using Babelstone.Packs;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure command shell for the projection-derived hold expiry (ADR-PC-037). In plain English: it
/// does the one write a due hold's expiry needs — load the account stream, append a <c>HoldExpired</c> release
/// fact idempotently — while the decision of WHICH holds are due stays upstream in the clock-owning ADR-PC-036
/// lifecycle-command driver (this shell never reads a clock or a projection to decide; it records the fact the
/// driver told it to).
/// </summary>
/// <remarks>
/// <para>
/// A SEPARATE service from <see cref="CurrentAccountLifecycleService"/> (the account open/dormant/close state
/// machine) and <see cref="CurrentAccountAuthorizeService"/> (the money-mover that PLACES holds): this one only
/// RELEASES a hold, moving no money (a HoldExpired is posting-free, ADR-PC-037). It depends only on the
/// generic engine runtime and the pinned pack — the dependency arrow is family→engine, never the reverse
/// (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// <para>
/// <b>No family-state legality gate.</b> A hold is a spine-owned fact (ADR-PC-033), orthogonal to the account's
/// own lifecycle — its events fold as no-ops on <see cref="AccountPosition"/> — so there is no decider or
/// transition table to consult (unlike the lifecycle commands). The append lands on the account stream; the
/// <c>AccountHoldProjector</c> transitions the account_holds row out of the ACTIVE set, or surfaces a
/// reconciliation signal if the hold already left it (a late/duplicate release — never a double-release).
/// </para>
/// <para>
/// <b>Idempotent on the command id (ADR-PC-029 slot 4).</b> The append threads the driver's canonical
/// number-pinned dispatch id, so an at-least-once re-POST returns the ORIGINAL head with no second HoldExpired
/// — the engine's command_dedup is the authoritative backstop under the driver's own dispatch ledger.
/// </para>
/// </remarks>
public sealed class CurrentAccountHoldExpiryService(AggregateRuntime<AccountPosition> runtime, VerifiedPack pack)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    /// <summary>
    /// Append the <c>HoldExpired</c> release fact for the command's hold and return the new stream head. Loads
    /// the account to resolve its opaque <c>account_ref</c> and the expected version, then appends idempotently
    /// on the command id. <paramref name="validTime"/> is the envelope's valid time (the host stamps it from the
    /// hold's economic value-date); the command's own <see cref="ExpireHoldCommand.ValueDate"/> is the domain
    /// value-date carried onto the event. Propagates <c>ConcurrencyException</c> /
    /// <c>DuplicateCommandException</c> for the endpoint to map.
    /// </summary>
    public async Task<long> ExpireHoldAsync(
        ExpireHoldCommand command, DateTimeOffset validTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hydrated = await runtime.LoadAsync(command.AccountId, ct);

        // AccountRef is the account's opaque spine key (ADR-PC-033), read off the folded position exactly as
        // the authorize shell does — never reconstructed. The HoldExpired carries it so the projector keys the
        // release against the same account_ref the placement used.
        var accountRef = hydrated.State.AccountRef;

        var @event = new HoldExpired(command.AccountId, command.HoldId, accountRef, command.ValueDate);

        return await runtime.AppendAsync(
            command.AccountId, hydrated.Version, [@event],
            Context(command.Actor, validTime, command.CommandId), ct);
    }

    // The family / pack / schema pins ride the EventEnvelope via AppendContext, never on the event record
    // (ADR-PC-009). commandId is the ADR-PC-029 idempotency key that makes a replay return the original head.
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
