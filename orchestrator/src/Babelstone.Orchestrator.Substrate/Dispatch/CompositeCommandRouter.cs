namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// The multi-saga routing seam (bd babelstone-mtto PR1). Generalises a single family
/// <see cref="ISagaCommandRouter"/> into a registry keyed by <c>saga_type</c>: it collects every
/// registered <see cref="ISagaCommandRouter"/> into a <c>saga_type → router</c> map and, when the
/// dispatcher resolves a command, delegates to the sub-router that serves the owning saga's type.
/// So a second saga (the H.3 renewal saga, PR2) registers its OWN <see cref="ISagaCommandRouter"/>
/// alongside the constitution one and its commands route correctly with no dispatcher change.
/// </summary>
/// <remarks>
/// Pure — a function of (command type, saga type) and the registered routers alone; no clock, no
/// I/O, no randomness (ADR-PC-010 §P5). A duplicate <see cref="ISagaCommandRouter.SagaType"/> is a
/// wiring error and throws at construction (the registry must be a function, the same defensive
/// stance <see cref="Saga.TableStateMachine"/> takes on a duplicate transition). The composite
/// resolves ONLY by the saga-type-aware <see cref="Resolve(string, string)"/> — the path the
/// dispatcher always uses (it reads <c>saga_type</c> off the outbox row's owning saga). The legacy
/// single-arg <see cref="Resolve(string)"/> would have to default to one saga type, which is exactly
/// the family-naming a family-agnostic substrate must not do (ADR-IC-018 §D2/§P3), so the composite
/// rejects it rather than picking a default family — there is no substrate-level "default saga".
/// </remarks>
public sealed class CompositeCommandRouter : ICommandRouter
{
    private readonly IReadOnlyDictionary<string, ISagaCommandRouter> _routers;

    public CompositeCommandRouter(IEnumerable<ISagaCommandRouter> routers)
    {
        ArgumentNullException.ThrowIfNull(routers);

        var map = new Dictionary<string, ISagaCommandRouter>(StringComparer.Ordinal);
        foreach (var router in routers)
        {
            if (!map.TryAdd(router.SagaType, router))
            {
                throw new InvalidOperationException(
                    $"Duplicate ISagaCommandRouter for saga_type '{router.SagaType}': the saga-type → " +
                    "router registry must be a function (bd babelstone-mtto PR1).");
            }
        }

        // An empty registry is a wiring error, not a valid configuration — every Resolve() would
        // return null and the drainer would surface every outbox row as a terminal-FAILED unroutable
        // command. Fail-closed at construction, the same defensive stance SagaAdvanceHandler takes on
        // an empty machine set (bd babelstone-mtto PR1).
        if (map.Count == 0)
        {
            throw new ArgumentException(
                "At least one ISagaCommandRouter must be registered.", nameof(routers));
        }

        _routers = map;
    }

    /// <inheritdoc />
    // The single-arg path cannot be served by a family-AGNOSTIC composite: with no saga_type it would
    // have to pick a default family router — the precise §P3 leak this substrate forbids (ADR-IC-018
    // §D2/§D3). The dispatcher ALWAYS supplies saga_type (read off the outbox row's owning saga), so
    // this overload is unreachable in production; it fails closed rather than naming a default family.
    public CommandRoute? Resolve(string commandType) =>
        throw new NotSupportedException(
            "CompositeCommandRouter requires a saga_type — use Resolve(commandType, sagaType). A "
            + "family-agnostic substrate router has no default saga to fall back to (ADR-IC-018 §D2/§D3).");

    /// <inheritdoc />
    public CommandRoute? Resolve(string commandType, string sagaType) =>
        _routers.TryGetValue(sagaType, out var router) ? router.Resolve(commandType) : null;

    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/> emitted by the saga of
    /// <paramref name="sagaType"/>, threading the leg's projected <c>ce_settlementtarget</c> extension
    /// header through to the owning saga's sub-router so the COUNTERPARTY selection (engine-CA vs
    /// legacy-DDA, ADR-PC-043 slots 1–2) happens in PRODUCTION, not only in tests (bd babelstone-u79p.3).
    /// The composite stays family-agnostic — it names no family and reads no header itself; it forwards the
    /// header dict to the sub-router the outbox row's <c>saga_type</c> selected, which alone decides the
    /// counterparty (header-only routing, ADR-IC-018 §D5). A header-BLIND sub-router uses the interface's
    /// default (ignores the headers), so its routing is unchanged. Returns <c>null</c> when no router is
    /// registered for the saga type, no route for the command, or an <c>engine-ca</c> leg has no engine-CA
    /// base URL configured (the sub-router fails closed).
    /// </summary>
    public CommandRoute? Resolve(
        string commandType, string sagaType, IReadOnlyDictionary<string, string>? extensionHeaders) =>
        _routers.TryGetValue(sagaType, out var router) ? router.Resolve(commandType, extensionHeaders) : null;
}
