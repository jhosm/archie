using System.Security.Cryptography;
using System.Text;
using Babelstone.Orchestrator.Inbox;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// Fans a MULTI-DIRECTION Movement-bearing event out into one settlement subject per Originated Movement
/// (ADR-PC-032 §A9 amendment 2026-06-26, option b; feature-design money-movement-settlement §6). In plain
/// English: a single event can carry money moving two opposite ways at once — a deposit renewal rolls the
/// principal over (a debit) AND pays the interest (a credit). One settlement saga instance branches to ONE
/// direction, so this helper turns that one event into N independent settlement subjects — one per Movement,
/// each gated by its own direction — so both legs settle correctly with no silent loss.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire contract it reads (the engine-spine producer's, <c>Babelstone.Engine.MovementHeaders</c>).</b>
/// The producer ALWAYS emits <c>ce_movementorigin = Originated</c> and <c>ce_movementdirection = &lt;first
/// direction&gt;</c>; on a multi-direction event it ADDITIONALLY emits <c>ce_movementdirections</c> = the
/// ordered, comma-separated list of every Originated direction in carrier order (e.g. <c>Debit,Credit</c>).
/// The substrate names no family — it reads only these header strings (ADR-IC-018 §D5; the extraction-ready,
/// payload-blind boundary), never the Avro payload. The PRIMARY instance (index 0) keeps the event's own
/// <c>ce_subject</c> as its process id, so the established SINGLE-direction path is byte-for-byte unchanged
/// (no composite ⇒ no fan-out ⇒ exactly the prior behaviour). The SECONDARY instances (index ≥ 1) are
/// derived (below).
/// </para>
/// <para>
/// <b>Deterministic per-Movement subjects (ADR-PC-010 §P5 — no clock, no mint).</b> A secondary instance's
/// process id is a v5-style (SHA-1, namespaced) hash of <c>(ce_subject, index)</c>, and its dedup message id
/// a v5-style hash of <c>(original message_id, index)</c> — NOT <see cref="System.Guid.NewGuid"/>. So a
/// redelivery of the SAME multi-direction event re-derives the SAME secondary subjects and the SAME dedup
/// ids: the auto-start INSERT collides on the process-id PK and the inbox dedup row collides on the message
/// id, so each secondary leg starts and advances EXACTLY ONCE (effectively-once), exactly as the primary leg
/// does on its own <c>ce_subject</c>. Index 0 is the identity (the primary keeps the real ids).
/// </para>
/// <para>
/// <b>Per-account FIFO holds.</b> Each instance gets its OWN process id, hence its OWN dispatcher
/// per-<c>process_id</c> FIFO lane (ADR-IC-004 / bd babelstone-t7o3.7). The legs are emitted in carrier order
/// (the composite's order), so a renewal's debit and credit are dispatched in declared order (feature-design
/// §6). No PII rides any derived id — they are structural references (ADR-PC-004 §P2).
/// </para>
/// </remarks>
public static class SettlementMovementFanout
{
    // A fixed namespace GUID for derived per-Movement settlement SUBJECTS — an arbitrary, stable constant,
    // DISTINCT from the result-event and self-emit namespaces so a derived subject can never collide with
    // another derived id by construction.
    private static readonly Guid SettlementSubjectNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000003");

    // A fixed namespace GUID for derived per-Movement settlement DEDUP MESSAGE IDs — distinct again, so the
    // secondary legs' inbox dedup keys cannot collide with the subject ids or any external ce_id.
    private static readonly Guid SettlementMessageNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000004");

    /// <summary>
    /// Parse the ordered Originated directions a Movement-bearing event's extension headers declare, or an
    /// empty list when the event declares no multi-direction composite (the single-direction case — the
    /// caller starts ONE instance the established way, no fan-out). Reads <see
    /// cref="Babelstone.Orchestrator.Saga.Settlement.SettlementSagaModule.OriginHeader"/>-gated headers ONLY;
    /// names no family. The values are the closed-enum NAMES the producer emits (<c>Debit</c> / <c>Credit</c>);
    /// the substrate keeps them as the WIRE STRINGS (the substitutor matches on them — ADR-IC-018 §D5), never
    /// re-typing them to an engine enum (the orchestrator stays extraction-ready, ADR-PC-019 §P2).
    /// </summary>
    /// <param name="extensionHeaders">The event's projected extension attributes (ce_-stripped, lowercased).
    /// Null/empty ⇒ no directions.</param>
    /// <returns>The ordered direction wire strings when a multi-direction composite is present (length ≥ 2);
    /// an EMPTY list otherwise (a single-direction or non-Movement event — no fan-out).</returns>
    public static IReadOnlyList<string> ParseDirections(
        IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        if (extensionHeaders is null
            || !extensionHeaders.TryGetValue(DirectionsHeader, out var composite)
            || string.IsNullOrWhiteSpace(composite))
        {
            return [];
        }

        // The composite is the comma-joined closed-enum names in carrier order. Split, trim, drop empties —
        // a defensively-empty entry never mints a phantom leg. A single entry is NOT a multi-direction event
        // (the producer only emits the composite for > 1 direction), but treat length < 2 as "no fan-out" so a
        // malformed single-entry composite degrades to the established single-instance path, fail-safe.
        var directions = composite
            .Split(DirectionsSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return directions.Length >= 2 ? directions : [];
    }

    /// <summary>
    /// The per-Movement subject for <paramref name="index"/> of the multi-direction event whose own subject is
    /// <paramref name="eventSubject"/>. Index 0 is the IDENTITY (the primary instance keeps the event's real
    /// <c>ce_subject</c>); index ≥ 1 is a deterministic v5-style derivation, stable across redelivery.
    /// </summary>
    public static Guid SubjectForMovement(Guid eventSubject, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index == 0
            ? eventSubject
            : DeriveV5(SettlementSubjectNamespace, eventSubject.ToString("N") + "|" + index);
    }

    /// <summary>
    /// The per-Movement dedup message id for <paramref name="index"/> of the multi-direction event whose own
    /// dedup id is <paramref name="eventMessageId"/>. Index 0 is the IDENTITY (the primary leg dedups on the
    /// event's real message id); index ≥ 1 is a deterministic v5-style derivation, so a redelivery re-derives
    /// the SAME id and the inbox dedup absorbs it (effectively-once per leg).
    /// </summary>
    public static Guid MessageIdForMovement(Guid eventMessageId, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index == 0
            ? eventMessageId
            : DeriveV5(SettlementMessageNamespace, eventMessageId.ToString("N") + "|" + index);
    }

    /// <summary>
    /// Project the event into the per-Movement <see cref="SagaInboxEvent"/> for <paramref name="index"/>: its
    /// derived subject + dedup id, and its extension headers with <c>movementdirection</c> OVERRIDDEN to this
    /// index's direction (so the machine's substitutor branches THIS leg correctly) and the now-consumed
    /// <c>movementdirections</c> composite removed (a secondary instance must NOT re-fan-out). The primary
    /// (index 0) keeps the event's real ids; only its direction header is pinned to the first direction (a
    /// no-op — it already carries it). Family-agnostic: it copies whatever other extension attributes the
    /// record carried (e.g. the SCA claims), naming none.
    /// </summary>
    public static SagaInboxEvent ProjectMovementEvent(
        SagaInboxEvent source, int index, string direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // Carry every extension attribute forward (the SCA claims, any future routing discriminator), but
        // pin movementdirection to THIS leg's direction and DROP the composite so the secondary instance is a
        // single-direction subject the established substitutor resolves. Ordinal-ignore-case to match the
        // consume loop's projection.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.ExtensionHeaders is { } existing)
        {
            foreach (var (key, value) in existing)
            {
                if (string.Equals(key, DirectionsHeader, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the composite is consumed by the fan-out; a leg must not re-fan-out.
                }

                headers[key] = value;
            }
        }

        headers[DirectionHeader] = direction;

        return source with
        {
            MessageId = MessageIdForMovement(source.MessageId, index),
            ProcessId = SubjectForMovement(source.ProcessId, index),
            ExtensionHeaders = headers,
        };
    }

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the ordered multi-direction
    /// composite (mirrors <c>Babelstone.Engine.MovementHeaders.DirectionsKey</c>). Pinned as a literal — the
    /// orchestrator stays extraction-ready (ADR-PC-019 §P2), so it cannot reference the engine-side constant;
    /// the producer↔consumer contract test asserts the two agree.</summary>
    public const string DirectionsHeader = "movementdirections";

    /// <summary>The ce_-stripped, lowercased single-direction header key (mirrors
    /// <see cref="SettlementProcess.DirectionHeader"/> / <c>Babelstone.Engine.MovementHeaders.DirectionKey</c>).</summary>
    public const string DirectionHeader = "movementdirection";

    /// <summary>The composite separator (mirrors <c>Babelstone.Engine.MovementHeaders.DirectionsSeparator</c>).</summary>
    private const string DirectionsSeparator = ",";

    // v5 (name-based, SHA-1) UUID over the namespace + name, the RFC-4122 §4.3 construction. Mirrors
    // SagaSettlementResultEmit.MessageId: no clock, no randomness, so a replay reproduces it exactly.
    private static Guid DeriveV5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
