using System.Net;
using System.Text.Json;

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
/// <see cref="SettlementProcess.ReserveRefused"/> (→ HIR, no hold to release); a declined confirm on either
/// direction maps to <see cref="SettlementProcess.DebitDeclined"/> /
/// <see cref="SettlementProcess.CreditDeclined"/> (→ HIR, the money did not move — ADR-PC-043 §Error model);
/// a clearance that cannot resolve maps to <see cref="SettlementProcess.ClearanceFailed"/> (→ HIR). The fact
/// is durable append-first, so the saga parks rather than inventing an undo.
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
        // The debit was DECLINED (a 4xx business decline) -> park (the money did not move, §P6) — the
        // CONFIRM-leg counterpart of the refused-reserve edge (ADR-PC-043 §Error model).
        (SettlementProcess.ConfirmDebit, CommandDeliveryKind.Refused) => SettlementProcess.DebitDeclined,
        // The debit returned INDETERMINATE (HTTP 202 reinterpreted by ClassifyResponse) -> debit clearance.
        (SettlementProcess.ConfirmDebit, CommandDeliveryKind.Indeterminate) => SettlementProcess.DebitIndeterminate,
        // The debit clearance query resolved: 2xx -> executed (late confirm); 4xx -> not executed (reissue).
        (SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Applied) => SettlementProcess.DebitClearedExecuted,
        (SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Refused) => SettlementProcess.DebitClearedNotExecuted,

        // --- Credit path (the NEW confirmation-gated surface) -----------------------------------------
        // The credit confirmed.
        (SettlementProcess.ConfirmCredit, CommandDeliveryKind.Applied) => SettlementProcess.CreditConfirmed,
        // The credit was DECLINED (a 4xx business decline against the engine-owned CA counterparty) -> park
        // (the money did not move, §P6) — the credit-path counterpart of the refused-reserve edge.
        (SettlementProcess.ConfirmCredit, CommandDeliveryKind.Refused) => SettlementProcess.CreditDeclined,
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
        /// (the ACL accepted the move but cannot yet confirm Core execution — ADR-IC-012). 202 is a 2xx,
        /// so the substrate would otherwise classify it Applied; this reinterpretation makes the dispatcher
        /// flip the row terminal and self-advance into the matching clearance wait (ADR-IC-003). ONLY for
        /// the two confirms; every other command/status returns null (the substrate's default classification
        /// applies). A confirm *timeout* is NOT this — it stays transient (the row stays PENDING for an
        /// idempotent retry).
        /// </summary>
        public CommandDeliveryKind? ClassifyResponse(string commandType, int httpStatusCode) =>
            httpStatusCode == (int)HttpStatusCode.Accepted
            && commandType is SettlementProcess.ConfirmDebit or SettlementProcess.ConfirmCredit
                ? CommandDeliveryKind.Indeterminate
                : null;

        /// <summary>
        /// The LIVE wiring of <see cref="SettlementProcess.ClassifySettlementDelivery"/> into the dispatch
        /// path (ADR-PC-043). Parses the receiver's ProblemDetails error <c>code</c> off
        /// <paramref name="responseBody"/> and routes the 4xx through the pure classifier: a
        /// <c>422 SCA_REQUIRED</c> on a cash confirm →
        /// <see cref="SettlementProcess.SettlementDeliveryDisposition.RetriablePending"/> ⇒ this returns
        /// <c>true</c>, so the dispatcher leaves the row PENDING (transient) for a re-drive on a fresh proof
        /// rather than flipping it terminal-FAILED. Every other 4xx (a genuine decline, or an SCA_REQUIRED on
        /// a non-confirm leg) →
        /// <see cref="SettlementProcess.SettlementDeliveryDisposition.Decline"/> ⇒ <c>false</c>, so the leg
        /// falls through to the substrate's terminal-Refused classification. A body with no parseable
        /// <c>code</c> yields a null error code, which the classifier treats as a plain Decline (never a
        /// silent retry).
        /// </summary>
        public bool IsRetriableStayPending(string commandType, int httpStatusCode, string? responseBody) =>
            SettlementProcess.ClassifySettlementDelivery(commandType, httpStatusCode, ReadErrorCode(responseBody))
                == SettlementProcess.SettlementDeliveryDisposition.RetriablePending;

        /// <summary>
        /// Extract the ProblemDetails <c>code</c> member from a settlement receiver's 4xx body, or
        /// <c>null</c> when the body is empty, not JSON, or carries no string <c>code</c> — the classifier
        /// then treats the 4xx as a plain decline. Tolerant by design: a malformed or truncated refusal body
        /// must never throw on the dispatch path.
        /// </summary>
        private static string? ReadErrorCode(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                return document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("code", out var code)
                       && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
