using Babelstone.Orchestrator.Handlers;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The per-saga BUSINESS REFERENCES the constitution saga pins at start and reads later to decide
/// the approval fork and to build the FULL typed command payloads (bd babelstone-t7o3.1). These are
/// the concrete facts a command body needs that the structural <see cref="SagaInstance"/> row does
/// not carry — the amount to reserve, the account to debit, the product/deposit references, and the
/// edge-pinned approval inputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned at the edge, never re-dereferenced (ADR-PC-010 §P5).</b> Every field is captured ONCE
/// when the edge admitted the constitution request and is immutable through the saga — the fork and
/// the command assembly read these as scalars, never reaching back into live product config or a
/// rate sheet at decision time. That is what makes the saga's decisions replay-stable: the SAME
/// request decides the SAME way across a config change or a replay.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> <see cref="AmountMinorUnits"/> is
/// integer CENTS (never a float, never a formatted amount string);
/// <see cref="SourceAccountRef"/> / <see cref="InterestAccountRef"/> are the OPAQUE account TOKENS
/// the engine's PII boundary already issued, NOT raw IBANs; <see cref="ProductRef"/> /
/// <see cref="DepositRef"/> are catalogue/aggregate references; <see cref="ClientType"/> is a closed
/// code. A subject's NIF/IBAN/name NEVER rides this row.
/// </para>
/// </remarks>
/// <param name="ProcessId">The saga instance these references belong to (the PROC-… reference).</param>
/// <param name="ProductRef">The product catalogue reference being constituted (e.g. TD-TRAD-12M).</param>
/// <param name="AmountMinorUnits">The deposit principal in integer cents — the amount
/// ReserveAccountBalance holds and the approval fork compares against the threshold.</param>
/// <param name="SourceAccountRef">The OPAQUE source-account token to reserve/debit against (a
/// token, not a raw IBAN).</param>
/// <param name="InterestAccountRef">The OPAQUE interest-account token, or null when the product
/// pays no interest to a distinct account.</param>
/// <param name="DepositRef">The deposit aggregate reference (DEP-…) the limits/activation commands
/// target.</param>
/// <param name="ClientType">The client's standing as the approval fork reads it (existing / new).</param>
/// <param name="AutoApprovalThresholdMinorUnits">The auto-approval ceiling PINNED at the edge in
/// integer cents — the fork's threshold argument, never a live-config dereference.</param>
public sealed record SagaBusinessReference(
    Guid ProcessId,
    string ProductRef,
    long AmountMinorUnits,
    string SourceAccountRef,
    string? InterestAccountRef,
    string DepositRef,
    ClientType ClientType,
    long AutoApprovalThresholdMinorUnits)
{
    /// <summary>
    /// The edge-pinned inputs the pure approval fork decides over (Document 05 step 3). A pure
    /// projection — no clock, no I/O — that lifts the amount, the pinned threshold, and the client
    /// type onto the <see cref="ApprovalDecisionInput"/> the <see cref="ApprovalForkHandler"/>
    /// reads. The fork never sees an account/product reference: it decides on the amount, the
    /// threshold, and the client standing alone.
    /// </summary>
    public ApprovalDecisionInput ToApprovalInput() =>
        new(AmountMinorUnits, AutoApprovalThresholdMinorUnits, ClientType);
}
