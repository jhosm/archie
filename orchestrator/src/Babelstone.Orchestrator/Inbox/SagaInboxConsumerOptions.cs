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
    /// The topics the saga reacts to — the orchestrator-produced process topic
    /// (<see cref="SagaConsumeTopics.ConstitutionProcessTopic"/>, Document 05 §1) AND the engine's
    /// term-deposit FAMILY INTEGRATION topic (<see cref="SagaConsumeTopics.TermDepositIntegrationTopic"/>),
    /// where the closing <c>DepositConstituted</c> fact arrives (bd babelstone-t7o3.11 Fork A). In
    /// production this is <see cref="SagaConsumeTopics.ConstitutionProcessTopics"/>.
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
}
