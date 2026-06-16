using Babelstone.Orchestrator.Saga;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The PURE command-outcome → result-event bridge for the <see cref="RenewalProcess"/> saga (bd
/// babelstone-mtto; modelled on <see cref="ConstitutionResultEvents"/>). When the dispatcher flips a
/// <c>saga_outbox</c> row to its terminal status, this maps <c>(command_type, delivery-kind)</c> to the
/// result-event TYPE the saga self-advances on — or to <c>null</c> when the outcome drives no advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is how the two forward signals are produced — SYNTHESIZED from each command's HTTP 2xx, NOT
/// read off the bus.</b> The renewal saga consumes the <c>term_deposit</c> topic ONLY for the
/// <c>DepositMatured</c> START trigger. Its two forward advances are bridge-synthesized from command
/// delivery outcomes and fed back IN-PROCESS (the SAME pattern as the constitution saga's ACL legs):
/// <list type="bullet">
///   <item><c>(ConstituteRenewal, Applied)</c> → <see cref="RenewalProcess.NewDepositConstituted"/> — the
///   engine opened the new stream (a 201). It MUST be synthesized, not read off the bus: the new stream's
///   real <c>DepositConstituted</c> fact carries <c>ce_subject = newDepositId ≠ process_id</c>, so it
///   cannot correlate to THIS saga (whose process_id is the CLOSING deposit id) off the bus.</item>
///   <item><c>(LinkRenewal, Applied)</c> → <see cref="RenewalProcess.RenewalLinkConfirmed"/> — the engine
///   folded the closing stream to Renewed (a 200). Closes the saga.</item>
/// </list>
/// </para>
/// <para>
/// <b>Refusals escalate, NEVER compensate (ADR-IC-003 §P6).</b> A 4xx Refused on either leg maps to the
/// per-leg failure event (<see cref="RenewalProcess.ConstituteRenewalFailed"/> /
/// <see cref="RenewalProcess.LinkRenewalFailed"/>), which the table routes to HUMAN_INTERVENTION_REQUIRED
/// with no reversal command — the payout already moved at maturity, so an operator reconciles. The kind is
/// <see cref="CommandDeliveryKind.Refused"/> (the substrate's enum name), not "Rejected".
/// </para>
/// <para>
/// <b>No carve-outs.</b> Both renewal commands have REAL HTTP routes (the engine's two renewal legs), so
/// there is no no-route auto-pass (<see cref="Bridge.IsNoRouteAutoPass"/> is always false). And there is
/// no HTTP-202 reinterpretation (<see cref="Bridge.ClassifyResponse"/> is always null) — the renewal legs
/// carry no INDETERMINATE settlement semantics (that is the constitution saga's ConfirmDebit-only
/// Scenario C). The default classification (2xx → Applied, 4xx → Refused, 5xx → transient) applies.
/// </para>
/// <para>
/// <b>Pure and drift-free (ADR-PC-010 §P5).</b> A function of the command type and the delivery kind alone
/// — no clock, no I/O, no randomness. Both the command names it matches and the event names it returns are
/// the SAME <see cref="RenewalProcess"/> string constants the transition table keys on, so the bridge and
/// the table cannot diverge.
/// </para>
/// </remarks>
public static class RenewalResultEvents
{
    /// <summary>
    /// Map the terminal delivery outcome of <paramref name="commandType"/> to the result-event type the
    /// renewal saga self-advances on, or <c>null</c> when the outcome drives no advance (an unmapped pair
    /// is a graceful no-op, never an invented transition).
    /// </summary>
    public static string? ForOutcome(string commandType, CommandDeliveryKind kind) => (commandType, kind) switch
    {
        // ConstituteRenewal: 2xx Applied → synthesize NewDepositConstituted (open the new stream → link).
        (RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Applied) => RenewalProcess.NewDepositConstituted,
        // ConstituteRenewal: 4xx Refused → escalate to HIR (no compensation — ADR-IC-003 §P6).
        (RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Refused) => RenewalProcess.ConstituteRenewalFailed,

        // LinkRenewal: 2xx Applied → synthesize RenewalLinkConfirmed (closing stream Renewed → done).
        (RenewalProcess.LinkRenewal, CommandDeliveryKind.Applied) => RenewalProcess.RenewalLinkConfirmed,
        // LinkRenewal: 4xx Refused → escalate to HIR (the new stream is already open; the payout moved —
        // NEVER compensate, ADR-IC-003 §P6; an operator reconciles the dangling stream).
        (RenewalProcess.LinkRenewal, CommandDeliveryKind.Refused) => RenewalProcess.LinkRenewalFailed,

        // Everything else drives no advance — a graceful no-op. (Indeterminate is meaningless for the
        // renewal legs; it falls through here to null.)
        _ => null,
    };

    /// <summary>
    /// The <see cref="IResultEventBridge"/> view of the renewal mapping (ADR-IC-018 §D2). The static
    /// <see cref="ForOutcome(string, CommandDeliveryKind)"/> IS the implementation — this adapter only
    /// carries the <see cref="RenewalProcess.Type"/> discriminator so the dispatcher resolves the right
    /// bridge by <c>saga_type</c>. No no-route auto-pass and no status reinterpretation: both renewal
    /// commands have real HTTP routes and carry no INDETERMINATE semantics.
    /// </summary>
    public sealed class Bridge : IResultEventBridge
    {
        /// <inheritdoc />
        public string SagaType => RenewalProcess.Type;

        /// <inheritdoc />
        public string? ForOutcome(string commandType, CommandDeliveryKind kind) =>
            RenewalResultEvents.ForOutcome(commandType, kind);

        /// <inheritdoc />
        public bool IsNoRouteAutoPass(string commandType) => false;

        /// <inheritdoc />
        public CommandDeliveryKind? ClassifyResponse(string commandType, int httpStatusCode) => null;
    }
}
