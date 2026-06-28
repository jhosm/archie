using Babelstone.Cadence;

namespace Babelstone.Notification;

/// <summary>
/// The "already raised this one" memory the core dedupes against (ADR-PC-025 slot 4) — family-agnostic. The
/// notification estate's named face of the shared <see cref="IDedupeLedger"/> (ADR-PC-036 §Decision 2 +
/// ADR-IC-019 mechanism reuse): a composite <c>notification_id</c> is reserved once; a second attempt to
/// reserve the same id is the idempotent replay the contract mandates and returns <see langword="false"/>.
/// Naming it distinctly keeps the notification host's DI registration explicit and lets the emission child
/// (bd babelstone-60n8.3) back it with a durable store while v1 proves the invariant against the in-memory
/// default; the reservation contract itself is the generic <see cref="IDedupeLedger"/>.
/// </summary>
public interface INotificationDedupeLedger : IDedupeLedger;

/// <summary>
/// The in-memory <see cref="INotificationDedupeLedger"/> v1 uses to prove the slot-4 idempotency invariant —
/// the notification estate's named face of the shared <see cref="InMemoryDedupeLedger"/> (ADR-PC-036
/// §Decision 2 + ADR-IC-019 mechanism reuse), inheriting its thread-safe reservation logic unchanged. A
/// durable, crash-surviving ledger is the emission child's concern (bd babelstone-60n8.3); within one process
/// lifetime this gives the "re-runs don't re-notify" guarantee the acceptance criteria require.
/// </summary>
public sealed class InMemoryNotificationDedupeLedger : InMemoryDedupeLedger, INotificationDedupeLedger;
