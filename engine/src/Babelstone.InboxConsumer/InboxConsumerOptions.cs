namespace Babelstone.InboxConsumer;

/// <summary>
/// Configuration for the Redpanda→inbox consumer (the mirror of <c>OutboxRelayOptions</c>).
/// Everything the consumer needs to subscribe, decode (the wire-format schema_id is embedded in
/// each record — no Schema-Registry lookup, ADR-IC-004 §P3), deduplicate by <c>message_id</c>
/// (the CloudEvents <c>ce_id</c> header, ADR-IC-015), and commit offsets.
/// </summary>
public sealed record InboxConsumerOptions
{
    /// <summary>PostgreSQL connection string for the consumer database holding the inbox table.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Kafka bootstrap servers for the Redpanda broker (e.g. "localhost:19092").</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// The Kafka consumer group id. Stable per logical consumer (ADR-IC-001): the offsets the
    /// group commits are what make a restart resume where it left off rather than re-reading the
    /// whole topic. Two processes in the same group share the partitions; the inbox dedup absorbs
    /// the at-least-once redelivery a rebalance can replay.
    /// </summary>
    public required string GroupId { get; init; }

    /// <summary>
    /// The topics this consumer subscribes to — the producer routes a record to a topic named after
    /// the <c>aggregate_type</c> (ADR-IC-004 §Consequences), so a consumer of term-deposit events
    /// subscribes to "term_deposit".
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
