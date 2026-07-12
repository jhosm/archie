namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// Configuration for the orchestrator's Redpanda→saga consume loop (the orchestrator analogue of the
/// engine's <c>InboxConsumerOptions</c>). Everything the loop needs to subscribe, deduplicate by
/// <c>message_id</c> (the CloudEvents <c>ce_id</c> header, ADR-IC-015), drive the saga, and commit
/// offsets (ADR-IC-003 §S2 "a Redpanda consumer like every other service").
/// </summary>
/// <remarks>
/// The bootstrap address is a broker ENDPOINT, not a credential, so it resolves straight from
/// configuration; the PostgreSQL connection string the saga persists through resolves at the
/// composition root through the credential boundary (ADR-PC-004 Amendment A1), distinct from the
/// migration-role connection. No field here is or carries PII (ADR-PC-004 §P2).
/// </remarks>
public sealed record SagaInboxConsumerOptions
{
    /// <summary>PostgreSQL connection string for the orchestrator application database (the
    /// <c>saga_state</c>, <c>saga_transition</c>, <c>saga_outbox</c>, and <c>inbox</c> tables).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Kafka bootstrap servers for the Redpanda broker (e.g. "localhost:19092").</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// The Kafka consumer group id. Stable per logical consumer (ADR-IC-001): the offsets the group
    /// commits are what make a restart resume where it left off rather than re-reading the whole
    /// topic. Two orchestrator instances in the same group share the partitions; the inbox dedup
    /// absorbs the at-least-once redelivery a rebalance can replay (effectively-once advance).
    /// </summary>
    public required string GroupId { get; init; }

    /// <summary>
    /// The topics the saga reacts to — supplied by the family's saga module (its
    /// <c>ISagaModule.ConsumeTopics</c>), so the substrate names no family topic. For the constitution
    /// saga this is the orchestrator-produced process topic AND the engine's term-deposit FAMILY
    /// INTEGRATION topic, where the closing <c>DepositConstituted</c> fact arrives (bd babelstone-t7o3.11
    /// Fork A). Each saga type runs its own consumer group over its own topic set (ADR-IC-018 §P4).
    /// </summary>
    public required IReadOnlyList<string> Topics { get; init; }

    /// <summary>
    /// Where a brand-new group starts when it has no committed offset: Earliest replays the whole
    /// retained topic (the default — a fresh consumer must not silently skip the backlog), Latest
    /// joins at the tail. After the first commit this is irrelevant — the committed offset wins.
    /// </summary>
    public bool StartFromEarliest { get; init; } = true;

    /// <summary>
    /// Max time one <c>Consume</c> call blocks waiting for a record before the loop comes back round
    /// (so cancellation is observed promptly on an idle topic). Not a delivery deadline — an idle
    /// poll just loops.
    /// </summary>
    public TimeSpan ConsumeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Optional librdkafka debug contexts (e.g. <c>"cgrp,broker,consumer"</c>) surfaced through the
    /// loop's log handler. Off by default (<c>null</c>); set via <c>Kafka:Debug</c> to diagnose a
    /// consumer that connects but never joins its group (bd babelstone-u79p.17) — it turns the otherwise
    /// invisible librdkafka group-coordination sequence into logged lines. Verbose; leave unset in
    /// normal operation.
    /// </summary>
    public string? KafkaDebug { get; init; }
}
