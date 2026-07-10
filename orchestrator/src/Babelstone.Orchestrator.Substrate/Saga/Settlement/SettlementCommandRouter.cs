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
    // No settlement-target header in scope → the LEGACY-DDA counterparty (ADR-PC-043: an absent target defaults
    // to legacy, so legacy routing is UNCHANGED). Every pre-ADR-PC-043 caller reaches the same routes it always
    // did through this overload.
    public CommandRoute? Resolve(string commandType) => Resolve(commandType, extensionHeaders: null);

    /// <summary>
    /// Resolve the HTTP target for <paramref name="commandType"/>, selecting the settlement COUNTERPARTY from
    /// the promoted <c>ce_settlementtarget</c> header ALONE (ADR-PC-043 slots 1–2) — <b>header-only, never the
    /// payload</b> (ADR-IC-018 §D5: the substrate MUST NOT read <c>Movement.AccountRef</c> from the body). The
    /// PATH + METHOD are counterparty-INVARIANT (the same <c>/v1/credits</c>, <c>/v1/debits</c>, … routes);
    /// only the BASE URL flips: <c>engine-ca</c> → <see cref="SagaCommandDispatcherOptions.EngineCaSettlementBaseUrl"/>,
    /// <c>legacy-dda</c> or absent → <see cref="SagaCommandDispatcherOptions.SettlementBaseUrl"/> (UNCHANGED).
    /// </summary>
    /// <param name="commandType">The settlement command name the state machine emitted.</param>
    /// <param name="extensionHeaders">The leg's projected CloudEvents extension attributes (ce_-stripped,
    /// lowercased). The router reads ONLY <c>settlementtarget</c>; a null/absent value is legacy-DDA.</param>
    /// <returns>The resolved route on the selected counterparty's base URL, or <c>null</c> for a command with
    /// no route (a terminal routing failure) OR an <c>engine-ca</c>-targeted leg with no engine-CA base URL
    /// configured (fail-closed — never silently fall back to the legacy counterparty and move real money to the
    /// wrong core).</returns>
    public CommandRoute? Resolve(
        string commandType, IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        var path = RoutePath(commandType);
        if (path is null)
        {
            // Anything else has no HTTP destination — a terminal routing failure, never a silent guess.
            return null;
        }

        var baseUrl = ResolveBaseUrl(extensionHeaders);
        return baseUrl is null ? null : new CommandRoute(baseUrl, path, HttpMethod.Post);
    }

    // The counterparty-INVARIANT route path for each settlement command (RELOCATED from the term-deposit
    // router; the NEW credit routes added by ADR-PC-032 / feature-design §8). Header-blind: the path is the
    // same for engine-CA and legacy-DDA — only the base URL flips (ADR-PC-043). A command not in the map has no
    // route (null).
    private static string? RoutePath(string commandType) => commandType switch
    {
        SettlementProcess.ReserveAccountBalance => "/v1/reservations",
        SettlementProcess.ConfirmDebit => "/v1/debits",
        SettlementProcess.QueryCoreDebitStatus => "/v1/debits/clearance",
        SettlementProcess.ConfirmCredit => "/v1/credits",
        SettlementProcess.QueryCoreCreditStatus => "/v1/credits/clearance",
        _ => null,
    };

    // Select the counterparty base URL from the ce_settlementtarget header alone (ADR-PC-043 slots 1–2). The
    // engine relay promotes the closed-enum value (Babelstone.Engine.MovementHeaders.SettlementTargetKey); the
    // substrate is extraction-ready (ADR-PC-019 §P2) so it matches the WIRE STRINGS as literals, never a shared
    // engine type. An engine-ca leg with no engine-CA base URL configured returns null → fail-closed (the drain
    // surfaces a routing failure), rather than silently settling engine-CA money on the legacy core.
    private string? ResolveBaseUrl(IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        // The VALUE compare is Ordinal (exact): the engine relay promotes the closed-enum wire string from the
        // EngineCaValue constant verbatim, so the value is GUARANTEED lowercase ("engine-ca") — no case folding
        // is needed on the value. (The header KEY lookup honours whatever comparer the extraction dictionary
        // was built with; that is the caller's concern, not this value match.)
        if (extensionHeaders is not null
            && extensionHeaders.TryGetValue(SettlementTargetHeader, out var target)
            && string.Equals(target, EngineCaValue, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(_options.EngineCaSettlementBaseUrl)
                ? null
                : _options.EngineCaSettlementBaseUrl;
        }

        // Absent, blank, or legacy-dda → the legacy counterparty. Legacy routing is UNCHANGED.
        return _options.SettlementBaseUrl;
    }

    /// <summary>The ce_-stripped, lowercased extension-attribute key the router keys the counterparty on
    /// (mirrors <c>Babelstone.Engine.MovementHeaders.SettlementTargetKey</c>). Pinned as a literal — the
    /// orchestrator stays extraction-ready (ADR-PC-019 §P2), so it cannot reference the engine-side constant;
    /// the producer↔consumer contract test asserts the two agree.</summary>
    public const string SettlementTargetHeader = "settlementtarget";

    /// <summary>The engine-CA settlement-target wire value (mirrors
    /// <c>Babelstone.Engine.MovementHeaders.EngineCaValue</c>). A leg carrying this on
    /// <see cref="SettlementTargetHeader"/> routes to the engine-owned CA counterparty.</summary>
    public const string EngineCaValue = "engine-ca";

    /// <summary>The legacy-DDA settlement-target wire value (mirrors
    /// <c>Babelstone.Engine.MovementHeaders.LegacyDdaValue</c>). The DEFAULT — an absent target routes here.</summary>
    public const string LegacyDdaValue = "legacy-dda";
}
