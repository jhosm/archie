namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// The multi-saga routing seam (bd babelstone-mtto PR1). Generalises the single
/// <see cref="SagaCommandRouter"/> into a registry keyed by <c>saga_type</c>: it collects every
/// registered <see cref="ISagaCommandRouter"/> into a <c>saga_type → router</c> map and, when the
/// dispatcher resolves a command, delegates to the sub-router that serves the owning saga's type.
/// So a second saga (the H.3 renewal saga, PR2) registers its OWN <see cref="ISagaCommandRouter"/>
/// alongside the constitution one and its commands route correctly with no dispatcher change.
/// </summary>
/// <remarks>
/// Pure — a function of (command type, saga type) and the registered routers alone; no clock, no
/// I/O, no randomness (ADR-PC-010 §P5). A duplicate <see cref="ISagaCommandRouter.SagaType"/> is a
/// wiring error and throws at construction (the registry must be a function, the same defensive
/// stance <see cref="Saga.TableStateMachine"/> takes on a duplicate transition). The legacy
/// single-arg <see cref="Resolve(string)"/> defaults to the <see cref="Saga.ConstitutionProcess.Type"/>
/// router so any caller that has not yet threaded <c>saga_type</c> keeps the v1 behaviour; the
/// saga-type-aware <see cref="Resolve(string, string)"/> is the path the dispatcher uses.
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

        _routers = map;
    }

    /// <inheritdoc />
    // The legacy single-arg path: no saga_type supplied, so default to the constitution router. A
    // caller that still routes a bare command name (pre-multi-saga) gets the v1 behaviour unchanged.
    public CommandRoute? Resolve(string commandType) =>
        Resolve(commandType, Saga.ConstitutionProcess.Type);

    /// <inheritdoc />
    public CommandRoute? Resolve(string commandType, string sagaType) =>
        _routers.TryGetValue(sagaType, out var router) ? router.Resolve(commandType) : null;
}
