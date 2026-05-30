namespace Babelstone.OutboxPublisher;

/// <summary>
/// Configuration for the outbox→Redpanda relay. The relay derives everything else
/// (topic, key, headers, wire-format value) from the outbox row itself (ADR-IC-004 §P1/§P3).
/// </summary>
public sealed record OutboxRelayOptions
{
    /// <summary>PostgreSQL connection string for the engine database holding the outbox table.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Kafka bootstrap servers for the Redpanda broker (e.g. "localhost:19092").</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// The CloudEvents <c>ce_source</c> — the URI of the producing service (ADR-IC-008).
    /// Constant per deployment; carried verbatim into every record header.
    /// </summary>
    public string Source { get; init; } = "urn:babelstone:engine";

    /// <summary>Max rows drained per poll cycle (the §P2 "LIMIT N ORDER BY created_at, sequence_number").</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>Poll interval for the hosted background loop (ADR-IC-004 §S1 default 200ms).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}
