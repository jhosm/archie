using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// Derives the DETERMINISTIC message id for an event the orchestrator SELF-EMITS into its own
/// advance loop (bd babelstone-t7o3.1) — the approval fork's chosen event
/// (<c>ConstitutionApproved</c> / <c>WorkflowApprovalRequired</c>) fed back in-process when the
/// parallel validations complete. The self-emit rides NOTHING on the durable bus (the bus stays
/// events-only); it is the impure shell scheduling the saga's next step within the SAME transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, not minted (ADR-PC-010 §P5).</b> A self-emitted event's message id is a v5-style
/// (SHA-1, namespaced) hash of the saga's process id + the emitted event type — NOT
/// <see cref="Guid.NewGuid"/>. That makes the self-emit IDEMPOTENT through the SAME inbox dedup the
/// external advance uses: a re-drive of the join (a redelivered sibling validation, a retried
/// transaction) derives the SAME message id, so the dedup row collides and the fork is never emitted
/// twice. A minted id would let a retry emit the fork's event a second time and double-advance the
/// saga. The derivation reads no clock and no randomness, so a replay reproduces it exactly.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2).</b> The inputs are a process reference and a structural event-type
/// name — never identity data. The derived id is itself a structural reference.
/// </para>
/// </remarks>
public static class SagaSelfEmit
{
    // A fixed namespace GUID for orchestrator self-emitted events — an arbitrary, stable constant
    // (distinct from any other id space) so a self-emit message id cannot collide with an external
    // event's ce_id by construction.
    private static readonly Guid SelfEmitNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000001");

    /// <summary>
    /// The deterministic dedup message id for self-emitting <paramref name="eventType"/> on saga
    /// <paramref name="processId"/>. Pure: the same (process id, event type) always yields the same
    /// id, with no clock and no randomness — so the self-emit dedups exactly like an external advance.
    /// </summary>
    public static Guid MessageId(Guid processId, string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        // v5 (name-based, SHA-1) UUID over the namespace + (process id, event type). The standard
        // RFC-4122 §4.3 construction: hash the namespace bytes followed by the name bytes, then set
        // the version (5) and variant bits.
        var namespaceBytes = SelfEmitNamespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(processId.ToString("N") + "|" + eventType);

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
