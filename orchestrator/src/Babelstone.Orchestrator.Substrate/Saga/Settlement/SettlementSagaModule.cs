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
        // MULTI-DIRECTION fan-out (ADR-PC-032 §A9/§A10, option b). A single Movement-bearing event MAY carry
        // money moving two ways at once (a renewal's rollover-debit + interest-credit). The producer
        // (Babelstone.Engine.MovementHeaders) emits the ordered movementdirections list; when it spans more
        // than one direction this projector fans it into ONE settlement instance per Movement, each at a
        // deterministic per-Movement process id and gated by its own direction (the substrate only INVOKES this
        // — the settlement-specific list shape lives here, family-agnostic; ADR-IC-018 §P5). A standalone
        // single-direction event's one-entry list does not fan out, so the projector returns the lone event
        // unchanged.
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
    /// Fan a matched Movement-bearing event into ONE per-Movement settlement event per Originated direction
    /// (ADR-PC-032 §A9/§A10, option b). Reads the ordered <c>movementdirections</c> list the producer emits;
    /// when it spans more than one direction, for each direction in carrier order it projects the source event
    /// into a per-Movement <see cref="Inbox.SagaInboxEvent"/> (its own derived process id / dedup id, its list
    /// reduced to that single direction). The PRIMARY (index 0) keeps the event's own ids; the secondaries are
    /// deterministically derived (so a redelivery re-derives them — effectively-once per leg). A standalone
    /// single-direction event (one-entry list) returns the lone source event UNCHANGED — the substrate then
    /// starts the established single instance. Pure (no clock, no I/O); names no family.
    /// </summary>
    public static IReadOnlyList<Inbox.SagaInboxEvent> FanOutByMovementDirection(Inbox.SagaInboxEvent source)
    {
        var directions = SettlementMovementFanout.ParseDirections(source.ExtensionHeaders);
        if (directions.Count < 2)
        {
            // A single-entry (or absent) movementdirections list → no fan-out. The established single instance
            // settles the event, keeping its own ce_subject; the substitutor's SingleDirection branches it.
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
