using Babelstone.Orchestrator.Dispatch;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The concrete, FAMILY-AGNOSTIC <see cref="ISagaCommandRouter"/> for the substrate-owned
/// <see cref="SettlementProcess"/> saga (ADR-PC-032 / feature-design money-movement-settlement §8). It maps
/// each settlement command the state machine emits to its Core-ACL HTTP target at the configured
/// <see cref="SagaCommandDispatcherOptions.SettlementBaseUrl"/> — the single settlement-command home the
/// design mandates (the constitution saga now CONSUMES these same command names, rather than its own
/// family-local copies).
/// </summary>
/// <remarks>
/// <para>
/// <b>One home for the settlement command surface (feature-design §8).</b> The account-generic debit legs
/// (<c>ReserveAccountBalance</c> / <c>ConfirmDebit</c> / <c>QueryCoreDebitStatus</c>) RELOCATED here from the
/// term-deposit <c>SagaCommandRouter</c> verbatim (the routes are unchanged: <c>/v1/reservations</c>,
/// <c>/v1/debits</c>, <c>/v1/debits/clearance</c>), and the NEW generic credit legs add their own routes
/// (<c>/v1/credits</c>, <c>/v1/credits/clearance</c>) — the credit surface ADR-PC-032 needs because only the
/// constitution <b>debit</b> was de-settled before, so only debit commands existed.
/// </para>
/// <para>
/// Pure and family-agnostic: the map keys on the same <see cref="SettlementProcess"/> command-name constants
/// the transition table, the payload factory, and the bridge share, so a name change is a compile error,
/// never a drifting literal. Every route targets <see cref="SagaCommandDispatcherOptions.SettlementBaseUrl"/>
/// (the v1 WireMock Core ACL; the real ACL is DEF-1, bd babelstone-ub9s). A command type not in the map
/// resolves to <c>null</c> — the drain surfaces it as a terminal routing failure, never a guessed target.
/// </para>
/// </remarks>
public sealed class SettlementCommandRouter(SagaCommandDispatcherOptions options) : ISagaCommandRouter
{
    private readonly SagaCommandDispatcherOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string SagaType => SettlementProcess.Type;

    /// <inheritdoc />
    // This router knows ONLY the SettlementProcess command map, so sagaType is ignored — the
    // CompositeCommandRouter already selected THIS router by saga_type before calling.
    public CommandRoute? Resolve(string commandType, string sagaType) => Resolve(commandType);

    /// <inheritdoc />
    public CommandRoute? Resolve(string commandType) => commandType switch
    {
        // --- Debit legs (RELOCATED from the term-deposit router, routes unchanged) -------------------
        SettlementProcess.ReserveAccountBalance =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/reservations", HttpMethod.Post),
        SettlementProcess.ConfirmDebit =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/debits", HttpMethod.Post),
        SettlementProcess.QueryCoreDebitStatus =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/debits/clearance", HttpMethod.Post),

        // --- Credit legs (NEW — the confirmation-gated credit surface, ADR-PC-032 / feature-design §8) -
        SettlementProcess.ConfirmCredit =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/credits", HttpMethod.Post),
        SettlementProcess.QueryCoreCreditStatus =>
            new CommandRoute(_options.SettlementBaseUrl, "/v1/credits/clearance", HttpMethod.Post),

        // Anything else has no HTTP destination — a terminal routing failure, never a silent guess.
        _ => null,
    };
}
