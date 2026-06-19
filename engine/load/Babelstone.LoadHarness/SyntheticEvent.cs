using Babelstone.Engine;

namespace Babelstone.LoadHarness;

/// <summary>
/// One synthetic event the generator produced: the cleartext <see cref="DomainEvent"/> the engine's
/// own Avro serializer will encode (§G1), plus the routing facts the driver needs — the
/// <c>partition_key</c> ([two-modes §5.3]) that pins per-aggregate FIFO order, and the §8.2 mix class
/// it was drawn from (so the driver/observer can break results down by sync vs async class).
/// </summary>
/// <param name="PartitionKey">The aggregate/partition key — the Kafka message key, guaranteeing
/// per-<c>partition_key</c> delivery order matches event-store order (§8.3 reliability invariant).</param>
/// <param name="Event">The cleartext domain event; the engine's <c>AvroEventSerializer</c> encodes it —
/// the SAME code path production uses, so the bytes on the bus are production bytes (§G1).</param>
/// <param name="MixClass">The §8.2 mix class this event was drawn from (e.g. "card_transactions").</param>
/// <param name="EmitInstant">The SIMULATED instant this event is scheduled to be emitted at (the peak
/// envelope places it within the simulated window); the driver paces real wall-clock to the target TPS.</param>
public sealed record SyntheticEvent(
    Guid PartitionKey,
    DomainEvent Event,
    string MixClass,
    DateTimeOffset EmitInstant);
