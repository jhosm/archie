namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The Redpanda topics the constitution saga reacts to (ADR-IC-003 §S2 "the orchestrator is a
/// Redpanda consumer like every other service"; Document 05 §1 "the Constitution Saga Orchestrator
/// subscribes to this topic"). The orchestrator keys its <see cref="Saga.ConstitutionProcess"/>
/// transition table on the inbound event's TYPE NAME alone (ADR-IC-003 §P2), never on the event's
/// PII-free payload — so the consume loop need only read the CloudEvents headers off each record to
/// build a <see cref="SagaInboxEvent"/>, and the topic set is just "where those events arrive".
/// </summary>
/// <remarks>
/// <para>
/// The constitution saga's triggering events — the start signal (<c>ConstitutionRequested</c>), the
/// validation results (<c>BalanceReserved</c>, <c>LimitsValidated</c>, …), and the closing engine fact
/// <c>DepositConstituted</c> (the VALUE of <see cref="Saga.ConstitutionProcess.ProcessConstituted"/>;
/// bd babelstone-3klm) — all flow on the internal DOMAIN topic <c>deposits.process.events</c>
/// (Document 05 §1 "publishes to the internal topic <c>deposits.process.events</c> … the
/// Constitution Saga Orchestrator subscribes to this topic"; it stays in the Deposits context, not an
/// integration topic — Document 10 "Only the Deposits service can produce to … <c>deposits.process.events</c>").
/// This is the same <c>source_topic</c> every existing <see cref="SagaInboxEvent"/> fixture carries.
/// </para>
/// <para>
/// <b>FLAG (bd babelstone-3klm) — the engine's relay topic differs from this committed topic.</b> The
/// ADR-IC-003 2026-06-14 amendment and Document 05 commit the engine to relaying <c>DepositConstituted</c>
/// on <c>deposits.process.events</c>, and that is what the saga subscribes to. But the engine's
/// <c>OutboxDrainer</c> today publishes every fact to a topic named after its <c>aggregate_type</c>
/// (<c>term_deposit</c> for a deposit stream), with NO router re-publishing <c>DepositConstituted</c> to
/// <c>deposits.process.events</c>. Closing THAT gap (a routed/dedicated process topic, or subscribing the
/// saga to <c>term_deposit</c>) is a separate engine-relay-routing decision left for the maintainer; this
/// change closes only the EVENT-NAME mismatch so a <c>DepositConstituted</c> record arriving on the
/// committed topic drives the saga to COMPLETED (ADR-PC-029 slot 2).
/// </para>
/// <para>
/// Kept as a named constant (not a buried literal) so the host wiring, the consume loop, and any test
/// fixture reference the SAME topic name — a drift between "what the loop subscribes to" and "where
/// the events are produced" is impossible to introduce silently.
/// </para>
/// </remarks>
public static class SagaConsumeTopics
{
    /// <summary>The internal domain topic the <see cref="Saga.ConstitutionProcess"/> saga's
    /// triggering events flow on (Document 05 §1).</summary>
    public const string ConstitutionProcessTopic = "deposits.process.events";

    /// <summary>The default topic set a constitution-saga consumer subscribes to (today: just the one
    /// process topic). A list so a future saga that reacts to events on more than one topic — e.g. a
    /// renewal saga (H.3) keyed on a separate aggregate's topic — extends it without changing the
    /// loop's shape.</summary>
    public static IReadOnlyList<string> ConstitutionProcessTopics { get; } = [ConstitutionProcessTopic];
}
