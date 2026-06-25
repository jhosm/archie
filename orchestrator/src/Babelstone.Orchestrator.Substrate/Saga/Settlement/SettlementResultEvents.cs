using System.Net;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The PURE command-outcome → result-event bridge for the substrate-owned <see cref="SettlementProcess"/>
/// saga (ADR-PC-032; modelled on the constitution/renewal bridges). When the dispatcher flips a
/// <c>saga_outbox</c> row to its terminal status, this maps <c>(command_type, delivery-kind)</c> to the
/// result-event TYPE the saga self-advances on — or to <c>null</c> when the outcome drives no advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synthesized from the command's HTTP outcome, NOT read off the bus.</b> At v1 the Core ACL is a
/// WireMock stub with no event producer, so the bridge synthesizes each settlement result event from the
/// command's own delivery outcome and self-advances IN-PROCESS (the SAME "rides nothing on the durable bus"
/// pattern as the constitution saga's ACL legs). DEF-1's real ACL (bd babelstone-ub9s) backs this out with
/// typed events off its own topic.
/// </para>
/// <para>
/// <b>Both directions, with the credit-clearance path (feature-design §10).</b> The debit legs map exactly
/// as the constitution debit did (Reserve / ConfirmDebit / clearance). The NEW credit legs
/// (<c>ConfirmCredit</c> / <c>QueryCoreCreditStatus</c>) mirror them: a non-confirmed credit enters
/// clearance, NEVER silent. <see cref="Bridge.ClassifyResponse"/> reads an HTTP 202 on EITHER confirm as
/// INDETERMINATE; the clearance query's 2xx/4xx encodes executed/not-executed for both directions.
/// </para>
/// <para>
/// <b>Failures escalate, NEVER compensate (ADR-IC-003 §P6).</b> A refused reserve maps to
/// <see cref="SettlementProcess.ReserveRefused"/> (→ HIR, no hold to release); a clearance that cannot
/// resolve maps to <see cref="SettlementProcess.ClearanceFailed"/> (→ HIR). The fact is durable
/// append-first, so the saga parks rather than inventing an undo.
/// </para>
/// <para>
/// <b>Pure and drift-free (ADR-PC-010 §P5).</b> A function of the command type and the delivery kind alone
/// — no clock, no I/O, no randomness. Both the command names it matches and the event names it returns are
/// the SAME <see cref="SettlementProcess"/> string constants the transition table keys on, so the bridge
/// and the table cannot diverge.
/// </para>
/// </remarks>
public static class SettlementResultEvents
{
    /// <summary>
    /// Map the terminal delivery outcome of <paramref name="commandType"/> to the result-event type the
    /// settlement saga self-advances on, or <c>null</c> when the outcome drives no advance (an unmapped pair
    /// is a graceful no-op, never an invented transition).
    /// </summary>
    public static string? ForOutcome(string commandType, CommandDeliveryKind kind) => (commandType, kind) switch
    {
        // --- Debit path -------------------------------------------------------------------------------
        // The reversible hold succeeded -> arm the irreversible debit.
        (SettlementProcess.ReserveAccountBalance, CommandDeliveryKind.Applied) => SettlementProcess.BalanceReserved,
        // The hold was refused (e.g. InsufficientBalance) -> park (no hold to release, §P6).
        (SettlementProcess.ReserveAccountBalance, CommandDeliveryKind.Refused) => SettlementProcess.ReserveRefused,
        // The irreversible debit cleared.
        (SettlementProcess.ConfirmDebit, CommandDeliveryKind.Applied) => SettlementProcess.DebitConfirmed,
        // The debit returned INDETERMINATE (HTTP 202 reinterpreted by ClassifyResponse) -> debit clearance.
        (SettlementProcess.ConfirmDebit, CommandDeliveryKind.Indeterminate) => SettlementProcess.DebitIndeterminate,
        // The debit clearance query resolved: 2xx -> executed (late confirm); 4xx -> not executed (reissue).
        (SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Applied) => SettlementProcess.DebitClearedExecuted,
        (SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Refused) => SettlementProcess.DebitClearedNotExecuted,

        // --- Credit path (the NEW confirmation-gated surface) -----------------------------------------
        // The credit confirmed.
        (SettlementProcess.ConfirmCredit, CommandDeliveryKind.Applied) => SettlementProcess.CreditConfirmed,
        // The credit returned INDETERMINATE -> credit clearance (never silent — feature-design §10).
        (SettlementProcess.ConfirmCredit, CommandDeliveryKind.Indeterminate) => SettlementProcess.CreditIndeterminate,
        // The credit clearance query resolved: 2xx -> executed (late confirm); 4xx -> not executed (reissue).
        (SettlementProcess.QueryCoreCreditStatus, CommandDeliveryKind.Applied) => SettlementProcess.CreditClearedExecuted,
        (SettlementProcess.QueryCoreCreditStatus, CommandDeliveryKind.Refused) => SettlementProcess.CreditClearedNotExecuted,

        // Everything else drives no advance — a graceful no-op.
        _ => null,
    };

    /// <summary>
    /// The <see cref="IResultEventBridge"/> view of the settlement mapping (ADR-IC-018 §D2). The static
    /// <see cref="ForOutcome(string, CommandDeliveryKind)"/> IS the implementation — this adapter carries the
    /// <see cref="SettlementProcess.Type"/> discriminator so the dispatcher resolves the right bridge by
    /// <c>saga_type</c>. No no-route auto-pass (every settlement command has a real ACL route); a 202 on
    /// either confirm is reinterpreted as INDETERMINATE (the indeterminate-clearance path).
    /// </summary>
    public sealed class Bridge : IResultEventBridge
    {
        /// <inheritdoc />
        public string SagaType => SettlementProcess.Type;

        /// <inheritdoc />
        public string? ForOutcome(string commandType, CommandDeliveryKind kind) =>
            SettlementResultEvents.ForOutcome(commandType, kind);

        /// <inheritdoc />
        // Every settlement command has a real ACL HTTP route — no no-route auto-pass carve-out.
        public bool IsNoRouteAutoPass(string commandType) => false;

        /// <summary>
        /// An HTTP 202 Accepted on EITHER irreversible confirm (<see cref="SettlementProcess.ConfirmDebit"/>
        /// or <see cref="SettlementProcess.ConfirmCredit"/>) is an EXPLICIT INDETERMINATE settlement signal
        /// (the ACL accepted the move but cannot yet confirm Core execution — ADR-IC-012 §P5). 202 is a 2xx,
        /// so the substrate would otherwise classify it Applied; this reinterpretation makes the dispatcher
        /// flip the row terminal and self-advance into the matching clearance wait (ADR-IC-003 §P4). ONLY for
        /// the two confirms; every other command/status returns null (the substrate's default classification
        /// applies). A confirm *timeout* is NOT this — it stays transient (the row stays PENDING for an
        /// idempotent retry).
        /// </summary>
        public CommandDeliveryKind? ClassifyResponse(string commandType, int httpStatusCode) =>
            httpStatusCode == (int)HttpStatusCode.Accepted
            && commandType is SettlementProcess.ConfirmDebit or SettlementProcess.ConfirmCredit
                ? CommandDeliveryKind.Indeterminate
                : null;
    }
}
