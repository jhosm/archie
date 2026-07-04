using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The substrate-owned, FAMILY-AGNOSTIC <c>settlement</c> saga module (ADR-IC-018 Amendment 2026-06-24;
/// ADR-PC-032). In plain English: this is the plug-in that joins the substrate's generic settlement saga to
/// the host. UNLIKE the term-deposit modules, it lives IN the substrate (it names no family — it keys only on
/// the ADR-PC-032 <c>Movement</c> atom), and so it is the one place the platform owns the cash-leg machinery.
/// </summary>
/// <remarks>
/// <para>
/// <b>EventAutoStarted on a <c>Movement</c>-bearing event (ADR-IC-018 §P5).</b> The saga is BORN when a
/// money-moving event carrying an <c>Originated</c> <c>Movement</c> arrives, admitted by the header predicate
/// (<c>movementorigin == Originated</c>); the engine relay promotes the movement's <c>origin</c> and the
/// ordered <c>movementdirections</c> list to CloudEvents extension headers, which the substrate reads — never
/// the payload. The machine's <see cref="SettlementProcess.SubstituteAsync"/> resolves the generic start event
/// to the debit or credit branch from this leg's single-entry <c>movementdirections</c> list.
/// </para>
/// <para>
/// <b>The consume topics are HOST-supplied, not named here (ORCH-3 / ADR-IC-018 §P4).</b> The substrate names
/// no family topic; the HOST (the §D4 composition root, which MAY name a family) passes the family integration
/// topics where <c>Movement</c>-bearing events arrive into this module's constructor. The module holds them as
/// opaque strings — exactly as the substrate persists a saga state as an opaque string — so the substrate
/// stays family-agnostic while the one generic saga subscribes wherever money moves.
/// </para>
/// </remarks>
public sealed class SettlementSagaModule : ISagaModule
{
    private readonly SettlementProcess _machine = new();
    private readonly SettlementResultEvents.Bridge _bridge = new();
    private readonly SettlementCommandRouter _router;
    private readonly IReadOnlyList<string> _consumeTopics;

    /// <summary>
    /// Construct the module from the host-supplied context (the configured Core-ACL/settlement endpoint the
    /// router resolves targets against) and the HOST-supplied <paramref name="consumeTopics"/> — the family
    /// integration topics where <c>Movement</c>-bearing events arrive. The module carries no family knowledge:
    /// it keys only on the generic <c>Movement</c> atom; the topics are opaque strings the host passes in.
    /// </summary>
    public SettlementSagaModule(SagaModuleContext context, IReadOnlyList<string> consumeTopics)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(consumeTopics);
        if (consumeTopics.Count == 0)
        {
            throw new ArgumentException(
                "The settlement saga must be given at least one consume topic by the host (the family "
                + "integration topics where Movement-bearing events arrive).",
                nameof(consumeTopics));
        }

        _consumeTopics = consumeTopics;
        _router = new SettlementCommandRouter(new SagaCommandDispatcherOptions
        {
            ConnectionString = context.RuntimeConnectionString,
            EngineBaseUrl = context.EngineBaseUrl,
            SettlementBaseUrl = context.SettlementBaseUrl,
        });
    }

    /// <inheritdoc />
    public string SagaType => SettlementProcess.Type;

    /// <inheritdoc />
    public ISagaStateMachine StateMachine => _machine;

    /// <inheritdoc />
    public IResultEventBridge ResultEventBridge => _bridge;

    /// <inheritdoc />
    public ISagaCommandRouter CommandRouter => _router;

    /// <summary>The family integration topics the settlement saga subscribes to — HOST-supplied (ORCH-3 /
    /// §P4); the substrate names none of them.</summary>
    public IReadOnlyList<string> ConsumeTopics => _consumeTopics;

    /// <summary>The settlement saga's OWN Kafka consumer group (ADR-IC-018 §P4) — distinct from every other
    /// saga's group, so it reads the shared family topics independently with no shared-group contention.</summary>
    public string ConsumerGroupId => "babelstone-orchestrator-settlement";

    /// <inheritdoc />
    public SagaStartMode StartMode => SagaStartMode.EventAutoStarted;

    /// <summary>
    /// Start a settlement instance on a <c>Movement</c>-bearing event whose promoted <c>movementorigin</c>
    /// header is <c>Originated</c> (ADR-IC-018 §P5/§D5; ADR-PC-032 slot 2 — an Originated movement has a cash
    /// leg to drive, an Observed one does not). The substrate evaluates this predicate on the record's
    /// extension-attribute HEADERS only (never the payload). The direction branch is resolved AFTER start by
    /// the machine's <see cref="SettlementProcess.SubstituteAsync"/> from this leg's single-entry
    /// <c>movementdirections</c> list.
    /// </summary>
    /// <remarks>
    /// <b>Record-name-AGNOSTIC (<see cref="AutoStartMatch.ByHeaderPredicate"/>).</b> UNLIKE every product-family
    /// saga (which starts on ONE named event type, e.g. the renewal saga on <c>DepositMatured</c>), this
    /// family-agnostic saga starts on ANY family's money-moving event — the §D5 amendment (2026-06-24) decides
    /// it "is auto-started by *any* family's money-moving event." A single <c>ce_type</c> record-name key
    /// cannot express that (a real <c>LoanDisbursed</c> / <c>DepositMatured</c> / … each has its OWN record
    /// name), and the substrate must NOT rewrite <c>ce_type</c> (it would break the engine inbox's
    /// <c>ce_type</c>↔<c>schema_id</c> decode). So the match is the <c>movementorigin == Originated</c> HEADER
    /// predicate alone, and <see cref="SettlementProcess.MovementOriginated"/> is the GENERIC start marker the
    /// substrate drives the auto-started advance with (the table / <see cref="SettlementProcess.SubstituteAsync"/>
    /// key on it). The promoted header is the engine-spine producer's (<c>MovementHeaders</c>, bd
    /// babelstone-t7o3.20).
    /// </remarks>
    public AutoStartRule? AutoStartRule => new AutoStartRule(
        StartEventType: SettlementProcess.MovementOriginated,
        HeaderPredicate: headers =>
            headers.TryGetValue(OriginHeader, out var origin)
            && string.Equals(origin, OriginatedValue, StringComparison.Ordinal),
        Match: AutoStartMatch.ByHeaderPredicate,
        // PER-OCCURRENCE fan-out (ADR-PC-032 §A9/§A10, option b + the per-occurrence-identity revision
        // 2026-07-04). The producer (Babelstone.Engine.MovementHeaders) emits the ordered movementdirections
        // list (one entry per Originated Movement); this projector fans EVERY Movement-bearing event into ONE
        // settlement instance per Movement — each at a deterministic per-occurrence process id derived from
        // (ce_subject, ce_id, movement index) and gated by its own direction — so a recurring subject's later
        // occurrences (installment N ≥ 2) get their OWN saga instead of no-oping at occurrence 1's terminal
        // saga, while a redelivery re-derives the same ids and dedups. The substrate only INVOKES this — the
        // settlement-specific list shape lives here, family-agnostic; ADR-IC-018 §P5.
        FanOut: FanOutByMovementDirection);

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The settlement saga's typed command sink — the family-agnostic sink the multi-saga
        // CompositeSagaCommandSink routes SettlementProcess commands to (ADR-IC-018 §D2). It carries NO
        // per-saga state and NO family config: the engine/ACL resolves the cash leg from the opaque
        // references the byte-stable payload factory derives.
        services.AddSingleton<ISagaTypedCommandSink>(sp =>
            new SettlementCommandOutboxSink(sp.GetRequiredService<SagaOutboxWriter>()));
    }

    /// <summary>
    /// Fan a matched Movement-bearing event into ONE per-OCCURRENCE settlement event per Originated direction
    /// (ADR-PC-032 §A9/§A10, option b + the per-occurrence-identity revision 2026-07-04). Reads the ordered
    /// <c>movementdirections</c> list the producer emits and, for EVERY entry in carrier order (a one-entry
    /// list included), projects the source event into a per-occurrence <see cref="Inbox.SagaInboxEvent"/>: a
    /// deterministic process id derived from (ce_subject, ce_id, movement index), the real subject preserved
    /// on <see cref="Inbox.SagaInboxEvent.SubjectId"/>, and the list reduced to that single direction. Same
    /// event redelivered → same derived ids (effectively-once per leg); a LATER event on the same subject →
    /// fresh instances (per-occurrence identity — the one-terminal-saga-per-ce_subject gap, bd
    /// babelstone-3o6m). An ALREADY-PROJECTED leg (non-null <c>SubjectId</c>) and a directions-less event
    /// return the lone source unchanged — the projection is inert on re-entry, and a defensive
    /// no-declared-directions event keeps the legacy single instance on its own <c>ce_subject</c>. Pure (no
    /// clock, no I/O); names no family.
    /// </summary>
    public static IReadOnlyList<Inbox.SagaInboxEvent> FanOutByMovementDirection(Inbox.SagaInboxEvent source)
    {
        if (source.SubjectId is not null)
        {
            // Already a projected per-occurrence leg (the fan-out stamped its SubjectId): inert on re-entry —
            // the recursive secondary advance must start THIS derived instance, never re-derive from it.
            return [source];
        }

        var directions = SettlementMovementFanout.ParseDirections(source.ExtensionHeaders);
        if (directions.Count == 0)
        {
            // No declared movementdirections list (the producer always emits one for an Originated event —
            // ADR-PC-032 §A9 — so this is defensive depth): fall back to the legacy single instance keyed on
            // the event's own ce_subject rather than minting an occurrence id from a phantom movement.
            return [source];
        }

        var projections = new List<Inbox.SagaInboxEvent>(directions.Count);
        for (var i = 0; i < directions.Count; i++)
        {
            projections.Add(SettlementMovementFanout.ProjectMovementEvent(source, i, directions[i]));
        }

        return projections;
    }

    /// <summary>The promoted CloudEvents extension-attribute name (ce_-stripped, lowercased) carrying a
    /// <c>Movement</c>'s <c>origin</c> (ADR-PC-032 slot 2). The engine relay promotes
    /// <c>Movement.Origin</c> to this header on the carrying event.</summary>
    public const string OriginHeader = "movementorigin";

    /// <summary>The promoted origin value that has a cash leg to drive (ADR-PC-032
    /// <c>MovementOrigin.Originated</c>). An Observed movement carries no cash leg, so its event starts NO
    /// settlement saga (the predicate fails).</summary>
    private const string OriginatedValue = "Originated";
}
