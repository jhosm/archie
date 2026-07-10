using System.Reflection;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// FAMILY-AGNOSTIC coverage sweep over the Avro catalog: for EVERY embedded <c>.avsc</c> the engine
/// ships, the matching CLR <see cref="DomainEvent"/> must Encode→Decode losslessly. This closes the
/// gap the per-event <c>AvroCodecRoundTripTests</c> leave — a family that adds its <c>.avsc</c> but
/// forgets a hand-written round-trip test is caught HERE, automatically, the moment its schema enters
/// the catalog (bd babelstone-a7d4.1, scope item 1). Pure (no container), default CI lane.
/// </summary>
/// <remarks>
/// Direction is deliberate: the sweep is driven by the CATALOG (the schemas), NOT by the set of
/// <see cref="DomainEvent"/> types. An event with no <c>.avsc</c> is legitimately not-yet-on-the-wire —
/// the F.2 lifecycle events (e.g. <c>InterestPaid</c>, <c>DepositTerminatedEarly</c>) have records and
/// even decider logic but no schema until their persist/publish slice is wired — and demanding a schema
/// for every CLR event would wrongly fail those. The invariant enforced here: every schema we ship
/// round-trips its event. The complementary "event wired to append but schema missing" check is a
/// decider→append integration test (same issue, scope item 3), which the catalog cannot drive.
/// </remarks>
public sealed class AvroCatalogSweepTests
{
    // A stub resolver: the round-trip needs only a stable id, not a real Schema Registry
    // (same shape as AvroCodecRoundTripTests.StubSchemaIdResolver).
    private sealed class StubSchemaIdResolver : ISchemaIdResolver
    {
        public int ResolveSchemaId(string eventType) => 1;
    }

    // One xUnit case per catalogued schema, keyed by the Avro record name (== the CLR event-type name,
    // AvroSchemaCatalog.ForRecordName) so a failure NAMES the offending event. DOWNSTREAM-producer
    // schemas (ADR-IC-017 amendment §3 — x-producer != engine, e.g. notification's SCHEDULED
    // NotificationDue) are engine-OWNED but not engine-EMITTED, so they have no engine CLR DomainEvent
    // to round-trip; they are excluded here exactly as the shell gate exempts them from §P3.
    public static IEnumerable<object[]> CataloguedEvents()
        => new AvroSchemaCatalog().Entries
            .Where(e => !DownstreamProducerSchemas.RecordNames.Contains(e.Schema.Name))
            .Select(e => new object[] { e.Schema.Name });

    [Theory]
    [MemberData(nameof(CataloguedEvents))]
    public void Every_catalogued_schema_round_trips_its_event(string recordName)
    {
        var eventType = ResolveDomainEventType(recordName);
        var original = Synthesize(eventType);

        var serializer = new AvroEventSerializer(new AvroSchemaCatalog(), new StubSchemaIdResolver());
        var decoded = serializer.Decode(serializer.Encode(original).Bytes, eventType);

        AssertRoundTripped(eventType, original, decoded);
    }

    // Distinct-valued synthesis (below) makes per-field equality a drop-AND-swap detector: a silently
    // dropped field decodes to its default and breaks equality, and two same-typed fields swapped would too.
    // Compared per CONSTRUCTOR PARAMETER (not whole-record Assert.Equal) because the Movement CARRIER member
    // is an IReadOnlyList<Movement>: record equality compares a list member by REFERENCE (List/array do not
    // override Equals), so a faithfully round-tripped carrier — same Movement VALUES, a fresh List instance —
    // would fail whole-record equality on the container identity alone. The carrier is compared element-wise
    // (xUnit collection equality on the value-equal Movement records); every other field by value.
    private static void AssertRoundTripped(Type eventType, DomainEvent original, DomainEvent decoded)
    {
        foreach (var parameter in eventType.GetConstructors()
                     .OrderByDescending(c => c.GetParameters().Length).First().GetParameters())
        {
            var property = eventType.GetProperty(parameter.Name!)!;
            var originalValue = property.GetValue(original);
            var decodedValue = property.GetValue(decoded);

            if (MovementCarrier.IsCarrierParameter(parameter.ParameterType))
            {
                Assert.Equal((IReadOnlyList<Movement>)originalValue!, (IReadOnlyList<Movement>)decodedValue!);
            }
            else
            {
                Assert.Equal(originalValue, decodedValue);
            }
        }
    }

    // Resolve the catalogued record name to its concrete DomainEvent record across the loaded
    // family assemblies. Fail loud (not silently skip) on a missing or ambiguous match — a catalogued
    // schema with no CLR type is exactly the drift this sweep exists to catch.
    private static Type ResolveDomainEventType(string recordName)
    {
        var matches = DomainEventTypes().Where(t => t.Name == recordName).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Catalogued schema '{recordName}' has no CLR DomainEvent type. Its .avsc record name must equal the event-type name."),
            _ => throw new InvalidOperationException(
                $"Catalogued schema '{recordName}' is ambiguous: {matches.Count} DomainEvent types share that name across families."),
        };
    }

    private static IEnumerable<Type> DomainEventTypes()
    {
        EnsureFamilyAssembliesLoaded();
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsAbstract: false } && typeof(DomainEvent).IsAssignableFrom(t));
    }

    // A C# `using` is compile-time only: a family assembly is loaded into the AppDomain lazily, on
    // first runtime USE of one of its types. This sweep never touches a concrete family type (that is
    // the point — it is family-agnostic), so without this nudge AppDomain.GetAssemblies() would not yet
    // see the family and every catalogued schema would falsely look type-less. Load every Babelstone
    // assembly sitting in the test output directory by NAME (not by referencing a type), which keeps the
    // sweep family-agnostic — no family is named here. Assembly.Load is idempotent for already-loaded ones.
    private static void EnsureFamilyAssembliesLoaded()
    {
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "Babelstone.*.dll"))
        {
            try
            {
                Assembly.Load(AssemblyName.GetAssemblyName(dll));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Native/unmanaged or unresolvable sidecar — not a family assembly; skip.
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    // Build an instance via the SAME primary-constructor notion the codec uses (most-parameters wins),
    // giving each parameter a DISTINCT non-default value of its type. Distinctness is deliberate — see
    // the equality assertion above.
    private static DomainEvent Synthesize(Type eventType)
    {
        var ctor = eventType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var seed = 1;
        var args = ctor.GetParameters().Select(p => SampleValue(p.ParameterType, seed++)).ToArray();
        return (DomainEvent)ctor.Invoke(args);
    }

    // The type→value factory. Every type the codec's ToAvro/FromAvro maps must appear here; an unmapped
    // type fails LOUD so a new wire-type cannot be added to an event without sweep + codec coverage.
    private static object SampleValue(Type type, int seed) => type switch
    {
        _ when type == typeof(Money) => new Money(1_000 + seed),
        _ when type == typeof(Guid) => DeterministicGuid(seed),
        _ when type == typeof(DateOnly) => new DateOnly(2026, 1, 1).AddDays(seed),
        _ when type == typeof(int) => 100 + seed,
        _ when type == typeof(long) => 10_000L + seed,
        _ when type == typeof(string) => $"v{seed}",
        // The Movement CARRIER (ADR-PC-032): a parameter typed IReadOnlyList<Movement> is the codec's
        // MovementCarrier array field. Sample a ONE-element list of a distinct Movement so a Movement-bearing
        // catalogued event (e.g. LoanDisbursed) round-trips its carrier through the sweep — the carrier is a
        // REQUIRED array, never null, so an empty-list sample would not exercise the array path. The carrier
        // is compared element-wise by AssertRoundTripped, so the synthesized container shape is irrelevant.
        _ when MovementCarrier.IsCarrierParameter(type) => new[] { SampleMovement(seed) },
        _ => throw new InvalidOperationException(
            $"AvroCatalogSweep has no sample value for parameter type '{type}'. Add one here AND a codec mapping in AvroEventSerializer if it is a new wire-type."),
    };

    // A distinct-valued Movement for the carrier sample (the same distinctness discipline as the scalar
    // factory): every field a non-default value of its type so a dropped Movement field breaks equality.
    // Origin is Observed, not Originated: Observed is a valid MovementOrigin symbol in BOTH the full
    // shared carrier (["Originated","Observed"]) AND the CA landing carriers narrowed to Observed-only
    // (AccountCredited/AccountDebited, ADR-PC-043 loop-breaker), so ONE sample round-trips every
    // catalogued Movement-bearing schema. Originated would fail Avro enum encoding against the narrowed
    // CA schemas.
    private static Movement SampleMovement(int seed) => new(
        AccountRef: $"acct-{seed}",
        Direction: SettlementDirection.Credit,
        Amount: new Money(5_000 + seed),
        ValueDate: new DateOnly(2026, 1, 1).AddDays(seed),
        Operation: MovementOperation.Disburse,
        Origin: MovementOrigin.Observed,
        CommandId: DeterministicGuid(seed + 1000));

    // Guid.NewGuid() would make a failure non-reproducible; derive a stable Guid from the seed.
    private static Guid DeterministicGuid(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        return new Guid(bytes);
    }
}
