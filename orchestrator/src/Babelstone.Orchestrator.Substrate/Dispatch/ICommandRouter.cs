namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// The dispatcher's routing seam (bd babelstone-t7o3.3): translate a saga <c>command_type</c> into
/// the concrete HTTP target that command is delivered to. ONE place owns the
/// command-name → (base URL, route, method) map, so adding a command's destination is a single edit
/// here, never threaded through the drain loop. Pure — a function of the command type and the
/// configured base URLs alone; no clock, no I/O, no randomness (ADR-PC-010 §P5).
/// </summary>
public interface ICommandRouter
{
    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/>, or <c>null</c> if no route is
    /// registered for it (an unknown/undeliverable command type — the drain treats that as a terminal
    /// routing failure, never a silent drop). The <see cref="CommandRoute.BaseUrl"/> selects the
    /// engine vs the settlement/ACL target; the <see cref="CommandRoute.Path"/> is the concrete route
    /// (e.g. ActivateDeposit → <c>/v1/deposits</c>, the Pact-pinned engine command route).
    /// </summary>
    CommandRoute? Resolve(string commandType);

    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/> emitted by the saga of
    /// <paramref name="sagaType"/> (bd babelstone-mtto PR1 — the multi-saga substrate). The dispatcher
    /// reads <c>saga_type</c> off the outbox row's owning saga and routes through the
    /// <see cref="CompositeCommandRouter"/> so each saga type's commands reach its OWN router. A
    /// single-saga router that only knows one type ignores <paramref name="sagaType"/> and delegates
    /// to <see cref="Resolve(string)"/>; the composite uses it to pick the sub-router. Returns
    /// <c>null</c> when no router is registered for the saga type or no route for the command.
    /// </summary>
    CommandRoute? Resolve(string commandType, string sagaType);
}

/// <summary>
/// An <see cref="ICommandRouter"/> that serves exactly ONE saga type (bd babelstone-mtto PR1). The
/// <see cref="CompositeCommandRouter"/> collects every registered <see cref="ISagaCommandRouter"/>
/// into a <c>saga_type → router</c> map and delegates by the outbox row's <c>saga_type</c>. Mirrors
/// <see cref="Saga.ISagaStateMachine.SagaType"/> / <see cref="Saga.IResultEventBridge.SagaType"/>:
/// the same discriminator selects the machine, the bridge, and the router.
/// </summary>
public interface ISagaCommandRouter : ICommandRouter
{
    /// <summary>The saga type this router's command map serves — matches
    /// <see cref="Saga.ISagaStateMachine.SagaType"/> and the persisted <c>saga_state.saga_type</c>.</summary>
    string SagaType { get; }
}

/// <summary>The resolved HTTP target for one command type — a base URL, a relative route, and the
/// HTTP method. Structural only, no PII.</summary>
public sealed record CommandRoute(string BaseUrl, string Path, HttpMethod Method);
