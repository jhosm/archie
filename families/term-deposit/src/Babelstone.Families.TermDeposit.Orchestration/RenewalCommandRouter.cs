using Babelstone.Orchestrator.Dispatch;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The concrete <see cref="ISagaCommandRouter"/> for the <see cref="RenewalProcess"/> saga (bd
/// babelstone-mtto; modelled on <see cref="SagaCommandRouter"/>). It maps each command the renewal state
/// machine emits to its ENGINE HTTP target — the two idempotent renewal legs PR B shipped:
/// <list type="bullet">
///   <item><b>ConstituteRenewal → <c>POST /v1/deposits/{process_id}/constitute-renewal</c></b> — opens the
///   renewed instance off the Matured closing deposit.</item>
///   <item><b>LinkRenewal → <c>POST /v1/deposits/{process_id}/renewal-link</c></b> — folds the closing
///   stream Matured → Renewed.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>The id is in the PATH, not the body (KEY INTEGRATION POINT).</b> PR B's renewal endpoints carry the
/// CLOSING deposit id in the URL path, and that id IS the saga's <c>process_id</c> (the saga was
/// auto-started on the closing deposit's <c>DepositMatured</c>, whose <c>ce_subject</c> = the closing
/// deposit id). The router declares the <c>{process_id}</c> template token in the route
/// <see cref="CommandRoute.Path"/>; the dispatcher (<c>SagaCommandDispatchDrainer</c>) substitutes it with
/// the outbox row's process_id when it builds the target URL. This is the GENERIC, family-agnostic
/// URL-templating seam (bd babelstone-mtto PR2) — the substrate knows the row's process_id and the single
/// token; it names no family and no specific endpoint. The body still carries the renewal facts (and the
/// derived new deposit id) the engine needs (<see cref="RenewalCommandPayloadFactory"/>).
/// </para>
/// <para>
/// Pure and family-local: the map keys on the same <see cref="RenewalProcess"/> command-name constants the
/// transition table and the payload factory share, so a name change is a compile error, never a drifting
/// literal. A command type not in the map resolves to <c>null</c> — the drain surfaces it as a terminal
/// routing failure rather than guessing a target.
/// </para>
/// </remarks>
public sealed class RenewalCommandRouter(SagaCommandDispatcherOptions options) : ISagaCommandRouter
{
    private readonly SagaCommandDispatcherOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string SagaType => RenewalProcess.Type;

    /// <inheritdoc />
    // This router knows ONLY the RenewalProcess command map, so sagaType is ignored here — the
    // CompositeCommandRouter already selected THIS router by saga_type before calling.
    public CommandRoute? Resolve(string commandType, string sagaType) => Resolve(commandType);

    /// <inheritdoc />
    public CommandRoute? Resolve(string commandType) => commandType switch
    {
        // The {process_id} token is substituted by the dispatcher with the outbox row's process_id (= the
        // closing deposit id PR B's endpoint expects in the path).
        RenewalProcess.ConstituteRenewal =>
            new CommandRoute(_options.EngineBaseUrl, "/v1/deposits/{process_id}/constitute-renewal", HttpMethod.Post),
        RenewalProcess.LinkRenewal =>
            new CommandRoute(_options.EngineBaseUrl, "/v1/deposits/{process_id}/renewal-link", HttpMethod.Post),

        // Anything else has no HTTP destination — a terminal routing failure, never a silent guess.
        _ => null,
    };
}
