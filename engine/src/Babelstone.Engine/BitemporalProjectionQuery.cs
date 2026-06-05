using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// One row of a projection's belief history, deserialized — what a row asserted (the world-time
/// slice <see cref="ValidFrom"/>/<see cref="ValidTo"/>) and when we believed it (the belief-time
/// slice <see cref="RecordedAt"/>/<see cref="SupersededAt"/>). A <see langword="null"/>
/// <see cref="SupersededAt"/> is the currently-believed row (ADR-PC-002 §P2); a
/// <see langword="null"/> <see cref="ValidTo"/> is an open-ended world-time slice (current and
/// onward, ADR-PC-002 §P1). The typed sibling of <see cref="ProjectionRecord"/>, with the
/// structural payload decoded into <typeparamref name="TState"/> and the opaque PII envelope
/// dropped (a projection's structural state carries no PII, ADR-PC-004 §P2).
/// </summary>
public sealed record BeliefRow<TState>(
    TState State,
    long SourceSequence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    DateTimeOffset? SupersededAt);

/// <summary>
/// The typed bitemporal query helper (ADR-PC-002 §P3) — the small layer family-schema code uses
/// instead of hand-writing the four-column temporal join, which is Path A's named correctness
/// risk (ADR-PC-002 §S4 / Residual Risk 1). Generic over <typeparamref name="TState"/> and
/// family-agnostic by construction (it names no family; the host closes the state type), so the
/// spine stays under ENGINE_FAMILY_AGNOSTIC (ADR-PC-021 §P2). It mirrors
/// <see cref="ProjectionStore{TState}"/> / <see cref="SnapshotStore{TState}"/>: the bitemporal SQL
/// lives in the byte store (<see cref="IProjectionStorage"/>); this layer just deserializes the
/// structural payload into <typeparamref name="TState"/>.
/// </summary>
/// <remarks>
/// <para>
/// The three §P3 primitives compose the four time-dimensional capabilities
/// (event-store §2):
/// </para>
/// <list type="number">
/// <item>
/// <b>As-of (#1)</b> — "the state on valid-time X, as known at transaction-time Y" — is
/// <see cref="AsOfAsync"/> directly: both axes are bound.
/// </item>
/// <item>
/// <b>Belief-time history (#2)</b> — "how belief about this projection changed" — is
/// <see cref="HistoryOfAsync"/>: the full supersession line of a single projection, disavowed rows
/// included, in belief-time order. This is the projection's belief history, NOT the event-log audit
/// trail (event sequence + actor) — that is a separate read over the event table, outside this
/// projection helper.
/// </item>
/// <item>
/// <b>Counterfactual replay (#3)</b> — a replay with corrected inputs is a runtime fold, but the
/// belief line it produces is read with this helper: the disavowed belief is <c>AsOf(validTime,
/// knownAt = just-before-the-correction)</c> and the corrected belief is <c>AsOf(validTime,
/// knownAt = now)</c>. After a retroactive correction the two genuinely differ — the
/// forced-correction round-trip (ADR-PC-002 §P2, spike criterion #1).
/// </item>
/// <item>
/// <b>Forward projection (#4)</b> — "the state on a future date if no further events occur" — is
/// <see cref="AsOfAsync"/> with <paramref name="validTime"/> in the future and
/// <c>knownAt = now</c>: the open-ended current belief (<c>valid_to IS NULL</c>) covers all future
/// valid-times, so it is returned unchanged, or <see cref="CurrentBeliefAsync"/> when the future
/// date is "current and onward".
/// </item>
/// </list>
/// </remarks>
public sealed class BitemporalProjectionQuery<TState>(IProjectionStorage storage, IStateSerializer<TState> serializer)
    where TState : class
{
    /// <summary>
    /// Capability #1 (as-of) and the read side of #3/#4: the belief about <paramref name="validTime"/>
    /// held at <paramref name="knownAt"/>, or <see langword="null"/> if no belief covered that
    /// valid-time at that transaction-time. Vary <paramref name="knownAt"/> across a correction's
    /// transaction_time to read the disavowed vs. corrected belief (the counterfactual pair, §P2);
    /// pass a future <paramref name="validTime"/> with <paramref name="knownAt"/> = now for the
    /// forward projection.
    /// </summary>
    public async Task<BeliefRow<TState>?> AsOfAsync(
        Guid streamId, string kind, DateTimeOffset validTime, DateTimeOffset knownAt, CancellationToken ct = default)
    {
        var record = await storage.ReadAsOfAsync(streamId, kind, validTime, knownAt, ct);
        return record is null ? null : Map(record);
    }

    /// <summary>
    /// The currently-believed state for the pair (the row with <c>superseded_at IS NULL</c>), or
    /// <see langword="null"/> if none exists. The "as we know it now" leg of the counterfactual
    /// pair (#3) and the base of the forward projection (#4) when the target date is current-and-onward.
    /// </summary>
    public async Task<BeliefRow<TState>?> CurrentBeliefAsync(Guid streamId, string kind, CancellationToken ct = default)
    {
        var record = await storage.ReadCurrentBeliefAsync(streamId, kind, ct);
        return record is null ? null : Map(record);
    }

    /// <summary>
    /// Capability #2 (belief-time history): the full supersession line of this projection in
    /// belief-time order — every row a correction superseded plus the current belief — so a caller
    /// can see how the belief about this projection evolved. This is the projection's belief history,
    /// not the event-log audit trail (event sequence + actor), which is a separate event-table read.
    /// Empty when the pair was never projected.
    /// </summary>
    public async Task<IReadOnlyList<BeliefRow<TState>>> HistoryOfAsync(
        Guid streamId, string kind, CancellationToken ct = default)
    {
        var records = await storage.ReadHistoryOfAsync(streamId, kind, ct);
        var history = new List<BeliefRow<TState>>(records.Count);
        foreach (var record in records)
        {
            history.Add(Map(record));
        }

        return history;
    }

    private BeliefRow<TState> Map(ProjectionRecord record) =>
        new(
            State: serializer.Deserialize(record.StructuralPayload),
            SourceSequence: record.SourceSequence,
            ValidFrom: record.ValidFrom,
            ValidTo: record.ValidTo,
            RecordedAt: record.RecordedAt,
            SupersededAt: record.SupersededAt);
}
