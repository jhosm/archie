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

    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/> emitted by the saga of
    /// <paramref name="sagaType"/>, threading the leg's projected <c>ce_settlementtarget</c> extension
    /// header so the settlement COUNTERPARTY is selected (engine-CA vs legacy-DDA, ADR-PC-043)
    /// on the PRODUCTION drain path. The dispatcher builds
    /// <paramref name="extensionHeaders"/> from the outbox row and passes it here; the
    /// <see cref="CompositeCommandRouter"/> forwards it to the sub-router the row's <c>saga_type</c>
    /// selects, which alone reads the header (header-only routing, ADR-IC-018). The DEFAULT
    /// implementation ignores the headers (delegates to <see cref="Resolve(string, string)"/>), so a
    /// header-blind router is unchanged.
    /// </summary>
    CommandRoute? Resolve(
        string commandType, string sagaType, IReadOnlyDictionary<string, string>? extensionHeaders)
        => Resolve(commandType, sagaType);
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

    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/>, selecting the settlement COUNTERPARTY
    /// from the leg's projected <c>ce_settlementtarget</c> extension header (ADR-PC-043) — the
    /// header-aware routing seam the dispatcher drains through in PRODUCTION. Routing
    /// reads the header ALONE (ADR-IC-018 / ADR-PC-043: the substrate stays
    /// payload-blind for routing — it never reads <c>Movement.AccountRef</c> to decide where a leg goes);
    /// the path + method are counterparty-INVARIANT, only the base URL flips (<c>engine-ca</c> →
    /// the engine-CA settlement surface, <c>legacy-dda</c> or absent → the legacy ACL — UNCHANGED).
    /// </summary>
    /// <param name="commandType">The command NAME the state machine emitted.</param>
    /// <param name="extensionHeaders">The leg's projected CloudEvents extension attributes (ce_-stripped,
    /// lowercased). A router reads ONLY <c>settlementtarget</c>; a null/absent value is the legacy-DDA
    /// counterparty. The DEFAULT implementation ignores the headers (delegates to
    /// <see cref="ICommandRouter.Resolve(string)"/>), so a header-BLIND router is unchanged — only a
    /// counterparty-selecting router (the constitution + substrate settlement routers) overrides it.</param>
    /// <returns>The resolved route on the selected counterparty's base URL, or <c>null</c> for a command
    /// with no route OR an <c>engine-ca</c>-targeted leg with no engine-CA base URL configured (fail-closed
    /// — never a silent fall-back to the legacy counterparty).</returns>
    CommandRoute? Resolve(string commandType, IReadOnlyDictionary<string, string>? extensionHeaders)
        => Resolve(commandType);
}

/// <summary>The resolved HTTP target for one command type — a base URL, a relative route, and the
/// HTTP method. Structural only, no PII.</summary>
public sealed record CommandRoute(string BaseUrl, string Path, HttpMethod Method);
