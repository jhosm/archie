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
/// <b>Single-direction-per-event for v1 (the multi-Movement split, ADR-PC-032 §A8 / feature-design
/// money-movement-settlement §6).</b> A <c>movementdirection</c> header carries ONE value, but an event MAY
/// carry more than one <see cref="Movement"/> (a renewal records a rollover-debit AND an interest-credit).
/// The substrate's <c>IEventSubstitutor</c> reads exactly one <c>ce_movementdirection</c> and resolves to one
/// debit/credit branch, so a single event carrying BOTH a debit and a credit Originated movement cannot be
/// expressed by one header. This helper therefore promotes headers for the SINGLE-Originated-direction case
/// (every v1 standalone leg — disbursement, maturity, coupon, early-termination — moves money in ONE
/// direction) and FAILS LOUD on an event whose Originated movements disagree on direction, rather than
/// silently promoting a guessed branch. Resolving the genuine multi-direction event (one settlement instance
/// per Movement, each with its own direction) is a substrate-side follow-up; this seam stays the
/// single-direction producer until then.
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
    /// substrate's settlement substitutor branches debit/credit on it (ADR-IC-018 §D5).</summary>
    public const string DirectionKey = "movementdirection";

    /// <summary>
    /// Derive the <c>movementorigin</c> / <c>movementdirection</c> extension headers for a Movement-bearing
    /// event from its movements, or <c>null</c> when there is nothing to promote. Route a Movement-bearing
    /// event's <see cref="DomainEvent.IntegrationHeaders"/> override through this so the headers ride the
    /// event for free, family-agnostically.
    /// </summary>
    /// <param name="movements">The event's recorded movements (the carrier list). May be empty.</param>
    /// <returns>
    /// A two-entry dictionary (<c>movementorigin</c> = <c>Originated</c>, <c>movementdirection</c> = the
    /// shared debit/credit) when the event carries one or more <see cref="MovementOrigin.Originated"/>
    /// movements that AGREE on direction; <c>null</c> when the event carries no Originated movement (an
    /// <see cref="MovementOrigin.Observed"/>-only or movement-free event has NO cash leg to drive, so it
    /// declares no settlement headers and starts no saga — the relay leaves its standard CE header set
    /// untouched).
    /// </returns>
    /// <exception cref="InvalidOperationException">The event carries Originated movements that DISAGREE on
    /// direction (a single event with both a debit and a credit Originated movement) — the single
    /// <c>movementdirection</c> header cannot express both. This is the deliberate v1 multi-direction split
    /// (see the type remarks): fail loud rather than promote a guessed branch.</exception>
    public static IReadOnlyDictionary<string, string>? ForOriginatedMovements(
        IReadOnlyList<Movement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        // Only Originated movements have a cash leg to drive (slot 2): an Observed movement arrived already
        // cleared, so its event starts no settlement saga. A movement-free or Observed-only event promotes
        // no settlement headers.
        SettlementDirection? direction = null;
        foreach (var movement in movements)
        {
            if (movement.Origin != MovementOrigin.Originated)
            {
                continue;
            }

            if (direction is { } pinned && pinned != movement.Direction)
            {
                // Both a debit and a credit Originated movement on ONE event: the single movementdirection
                // header cannot carry both, and the substrate's substitutor reads exactly one. Fail loud —
                // the multi-direction event is a substrate-side follow-up, not a silently-guessed branch.
                throw new InvalidOperationException(
                    "A Movement-bearing event carries Originated movements in BOTH directions "
                    + $"({pinned} and {movement.Direction}); the single ce_movementdirection header cannot "
                    + "express both (ADR-PC-032 §A8 multi-Movement split). Split the directions across "
                    + "events, or extend the substrate to one settlement instance per Movement.");
            }

            direction ??= movement.Direction;
        }

        if (direction is not { } resolved)
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OriginKey] = MovementOrigin.Originated.ToString(),
            [DirectionKey] = resolved.ToString(),
        };
    }
}
