using System.Security.Cryptography;
using System.Text;
using Babelstone.Orchestrator.Inbox;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// Fans a MULTI-DIRECTION Movement-bearing event out into one settlement subject per Originated Movement
/// (ADR-PC-032 §A9/§A10, option b; feature-design money-movement-settlement §6). In plain English: a single
/// event can carry money moving two opposite ways at once — a deposit renewal rolls the principal over (a
/// debit) AND pays the interest (a credit). One settlement saga instance branches to ONE direction, so this
/// helper turns that one event into N independent settlement subjects — one per Movement, each gated by its
/// own direction — so both legs settle correctly with no silent loss.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire contract it reads (the engine-spine producer's, <c>Babelstone.Engine.MovementHeaders</c>).</b>
/// The producer emits <c>ce_movementorigin = Originated</c> and ONE ordered <c>ce_movementdirections</c> list
/// — the comma-separated closed-enum names of every Originated direction in carrier order: a single entry for
/// a standalone leg (e.g. <c>Credit</c>), N entries for a multi-direction event (e.g. <c>Debit,Credit</c>).
/// The substrate names no family — it reads only these header strings (ADR-IC-018 §D5; the extraction-ready,
/// payload-blind boundary), never the Avro payload. Fan-out triggers only when the list has ≥ 2 entries; a
/// single-entry list is one settlement instance the established way — the lone event flows through unchanged,
/// keeping its own <c>ce_subject</c> as its process id (byte-for-byte the prior single-direction behaviour),
/// and <see cref="SingleDirection"/> branches it. For a genuinely multi-direction event the PRIMARY instance
/// (index 0) keeps the event's own <c>ce_subject</c>; the SECONDARY instances (index ≥ 1) are derived (below).
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
/// (the list's order), so a renewal's debit and credit are dispatched in declared order (feature-design
/// §6). No PII rides any derived id — they are structural references (ADR-PC-004 §P2).
/// </para>
/// </remarks>
public static class SettlementMovementFanout
{
    // A fixed namespace GUID for derived per-Movement settlement SUBJECTS — an arbitrary, stable constant,
    // DISTINCT from every other derived-id namespace in the repo (self-emit …001, settlement-result …002,
    // renewal-deposit …003, pack …009) so a derived subject can never collide with another derived id by
    // construction.
    private static readonly Guid SettlementSubjectNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-00000000000a");

    // A fixed namespace GUID for derived per-Movement settlement DEDUP MESSAGE IDs — distinct again, so the
    // secondary legs' inbox dedup keys cannot collide with the subject ids or any external ce_id.
    private static readonly Guid SettlementMessageNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-00000000000b");

    /// <summary>
    /// Parse the ordered Originated directions a Movement-bearing event's <c>movementdirections</c> list
    /// declares that REQUIRE FAN-OUT (length ≥ 2 — a genuinely multi-direction event), or an EMPTY list
    /// otherwise (a standalone single-direction leg, or a non-Movement event — the caller starts ONE instance
    /// the established way, no fan-out). Reads the <see cref="DirectionsHeader"/> header ONLY; names no family.
    /// The values are the closed-enum NAMES the producer emits (<c>Debit</c> / <c>Credit</c>); the substrate
    /// keeps them as the WIRE STRINGS (the substitutor matches on them — ADR-IC-018 §D5), never re-typing them
    /// to an engine enum (the orchestrator stays extraction-ready, ADR-PC-019 §P2).
    /// </summary>
    /// <param name="extensionHeaders">The event's projected extension attributes (ce_-stripped, lowercased).
    /// Null/empty ⇒ no directions.</param>
    /// <returns>The ordered direction wire strings when the event spans MORE THAN ONE Movement (length ≥ 2);
    /// an EMPTY list otherwise (a single-direction or non-Movement event — no fan-out, the lone event flows
    /// through unchanged keeping its real <c>ce_subject</c>).</returns>
    public static IReadOnlyList<string> ParseDirections(
        IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        // The producer ALWAYS emits movementdirections as an ordered list — one entry for a standalone leg, N
        // for a multi-direction event. Fan-out is needed ONLY for length ≥ 2: a single-entry list is one
        // settlement instance the established way (the event keeps its own ce_subject; SingleDirection branches
        // it). Treating length < 2 as "no fan-out" is ALSO what makes a fanned-out leg inert on re-entry — each
        // leg carries a single-entry list, so re-parsing it returns [] and it never re-fans-out (no recursion
        // past depth 1).
        var directions = SplitDirections(extensionHeaders);
        return directions.Length >= 2 ? directions : [];
    }

    /// <summary>
    /// The lone Originated direction a (post-fan-out) leg's <c>movementdirections</c> header declares — the
    /// single entry of its list — or <c>null</c> when the list is absent/empty or carries MORE THAN ONE entry.
    /// The substitutor (<see cref="SettlementProcess.SubstituteAsync"/>) resolves each leg's debit/credit
    /// branch from this: by the time the table sees a Movement-bearing event, the fan-out has reduced it to a
    /// single-direction leg, so a multi-entry list here means it was NOT fanned out — return <c>null</c> so the
    /// substitutor fail-closes (the un-substituted start event has no transition out of SETTLEMENT_STARTED →
    /// NoTransition, never a guessed direction).
    /// </summary>
    /// <param name="extensionHeaders">The leg's projected extension attributes (ce_-stripped, lowercased).</param>
    /// <returns>The lone direction wire string when exactly one is present; <c>null</c> otherwise.</returns>
    public static string? SingleDirection(
        IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        var directions = SplitDirections(extensionHeaders);
        return directions.Length == 1 ? directions[0] : null;
    }

    // Split the movementdirections header into its ordered closed-enum names. The list is the comma-joined
    // names in carrier order; split, trim, drop empties — a defensively-empty entry never mints a phantom leg.
    // A null/blank header (a non-Movement event) is an empty split.
    private static string[] SplitDirections(IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        if (extensionHeaders is null
            || !extensionHeaders.TryGetValue(DirectionsHeader, out var list)
            || string.IsNullOrWhiteSpace(list))
        {
            return [];
        }

        return list.Split(
            DirectionsSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
    /// derived subject + dedup id, and its extension headers with <c>movementdirections</c> OVERWRITTEN to this
    /// leg's SINGLE direction (so the machine's substitutor branches THIS leg, and the now-single-entry list is
    /// inert on re-entry — a leg must NOT re-fan-out). The primary (index 0) keeps the event's real ids; its
    /// list is pinned to the first direction. Family-agnostic: it copies whatever other extension attributes
    /// the record carried (e.g. the SCA claims), naming none.
    /// </summary>
    public static SagaInboxEvent ProjectMovementEvent(
        SagaInboxEvent source, int index, string direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // Carry every extension attribute forward (the SCA claims, any future routing discriminator), then
        // OVERWRITE movementdirections with THIS leg's single direction. Reducing the list to one entry does
        // both jobs at once: the substitutor's SingleDirection now resolves THIS leg's branch, and a re-parse
        // returns [] (length < 2) so the leg never re-fans-out (no recursion past depth 1). Ordinal-ignore-case
        // to match the consume loop's projection.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.ExtensionHeaders is { } existing)
        {
            foreach (var (key, value) in existing)
            {
                headers[key] = value;
            }
        }

        headers[DirectionsHeader] = direction;

        return source with
        {
            MessageId = MessageIdForMovement(source.MessageId, index),
            ProcessId = SubjectForMovement(source.ProcessId, index),
            ExtensionHeaders = headers,
        };
    }

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the ordered
    /// <c>movementdirections</c> list (mirrors <c>Babelstone.Engine.MovementHeaders.DirectionsKey</c>). Pinned
    /// as a literal — the orchestrator stays extraction-ready (ADR-PC-019 §P2), so it cannot reference the
    /// engine-side constant; the producer↔consumer contract test asserts the two agree.</summary>
    public const string DirectionsHeader = "movementdirections";

    /// <summary>The list separator (mirrors <c>Babelstone.Engine.MovementHeaders.DirectionsSeparator</c>).</summary>
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
