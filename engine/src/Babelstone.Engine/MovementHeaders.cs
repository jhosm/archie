namespace Babelstone.Engine;

/// <summary>
/// The GENERIC engine-spine seam that promotes an event's <see cref="Movement"/> origin/direction to the
/// CloudEvents extension headers the substrate-owned settlement saga auto-starts on (ADR-PC-032 §A7/§A8;
/// ADR-IC-018 §P5/§D5). In plain English: when a family event records that money was decided
/// (<see cref="MovementOrigin.Originated"/>), the settlement saga downstream needs to know two things WITHOUT
/// opening the (PII-free but Avro-encoded) payload — is there a cash leg to drive at all, and which way (or
/// ways) the money moves. This helper turns a Movement-bearing event's movements into two closed-enum header
/// values (<c>movementorigin</c> and the ordered <c>movementdirections</c> list) the engine's outbox relay
/// then promotes to <c>ce_movementorigin</c> / <c>ce_movementdirections</c> — the operational-metadata
/// channel, never the payload, never PII (<see cref="DomainEvent.IntegrationHeaders"/> / ADR-PC-004 §P2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic and family-agnostic (ADR-PC-021 §P2 / §D2).</b> This keys ONLY on the spine's
/// <see cref="Movement"/> atom and its two closed enums (<see cref="MovementOrigin"/> /
/// <see cref="SettlementDirection"/>) — it names no family, so a Movement-bearing event in ANY family gets
/// the headers for free by routing its <see cref="DomainEvent.IntegrationHeaders"/> override through
/// <see cref="ForOriginatedMovements"/>. It is the producer counterpart of the substrate's consumer seam
/// (the settlement module's <c>movementorigin</c> auto-start predicate + the <c>movementdirections</c>
/// fan-out/substitutor): the same closed-enum strings the saga reads off the headers.
/// </para>
/// <para>
/// <b>Closed-enum values only — no PII, no amount, no account ref (ADR-PC-004 §P2 / ADR-PC-032 §A8).</b> The
/// header values are <see cref="MovementOrigin"/>'s and <see cref="SettlementDirection"/>'s member NAMES
/// (<c>Originated</c> / <c>Observed</c> and <c>Debit</c> / <c>Credit</c>) — the SAME stable strings
/// <c>MovementCarrier</c> writes for the Avro enum symbols, and the SAME strings the substrate's
/// <c>SettlementSagaModule.OriginatedValue</c> / <c>SettlementProcess</c> direction branch match on. The
/// amount, the opaque <see cref="Movement.AccountRef"/>, and the <see cref="Movement.CommandId"/> stay in the
/// payload; only the routing discriminators ride the headers.
/// </para>
/// <para>
/// <b>The SCA freshness claims ride the SAME hop (ADR-PC-032 §A8; t7o3.19).</b> For an Originated
/// money-mover subject to step-up SCA, the gateway-attested <c>acr</c> / <c>auth_time</c> claims propagate
/// forward to the event-auto-started settlement leg on these SAME Movement-bearing-event CloudEvents headers
/// (the operational-metadata channel, never the payload, never PII — the same posture
/// <c>movementorigin</c> / <c>movementdirections</c> take here). They are promoted as their own extension
/// attributes (e.g. <c>ce_scaacr</c> / <c>ce_scaauthtime</c>) co-carried on the engine boundary that appends
/// the event — NOT populated here: that gate and its non-double-populate enforcement are bd t7o3.19's, and
/// this seam stays the movement-routing producer. That carriage adds entries to the SAME
/// <see cref="DomainEvent.IntegrationHeaders"/> map this helper seeds (no double-populate: distinct keys), so
/// the two producers compose on one hop.
/// </para>
/// <para>
/// <b>One ordered <c>movementdirections</c> list — a multi-direction event fans out to one settlement
/// instance per Movement (ADR-PC-032 §A9/§A10 / feature-design money-movement-settlement §6).</b> An event MAY
/// carry more than one <see cref="Movement"/>: a renewal records a rollover-debit AND an interest-credit on one
/// append (both are recorded in ONE transaction, so they cannot be split across two events without reopening
/// the orphan window — ADR-PC-032 §A9 chooses option b over one-event-per-Movement). A single settlement saga
/// instance branches to ONE direction, so the substrate fans a Movement-bearing event into one
/// <c>SettlementProcess</c> instance per Originated Movement, each gated by its own direction. To carry that,
/// this helper ALWAYS emits the directions as an ORDERED, comma-separated <c>movementdirections</c> list in
/// carrier order — one entry for a standalone leg (disbursement, maturity, coupon, early-termination), N
/// entries for a multi-direction event (e.g. <c>Debit,Credit</c>). The substrate splits the list and starts
/// one instance per entry at a deterministic per-Movement process id derived from <c>(ce_subject, index)</c>,
/// each gated by its own direction — so even a multi-direction event settles each leg correctly with no silent
/// loss, and per-account FIFO holds (each instance gets its own <c>process_id</c> = its own dispatcher FIFO
/// lane). The values stay closed-enum NAMES only — still no amount, no <see cref="Movement.AccountRef"/>, no
/// PII.
/// </para>
/// </remarks>
public static class MovementHeaders
{
    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying a <see cref="Movement"/>'s
    /// <see cref="MovementOrigin"/>. The relay promotes it to <c>ce_movementorigin</c>; the substrate's
    /// settlement module auto-starts on <c>Originated</c> (ADR-IC-018 §P5).</summary>
    public const string OriginKey = "movementorigin";

    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying the ORDERED, comma-separated
    /// list of EVERY Originated <see cref="SettlementDirection"/> on the event, in carrier order (e.g.
    /// <c>Debit</c> for a standalone leg, <c>Debit,Credit</c> for a renewal's rollover-debit +
    /// interest-credit). Present on every event with a cash leg — a single entry for the seven standalone
    /// legs, N entries for a multi-direction event. The relay promotes it to <c>ce_movementdirections</c>; the
    /// substrate splits it to fan the event out into one settlement instance per Movement and to branch each
    /// leg debit/credit (ADR-PC-032 §A9/§A10; ADR-IC-018 §D5). The values are the closed-enum member NAMES —
    /// no amount, no account ref, no PII (ADR-PC-004 §P2).</summary>
    public const string DirectionsKey = "movementdirections";

    /// <summary>The separator joining the ordered directions in the <see cref="DirectionsKey"/> list. A bare
    /// comma — the values are closed-enum NAMES (<c>Debit</c> / <c>Credit</c>), so a comma never collides with
    /// a value.</summary>
    public const string DirectionsSeparator = ",";

    /// <summary>
    /// Derive the <c>movementorigin</c> / <c>movementdirections</c> extension headers for a Movement-bearing
    /// event from its movements, or <c>null</c> when there is nothing to promote. Route a Movement-bearing
    /// event's <see cref="DomainEvent.IntegrationHeaders"/> override through this so the headers ride the
    /// event for free, family-agnostically.
    /// </summary>
    /// <param name="movements">The event's recorded movements (the carrier list). May be empty.</param>
    /// <returns>
    /// <para>
    /// <c>null</c> when the event carries no <see cref="MovementOrigin.Originated"/> movement (an
    /// <see cref="MovementOrigin.Observed"/>-only or movement-free event has NO cash leg to drive, so it
    /// declares no settlement headers and starts no saga — the relay leaves its standard CE header set
    /// untouched).
    /// </para>
    /// <para>
    /// Otherwise a two-entry dictionary: <c>movementorigin</c> = <c>Originated</c>, and
    /// <c>movementdirections</c> = the ORDERED, comma-separated list of every Originated direction in carrier
    /// order. A standalone leg yields a single-entry list (e.g. <c>Debit</c>); a multi-direction event yields
    /// the full list (e.g. <c>Debit,Credit</c>). The substrate fans the list out into one settlement instance
    /// per entry (ADR-PC-032 §A9/§A10, option b) — no silent loss, no guessed branch.
    /// </para>
    /// </returns>
    public static IReadOnlyDictionary<string, string>? ForOriginatedMovements(
        IReadOnlyList<Movement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        // Only Originated movements have a cash leg to drive (slot 2): an Observed movement arrived already
        // cleared, so its event starts no settlement saga. A movement-free or Observed-only event promotes
        // no settlement headers. Collect the Originated directions IN CARRIER ORDER — the order the substrate
        // fans out and the dispatcher's per-process FIFO preserves (feature-design §6 "in declared order").
        var directions = new List<SettlementDirection>();
        foreach (var movement in movements)
        {
            if (movement.Origin == MovementOrigin.Originated)
            {
                directions.Add(movement.Direction);
            }
        }

        if (directions.Count == 0)
        {
            return null;
        }

        // ONE ordered movementdirections list — always, whether the event carries one Originated direction or
        // several (ADR-PC-032 §A9/§A10, option b). The substrate splits it and starts one settlement instance
        // per entry, each gated by its own direction: a standalone leg's single-entry list fans to exactly one
        // instance (no behaviour change for the seven standalone legs), a renewal's two-entry list (Debit,Credit)
        // to two. The list is the closed-enum NAME of each direction joined by comma; a comma never collides
        // with a closed-enum name.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OriginKey] = MovementOrigin.Originated.ToString(),
            [DirectionsKey] = string.Join(DirectionsSeparator, directions.Select(static d => d.ToString())),
        };
    }
}
