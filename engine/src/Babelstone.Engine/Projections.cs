using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// How a projection is kept up to date (two-modes §5.4, ADR-PC-002). Declared PER
/// PROJECTION by the family (not hardcoded in the engine); the engine dispatches accordingly.
/// </summary>
public enum ProjectionMode
{
    /// <summary>
    /// Updated by a separate background drainer, eventually consistent within a stated lag.
    /// The v1 default for every projection.
    /// </summary>
    Async,

    /// <summary>
    /// Driven immediately after the event commits, within a bounded budget (the post-commit
    /// hook). A projection failure NEVER rolls back the event commit — "the event is true
    /// regardless" (two-modes §5.4). Built and selectable in v1, exercised in production at v4.
    /// </summary>
    Sync,
}

/// <summary>
/// The deterministic bitemporal stamps for one projection write (ADR-PC-002). All come
/// from the source event, never the wall clock, so a cold rebuild reproduces them exactly
/// (ADR-PC-010): <see cref="RecordedAt"/> = the event's transaction_time,
/// <see cref="ValidFrom"/> = the event's valid_time. A position projection's world-time slice
/// is open-ended, so <see cref="ValidTo"/> is normally <see langword="null"/>.
/// </summary>
public sealed record ProjectionTemporalContext(
    DateTimeOffset RecordedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

/// <summary>
/// One projection's runtime: folds events of a single <see cref="Kind"/> into bitemporal
/// rows. Non-generic so a registry can hold projections of different state types; the concrete
/// <see cref="ProjectionRunner{TState}"/> closes the state type.
/// </summary>
public interface IProjectionRunner
{
    /// <summary>Family-prefixed discriminator, e.g. <c>term_deposit.deposit_position</c>.</summary>
    string Kind { get; }

    /// <summary>The family whose event streams feed this projection (e.g. <c>term_deposit</c>).</summary>
    string Family { get; }

    /// <summary>Sync vs async (two-modes §5.4).</summary>
    ProjectionMode Mode { get; }

    /// <summary>
    /// Folds one event into the current belief, idempotently: events this projection does not
    /// handle are skipped, and an event whose <c>sequence_number</c> is at or below the current
    /// belief's <c>source_sequence</c> is skipped (at-least-once safe).
    /// </summary>
    Task ApplyAsync(EventEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Rebuild entry point (ADR-PC-002): supersede every current belief for this kind
    /// before a cold re-fold. The drainer then re-reads from sequence 0.
    /// </summary>
    Task SupersedeAllForRebuildAsync(DateTimeOffset supersededAt, CancellationToken ct = default);
}

/// <summary>
/// Resolves projection runners by kind, built once from the loaded <see cref="IProjectionModule"/>s
/// (mirrors <see cref="HandlerRegistry"/>). Rejects duplicate kinds at load.
/// </summary>
public sealed class ProjectionRegistry
{
    private readonly IReadOnlyDictionary<string, IProjectionRunner> _byKind;

    public ProjectionRegistry(IEnumerable<IProjectionRunner> runners)
    {
        var byKind = new Dictionary<string, IProjectionRunner>(StringComparer.Ordinal);
        foreach (var runner in runners)
        {
            if (!byKind.TryAdd(runner.Kind, runner))
            {
                throw new InvalidOperationException(
                    $"Duplicate projection kind '{runner.Kind}'. Each projection_kind must be unique across families.");
            }
        }

        _byKind = byKind;
    }

    public IReadOnlyCollection<IProjectionRunner> Runners => (IReadOnlyCollection<IProjectionRunner>)_byKind.Values;

    public IEnumerable<IProjectionRunner> AsyncRunners =>
        _byKind.Values.Where(r => r.Mode == ProjectionMode.Async);

    public IEnumerable<IProjectionRunner> SyncRunnersForFamily(string family) =>
        _byKind.Values.Where(r => r.Mode == ProjectionMode.Sync && string.Equals(r.Family, family, StringComparison.Ordinal));

    public bool TryResolve(string kind, out IProjectionRunner runner) => _byKind.TryGetValue(kind, out runner!);
}

/// <summary>
/// The shared infrastructure a family needs to build its projection runners — supplied by the
/// host (which owns the database + codec), so the family stays infra-free. The family knows its
/// state types, seeds, and folds (it builds its own handler registry); the host knows storage and
/// serialization.
/// </summary>
public sealed record ProjectionInfra(IProjectionStorage Storage, IEventSerializer EventSerializer);

/// <summary>
/// The shared infrastructure a family needs to build its CQRS read-model runner (ADR-IC-005),
/// supplied by the host. The sibling of <see cref="ProjectionInfra"/> for the denormalized read
/// side: the host owns the family-typed <see cref="IReadModelStore{TRow}"/> (the family's own
/// read-model table) and the codec; the family supplies the fold + the state→row mapper. Generic
/// over the family's row type <typeparamref name="TRow"/> so the engine spine never names a
/// deposit's read-model shape (ADR-PC-021 — the family closes the type). Kept separate from
/// <see cref="ProjectionInfra"/> because the bitemporal <c>projections</c> store and the flat read
/// model are distinct surfaces with distinct rebuild disciplines (supersede-all vs
/// truncate-and-refold).
/// </summary>
public sealed record ReadModelInfra<TRow>(IReadModelStore<TRow> Store, IEventSerializer EventSerializer)
    where TRow : IReadModelRow;

/// <summary>
/// A family's projection declarations (two-modes §5.4: "declared in the family schema, not
/// hardcoded in the engine"). Discovered alongside <see cref="IFamilyModule"/>; optional — a
/// family with no projections simply does not export one. Kept separate from
/// <see cref="IFamilyModule"/> so event→state handlers and event-stream→row projections stay
/// orthogonal concerns.
/// </summary>
public interface IProjectionModule
{
    string FamilyName { get; }

    /// <summary>Builds this family's runners, given host-supplied storage + codec.</summary>
    IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra);
}

/// <summary>
/// The sync-mode post-commit hook (two-modes §5.4). The durable runtime invokes it AFTER an
/// append commits; it drives the family's sync projections within a bounded budget and NEVER
/// throws — a projection failure does not roll back the committed event.
/// </summary>
public interface IPostCommitProjector
{
    Task NotifyAppendedAsync(string family, CancellationToken ct = default);
}

/// <summary>The default no-op post-commit projector (v1: every projection is async).</summary>
public sealed class NoOpPostCommitProjector : IPostCommitProjector
{
    public Task NotifyAppendedAsync(string family, CancellationToken ct = default) => Task.CompletedTask;
}
