using System.Security.Cryptography;
using System.Text;
using Babelstone.EventStore;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The precondition verdict an operation's adapter returns for one target (ADR-PC-035 §P4 step 1):
/// either the per-instance event should be applied, or this instance is declined and recorded
/// <c>SKIPPED</c>. The reason is an operational-tier note for logs — deliberately NOT persisted
/// (migration 0018 reserves <c>failure_reason</c> for <c>FAILED</c> rows).
/// </summary>
public abstract record BulkPreconditionVerdict
{
    private BulkPreconditionVerdict() { }

    /// <summary>Run the event factory and append (§P4 steps 2–3).</summary>
    public sealed record Apply : BulkPreconditionVerdict;

    /// <summary>Decline this instance — recorded <c>SKIPPED</c>, the run continues (§P5).</summary>
    public sealed record Skip(string Reason) : BulkPreconditionVerdict;
}

/// <summary>
/// The thin per-operation adapter of ADR-PC-035 §P4: a bulk operation rides the ONE generic runner
/// as an optional precondition plus a per-instance event factory — never a bespoke per-operation
/// execution path. In plain English: the runner owns registering, claiming, appending, status
/// bookkeeping, retry, and restart; an adapter says only "should this instance get the event, and
/// what exactly is the event". The four cross-cutting operations (<c>PackVersionMigrated</c>,
/// <c>SchemaVersionMigrated</c>, <c>FundsHeld</c>, <c>AccountFrozen</c>) each implement this in a
/// tracked sibling follow-up; the runner itself ships adapter-free.
/// </summary>
/// <remarks>
/// Family-agnostic (ADR-PC-021): an adapter reads only the opaque work-table row — the instance
/// reference and its frozen JSON params — and returns an engine-declared, STORE-ONLY cross-cutting
/// event (ADR-PC-035 §P4 / ADR-IC-017; <see cref="BulkInstanceAppender"/> enforces store-only
/// fail-loud). Both members must be PURE data mapping — no I/O, no clock — so a re-claimed row
/// re-derives the identical event and the §P3 command-id dedupe holds end to end.
/// </remarks>
public interface IBulkOperationStrategy
{
    /// <summary>The <c>operation_kind</c> this adapter serves — the dispatch key on the job header.</summary>
    string OperationKind { get; }

    /// <summary>The §P4 optional precondition. An adapter without one returns <see cref="BulkPreconditionVerdict.Apply"/> unconditionally.</summary>
    BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target);

    /// <summary>Build the store-only cross-cutting event for one instance (§P4), from the frozen row only.</summary>
    DomainEvent CreateEvent(BulkOperationTargetRow target);
}

/// <summary>
/// The deterministic per-instance command id of ADR-PC-035 §P3: a v5-style namespaced SHA-1 UUID
/// over <c>(job_id, instance_id)</c> — the same id whether the step runs first, on a selective
/// retry, or after a host restart, so the engine's receiver-dedupe (ADR-PC-029,
/// ENGINE_COMMAND_IDEMPOTENT) makes every re-run a no-op append. Pure by construction: no clock,
/// no randomness (the §Residual-risks "the derivation must stay deterministic and tested" hook —
/// pinned by <c>BulkOperationCommandIdTests</c>). Mirrors the pack-migration derivation in
/// <see cref="PackMigrationService{TState}"/>, under its own namespace so bulk command ids can
/// never collide with another deterministic id space.
/// </summary>
public static class BulkOperationCommandId
{
    // A fixed namespace GUID for bulk-operation command ids — an arbitrary, stable constant
    // distinct from the other deterministic id spaces (pack migration, renewal new-deposit).
    private static readonly Guid Namespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000035");

    public static Guid For(Guid jobId, Guid instanceId)
    {
        var namespaceBytes = Namespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes($"{jobId:D}:{instanceId:D}");

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        // Version 5 in the high nibble of byte 6; RFC-4122 variant in the high bits of byte 8.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
