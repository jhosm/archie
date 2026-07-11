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
///   <item><b>Settlement funding legs → the selected settlement COUNTERPARTY</b> — the
///   reversible/irreversible money legs (ReserveAccountBalance / ConfirmDebit /
///   ReleaseBalanceReservation / ReverseCoreDebit). The BASE URL is chosen the SAME way the
///   substrate-owned <c>SettlementCommandRouter</c> chooses it (bd babelstone-u79p.3, ADR-PC-043
///   slots 1–2): a leg carrying <c>ce_settlementtarget = engine-ca</c> routes to the engine-owned CA
///   settlement surface (<see cref="SagaCommandDispatcherOptions.EngineCaSettlementBaseUrl"/>); a
///   legacy-DDA or header-absent leg routes to the legacy Core ACL
///   (<see cref="SagaCommandDispatcherOptions.SettlementBaseUrl"/>, UNCHANGED — a WireMock stub at
///   v1; the real ACL is DEF-1, bd ub9s). The PATH + METHOD are counterparty-INVARIANT; only the base
///   URL flips (ADR-PC-043 flips the base URL, never the path).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Pure and family-local: the map keys on the same <see cref="ConstitutionProcess"/> command-name
/// constants the transition table and the command DTOs share, so a name change is caught at compile
/// time rather than drifting into a re-typed literal. A command type not in the map resolves to
/// <c>null</c> — the drain surfaces it as a terminal routing failure rather than guessing a target.
/// ValidateProductLimits has NO route here: it is an in-aggregate validation the engine performs as
/// part of constitution, not a standalone HTTP command at v1 — so the substrate's bare-name
/// transition emits it but the dispatcher has no engine endpoint to deliver it to. A future change
/// that gives it a dedicated route adds the entry here; until then it is an unrouted (terminal)
/// command, never a silent guess.
/// </para>
/// <para>
/// <b>Header-only counterparty selection (bd babelstone-u79p.3; ADR-PC-043 §D5 amendment (b),
/// ADR-IC-018 §D5).</b> The engine-CA / legacy-DDA choice reads the projected <c>ce_settlementtarget</c>
/// extension header ALONE — the router stays payload-blind for ROUTING, never reading
/// <c>Movement.AccountRef</c> from the body to decide where a leg goes. The wire literals (the
/// <c>settlementtarget</c> key + the <c>engine-ca</c> value) MIRROR the engine relay's promoted
/// closed-enum (the orchestrator stays extraction-ready, ADR-PC-019 §P2 — it pins the literals rather
/// than referencing the engine-side constant; the producer↔consumer contract test asserts they
/// agree). An <c>engine-ca</c> leg with no engine-CA base URL configured returns <c>null</c> →
/// fail-closed (never a silent fall-back that settles engine-CA money on the legacy core).
/// </para>
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
    // No settlement-target header in scope → the LEGACY-DDA counterparty (ADR-PC-043: an absent target
    // defaults to legacy, so legacy routing is UNCHANGED). Every pre-ADR-PC-043 caller reaches the same
    // routes it always did through this overload.
    public CommandRoute? Resolve(string commandType) => Resolve(commandType, extensionHeaders: null);

    /// <inheritdoc />
    public CommandRoute? Resolve(
        string commandType, IReadOnlyDictionary<string, string>? extensionHeaders) => commandType switch
    {
        // The engine-bound constitution command — the Pact-pinned route (counterparty-agnostic).
        ConstitutionProcess.ActivateDeposit =>
            new CommandRoute(_options.EngineBaseUrl, "/v1/deposits", HttpMethod.Post),

        // The settlement funding legs — routed to the SELECTED counterparty (engine-CA vs legacy) by the
        // ce_settlementtarget header alone. The routes are counterparty-INVARIANT; only the base URL flips.
        // The engine-CA funding wire (bd babelstone-u79p.3): the fresh deposit funds itself by debiting the
        // customer's engine current account, so ReserveAccountBalance → the CA authorize/hold and
        // ConfirmDebit → the CA capture, reached through the counterparty-invariant /v1/reservations,
        // /v1/debits paths on the engine-CA base URL (the engine ingress, bd babelstone-u79p.5, maps them
        // onto the CA family's authorize/capture writers).
        ConstitutionProcess.ReserveAccountBalance =>
            Route("/v1/reservations", extensionHeaders),
        ConstitutionProcess.ConfirmDebit =>
            Route("/v1/debits", extensionHeaders),
        ConstitutionProcess.ReleaseBalanceReservation =>
            Route("/v1/reservations/release", extensionHeaders),
        ConstitutionProcess.ReverseCoreDebit =>
            Route("/v1/debits/reverse", extensionHeaders),

        // The clearance query for an INDETERMINATE debit (Document 05 Scenario C; bd babelstone-t7o3.10) —
        // the saga's single event-driven query asking whether the debit actually executed. Routed to the
        // SAME selected counterparty as the other money legs; the v1 ACL stub answers with the outcome
        // encoded as the HTTP status (2xx executed / 4xx not-executed). DEF-1's real ACL replaces this with
        // typed clearance events.
        ConstitutionProcess.QueryCoreDebitStatus =>
            Route("/v1/debits/clearance", extensionHeaders),

        // Anything else (incl. the in-aggregate ValidateProductLimits) has no HTTP destination at v1.
        _ => null,
    };

    // Compose the counterparty-INVARIANT path with the base URL the ce_settlementtarget header selects.
    // Returns null for an engine-ca leg with no engine-CA base URL configured (fail-closed, ADR-PC-043) so
    // the drain surfaces a routing failure rather than settling engine-CA money on the legacy core.
    private CommandRoute? Route(string path, IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        var baseUrl = ResolveBaseUrl(extensionHeaders);
        return baseUrl is null ? null : new CommandRoute(baseUrl, path, HttpMethod.Post);
    }

    // Select the counterparty base URL from the ce_settlementtarget header alone (ADR-PC-043 slots 1–2;
    // header-only routing, ADR-IC-018 §D5). The engine relay promotes the closed-enum wire string; the
    // orchestrator is extraction-ready (ADR-PC-019 §P2), so it matches the WIRE STRINGS as literals, never
    // a shared engine type. An engine-ca leg with no engine-CA base URL configured returns null →
    // fail-closed. The VALUE compare is Ordinal (exact): the promoted value is GUARANTEED lowercase
    // ("engine-ca"), so no case folding is needed on the value.
    private string? ResolveBaseUrl(IReadOnlyDictionary<string, string>? extensionHeaders)
    {
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
    /// (mirrors <c>Babelstone.Engine.MovementHeaders.SettlementTargetKey</c> and
    /// <c>SettlementCommandRouter.SettlementTargetHeader</c>). Pinned as a literal — the orchestrator stays
    /// extraction-ready (ADR-PC-019 §P2); the producer↔consumer contract test asserts the two agree.</summary>
    public const string SettlementTargetHeader = "settlementtarget";

    /// <summary>The engine-CA settlement-target wire value (mirrors
    /// <c>Babelstone.Engine.MovementHeaders.EngineCaValue</c> and
    /// <c>SettlementCommandRouter.EngineCaValue</c>). A leg carrying this on
    /// <see cref="SettlementTargetHeader"/> routes to the engine-owned CA counterparty.</summary>
    public const string EngineCaValue = "engine-ca";

    /// <summary>The legacy-DDA settlement-target wire value (mirrors
    /// <c>Babelstone.Engine.MovementHeaders.LegacyDdaValue</c>). The DEFAULT — an absent target routes here.</summary>
    public const string LegacyDdaValue = "legacy-dda";
}
