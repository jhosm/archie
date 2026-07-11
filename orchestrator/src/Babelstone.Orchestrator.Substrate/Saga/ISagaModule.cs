using Babelstone.Orchestrator.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// A family-owned saga module — the plug-in contract by which a concrete saga joins the substrate
/// (ADR-IC-018 §D4/§P4). Each family implements exactly one of these per saga type and contributes it
/// to the host's explicit module list (ADR-PC-021 §A3 — explicit now, assembly-scan later). Mirrors the
/// engine's <c>IFamilyHostModule</c> (ADR-PC-021 §A1) for the saga layer: the substrate exposes the
/// generic ports, the family module supplies the concrete machine/bridge/router and declares its
/// consume + auto-start wiring; the host composes by looping over the modules. The substrate names no
/// family — the host (the §D4 composition root) is the only place that does.
/// </summary>
public interface ISagaModule
{
    /// <summary>The <c>saga_type</c> discriminator this module governs. Must equal
    /// <see cref="ISagaStateMachine.SagaType"/> on the machine this module provides.</summary>
    string SagaType { get; }

    /// <summary>The state machine this module contributes.</summary>
    ISagaStateMachine StateMachine { get; }

    /// <summary>The result-event bridge this module contributes.</summary>
    IResultEventBridge ResultEventBridge { get; }

    /// <summary>The command router this module contributes.</summary>
    ISagaCommandRouter CommandRouter { get; }

    /// <summary>
    /// The consume topics this module's saga reacts to. The host registers one
    /// <see cref="Inbox.SagaInboxConsumerOptions"/> / consume loop per module using this set.
    /// </summary>
    IReadOnlyList<string> ConsumeTopics { get; }

    /// <summary>
    /// The Kafka consumer-group id this module's consume loop uses. Each saga type gets its OWN group
    /// so two sagas read a shared family topic independently (ADR-IC-018 §P4) — never shared-group
    /// contention. For the constitution saga this MUST equal the value the host used before the split
    /// (so existing committed offsets are preserved).
    /// </summary>
    string ConsumerGroupId { get; }

    /// <summary>
    /// The family-INTEGRATION topics this module's family publishes its engine facts on (topic ==
    /// channel == aggregate_type, the relay's documented convention) — as distinct from
    /// <see cref="ConsumeTopics"/>, which is what THIS saga subscribes to (and may include internal
    /// domain topics). Declared so the HOST can derive, from the DISCOVERED family modules, the
    /// subscribe set a family-agnostic substrate saga (the ADR-PC-032 settlement saga) needs for
    /// Movement-bearing events — without host code naming any family (ADR-PC-040 §D3; ADR-IC-018
    /// Revised 2026-07-02). A family module answers it from its catalogue-generated
    /// <c>FamilyIntegrationTopics.All</c> constants (CI-gated against the AsyncAPI catalogue);
    /// duplicates across a family's modules are unioned by the host. DEFAULTED to empty so the member
    /// is additive: a substrate-owned or integration-topic-less module declares nothing.
    /// </summary>
    IReadOnlyList<string> FamilyIntegrationTopics => [];

    /// <summary>
    /// How this saga is started (ADR-IC-018 §P5). Edge-started sagas are initiated by an explicit HTTP
    /// call to the edge (the constitution saga); event-auto-started sagas start on a matching bus event.
    /// The host uses this to decide whether to wire the edge starter for this module.
    /// </summary>
    SagaStartMode StartMode { get; }

    /// <summary>
    /// For event-auto-started modules: the start event type and optional CloudEvents-header predicate
    /// the substrate evaluates to decide whether to start a new instance (ADR-IC-018 §D5/§P5). Null for
    /// edge-started modules (<see cref="StartMode"/> == <see cref="SagaStartMode.EdgeStarted"/>).
    /// </summary>
    AutoStartRule? AutoStartRule { get; }

    /// <summary>
    /// Register this module's family-owned DI services into the host container (the per-saga business
    /// stores, the command sink, anything the module's machine/bridge/router need). Replaces the
    /// per-module boilerplate in the host's composition root — the host loops over modules calling this
    /// before Build() (ADR-IC-018 §P4).
    /// </summary>
    void ConfigureServices(IServiceCollection services, SagaModuleContext context);
}

/// <summary>How a saga is started (ADR-IC-018 §P5).</summary>
public enum SagaStartMode
{
    /// <summary>Started by an HTTP call to the edge (the edge saga starter). The constitution
    /// process is edge-started.</summary>
    EdgeStarted,

    /// <summary>Started automatically when a matching bus event arrives with the right CloudEvents
    /// headers (the renewal saga's mode).</summary>
    EventAutoStarted,
}

/// <summary>
/// The auto-start rule for an event-auto-started saga (ADR-IC-018 §P5/§D5). The substrate evaluates only
/// CloudEvents HEADERS — never the Avro payload — preserving the extraction-ready property.
/// </summary>
/// <remarks>
/// <para>
/// Two match shapes, both header-only (ADR-IC-018 §P5/§D5; the §D5 family-agnostic-saga amendment
/// 2026-06-24, which decides the settlement saga "is auto-started by *any* family's money-moving event"):
/// </para>
/// <list type="bullet">
///   <item><b><see cref="AutoStartMatch.ByStartEventType"/> (default).</b> The rule is keyed by
///   <see cref="StartEventType"/> == the inbound <c>ce_type</c> record name; the optional
///   <see cref="HeaderPredicate"/> is an ADDITIONAL filter (e.g. <c>ce_autorenewalpolicy != "NONE"</c>).
///   The effective start event the substrate drives is the inbound event itself. This is the renewal-saga
///   shape — one saga starts on ONE named event type.</item>
///   <item><b><see cref="AutoStartMatch.ByHeaderPredicate"/> (record-name-AGNOSTIC).</b> The rule matches on
///   the <see cref="HeaderPredicate"/> ALONE, against ANY consumed event regardless of its <c>ce_type</c>
///   record name — the shape a FAMILY-AGNOSTIC saga that must start on many families' events needs (the
///   settlement saga: any event carrying <c>ce_movementorigin == Originated</c>). The substrate must NOT
///   rewrite <c>ce_type</c> (that would break the engine inbox's <c>ce_type</c>↔<c>schema_id</c> decode), so
///   the rule names the GENERIC start-event marker its table keys on (e.g.
///   <c>SettlementProcess.MovementOriginated</c>) in <see cref="StartEventType"/>, and the substrate drives
///   the auto-started advance with THAT marker (not the real record name) so the machine's table /
///   <see cref="IEventSubstitutor"/> resolve it. A <see cref="HeaderPredicate"/> is REQUIRED for this shape
///   (a null predicate would match every event — fail-closed at construction).</item>
/// </list>
/// </remarks>
/// <param name="StartEventType">For <see cref="AutoStartMatch.ByStartEventType"/>: the <c>ce_type</c> record
/// name that triggers a new instance. For <see cref="AutoStartMatch.ByHeaderPredicate"/>: the GENERIC
/// start-event marker the saga's table keys on (the substrate drives the advance with this, not the real
/// record name).</param>
/// <param name="HeaderPredicate">A CloudEvents-header predicate. For
/// <see cref="AutoStartMatch.ByStartEventType"/> it is an optional additional filter (null = every event of
/// <paramref name="StartEventType"/> starts an instance); for <see cref="AutoStartMatch.ByHeaderPredicate"/>
/// it is REQUIRED (it is the whole match).</param>
/// <param name="Match">Which match shape this rule uses (default <see cref="AutoStartMatch.ByStartEventType"/>,
/// so every existing rule is unchanged).</param>
/// <param name="FanOut">An OPTIONAL, family-agnostic fan-out projector (ADR-PC-032 §A9 amendment 2026-06-26;
/// ADR-IC-018 §P5). A single inbound event MAY need to start MORE THAN ONE saga instance — the canonical case
/// is a multi-direction Movement-bearing event (a renewal's rollover-debit + interest-credit), which the
/// settlement saga fans into one instance per Movement, each gated by its own direction. When non-null, the
/// substrate invokes this on the matched inbound event and auto-starts/advances ONE instance per returned
/// projection (each carrying its own derived <c>ProcessId</c> / <c>MessageId</c> and per-instance headers);
/// when null (every other rule) the event starts the single instance keyed on its own <c>ce_subject</c>,
/// unchanged. The projector reads ONLY the event's CloudEvents headers (never the payload) and names no family
/// — it is the module's, the substrate only invokes it, exactly as it only invokes
/// <paramref name="HeaderPredicate"/>. It MUST be a pure function of the event (no clock, no I/O), MUST return
/// the PRIMARY instance (keeping the event's own ids) first, and a null/empty/single-element result is treated
/// as "no fan-out" (the single established instance).</param>
public sealed record AutoStartRule(
    string StartEventType,
    Func<IReadOnlyDictionary<string, string>, bool>? HeaderPredicate = null,
    AutoStartMatch Match = AutoStartMatch.ByStartEventType,
    Func<Inbox.SagaInboxEvent, IReadOnlyList<Inbox.SagaInboxEvent>>? FanOut = null);

/// <summary>How an <see cref="AutoStartRule"/> matches an inbound event (ADR-IC-018 §P5/§D5).</summary>
public enum AutoStartMatch
{
    /// <summary>Keyed by <see cref="AutoStartRule.StartEventType"/> == the inbound <c>ce_type</c> record name,
    /// with the predicate (if any) an additional filter. The renewal/edge shape — one named start event.</summary>
    ByStartEventType,

    /// <summary>Record-name-AGNOSTIC: matched on the header predicate alone, against ANY consumed event. The
    /// family-agnostic-saga shape (the settlement saga starting on any family's money-moving event) — the
    /// substrate drives the advance with the rule's GENERIC <see cref="AutoStartRule.StartEventType"/> marker,
    /// never rewriting the inbound <c>ce_type</c>.</summary>
    ByHeaderPredicate,
}

/// <summary>
/// Host-side ingredients passed to <see cref="ISagaModule.ConfigureServices"/> — the configuration and
/// shared endpoints a module needs to wire its family-owned services at the composition root.
/// </summary>
/// <param name="RuntimeConnectionString">The orchestrator runtime-role DB connection (ADR-PC-004
/// Amendment A1 boundary).</param>
/// <param name="EngineBaseUrl">The engine command-surface base URL (ADR-PC-029).</param>
/// <param name="SettlementBaseUrl">The Core-ACL / settlement base URL (a WireMock stub at v1) — the
/// LEGACY-DDA counterparty (ADR-PC-043; an absent settlement target routes here).</param>
/// <param name="EngineCaSettlementBaseUrl">The engine-OWNED current-account settlement base URL — the
/// ADR-PC-043 engine-CA counterparty a leg routes to when its promoted <c>ce_settlementtarget</c> header is
/// <c>engine-ca</c> (bd babelstone-u79p.3). OPTIONAL: null (or blank) means no leg is engine-CA-routed and
/// every settlement command stays on <paramref name="SettlementBaseUrl"/> — so an estate that has not
/// stood up the engine-CA surface keeps the pre-ADR-PC-043 behaviour with no config change. A family module
/// that composes a header-aware command router (the constitution + settlement routers) pins it onto its
/// <see cref="Dispatch.SagaCommandDispatcherOptions.EngineCaSettlementBaseUrl"/>.</param>
public sealed record SagaModuleContext(
    string RuntimeConnectionString,
    string EngineBaseUrl,
    string SettlementBaseUrl,
    string? EngineCaSettlementBaseUrl = null);
