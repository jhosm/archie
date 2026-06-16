using Babelstone.Orchestrator.Dispatch;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The concrete <see cref="ICommandRouter"/> for the constitution saga (bd babelstone-t7o3.3). It
/// maps each command the <see cref="ConstitutionProcess"/> state machine emits to its HTTP target:
/// <list type="bullet">
///   <item><b>ActivateDeposit → the ENGINE</b> at <c>POST /v1/deposits</c> — the concrete,
///   Pact-pinned engine command route (ADR-PC-029 slot 1; the write companion to ADR-PC-027's read
///   surface). The engine-bound constitution command rides idempotent HTTP and the engine dedups on
///   the Idempotency-Key.</item>
///   <item><b>Settlement commands → the Core ACL</b> at the configured settlement base URL — the
///   reversible/irreversible money legs (ReserveAccountBalance / ConfirmDebit /
///   ReleaseBalanceReservation / ReverseCoreDebit). At v1 the ACL is a WireMock stub (the real ACL is
///   DEF-1, bd ub9s); this router only provides the routing seam + the configurable target — it does
///   NOT stand up the stub service nor de-settle the engine (that is bd t7o3.4).</item>
/// </list>
/// </summary>
/// <remarks>
/// Pure and family-local: the map keys on the same <see cref="ConstitutionProcess"/> command-name
/// constants the transition table and the command DTOs share, so a name change is caught at compile
/// time rather than drifting into a re-typed literal. A command type not in the map resolves to
/// <c>null</c> — the drain surfaces it as a terminal routing failure rather than guessing a target.
/// ValidateProductLimits has NO route here: it is an in-aggregate validation the engine performs as
/// part of constitution, not a standalone HTTP command at v1 — so the substrate's bare-name
/// transition emits it but the dispatcher has no engine endpoint to deliver it to. A future change
/// that gives it a dedicated route adds the entry here; until then it is an unrouted (terminal)
/// command, never a silent guess.
/// </remarks>
public sealed class SagaCommandRouter(SagaCommandDispatcherOptions options) : ISagaCommandRouter
{
    private readonly SagaCommandDispatcherOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string SagaType => ConstitutionProcess.Type;

    /// <inheritdoc />
    // This router knows ONLY the ConstitutionProcess command map, so sagaType is ignored here — the
    // CompositeCommandRouter already selected THIS router by saga_type before calling. A command type
    // not in the constitution map resolves to null exactly as the single-arg overload does.
    public CommandRoute? Resolve(string commandType, string sagaType) => Resolve(commandType);

    /// <inheritdoc />
    public CommandRoute? Resolve(string commandType) => commandType switch
    {
        // The engine-bound constitution command — the Pact-pinned route.
        ConstitutionProcess.ActivateDeposit =>
            new CommandRoute(_options.EngineBaseUrl, "/v1/deposits", HttpMethod.Post),

        // The Core-ACL settlement legs — routed to the configurable settlement target. The concrete
        // ACL routes are DEF-1's (bd ub9s); the v1 stub accepts a POST to the command-named path so
        // the routing seam is exercised end-to-end without committing the real ACL wire shape here.
        ConstitutionProcess.ReserveAccountBalance =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/reservations", HttpMethod.Post),
        ConstitutionProcess.ConfirmDebit =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/debits", HttpMethod.Post),
        ConstitutionProcess.ReleaseBalanceReservation =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/reservations/release", HttpMethod.Post),
        ConstitutionProcess.ReverseCoreDebit =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/debits/reverse", HttpMethod.Post),

        // The clearance query for an INDETERMINATE debit (Document 05 Scenario C; bd babelstone-t7o3.10) —
        // the saga's single event-driven query to the Core ACL asking whether the debit actually executed.
        // Routed to the same Settlement target as the other money legs; the v1 ACL stub answers with the
        // outcome encoded as the HTTP status (2xx executed / 4xx not-executed). DEF-1's real ACL replaces
        // this with typed clearance events.
        ConstitutionProcess.QueryCoreDebitStatus =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/debits/clearance", HttpMethod.Post),

        // Anything else (incl. the in-aggregate ValidateProductLimits) has no HTTP destination at v1.
        _ => null,
    };
}
