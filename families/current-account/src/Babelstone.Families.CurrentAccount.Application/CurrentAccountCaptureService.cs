using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.Packs;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure command shell for the settlement CAPTURE path (ADR-PC-043 §2 ConfirmDebit → HoldCaptured +
/// Debit Movement / ADR-PC-037 §D4). In plain English: it turns an authorize reservation into a REAL debit —
/// it releases the placed hold and lands a Debit that actually moves the money — in one atomic append. It
/// funds a fresh deposit or collects a loan installment against the customer's engine-owned current account.
/// </summary>
/// <remarks>
/// <para>
/// <b>One atomic append: the spine HoldCaptured + the family AccountDebited (ADR-PC-043 §2).</b> The service
/// appends BOTH the engine-owned <see cref="HoldCaptured"/> (the earmark release — REUSED, never redefined,
/// ADR-PC-033) and the family <see cref="AccountDebited"/> (the Debit Movement) in ONE
/// <see cref="AggregateRuntime{TState}.AppendAsync"/> call, so the hold leaves the active set and the debit
/// posts together or not at all. When the spine projection drive folds the batch, the
/// <c>AccountHoldProjector</c> captures the hold WHERE its state is ACTIVE (the store's
/// <c>UPDATE … WHERE state='ACTIVE'</c>) and SURFACES a partial / over-capture as a
/// <see cref="HoldReleaseResult"/> reconciliation signal (ADR-PC-037 §D4) — never silently absorbed.
/// </para>
/// <para>
/// <b>Double-guarded idempotency (ADR-PC-043 §4).</b> command_dedup on the intent-derived
/// <c>command_id</c> (a byte-identical saga reissue collapses to ONE append) PLUS the capture applying only
/// WHERE the hold state is ACTIVE (a second capture of an already-captured hold folds as a no-op, never a
/// double-release). So a redelivered / reissued capture lands EXACTLY ONE Debit Movement
/// (SETTLEMENT_CA_CAPTURE_HOLD_MATCH).
/// </para>
/// <para>
/// <b>Hold-match enforced for one intent (ADR-PC-043).</b> Before appending, the shell drains the spine read
/// model (read-your-writes) and requires the command's <c>target_hold_id</c> to name an ACTIVE authorization
/// hold on this account — the authorize's hold. A capture naming no such hold is a
/// <see cref="DomainRejectedException"/> (a 4xx / HIR park, never a silent no-op or a phantom debit).
/// </para>
/// <para>
/// It depends only on generic engine ports (the runtime, the spine balance/hold reader, the projection
/// drainer) plus the pinned pack — the dependency arrow is family→engine (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// </remarks>
public sealed class CurrentAccountCaptureService(
    AggregateRuntime<AccountPosition> runtime,
    AccountBalanceReader balances,
    SpineProjectionDrainer drainer,
    VerifiedPack pack)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    /// <summary>
    /// Capture a reservation into a real debit and return the outcome. Loads the account, drains the spine
    /// read model so the placed hold is visible, requires the command's target hold to be an ACTIVE
    /// authorization hold on this account (else <see cref="DomainRejectedException"/>), then appends the
    /// engine <see cref="HoldCaptured"/> + the family <see cref="AccountDebited"/> in one batch, idempotently
    /// on the intent-derived command id. Reports a partial / over-capture reconciliation signal (ADR-PC-037
    /// §D4) on the response. Propagates <see cref="DuplicateCommandException"/> /
    /// <see cref="ConcurrencyException"/> for the endpoint to map.
    /// </summary>
    public async Task<CaptureOutcome> CaptureAsync(
        CaptureAccountCommand command, DateTimeOffset validTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hydrated = await runtime.LoadAsync(command.AccountId, ct);
        var accountRef = hydrated.State.AccountRef;

        // Drain-before-read (ADR-PC-033 read-your-writes): fold the placing authorize's HoldPlaced into the
        // read model BEFORE reading the active holds, so a just-authorized reservation is visible to the
        // hold-match check below (the same safety the authorize shell drains for).
        await drainer.DrainOnceAsync(ct);

        // The pure decision: enforce the hold match (SETTLEMENT_CA_CAPTURE_HOLD_MATCH), classify the
        // partial/over-capture (ADR-PC-037 §D4), and build the ONE atomic append batch (the spine HoldCaptured
        // + the family AccountDebited). GetActiveHoldsAsync returns only ACTIVE rows, so the decider's target
        // match is the command-time half of the double guard; the projection-time half is the store's WHERE
        // state='ACTIVE'. A non-positive amount or an unmatched target throws DomainRejectedException here,
        // BEFORE any append (the endpoint maps it to a 4xx — never a phantom debit).
        var activeHolds = await balances.GetActiveHoldsAsync(accountRef, ct);
        var decision = CurrentAccountCaptureDecider.Decide(accountRef, activeHolds, command);

        var commitSequence = await runtime.AppendAsync(
            command.AccountId, hydrated.Version, decision.Events,
            Context(command.Actor, validTime, command.CommandId), ct);

        return new CaptureOutcome(commitSequence, decision.Reconciliation);
    }

    // The family / pack / schema pins ride the EventEnvelope via AppendContext, never on the event record
    // (ADR-PC-009). commandId is the ADR-PC-043 intent-derived idempotency key (NOT the HTTP Idempotency-Key).
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}

/// <summary>The outcome of a capture: the commit sequence the batch reached (the read-your-writes token,
/// ADR-IC-005) and a non-normal reconciliation signal when the captured amount did not equal the held amount
/// (ADR-PC-037 §D4), or null on a clean full capture.</summary>
public sealed record CaptureOutcome(long CommitSequence, string? Reconciliation);

/// <summary>The bounded capture-reconciliation taxonomy (ADR-PC-037 §D4): the two non-normal capture-amount
/// outcomes a capture surfaces so a partial / over-capture is never silently absorbed. A clean full capture
/// carries neither (null).</summary>
public static class SettlementReconciliation
{
    /// <summary>The captured amount was LESS than the placed hold — the remainder is released (the whole hold
    /// leaves the active set; only the captured cents posted, ADR-PC-033 / ADR-PC-037 §D4).</summary>
    public const string PartialCapture = "PARTIAL_CAPTURE";

    /// <summary>The captured amount EXCEEDED the placed hold — the money moved (the row transitioned), but the
    /// mismatch is a reconciliation signal, never silently absorbed (ADR-PC-037 §D4;
    /// HoldReleaseResult.TransitionedOverCaptured).</summary>
    public const string OverCaptured = "OVER_CAPTURED";
}
