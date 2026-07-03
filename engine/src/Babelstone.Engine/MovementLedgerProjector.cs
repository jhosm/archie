using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The spine-owned generic projector that folds the <c>account_ref</c>-keyed movement ledger
/// (ADR-PC-032 §A1 / §95 read side). In plain English: as money-moving events are appended, this turns
/// each <see cref="Movement"/> they carry into a line of an account statement, so the engine can answer
/// "what moved against this account, and what is its balance" without re-reading and re-decoding every
/// stream. It is the read counterpart of the append-first write atom the ADR fixed — the projector the
/// §Decision named but deferred until the discoverability seam (<see cref="IMovementBearing"/>) existed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021 §P2).</b> It pattern-matches a
/// replayed event on <see cref="IMovementBearing"/> — a SPINE interface — and folds <c>evt.Movements</c>,
/// the family-agnostic <see cref="Movement"/> atom. It reads NEITHER a family-typed event shape NOR the
/// opaque payload's internal structure (it receives the already-decoded <see cref="DomainEvent"/>), so it
/// names no family. An event that does not implement the seam contributes nothing — exactly the §A2
/// posture (only a conforming event is folded).
/// </para>
/// <para>
/// <b>Deterministic and idempotent (ADR-PC-032 §A5, ADR-PC-010 §P5).</b> The fold is pure data-shuffling:
/// each movement becomes one <see cref="MovementLedgerEntry"/> keyed by the producing event's identity
/// (<c>stream_id</c> + <c>sequence_number</c> + the movement's index within the carrier). No clock, no
/// randomness — every column is event-derived. The store's append is idempotent on that identity, so a
/// re-delivered event (the at-least-once drainer after a crash) re-applies the same lines as a no-op, and
/// a cold rebuild reproduces the same balance (the balance is an order-insensitive signed sum).
/// </para>
/// <para>
/// Unlike <see cref="ProjectionRunner{TState}"/> / <see cref="ReadModelRunner{TState,TRow}"/>, this
/// projector is NOT keyed per stream or per family: a single <c>account_ref</c> can receive movements
/// from many streams across families (a card-capture credit, then loan-installment debits), so the ledger
/// is account-keyed and cross-family, and the projector folds the decoded event regardless of which
/// family produced it. That is why it is a spine component the host drives directly rather than an
/// <see cref="IProjectionRunner"/> on the per-family drainer.
/// </para>
/// </remarks>
public sealed class MovementLedgerProjector(IMovementLedgerStore store) : ISpineProjector
{
    /// <summary>
    /// Fold one decoded event into the account-keyed ledger. A no-op unless <paramref name="event"/>
    /// implements <see cref="IMovementBearing"/> and carries at least one movement; otherwise each carried
    /// <see cref="Movement"/> is appended as an idempotent ledger line keyed by
    /// <paramref name="streamId"/> + <paramref name="sequenceNumber"/> + its carrier index.
    /// </summary>
    /// <param name="streamId">The stream the event was appended to (part of the idempotency key).</param>
    /// <param name="sequenceNumber">The event's per-stream sequence (part of the idempotency key).</param>
    /// <param name="event">The already-decoded domain event; folded only if it is Movement-bearing.</param>
    public async Task ApplyAsync(
        Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Only a Movement-bearing event contributes ledger lines (ADR-PC-032 §A1/§A2): the spine reads the
        // seam, never the family-typed shape. A non-bearing or empty-carrier event folds to nothing.
        if (@event is not IMovementBearing bearing)
        {
            return;
        }

        var movements = bearing.Movements;
        if (movements.Count == 0)
        {
            return;
        }

        var entries = new List<MovementLedgerEntry>(movements.Count);
        for (var index = 0; index < movements.Count; index++)
        {
            var movement = movements[index];
            entries.Add(new MovementLedgerEntry(
                AccountRef: movement.AccountRef,
                StreamId: streamId,
                SequenceNumber: sequenceNumber,
                // The carrier index disambiguates a multi-movement event's legs (ADR-PC-032 §A3): a
                // renewal's rollover-debit (0) and interest-credit (1) become two distinct ledger lines
                // under one (stream, sequence), so the idempotency key stays unique per movement.
                MovementIndex: index,
                Direction: movement.Direction.ToString(),
                AmountCents: movement.Amount.Cents,
                ValueDate: movement.ValueDate,
                Operation: movement.Operation.ToString(),
                Origin: movement.Origin.ToString(),
                CommandId: movement.CommandId));
        }

        await store.AppendAsync(entries, ct);
    }

    /// <summary>Truncate the ledger for a rebuild (truncate-then-refold, ADR-PC-032).</summary>
    public Task ResetForRebuildAsync(CancellationToken ct = default) => store.TruncateAsync(ct);
}
