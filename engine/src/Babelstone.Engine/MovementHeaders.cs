using System.Globalization;

namespace Babelstone.Engine;

/// <summary>
/// The GENERIC engine-spine seam that promotes an event's <see cref="Movement"/> origin/direction to the
/// CloudEvents extension headers the substrate-owned settlement saga auto-starts on (ADR-PC-032;
/// ADR-IC-018). In plain English: when a family event records that money was decided
/// (<see cref="MovementOrigin.Originated"/>), the settlement saga downstream needs to know two things WITHOUT
/// opening the (PII-free but Avro-encoded) payload — is there a cash leg to drive at all, and which way (or
/// ways) the money moves. This helper turns a Movement-bearing event's movements into two closed-enum header
/// values (<c>movementorigin</c> and the ordered <c>movementdirections</c> list) the engine's outbox relay
/// then promotes to <c>ce_movementorigin</c> / <c>ce_movementdirections</c> — the operational-metadata
/// channel, never the payload, never PII (<see cref="DomainEvent.IntegrationHeaders"/> / ADR-PC-004).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic and family-agnostic (ADR-PC-021).</b> This keys ONLY on the spine's
/// <see cref="Movement"/> atom and its two closed enums (<see cref="MovementOrigin"/> /
/// <see cref="SettlementDirection"/>) — it names no family, so a Movement-bearing event in ANY family gets
/// the headers for free by routing its <see cref="DomainEvent.IntegrationHeaders"/> override through
/// <see cref="ForOriginatedMovements(IReadOnlyList{Movement})"/> (or the counterparty-aware overload,
/// <see cref="ForOriginatedMovements(IReadOnlyList{Movement}, SettlementTarget)"/>, when the leg settles
/// against the engine-owned CA — ADR-PC-043). It is the producer counterpart of the substrate's consumer seam
/// (the settlement module's <c>movementorigin</c> auto-start predicate + the <c>movementdirections</c>
/// fan-out/substitutor): the same closed-enum strings the saga reads off the headers.
/// </para>
/// <para>
/// <b>Closed-enum values only — no PII, no amount, no account ref (ADR-PC-004 / ADR-PC-032).</b> The
/// header values are <see cref="MovementOrigin"/>'s and <see cref="SettlementDirection"/>'s member NAMES
/// (<c>Originated</c> / <c>Observed</c> and <c>Debit</c> / <c>Credit</c>) — the SAME stable strings
/// <c>MovementCarrier</c> writes for the Avro enum symbols, and the SAME strings the substrate's
/// <c>SettlementSagaModule.OriginatedValue</c> / <c>SettlementProcess</c> direction branch match on. The
/// amount, the opaque <see cref="Movement.AccountRef"/>, and the <see cref="Movement.CommandId"/> stay in the
/// payload on the legacy-DDA / no-target path; only the routing discriminators ride the headers there. The
/// <b>engine-CA</b> overload is the bounded exception (ADR-PC-043 §D5 amendment 2026-07-11): it ALSO promotes
/// the per-movement destination <see cref="Movement.AccountRef"/> + integer-cents amount as the ordered
/// <see cref="AccountRefsKey"/> / <see cref="AmountsKey"/> lists — the settlement-command-body fields the
/// payload-blind substrate forwards untouched (opaque refs + cents, still no PII; the
/// <see cref="Movement.CommandId"/> stays in the payload).
/// </para>
/// <para>
/// <b>The SCA freshness claims ride the SAME hop (ADR-PC-032).</b> For an Originated
/// money-mover subject to step-up SCA, the gateway-attested <c>acr</c> / <c>auth_time</c> claims propagate
/// forward to the event-auto-started settlement leg on these SAME Movement-bearing-event CloudEvents headers
/// (the operational-metadata channel, never the payload, never PII — the same posture
/// <c>movementorigin</c> / <c>movementdirections</c> take here). They are promoted as their own extension
/// attributes (e.g. <c>ce_scaacr</c> / <c>ce_scaauthtime</c>) co-carried on the engine boundary that appends
/// the event — NOT populated here: that gate and its non-double-populate enforcement belong to the SCA-claims producer, and
/// this seam stays the movement-routing producer. That carriage adds entries to the SAME
/// <see cref="DomainEvent.IntegrationHeaders"/> map this helper seeds (no double-populate: distinct keys), so
/// the two producers compose on one hop.
/// </para>
/// <para>
/// <b>One ordered <c>movementdirections</c> list — a multi-direction event fans out to one settlement
/// instance per Movement (ADR-PC-032 / feature-design money-movement-settlement §6).</b> An event MAY
/// carry more than one <see cref="Movement"/>: a renewal records a rollover-debit AND an interest-credit on one
/// append (both are recorded in ONE transaction, so they cannot be split across two events without reopening
/// the orphan window — ADR-PC-032 chooses option b over one-event-per-Movement). A single settlement saga
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
    /// settlement module auto-starts on <c>Originated</c> (ADR-IC-018).</summary>
    public const string OriginKey = "movementorigin";

    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying the ORDERED, comma-separated
    /// list of EVERY Originated <see cref="SettlementDirection"/> on the event, in carrier order (e.g.
    /// <c>Debit</c> for a standalone leg, <c>Debit,Credit</c> for a renewal's rollover-debit +
    /// interest-credit). Present on every event with a cash leg — a single entry for the seven standalone
    /// legs, N entries for a multi-direction event. The relay promotes it to <c>ce_movementdirections</c>; the
    /// substrate splits it to fan the event out into one settlement instance per Movement and to branch each
    /// leg debit/credit (ADR-PC-032; ADR-IC-018). The values are the closed-enum member NAMES —
    /// no amount, no account ref, no PII (ADR-PC-004).</summary>
    public const string DirectionsKey = "movementdirections";

    /// <summary>The separator joining the ordered directions in the <see cref="DirectionsKey"/> list. A bare
    /// comma — the values are closed-enum NAMES (<c>Debit</c> / <c>Credit</c>), so a comma never collides with
    /// a value.</summary>
    public const string DirectionsSeparator = ",";

    /// <summary>The extension-attribute key (ce_-stripped, lowercase) carrying the settlement COUNTERPARTY the
    /// Originated cash leg settles against (ADR-PC-043 slot 1). The relay promotes it to
    /// <c>ce_settlementtarget</c>; the substrate's <c>SettlementCommandRouter</c> keys the counterparty
    /// selection on it — <b>header-only</b>, so the substrate stays payload-blind (ADR-IC-018 §D5) and MUST NOT
    /// read <c>Movement.AccountRef</c> from the body. A closed-enum value (<see cref="EngineCaValue"/> |
    /// <see cref="LegacyDdaValue"/>), no PII (ADR-PC-004).</summary>
    public const string SettlementTargetKey = "settlementtarget";

    /// <summary>The <see cref="SettlementTargetKey"/> value routing the leg to the engine-OWNED current-account
    /// family (ADR-PC-037 / ADR-PC-043 — the single-owner engine-CA settlement counterparty).</summary>
    public const string EngineCaValue = "engine-ca";

    /// <summary>The <see cref="SettlementTargetKey"/> value routing the leg to the LEGACY demand-deposit core
    /// over the ACL (the pre-ADR-PC-043 path — ADR-PC-016). The DEFAULT when no target is promoted, so a family
    /// that does not opt into engine-CA settlement keeps legacy routing UNCHANGED.</summary>
    public const string LegacyDdaValue = "legacy-dda";

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
    /// per entry (ADR-PC-032, option b) — no silent loss, no guessed branch.
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

        // ONE ordered movementdirections list — always, single or multi-direction (ADR-PC-032, option b).
        // Carrier order; the substrate splits it and fans out one settlement instance per entry. See the
        // <returns> doc and DirectionsKey for the closed-enum-name encoding.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OriginKey] = MovementOrigin.Originated.ToString(),
            [DirectionsKey] = string.Join(DirectionsSeparator, directions.Select(static d => d.ToString())),
        };
    }

    /// <summary>
    /// Derive the <c>movementorigin</c> / <c>movementdirections</c> headers AND promote the settlement
    /// COUNTERPARTY as <c>settlementtarget</c> (ADR-PC-043 slot 1), or <c>null</c> when there is no Originated
    /// cash leg to drive. Use this overload from a family whose Originated movement settles against the
    /// engine-owned current account (<see cref="SettlementTarget.EngineCa"/>); the substrate's router then
    /// keys the counterparty selection on the promoted header ALONE (never <see cref="Movement.AccountRef"/> —
    /// the payload-blind boundary, ADR-IC-018 §D5).
    /// </summary>
    /// <param name="movements">The event's recorded movements (the carrier list). May be empty.</param>
    /// <param name="target">The settlement counterparty the cash leg settles against. <see cref="SettlementTarget.LegacyDda"/>
    /// promotes no target header (the router defaults to legacy — so this overload with the legacy target is
    /// byte-identical to the no-target overload, keeping legacy routing UNCHANGED);
    /// <see cref="SettlementTarget.EngineCa"/> adds <c>settlementtarget = engine-ca</c>.</param>
    /// <returns><c>null</c> for an Observed-only / movement-free event (as the no-target overload); otherwise
    /// the origin + directions map. For an <see cref="SettlementTarget.EngineCa"/> target it ALSO carries the
    /// <c>settlementtarget</c> counterparty header PLUS the per-Originated-movement <c>movementaccountrefs</c> /
    /// <c>movementamounts</c> ORDERED lists (ADR-PC-043 §D5 amendment 2026-07-11, bd <c>babelstone-u79p.13</c>):
    /// the promoted persistent <see cref="Movement.AccountRef"/> destination + the integer-cents
    /// <c>Money</c> the CA writer lands, in carrier order, one entry per Originated movement (parallel to
    /// <c>movementdirections</c>). These are the SETTLEMENT-COMMAND-BODY fields the substrate forwards untouched
    /// (never routing inputs — routing keys on <c>settlementtarget</c> ALONE, the payload-blind boundary
    /// ADR-IC-018 §D5). A <see cref="SettlementTarget.LegacyDda"/> target promotes none of these — the legacy
    /// core resolves the account from the process-scoped business reference, so a legacy leg stays byte-identical
    /// to the no-target shape. The values are opaque refs + cents — no PII (ADR-PC-004).</returns>
    public static IReadOnlyDictionary<string, string>? ForOriginatedMovements(
        IReadOnlyList<Movement> movements, SettlementTarget target)
    {
        var headers = ForOriginatedMovements(movements);
        if (headers is null)
        {
            return null;
        }

        // Legacy-DDA is the DEFAULT the router falls back to when no target header is present, so promoting it
        // would be redundant wire weight — a legacy leg stays byte-identical to the no-target shape (legacy
        // routing UNCHANGED). Only the engine-CA counterparty needs the explicit header to divert the router.
        if (target == SettlementTarget.LegacyDda)
        {
            return headers;
        }

        // ADR-PC-043 §D5 (2026-07-11, bd u79p.13): an engine-CA leg promotes the per-movement DESTINATION
        // (the persistent Movement.AccountRef) and AMOUNT (integer cents) as ORDERED lists parallel to
        // movementdirections — carrier order, one entry per Originated movement — so the payload-blind
        // substrate can forward them into the CA-apply command body (SettlementCommandPayloadFactory) without
        // reading the Avro payload. Fitness: SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED (CA-17) — the engine-CA leg's
        // AccountRef equals THIS promoted value, never the ACCT-{processId} placeholder. Only Originated
        // movements have a cash leg, in the SAME order ForOriginatedMovements collected the directions.
        var accountRefs = new List<string>();
        var amounts = new List<string>();
        foreach (var movement in movements)
        {
            if (movement.Origin == MovementOrigin.Originated)
            {
                accountRefs.Add(movement.AccountRef);
                amounts.Add(movement.Amount.Cents.ToString(CultureInfo.InvariantCulture));
            }
        }

        var withTarget = new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            [SettlementTargetKey] = EngineCaValue,
            [AccountRefsKey] = string.Join(DirectionsSeparator, accountRefs),
            [AmountsKey] = string.Join(DirectionsSeparator, amounts),
        };
        return withTarget;
    }

    /// <summary>The ce_-stripped extension-attribute key carrying the ORDERED, comma-separated list of every
    /// Originated movement's persistent destination <see cref="Movement.AccountRef"/> — parallel to
    /// <see cref="DirectionsKey"/>, one entry per movement in carrier order (ADR-PC-043 §D5). Promoted ONLY on
    /// an engine-CA leg; the substrate forwards the fanned-out per-leg entry into the CA-apply command body as
    /// the credit/debit destination, never as a routing input (ADR-IC-018 §D5).</summary>
    public const string AccountRefsKey = "movementaccountrefs";

    /// <summary>The ce_-stripped extension-attribute key carrying the ORDERED, comma-separated list of every
    /// Originated movement's integer-cents <c>Money</c> amount — parallel to <see cref="DirectionsKey"/>,
    /// one entry per movement in carrier order (ADR-PC-043 §D5, the in-band guard against WRONG-AMOUNT that
    /// every identity-keyed dedup misses). Promoted ONLY on an engine-CA leg.</summary>
    public const string AmountsKey = "movementamounts";
}

/// <summary>
/// The CLOSED set of settlement counterparties an Originated <see cref="Movement"/> can settle against
/// (ADR-PC-043 slot 1). Exactly two members — the engine either settles the cash leg against a current
/// account it OWNS, or against the LEGACY demand core over the ACL; there is no third counterparty. The
/// member maps to the closed-enum wire value the relay promotes as <c>ce_settlementtarget</c>
/// (<see cref="MovementHeaders.EngineCaValue"/> / <see cref="MovementHeaders.LegacyDdaValue"/>), which the
/// substrate's router keys on — never the payload's <see cref="Movement.AccountRef"/>.
/// </summary>
public enum SettlementTarget
{
    /// <summary>Settle against the LEGACY demand-deposit core over the ACL (ADR-PC-016). The enum's ZERO value,
    /// and the router's fallback when NO <c>ce_settlementtarget</c> header is promoted — but the explicit
    /// opt-OUT selection on the PRODUCER side: the term-deposit producer default is now
    /// <see cref="EngineCa"/>, so a family sets this only when a leg must stay on the legacy core, and it
    /// promotes no target header so the router routes it exactly as before (UNCHANGED).</summary>
    LegacyDda,

    /// <summary>Settle against the engine-OWNED current-account family (ADR-PC-037 / ADR-PC-043). The
    /// single-owner counterparty the router diverts the leg to on the promoted <c>ce_settlementtarget</c>
    /// header, and the PRODUCER default for the term-deposit family. The producer default is governed by the
    /// event's property initializer, not this enum's zero value.</summary>
    EngineCa,
}
