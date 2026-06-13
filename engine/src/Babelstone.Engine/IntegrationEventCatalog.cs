namespace Babelstone.Engine;

/// <summary>
/// The catalog-gated-relay membership seam (ADR-IC-017 §P1). In plain English: the engine records
/// lots of events, but only the ones a deliberate promotion has put in the integration catalogue
/// belong on the shared message bus. This is the family-agnostic predicate the append spine consults
/// to decide whether a just-appended event also gets an outbox row (and so reaches the bus) — an
/// uncatalogued event is <em>store-only by construction</em>: still appended, folded, and replayable,
/// it simply never produces a published message.
/// </summary>
/// <remarks>
/// The seam lives in the engine spine and carries NO family vocabulary — it is keyed purely by the
/// stored <c>event_type</c> string. The concrete catalogue (the embedded governed <c>.avsc</c> set,
/// <c>Babelstone.Engine.Avro.AvroSchemaCatalog</c>) implements it; the spine never references that
/// assembly (the family → engine arrow is one-way, ENGINE_FAMILY_AGNOSTIC). The contract is
/// FAIL-CLOSED: an unknown event_type returns <c>false</c> (not published), never throws — the append
/// itself always succeeds, only the bus side is gated (so the §P1 store-only guarantee holds even for
/// a brand-new schemaless event).
/// </remarks>
public interface IIntegrationEventCatalog
{
    /// <summary>True iff this stored <c>event_type</c> is a catalogued integration event the relay may publish.</summary>
    bool IsCataloguedIntegrationEvent(string eventType);
}

/// <summary>
/// The status-quo catalogue: publishes EVERYTHING. Preserves the pre-ADR-IC-017 behaviour for the
/// engine-internal test/dry-run wiring that has no real catalogue to gate on (and is the
/// <see cref="AggregateRuntime{TState}"/> default when no catalogue is injected). The PRODUCTION host
/// wires the real <c>AvroSchemaCatalog</c> instead, so the gate is fail-closed where it matters.
/// </summary>
/// <remarks>
/// This is deliberately NOT the default in production: a host that forgets to wire the catalogue would
/// fall back to publishing everything, the exact drift ADR-IC-017 exists to prevent. The catalog-gated
/// integration test (INTEGRATION_EVENT_CATALOG_GATED) and the reverse-orphan fitness test
/// (NO_UNCATALOGUED_EVENT_ON_BUS) both exercise the REAL catalogue, not this stand-in.
/// </remarks>
public sealed class PublishAllIntegrationEventCatalog : IIntegrationEventCatalog
{
    public static readonly PublishAllIntegrationEventCatalog Instance = new();

    public bool IsCataloguedIntegrationEvent(string eventType) => true;
}
