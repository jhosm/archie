using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Packs;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure command shell for the settlement CREDIT-receive path (ADR-PC-043):
/// it does the one atomic read-modify-write a landing credit needs — load the account's OWN stream, ask the
/// pure <see cref="CurrentAccountCreditAdmissionDecider"/> whether the credit may land, and append the
/// produced events at the LOADED expectedVersion under per-stream OCC — while the admission DECISION stays
/// clock-free and Docker-testable. In plain English: this is the family's first way to RECEIVE money, and it
/// refuses a credit into a closed/erased account by construction, because it decides admissibility on the
/// account's own stream before anything is recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Closed by construction (ADR-PC-043; the own-stream OCC seam is integration-pinned, its fitness anchor still planned).</b>
/// The load→admit→append-at-loaded-version cycle is serialized against a concurrent Close/Erase by the SAME
/// per-stream OCC seam (the stale-head check) that serializes authorize against concurrent debits: a
/// concurrent close is either seen on reload (→ reject) or loses the OCC race (→ <see cref="ConcurrencyException"/>
/// → reload-and-redecide → reject). So a credit-receive racing a CloseAccount at the same version yields
/// exactly ONE commit and an ACCOUNT_CLOSED reject on the retry — no credit ever folds into a Closed account.
/// </para>
/// <para>
/// <b>Body-derived append key, not the HTTP Idempotency-Key (ADR-PC-043 — the scoped ADR-PC-029
/// inversion).</b> The append <c>command_id</c> is derived deterministically from the body's economic-intent
/// reference (<see cref="ReceiveCreditCommand.CommandId"/>, computed by the endpoint from the intent string),
/// so a saga reissue with a byte-identical body but a fresh dispatch message_id collapses at command_dedup to
/// ONE append. The credit path rests SOLELY on command_dedup (single-guarded), so this is load-bearing.
/// </para>
/// <para>
/// It depends only on generic engine ports (<see cref="AggregateRuntime{TState}"/>) plus the pinned pack —
/// the dependency arrow is family→engine, never the reverse (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// </remarks>
public sealed class CurrentAccountCreditReceiveService(AggregateRuntime<AccountPosition> runtime, VerifiedPack pack)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    // The bound on reload-and-redecide retries under OCC contention: a handful is ample — a credit-receive
    // only ever races a lifecycle transition on the SAME stream (a close/erase/dormancy), a bounded set, so a
    // livelock is not possible; the cap is a fail-loud backstop against an unforeseen hot loop, never the
    // expected path (the common case commits on the first attempt).
    private const int MaxOccRetries = 8;

    /// <summary>
    /// Land a settlement credit and return the new stream head. Loads the account, runs the pure admission
    /// decider, and appends the produced events (an <see cref="AccountCredited"/>, or a reactivate-then-credit
    /// batch on a Dormant account) at the loaded version, idempotently on the intent-derived command id. A
    /// concurrent lifecycle change that loses the OCC race is reloaded and re-decided (so a since-closed
    /// account now rejects). Throws <see cref="DomainRejectedException"/> when the account cannot receive the
    /// credit (Closed → ACCOUNT_CLOSED, Erased → ACCOUNT_ERASED), and propagates
    /// <see cref="DuplicateCommandException"/> for the endpoint to map (the idempotent replay).
    /// </summary>
    public async Task<long> ReceiveCreditAsync(
        ReceiveCreditCommand command, DateTimeOffset validTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Reload-and-redecide under per-stream OCC (ADR-PC-029 / ADR-PC-043): the admission decision reads
        // lifecycle from the synchronous own-stream fold and appends on the SAME stream at the SAME loaded
        // version, so a concurrent Close/Erase that commits first makes this append lose the stale-head race
        // (ConcurrencyException) — we reload and re-run admission, which now sees the closed account and
        // rejects. A DuplicateCommandException (the intent-derived command id already applied) is NOT caught
        // here — it propagates so the endpoint replays the original outcome (idempotent, ADR-PC-029).
        for (var attempt = 0; ; attempt++)
        {
            var hydrated = await runtime.LoadAsync(command.AccountId, ct);

            // Admission is decided UPSTREAM of the generic movement_ledger fold (ADR-PC-043
            // credit-admission gate): the decider throws DomainRejectedException for a Closed/Erased account
            // BEFORE any append, so the fold only ever folds an ADMITTED credit — never a lifecycle-blind one.
            var events = CurrentAccountCreditAdmissionDecider.Decide(hydrated.State, command);

            try
            {
                return await runtime.AppendAsync(
                    command.AccountId, hydrated.Version, events,
                    Context(command.Actor, validTime, command.CommandId), ct);
            }
            catch (ConcurrencyException) when (attempt < MaxOccRetries)
            {
                // A concurrent write to this stream won the OCC race between our load and append — reload and
                // re-decide (a since-Close now rejects; a since-credit is a different intent and still admits).
            }
        }
    }

    // The family / pack / schema pins ride the EventEnvelope via AppendContext, never on the event record
    // (ADR-PC-009). commandId is the ADR-PC-043 intent-derived idempotency key (NOT the HTTP Idempotency-Key).
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
