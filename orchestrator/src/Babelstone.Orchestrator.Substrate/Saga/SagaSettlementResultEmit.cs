using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// Derives the DETERMINISTIC message id for a RESULT EVENT the orchestrator synthesizes from a saga
/// command's delivery outcome and self-advances in-process (bd babelstone-t7o3.8). At v1 the Core ACL
/// is a WireMock shim with no event producer, so when the dispatcher flips a <c>saga_outbox</c> row to
/// its terminal status it maps the outcome to a result-event type (the family's <c>IResultEventBridge</c>)
/// and feeds that event back into the SAME advance loop — the SAME "rides nothing on the durable bus"
/// pattern as the t7o3.1 approval-fork self-emit (<see cref="SagaSelfEmit"/>), but triggered by a
/// COMMAND outcome rather than a validation join.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, not minted (ADR-PC-010 §P5).</b> The id is a v5-style (SHA-1, namespaced) hash of
/// the triggering COMMAND's <c>message_id</c> + the synthesized result-event type — NOT
/// <see cref="Guid.NewGuid"/>. That makes the self-advance IDEMPOTENT through the SAME inbox dedup an
/// external advance uses: a crash between the HTTP 2xx and the commit leaves the row PENDING, the next
/// cycle re-POSTs the same command (the engine's idempotency replays the original outcome), the bridge
/// re-derives the SAME result-event id, and the inbox dedup row collides — so the saga advances exactly
/// once (effectively-once). The derivation reads no clock and no randomness, so a replay reproduces it.
/// </para>
/// <para>
/// <b>A DISTINCT namespace from the approval-fork self-emit.</b> A separate namespace GUID guarantees a
/// settlement-result id can NEVER collide with an approval-fork self-emit id (<see cref="SagaSelfEmit"/>)
/// nor with an external event's <c>ce_id</c>, even for the same process — the two id spaces are disjoint
/// by construction.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2).</b> The inputs are a command-delivery reference and a structural
/// event-type name — never identity data. The derived id is itself a structural reference.
/// </para>
/// </remarks>
public static class SagaSettlementResultEmit
{
    /// <summary>The synthetic source topic recorded on a synthesized result event's inbox dedup row. An
    /// INTERNAL marker — the bridge never touches the durable bus (ADR-IC-003 §S2) — named distinctly
    /// from any real Redpanda topic AND from the approval-fork self-emit's marker.</summary>
    public const string SourceTopic = "saga.settlement-shim";

    // A fixed namespace GUID for orchestrator settlement-result events — an arbitrary, stable constant,
    // DISTINCT from SagaSelfEmit's namespace so a settlement-result id can never collide with an
    // approval-fork self-emit id by construction.
    private static readonly Guid SettlementResultNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000002");

    /// <summary>
    /// The deterministic dedup message id for synthesizing <paramref name="resultEventType"/> off the
    /// command whose delivery id is <paramref name="commandMessageId"/>. Pure: the same (command id,
    /// result type) always yields the same id, with no clock and no randomness — so a re-POST of the
    /// same PENDING command re-derives the same id and the inbox dedup absorbs the re-advance.
    /// </summary>
    public static Guid MessageId(Guid commandMessageId, string resultEventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultEventType);

        // v5 (name-based, SHA-1) UUID over the namespace + (command id, result event type). The standard
        // RFC-4122 §4.3 construction: hash the namespace bytes followed by the name bytes, then set the
        // version (5) and variant bits. Mirrors SagaSelfEmit.MessageId, with a distinct namespace and a
        // COMMAND-id-keyed name.
        var namespaceBytes = SettlementResultNamespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(commandMessageId.ToString("N") + "|" + resultEventType);

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
