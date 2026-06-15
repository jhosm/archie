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
/// The constitution saga reacts to events from TWO sources, each on its own topic (ADR-IC-003 §S2
/// 2026-06-15 amendment; bd babelstone-t7o3.11):
/// <list type="bullet">
///   <item><b><see cref="ConstitutionProcessTopic"/> (<c>deposits.process.events</c>)</b> — the internal
///   DOMAIN topic for ORCHESTRATOR-produced process events (the start signal
///   <c>ConstitutionRequested</c> and any future process-internal facts). It stays in the Deposits
///   context, not an integration topic (Document 05 §1 / Document 10 "Only the Deposits service can
///   produce to … <c>deposits.process.events</c>"). The substrate's <see cref="SagaInboxEvent"/> fixtures
///   carry this as their <c>source_topic</c>.</item>
///   <item><b><see cref="TermDepositIntegrationTopic"/> (<c>term_deposit</c>)</b> — the FAMILY
///   INTEGRATION topic the engine publishes every term-deposit fact to. The engine's
///   <c>OutboxDrainer</c> names the topic after the <c>aggregate_type</c> (<c>term_deposit</c>) and keeps
///   the engine kernel family-agnostic; it never routes a fact to a per-process topic. So the closing
///   engine fact <c>DepositConstituted</c> (the VALUE of
///   <see cref="Saga.ConstitutionProcess.ProcessConstituted"/>; bd babelstone-3klm) arrives HERE, with
///   <c>ce_subject = aggregate_id</c>. The saga POSTs <c>deposit_id = process_id</c> to the engine
///   (bd babelstone-t7o3.11 / 3k10), so <c>aggregate_id == process_id</c> and the consume loop correlates
///   the integration fact back to the saga by identity (<c>ce_subject → process_id</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>This IS the multi-saga dispatch model (Fork A, bd babelstone-t7o3.11).</b> The orchestrator —
/// not the engine — bridges the family topic to the saga. The consume loop keys the transition table on
/// the inbound event's TYPE NAME alone (the <c>ce_type</c>'s record name, ADR-IC-003 §P2); an event with
/// no <c>(state, type)</c> transition is a benign no-op (NoTransition → committed past). Each saga runs
/// in its OWN Kafka consumer group, so the renewal saga (H.3, bd babelstone-mtto) extends this by
/// subscribing the SAME family topic under its own group and dispatching the renewal facts it cares about
/// — no engine change, no per-process topic, no shared-group contention.
/// <para>
/// <b>The hosting substrate is now multi-saga (bd babelstone-mtto PR1).</b> The advance handler, the
/// command router, and the result-event bridge are all keyed by <c>saga_type</c>, so a second saga is a
/// matter of registering its <see cref="Saga.ISagaStateMachine"/> /
/// <see cref="Dispatch.ISagaCommandRouter"/> / <see cref="Saga.IResultEventBridge"/> alongside the
/// constitution ones. The renewal saga (PR2) registers its OWN
/// <see cref="SagaInboxConsumerOptions"/> (a distinct <c>GroupId</c>) with a <c>Topics</c> list that
/// includes <see cref="TermDepositIntegrationTopic"/> — it does NOT extend
/// <see cref="ConstitutionProcessTopics"/>, which stays the constitution consumer's subscription. The
/// two consumer groups read the same family topic independently; only the saga whose transition table
/// has a row for an inbound fact advances on it.</para>
/// </para>
/// <para>
/// Kept as named constants (not buried literals) so the host wiring, the consume loop, and any test
/// fixture reference the SAME topic names — a drift between "what the loop subscribes to" and "where
/// the events are produced" is impossible to introduce silently.
/// </para>
/// </remarks>
public static class SagaConsumeTopics
{
    /// <summary>The internal domain topic for ORCHESTRATOR-produced process events — the start signal
    /// (<c>ConstitutionRequested</c>) and any future process-internal facts (Document 05 §1). NOT where
    /// the engine's integration facts arrive — those land on <see cref="TermDepositIntegrationTopic"/>.</summary>
    public const string ConstitutionProcessTopic = "deposits.process.events";

    /// <summary>The term-deposit FAMILY INTEGRATION topic the engine's <c>OutboxDrainer</c> publishes
    /// every term-deposit fact to (topic = <c>aggregate_type</c>; ADR-IC-004 §Consequences). The closing
    /// engine fact <c>DepositConstituted</c> arrives here with <c>ce_subject = aggregate_id</c>, which the
    /// saga pins to its own <c>process_id</c> by POSTing <c>deposit_id = process_id</c> to the engine
    /// (bd babelstone-t7o3.11 / 3k10). The saga subscribes to it to advance on the engine's REAL event
    /// (ADR-PC-029 slot 2), keeping the engine kernel family-agnostic (the orchestrator reads the family
    /// topic; the engine adds no routing).</summary>
    public const string TermDepositIntegrationTopic = "term_deposit";

    /// <summary>The topic set a constitution-saga consumer subscribes to: the orchestrator-produced
    /// process topic AND the engine's term-deposit integration topic (bd babelstone-t7o3.11). A list so a
    /// future saga that reacts to events on more than one topic — e.g. the renewal saga (H.3,
    /// bd babelstone-mtto) on its own consumer group over the SAME family topic — extends it without
    /// changing the loop's shape.</summary>
    public static IReadOnlyList<string> ConstitutionProcessTopics { get; } =
        [ConstitutionProcessTopic, TermDepositIntegrationTopic];
}
