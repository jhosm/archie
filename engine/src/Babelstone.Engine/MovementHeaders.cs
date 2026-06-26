namespace Babelstone.Engine;

/// <summary>
/// The GENERIC engine-spine seam that promotes an event's <see cref="Movement"/> origin/direction to the
/// CloudEvents extension headers the substrate-owned settlement saga auto-starts on (ADR-PC-032 §A7/§A8;
/// ADR-IC-018 §P5/§D5). In plain English: when a family event records that money was decided
/// (<see cref="MovementOrigin.Originated"/>), the settlement saga downstream needs to know two things WITHOUT
/// opening the (PII-free but Avro-encoded) payload — was it a debit or a credit, and is there a cash leg to
/// drive at all. This helper turns a Movement-bearing event's movements into the two closed-enum header
/// values (<c>movementorigin</c> / <c>movementdirection</c>) the engine's outbox relay then promotes to
/// <c>ce_movementorigin</c> / <c>ce_movementdirection</c> — the operational-metadata channel, never the
/// payload, never PII (<see cref="DomainEvent.IntegrationHeaders"/> / ADR-PC-004 §P2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic and family-agnostic (ADR-PC-021 §P2 / §D2).</b> This keys ONLY on the spine's
/// <see cref="Movement"/> atom and its two closed enums (<see cref="MovementOrigin"/> /
/// <see cref="SettlementDirection"/>) — it names no family, so a Movement-bearing event in ANY family gets
/// the headers for free by routing its <see cref="DomainEvent.IntegrationHeaders"/> override through
/// <see cref="ForOriginatedMovements"/>. It is the producer counterpart of the substrate's consumer seam
/// (the settlement module's <c>movementorigin</c> auto-start predicate + the <c>movementdirection</c>
/// substitutor): the same two closed-enum strings the saga reads off the headers.
/// </para>
/// <para>
/// <b>Closed-enum values only — no PII, no amount, no account ref (ADR-PC-004 §P2 / ADR-PC-032 §A8).</b> The
/// header values are <see cref="MovementOrigin"/>'s and <see cref="SettlementDirection"/>'s member NAMES
/// (<c>Originated</c> / <c>Observed</c> and <c>Debit</c> / <c>Credit</c>) — the SAME stable strings
/// <c>MovementCarrier</c> writes for the Avro enum symbols, and the SAME strings the substrate's
/// <c>SettlementSagaModule.OriginatedValue</c> / <c>SettlementProcess.DirectionHeader</c> match on. The
/// amount, the opaque <see cref="Movement.AccountRef"/>, and the <see cref="Movement.CommandId"/> stay in the
/// payload; only the two routing discriminators ride the headers.
/// </para>
/// <para>
/// <b>The SCA freshness claims ride the SAME hop (ADR-PC-032 §A8; t7o3.19).</b> For an Originated
/// money-mover subject to step-up SCA, the gateway-attested <c>acr</c> / <c>auth_time</c> claims propagate
/// forward to the event-auto-started settlement leg on these SAME Movement-bearing-event CloudEvents headers
/// (the operational-metadata channel, never the payload, never PII — the same posture
/// <c>movementorigin</c> / <c>movementdirection</c> take here). They will be promoted as their own extension
/// attributes (e.g. <c>ce_scaacr</c> / <c>ce_scaauthtime</c>) co-carried on the engine boundary that appends
/// the event — NOT populated here: that gate and its non-double-populate enforcement are bd t7o3.19's, and
/// this seam stays the movement-routing producer. When t7o3.19 lands its carriage, it adds entries to the
/// SAME <see cref="DomainEvent.IntegrationHeaders"/> map this helper seeds (no double-populate: distinct
/// keys), so the two producers compose on one hop.
/// </para>
/// <para>
/// <b>Multi-direction events fan out to one settlement instance per Movement (ADR-PC-032 §A9 amendment
/// 2026-06-26 / feature-design money-movement-settlement §6).</b> A <c>movementdirection</c> header carries ONE
/// value, but an event MAY carry more than one <see cref="Movement"/> (a renewal records a rollover-debit AND
/// an interest-credit). The substrate's <c>IEventSubstitutor</c> reads exactly one <c>ce_movementdirection</c>
/// and resolves to one debit/credit branch, so a single event carrying BOTH a debit and a credit Originated
/// movement cannot be settled by one saga instance. The chosen model (ADR-PC-032 §A9, option b) is
/// <b>per-Movement header carriage with one <c>SettlementProcess</c> instance per Originated Movement, each
/// gated by its own <c>movementdirection</c></b> — NOT one-event-per-Movement (option a), which would split
/// the renewal's two legs across two appends and break the append-first atomicity of slot 5 (both Movements
/// are recorded in ONE transaction). So this helper ALWAYS emits the <c>movementdirection</c> for the FIRST
/// Originated direction (so the single-direction path is byte-for-byte unchanged and the auto-start branches
/// the primary instance), and ADDITIONALLY emits an ordered <c>movementdirections</c> composite (e.g.
/// <c>Debit,Credit</c>) listing every Originated direction in carrier order whenever the event carries more
/// than one Originated direction. The substrate reads the composite and starts one settlement instance per
/// entry at a deterministic per-Movement process id derived from <c>(ce_subject, index)</c>, each gated by its
/// own direction — so the multi-direction event settles each leg correctly with no silent loss, and
/// per-account FIFO holds (each instance gets its own <c>process_id</c> = its own dispatcher FIFO lane). The
/// values stay closed-enum NAMES only — still no amount, no <see cref="Movement.AccountRef"/>, no PII.
/// </para>
/// </remarks>
public static class MovementHeaders
{
    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying a <see cref="Movement"/>'s
    /// <see cref="MovementOrigin"/>. The relay promotes it to <c>ce_movementorigin</c>; the substrate's
    /// settlement module auto-starts on <c>Originated</c> (ADR-IC-018 §P5).</summary>
    public const string OriginKey = "movementorigin";

    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying a <see cref="Movement"/>'s
    /// <see cref="SettlementDirection"/>. The relay promotes it to <c>ce_movementdirection</c>; the
    /// substrate's settlement substitutor branches debit/credit on it (ADR-IC-018 §D5). On a multi-direction
    /// event this carries the FIRST Originated direction (the primary instance's branch); the full ordered set
    /// rides <see cref="DirectionsKey"/>.</summary>
    public const string DirectionKey = "movementdirection";

    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying the ORDERED, comma-separated
    /// list of EVERY Originated <see cref="SettlementDirection"/> on a multi-direction event, in carrier order
    /// (e.g. <c>Debit,Credit</c> for a renewal's rollover-debit + interest-credit). Present ONLY when the event
    /// carries more than one Originated direction; a single-direction event omits it (its lone direction is on
    /// <see cref="DirectionKey"/> alone). The relay promotes it to <c>ce_movementdirections</c>; the substrate
    /// reads it to fan the event out into one settlement instance per Movement (ADR-PC-032 §A9, option b). The
    /// values are the closed-enum member NAMES — no amount, no account ref, no PII (ADR-PC-004 §P2).</summary>
    public const string DirectionsKey = "movementdirections";

    /// <summary>The separator joining the ordered directions in the <see cref="DirectionsKey"/> composite. A
    /// bare comma — the values are closed-enum NAMES (<c>Debit</c> / <c>Credit</c>), so a comma never collides
    /// with a value.</summary>
    public const string DirectionsSeparator = ",";

    /// <summary>
    /// Derive the <c>movementorigin</c> / <c>movementdirection</c> extension headers for a Movement-bearing
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
    /// For a SINGLE Originated direction (every standalone leg — disbursement, maturity, coupon,
    /// early-termination — and any set of same-direction movements): a two-entry dictionary
    /// (<c>movementorigin</c> = <c>Originated</c>, <c>movementdirection</c> = the shared direction). No
    /// composite is emitted (the substrate starts ONE instance, byte-for-byte the established path).
    /// </para>
    /// <para>
    /// For MULTIPLE Originated directions (a renewal's rollover-debit + interest-credit): a three-entry
    /// dictionary that ADDS <c>movementdirections</c> = the ordered, comma-separated list of every Originated
    /// direction in carrier order. <c>movementdirection</c> still carries the FIRST direction (the primary
    /// instance's branch). The substrate fans this out into one settlement instance per entry (ADR-PC-032 §A9,
    /// option b) — no silent loss, no guessed branch.
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

        // The primary instance's branch is the FIRST Originated direction (byte-stable: carrier order is
        // deterministic). A single-direction event stops here — its lone direction on movementdirection alone,
        // exactly the established single-instance path (no composite, no behaviour change for the 7 standalone
        // legs).
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OriginKey] = MovementOrigin.Originated.ToString(),
            [DirectionKey] = directions[0].ToString(),
        };

        // A genuinely MULTI-DIRECTION event (e.g. a renewal: rollover-Debit + interest-Credit) ALSO carries the
        // ordered composite the substrate fans out on — one settlement instance per Originated Movement, each
        // gated by its own direction (ADR-PC-032 §A9, option b — the case that previously FAILED LOUD). The
        // composite is the ENUM-NAME list joined by comma; a comma never collides with a closed-enum name. It
        // is emitted ONLY when the Originated movements span MORE THAN ONE distinct direction: a same-direction
        // set (two debits) already resolves to ONE branch and is settled by a single instance, so its wire
        // shape stays movementorigin + movementdirection only — unchanged from before this amendment.
        var distinctDirections = directions.Distinct().Count();
        if (distinctDirections > 1)
        {
            headers[DirectionsKey] = string.Join(
                DirectionsSeparator, directions.Select(static d => d.ToString()));
        }

        return headers;
    }
}
