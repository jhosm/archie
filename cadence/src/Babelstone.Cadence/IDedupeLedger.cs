namespace Babelstone.Cadence;

/// <summary>
/// The "already acted on this one" memory a cadence pass dedupes against (ADR-PC-036 §Decision 2 + ADR-IC-019 /
/// ADR-PC-025 slot 4) — generic. In plain terms: every unit of work a pass produces carries a stable composite
/// id (<see cref="CompositeId"/>); reserving that id once admits the work, and a second attempt to reserve the
/// same id is the idempotent replay the contract mandates and returns <see langword="false"/>. The notification
/// scheduler dedupes a <c>notification_id</c> so a customer is never double-notified; the ADR-PC-036
/// lifecycle-command driver dedupes a command-occurrence id so a due command is never double-fired. The
/// abstraction lets a v1 consumer prove the invariant against the in-memory default while a later one backs it
/// with a durable, crash-surviving store.
/// </summary>
public interface IDedupeLedger
{
    /// <summary>Reserve <paramref name="id"/> if it is new to this ledger. Returns <see langword="true"/> the
    /// FIRST time an id is seen (the work is new and should be acted on), and <see langword="false"/> on every
    /// subsequent attempt (the idempotent replay — already acted on).</summary>
    Task<bool> TryReserveAsync(Guid id, CancellationToken ct = default);
}
