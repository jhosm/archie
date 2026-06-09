using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The deterministic fold context handed to a read-model mapper: the folded
/// <typeparamref name="TState"/> plus the producing event's structural identity. Everything here is
/// event-derived (the stream id, the per-stream sequence, the event's transaction_time) — never the
/// wall clock — so the mapper a family supplies stays a pure function and a cold rebuild reproduces
/// the read-model row byte-for-byte (ADR-PC-010 §P5).
/// </summary>
public sealed record ReadModelFold<TState>(
    TState State,
    Guid StreamId,
    long SourceSequence,
    DateTimeOffset TransactionTime);

/// <summary>
/// Materialises a family's events into the denormalized CQRS read model (ADR-IC-005) by folding the
/// SAME pure dispatch the aggregate runtime and the bitemporal projection use
/// (<see cref="HandlerRegistry"/> + <see cref="IDispatchableHandler.ApplyBoxed"/>), then mapping the
/// folded <typeparamref name="TState"/> to a family-owned <typeparamref name="TRow"/> via a
/// family-supplied <paramref name="map"/> function and UPSERTing it through
/// <see cref="IReadModelStore{TRow}"/>.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IProjectionRunner"/>, so it rides the existing async <see cref="ProjectionDrainer"/>
/// / relay unchanged — the read model is just another projection kind. Generic over
/// <typeparamref name="TState"/> AND the family's row type <typeparamref name="TRow"/>, and
/// family-agnostic by construction: the engine spine knows the row only through
/// <see cref="IReadModelRow"/> (stream id + the §P2 sequence guard + the opaque <see cref="IReadModelRow.Detail"/>
/// body), never a deposit column. The family closes both type parameters and supplies the
/// <paramref name="map"/>, so the spine stays under ENGINE_FAMILY_AGNOSTIC (ADR-PC-021 §D2/§P2). The
/// mirror of <see cref="ProjectionRunner{TState}"/>, but writing the flat read-model row instead of a
/// bitemporal belief row.
/// </para>
/// <para>
/// Idempotency under at-least-once delivery is the ADR-IC-005 §P2 monotonicity guard inside
/// <see cref="IReadModelStore{TRow}.UpsertAsync"/>: a re-delivered event whose sequence is at or below
/// the stored row's is dropped by the UPSERT's WHERE, so re-applying an already-folded event is a
/// no-op. The mapper MUST be pure (no clock, no I/O, no randomness) — it receives the event's
/// transaction_time as data, so the rebuild stays deterministic.
/// </para>
/// </remarks>
public sealed class ReadModelRunner<TState, TRow>(
    string kind,
    string family,
    ProjectionMode mode,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    Func<TState> seed,
    Func<ReadModelFold<TState>, TRow> map,
    IReadModelStore<TRow> store) : IProjectionRunner
    where TState : class
    where TRow : IReadModelRow
{
    public string Kind => kind;
    public string Family => family;
    public ProjectionMode Mode => mode;

    public async Task ApplyAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        // A read model may not fold every family event type; an unhandled type leaves the row
        // unchanged (the runner skips it), exactly like ProjectionRunner.
        if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
        {
            return;
        }

        // Re-fold from the current read-model row's body when present, so the accumulating fold
        // (state.X + event.Y) continues from where the last event left off rather than from seed.
        // The §P2 guard in the store makes a re-delivered event a no-op, so reading-then-folding is
        // safe under at-least-once.
        var existing = await store.GetAsync(envelope.StreamId, ct);
        if (existing is not null && existing.LastSequence >= envelope.SequenceNumber)
        {
            return;
        }

        var state = existing is null
            ? seed()
            : DeserializeDetail(existing.Detail);
        var @event = serializer.Decode(envelope.Payload, registration.PayloadType);
        var next = (TState)registration.Handler.ApplyBoxed(state, @event).NewState;

        var fold = new ReadModelFold<TState>(
            State: next,
            StreamId: envelope.StreamId,
            SourceSequence: envelope.SequenceNumber,
            TransactionTime: envelope.TransactionTime);

        await store.UpsertAsync(map(fold), ct);
    }

    // The read-model body is the same structural state, serialized; re-hydrating it lets the fold
    // continue across events without re-reading the whole stream. JSON via the family's serializer
    // is deterministic (declaration-order), the same codec the bitemporal projection uses.
    private static TState DeserializeDetail(ReadOnlyMemory<byte> detail) =>
        new JsonStateSerializer<TState>().Deserialize(detail);

    public Task SupersedeAllForRebuildAsync(DateTimeOffset supersededAt, CancellationToken ct = default)
        // The read model is rebuilt by TRUNCATE + re-fold (ADR-IC-005 §P5), not by supersession —
        // there is no belief history to preserve (it is a flat cache, not the bitemporal store). The
        // drainer's RebuildAsync calls this before resetting checkpoints and re-folding from 0.
        // ASSUMPTION: one read-model runner owns the underlying table, so the store's TRUNCATE is
        // safe per-kind. The drainer's RebuildAsync is per-runner (it resets only this runner.Kind),
        // but TruncateAsync clears the whole table; if a second read-model kind ever shared the
        // table, the truncate must be scoped by a kind discriminator (see the family-owned store).
        => store.TruncateAsync(ct);
}
