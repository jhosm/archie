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
/// <param name="StartEventType">The <c>ce_type</c> record name that triggers a new saga instance.</param>
/// <param name="HeaderPredicate">An optional additional header predicate (e.g.
/// <c>ce_autorenewalpolicy != "NONE"</c>). Null means every event of <paramref name="StartEventType"/>
/// starts an instance.</param>
public sealed record AutoStartRule(
    string StartEventType,
    Func<IReadOnlyDictionary<string, string>, bool>? HeaderPredicate = null);

/// <summary>
/// Host-side ingredients passed to <see cref="ISagaModule.ConfigureServices"/> — the configuration and
/// shared endpoints a module needs to wire its family-owned services at the composition root.
/// </summary>
/// <param name="RuntimeConnectionString">The orchestrator runtime-role DB connection (ADR-PC-004
/// Amendment A1 boundary).</param>
/// <param name="EngineBaseUrl">The engine command-surface base URL (ADR-PC-029).</param>
/// <param name="SettlementBaseUrl">The Core-ACL / settlement base URL (a WireMock stub at v1).</param>
public sealed record SagaModuleContext(
    string RuntimeConnectionString,
    string EngineBaseUrl,
    string SettlementBaseUrl);
