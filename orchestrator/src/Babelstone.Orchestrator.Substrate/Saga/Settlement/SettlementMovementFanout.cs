using System.Security.Cryptography;
using System.Text;
using Babelstone.Orchestrator.Inbox;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// Projects a Movement-bearing event into one PER-OCCURRENCE settlement subject per Originated Movement
/// (ADR-PC-032 §A9/§A10, option b + the per-occurrence-identity revision 2026-07-04; feature-design
/// money-movement-settlement §6). In plain English: every time money moves, the settlement machinery needs
/// its own saga instance to succeed, fail, or park in — including the SECOND and later times money moves on
/// the SAME account (a loan's monthly installments), and including a single event that moves money two
/// opposite ways at once (a renewal's rollover-debit + interest-credit). This helper turns each
/// Movement-bearing event into N independent settlement instances — one per Movement — whose process ids are
/// deterministic derivations of (subject, event id, movement index), so occurrence 2 never collides with
/// occurrence 1's completed saga, while a REDELIVERY of the same event re-derives the same ids and dedups.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire contract it reads (the engine-spine producer's, <c>Babelstone.Engine.MovementHeaders</c>).</b>
/// The producer emits <c>ce_movementorigin = Originated</c> and ONE ordered <c>ce_movementdirections</c> list
/// — the comma-separated closed-enum names of every Originated direction in carrier order: a single entry for
/// a standalone leg (e.g. <c>Credit</c>), N entries for a multi-direction event (e.g. <c>Debit,Credit</c>).
/// The substrate names no family — it reads only these header strings (ADR-IC-018 §D5; the extraction-ready,
/// payload-blind boundary), never the Avro payload.
/// </para>
/// <para>
/// <b>Per-occurrence identity (ADR-PC-032 §A9/§A10 Revised 2026-07-04; bd babelstone-3o6m / Q-BH).</b>
/// EVERY settlement instance's process id — index 0 included — is a v5-style (SHA-1, namespaced) hash of
/// <c>(ce_subject, ce_id, movement index)</c>, NOT the bare <c>ce_subject</c>. Deterministic (ADR-PC-010 §P5
/// — no clock, no mint), so a redelivery of the SAME event re-derives the SAME process ids and the auto-start
/// INSERT collides on the <c>saga_state</c> PK (effectively-once per leg) — while a LATER occurrence on the
/// same subject (installment N's event has its own <c>ce_id</c>) derives a FRESH instance, so
/// <c>SETTLEMENT_COMPLETED</c> stays terminal PER OCCURRENCE and a recurring schedule's occurrence N ≥ 2 has
/// its own saga to drive or park. The account/instrument linkage is preserved on the projection's
/// <see cref="SagaInboxEvent.SubjectId"/> (persisted as the indexed <c>saga_state.subject_id</c> the LCD-2
/// probe keys on, ADR-PC-036 §Decision 4 Revised 2026-07-04). Each occurrence's process id is also what the
/// ACL idempotency references derive from (<see cref="SettlementReferences"/>), so installment 2's debit
/// token can never dedup against installment 1's.
/// </para>
/// <para>
/// <b>Dedup message ids.</b> The PRIMARY leg (index 0) keeps the event's own <c>ce_id</c> as its inbox dedup
/// identity (one physical delivery ↔ one primary advance); a SECONDARY leg's (index ≥ 1) dedup id is a
/// v5-style hash of <c>(ce_id, index)</c> — so a redelivery's every leg collides on its own dedup row.
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
    // A fixed namespace GUID for derived per-occurrence settlement PROCESS IDS — an arbitrary, stable
    // constant, DISTINCT from every other derived-id namespace in the repo (self-emit …001,
    // settlement-result …002, renewal-deposit …003, pack …009) so a derived occurrence id can never collide
    // with another derived id by construction.
    private static readonly Guid SettlementSubjectNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-00000000000a");

    // A fixed namespace GUID for derived per-Movement settlement DEDUP MESSAGE IDs — distinct again, so the
    // secondary legs' inbox dedup keys cannot collide with the occurrence ids or any external ce_id.
    private static readonly Guid SettlementMessageNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-00000000000b");

    /// <summary>
    /// Parse the ordered Originated directions a Movement-bearing event's <c>movementdirections</c> list
    /// declares — ONE entry per Movement, in carrier order (a single entry for a standalone leg, N for a
    /// multi-direction event), or an EMPTY list for a non-Movement event (no/blank header). Reads the
    /// <see cref="DirectionsHeader"/> header ONLY; names no family. The values are the closed-enum NAMES the
    /// producer emits (<c>Debit</c> / <c>Credit</c>); the substrate keeps them as the WIRE STRINGS (the
    /// substitutor matches on them — ADR-IC-018 §D5), never re-typing them to an engine enum (the
    /// orchestrator stays extraction-ready, ADR-PC-019 §P2). Every declared entry is projected into its own
    /// per-occurrence instance by <see cref="ProjectMovementEvent"/> — the fan-out's inertia guard is the
    /// projection's <see cref="SagaInboxEvent.SubjectId"/> stamp, not the list length.
    /// </summary>
    /// <param name="extensionHeaders">The event's projected extension attributes (ce_-stripped, lowercased).
    /// Null/empty ⇒ no directions.</param>
    /// <returns>The ordered direction wire strings the event declares (one per Movement); an EMPTY list for
    /// a non-Movement event.</returns>
    public static IReadOnlyList<string> ParseDirections(
        IReadOnlyDictionary<string, string>? extensionHeaders)
        => SplitDirections(extensionHeaders);

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
    /// The PER-OCCURRENCE settlement process id for movement <paramref name="index"/> of the event
    /// <paramref name="eventMessageId"/> on subject <paramref name="eventSubject"/> (ADR-PC-032 §A9/§A10
    /// Revised 2026-07-04): a deterministic v5-style derivation of (ce_subject, ce_id, movement index) —
    /// for EVERY index, 0 included. Same inputs → same id (a redelivery collides on the saga_state PK,
    /// effectively-once); a different event id (installment N+1) → a fresh instance on the same subject.
    /// Never <see cref="Guid.NewGuid"/> (ADR-PC-010 §P5).
    /// </summary>
    public static Guid OccurrenceProcessId(Guid eventSubject, Guid eventMessageId, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return DeriveV5(
            SettlementSubjectNamespace,
            eventSubject.ToString("N") + "|" + eventMessageId.ToString("N") + "|" + index);
    }

    /// <summary>
    /// The per-Movement dedup message id for <paramref name="index"/> of the multi-direction event whose own
    /// dedup id is <paramref name="eventMessageId"/>. Index 0 is the IDENTITY (the primary leg dedups on the
    /// event's real message id — one physical delivery ↔ one primary advance); index ≥ 1 is a deterministic
    /// v5-style derivation, so a redelivery re-derives the SAME id and the inbox dedup absorbs it
    /// (effectively-once per leg).
    /// </summary>
    public static Guid MessageIdForMovement(Guid eventMessageId, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index == 0
            ? eventMessageId
            : DeriveV5(SettlementMessageNamespace, eventMessageId.ToString("N") + "|" + index);
    }

    /// <summary>
    /// Project the event into the per-occurrence <see cref="SagaInboxEvent"/> for movement
    /// <paramref name="index"/>: its derived per-occurrence process id
    /// (<see cref="OccurrenceProcessId"/>), its dedup id (<see cref="MessageIdForMovement"/>), its REAL
    /// <c>ce_subject</c> preserved on <see cref="SagaInboxEvent.SubjectId"/> (the account/instrument linkage
    /// the start path persists as <c>saga_state.subject_id</c>), and its extension headers with
    /// <c>movementdirections</c> OVERWRITTEN to this leg's SINGLE direction (so the machine's substitutor
    /// branches THIS leg). The non-null <see cref="SagaInboxEvent.SubjectId"/> is ALSO the inertia stamp: a
    /// projected leg re-entering the fan-out flows through unchanged (no recursion past depth 1).
    /// Family-agnostic: it copies whatever other extension attributes the record carried (e.g. the SCA
    /// claims), naming none.
    /// </summary>
    public static SagaInboxEvent ProjectMovementEvent(
        SagaInboxEvent source, int index, string direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // Carry every extension attribute forward (the SCA claims, any future routing discriminator), then
        // OVERWRITE movementdirections with THIS leg's single direction so the substitutor's SingleDirection
        // resolves THIS leg's branch. Ordinal-ignore-case to match the consume loop's projection.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.ExtensionHeaders is { } existing)
        {
            foreach (var (key, value) in existing)
            {
                headers[key] = value;
            }
        }

        headers[DirectionsHeader] = direction;

        // ADR-PC-043 §D5: reduce the engine-CA per-movement destination + amount ORDERED lists to THIS leg's
        // single index-th entry, parallel to the direction reduction — so the leg's ConfirmCredit/ConfirmDebit
        // body carries the right account_ref + amount (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED, CA-17). Absent on a
        // legacy-DDA leg (no promotion) → removed, so the substrate's ACCT-{processId} placeholder path stands.
        ReduceOrderedListToLeg(headers, AccountRefsHeader, index);
        ReduceOrderedListToLeg(headers, AmountsHeader, index);

        return source with
        {
            MessageId = MessageIdForMovement(source.MessageId, index),
            ProcessId = OccurrenceProcessId(source.ProcessId, source.MessageId, index),
            // The event's REAL ce_subject — the projection is built from an UN-projected event (the fan-out
            // guard returns a projected leg unchanged), so source.ProcessId IS the subject here.
            SubjectId = source.ProcessId,
            ExtensionHeaders = headers,
        };
    }

    // Reduce an ORDERED, comma-separated per-movement header list to THIS leg's single index-th entry — the
    // account_ref / amount analog of the movementdirections reduction above. The list is positionally aligned
    // with movementdirections (one entry per Originated movement, carrier order), so split WITHOUT dropping
    // empties (RemoveEmptyEntries would shift indices) and pick the index-th. When the header is absent, blank,
    // or the index is out of range / the entry is blank, REMOVE the key — a leg with no promoted value falls
    // back to the substrate's placeholder path rather than carrying a wrong or shifted value (fail to the
    // documented legacy-DDA behaviour, never a guess).
    private static void ReduceOrderedListToLeg(IDictionary<string, string> headers, string key, int index)
    {
        if (!headers.TryGetValue(key, out var list) || string.IsNullOrWhiteSpace(list))
        {
            headers.Remove(key);
            return;
        }

        var parts = list.Split(DirectionsSeparator, StringSplitOptions.TrimEntries);
        if (index >= 0 && index < parts.Length && !string.IsNullOrWhiteSpace(parts[index]))
        {
            headers[key] = parts[index];
        }
        else
        {
            headers.Remove(key);
        }
    }

    /// <summary>This (post-fan-out) leg's single destination account_ref — the lone entry of its
    /// <see cref="AccountRefsHeader"/> list (ADR-PC-043 §D5), or <c>null</c> when absent (a legacy-DDA leg, or a
    /// not-yet-reduced multi-entry list). The substrate threads it into the CA-apply command body as the
    /// credit/debit destination (never a routing input — routing keys on <c>settlementtarget</c> alone).</summary>
    public static string? SingleAccountRef(IReadOnlyDictionary<string, string>? extensionHeaders)
        => extensionHeaders is not null
            && extensionHeaders.TryGetValue(AccountRefsHeader, out var value)
            && !string.IsNullOrWhiteSpace(value)
            && !value.Contains(DirectionsSeparator, StringComparison.Ordinal)
            ? value
            : null;

    /// <summary>This (post-fan-out) leg's single amount in integer cents — the lone entry of its
    /// <see cref="AmountsHeader"/> list (ADR-PC-043 §D5, the WRONG-AMOUNT guard), or <c>null</c> when absent /
    /// non-numeric (a legacy-DDA leg, or a not-yet-reduced multi-entry list). Parsed invariant-culture.</summary>
    public static long? SingleAmountCents(IReadOnlyDictionary<string, string>? extensionHeaders)
        => extensionHeaders is not null
            && extensionHeaders.TryGetValue(AmountsHeader, out var value)
            && long.TryParse(
                value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var cents)
            ? cents
            : null;

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the ordered
    /// <c>movementdirections</c> list (mirrors <c>Babelstone.Engine.MovementHeaders.DirectionsKey</c>). Pinned
    /// as a literal — the orchestrator stays extraction-ready (ADR-PC-019 §P2), so it cannot reference the
    /// engine-side constant; the producer↔consumer contract test asserts the two agree.</summary>
    public const string DirectionsHeader = "movementdirections";

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the ORDERED per-movement
    /// destination account_ref list (mirrors <c>Babelstone.Engine.MovementHeaders.AccountRefsKey</c>, ADR-PC-043
    /// §D5). Pinned as a literal — the orchestrator stays extraction-ready (ADR-PC-019 §P2); the producer↔consumer
    /// contract test asserts the two agree. <see cref="ProjectMovementEvent"/> reduces it to THIS leg's single
    /// entry, which <see cref="SingleAccountRef"/> reads.</summary>
    public const string AccountRefsHeader = "movementaccountrefs";

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the ORDERED per-movement
    /// integer-cents amount list (mirrors <c>Babelstone.Engine.MovementHeaders.AmountsKey</c>, ADR-PC-043 §D5).
    /// Pinned as a literal (extraction-ready). Reduced per leg by <see cref="ProjectMovementEvent"/>, read by
    /// <see cref="SingleAmountCents"/>.</summary>
    public const string AmountsHeader = "movementamounts";

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
