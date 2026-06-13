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
}

/// <summary>The resolved HTTP target for one command type — a base URL, a relative route, and the
/// HTTP method. Structural only, no PII.</summary>
public sealed record CommandRoute(string BaseUrl, string Path, HttpMethod Method);
