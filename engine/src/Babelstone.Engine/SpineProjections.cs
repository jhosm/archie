using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// A spine-owned, CROSS-FAMILY projector the host drives directly (ADR-PC-032 / ADR-PC-033):
/// it folds already-decoded events, keyed by something that spans streams and families (an
/// <c>account_ref</c>), which is exactly why it is NOT an <see cref="IProjectionRunner"/> on the
/// per-family/per-stream <see cref="ProjectionDrainer"/>. The two v1 implementations are the
/// account-keyed <see cref="MovementLedgerProjector"/> and the hold-lifecycle
/// <see cref="AccountHoldProjector"/>.
/// </summary>
/// <remarks>
/// The contract the <see cref="SpineProjectionDrainer"/> relies on: <see cref="ApplyAsync"/> MUST be
/// idempotent on the producing event's identity (at-least-once re-delivery after a crash between
/// apply and checkpoint re-applies as a no-op), and <see cref="ResetForRebuildAsync"/> MUST clear
/// the projector's derived state so a truncate-then-refold reproduces it deterministically
/// (ACCOUNT_BALANCE_IS_A_FOLD).
/// </remarks>
public interface ISpineProjector
{
    /// <summary>Fold one decoded event; a no-op for events the projector does not read.</summary>
    Task ApplyAsync(Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default);

    /// <summary>Clear the derived state for a rebuild (the truncate half of truncate-then-refold).</summary>
    Task ResetForRebuildAsync(CancellationToken ct = default);
}

/// <summary>
/// The production caller that feeds appended/replayed events to the spine projectors — the live
/// projection drive of the ADR-PC-032 read side. In plain English: the movement ledger and the
/// active-hold set only fill up if something reads the event log and hands each decoded event to
/// their <c>ApplyAsync</c>; this is that something. It is registered ONCE at the composition root as a spine singleton — NOT on the
/// per-family <see cref="ProjectionDrainer"/> — because its projections are account-keyed and
/// cross-family (one <c>account_ref</c> receives movements from many streams across families).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-stream tail draining, like <see cref="ProjectionDrainer"/>.</b> The events table carries
/// no cluster-wide total order, so the drive iterates every registered family's streams and folds
/// each stream's tail from the shared <see cref="CheckpointKind"/> checkpoint forward,
/// project-THEN-checkpoint. A crash between apply and checkpoint re-delivers the tail — safe
/// because every <see cref="ISpineProjector"/> apply is idempotent on the producing event's
/// identity (the movement ledger's <c>ON CONFLICT DO NOTHING</c>, the hold store's
/// active-only transitions), so at-least-once re-apply stays a no-op.
/// </para>
/// <para>
/// <b>Family-agnostic decode through the family's own bindings (ADR-PC-021).</b> The spine cannot
/// parse an opaque payload, and one merged registry is impossible (every family legitimately binds
/// the same cross-cutting <c>operations.*</c> event types). So the drive decodes each family's
/// streams through THAT family's <see cref="IFamilyModule.Handlers"/> bindings — spine types the
/// modules already export — with the store codec, structural-only (no PII unprotect: the
/// projectors read only the <see cref="IMovementBearing"/> seam and the hold facts, never a PII
/// field — the same posture as <c>ProjectionRunner</c>). An event type missing from its own
/// family's bindings fails LOUD, the same fail-closed stance as the aggregate fold.
/// </para>
/// <para>
/// <b>One writer at a time.</b> <see cref="DrainOnceAsync"/> and <see cref="RebuildAsync"/>
/// serialize on an in-process gate: the drainer is the composition root's ONE singleton and the
/// only writer of these read models, so the gate is the mutual exclusion that keeps a live relay
/// pass from writing a stale checkpoint into the middle of a rebuild (a permanent fold hole). A
/// second engine process would need a cross-process lock — no current deployment runs one; adding
/// one is a deliberate change to this type, not a config knob.
/// </para>
/// <para>
/// <b>Rebuild is truncate-then-refold and READ-deterministic.</b> <see cref="RebuildAsync"/>
/// resets the shared checkpoints, clears every projector's derived state, then re-drains from
/// sequence 0; every projected column is event-derived (no clock, no randomness), so the rebuilt
/// tables answer every read identically to the incrementally-built ones. Read-identical, not
/// byte-identical: <c>movement_ledger.row_id</c> is a BIGSERIAL surrogate a rebuild re-mints, and
/// every read excludes it — the same surrogate-vs-read split the bitemporal projection store
/// draws (ACCOUNT_BALANCE_IS_A_FOLD).
/// </para>
/// </remarks>
public sealed class SpineProjectionDrainer
{
    /// <summary>
    /// The shared checkpoint discriminator for the spine account projections (the
    /// <c>projection_checkpoints</c> kind, migration 0011). ONE kind for the whole drive: each
    /// stream's tail is decoded once and fanned to every projector, so the projectors advance
    /// together and a rebuild resets one high-water set.
    /// </summary>
    public const string CheckpointKind = "spine.account_ledger";

    private readonly IEventStore _eventStore;
    private readonly IProjectionCheckpointStore _checkpoints;
    private readonly IEventSerializer _serializer;
    private readonly IReadOnlyList<ISpineProjector> _projectors;
    private readonly TimeProvider _clock;

    // The one-writer gate (see class remarks): serializes drain passes and rebuilds on the
    // composition root's singleton drainer, so a relay pass can never interleave with a rebuild.
    private readonly SemaphoreSlim _writerGate = new(1, 1);

    // family name → that family's event-type bindings, materialized once: the drive iterates
    // ReadStreamIdsAsync per family and decodes with the matching registry (see class remarks for
    // why per-family, not merged).
    private readonly IReadOnlyList<(string Family, HandlerRegistry Registry)> _families;

    public SpineProjectionDrainer(
        IEventStore eventStore,
        IProjectionCheckpointStore checkpoints,
        IEventSerializer serializer,
        IReadOnlyList<IFamilyModule> familyModules,
        IReadOnlyList<ISpineProjector> projectors,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(familyModules);
        ArgumentNullException.ThrowIfNull(projectors);
        _eventStore = eventStore;
        _checkpoints = checkpoints;
        _serializer = serializer;
        _projectors = projectors;
        _clock = clock;
        _families = familyModules
            .Select(module => (module.FamilyName, new HandlerRegistry(module.Handlers)))
            .ToList();
    }

    /// <summary>
    /// Drains one full pass across every registered family's streams, feeding each decoded tail
    /// event to every projector. Returns the number of events folded (0 = fully caught up).
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        await _writerGate.WaitAsync(ct);
        try
        {
            return await DrainAllUnderGateAsync(ct);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private async Task<int> DrainAllUnderGateAsync(CancellationToken ct)
    {
        var folded = 0;
        foreach (var (family, registry) in _families)
        {
            var streamIds = await _eventStore.ReadStreamIdsAsync(family, ct);
            foreach (var streamId in streamIds)
            {
                var checkpoint = await _checkpoints.ReadAsync(CheckpointKind, streamId, ct);
                var lastSequence = checkpoint?.LastSequenceNumber ?? -1;
                var advancedTo = lastSequence;

                await foreach (var envelope in _eventStore.LoadAsync(streamId, lastSequence + 1, ct))
                {
                    var @event = Decode(registry, envelope);
                    foreach (var projector in _projectors)
                    {
                        await projector.ApplyAsync(streamId, envelope.SequenceNumber, @event, ct);
                    }

                    advancedTo = envelope.SequenceNumber;
                    folded++;
                }

                if (advancedTo > lastSequence)
                {
                    // Project-then-checkpoint: the high-water mark is informational (wall clock is
                    // fine — it is never read by rebuild logic), and losing it merely re-applies an
                    // idempotent tail.
                    await _checkpoints.WriteAsync(
                        new ProjectionCheckpointRecord(CheckpointKind, streamId, advancedTo, _clock.GetUtcNow()), ct);
                }
            }
        }

        return folded;
    }

    /// <summary>
    /// Cold rebuild (truncate-then-refold): reset the shared checkpoints, clear every projector's
    /// derived state, and re-fold every stream from sequence 0 — all under the one-writer gate, so
    /// a live relay pass cannot interleave. The rebuilt read models answer every read identically
    /// to the incrementally-built ones (read-identical — see the class remarks;
    /// ACCOUNT_BALANCE_IS_A_FOLD).
    /// </summary>
    public async Task<int> RebuildAsync(CancellationToken ct = default)
    {
        await _writerGate.WaitAsync(ct);
        try
        {
            // Checkpoints FIRST, then truncate: a crash between the two leaves populated tables
            // under reset checkpoints, and the next drain re-folds from 0 into idempotent applies —
            // self-healing. The inverse order would strand EMPTY tables under caught-up checkpoints
            // (an under-stated hold set inflating the available balance) until an operator noticed.
            await _checkpoints.ResetAsync(CheckpointKind, ct);
            foreach (var projector in _projectors)
            {
                await projector.ResetForRebuildAsync(ct);
            }

            return await DrainAllUnderGateAsync(ct);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    // Structural-only decode through the family's own binding (no PII unprotect — the projectors
    // read only structural facts; the same posture as ProjectionRunner). Fail-loud on an event
    // type the family's own module does not bind: silence here would fold zero movements off a
    // conforming event — the correctness hole the seam exists to close (ADR-PC-032).
    private DomainEvent Decode(HandlerRegistry registry, EventEnvelope envelope)
    {
        if (!registry.TryResolveByEventType(envelope.EventType, out var registration))
        {
            throw new InvalidOperationException(
                $"No handler registered for event type '{envelope.EventType}' in family '{envelope.Family}' "
                + "— the spine account-projection drive cannot decode it.");
        }

        return _serializer.Decode(envelope.Payload, registration.PayloadType);
    }
}
