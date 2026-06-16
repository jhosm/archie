namespace Babelstone.Families.TermDeposit.Application;

// The renewal-saga command surface (bd babelstone-mtto PR B). The monolithic, un-idempotent
// RenewAsync (which matured + constituted + linked in one cross-stream call) is decomposed into
// TWO idempotent engine operations the renewal saga drives, with the MATURITY leg dropped:
// maturity is now autonomous (MatureAsync runs first and the closing deposit is already Matured
// when the saga fires). Each command carries only the per-step facts the engine needs; the
// renewal rate, term, variant, cadence and policy are read off the (Matured) closing deposit, not
// supplied here. The optional CommandId is the ADR-PC-029 slot 4 idempotency key (the saga's
// saga_outbox row id, threaded by the dispatcher as the Idempotency-Key header) — a replay returns
// the original outcome with no second append. It stays nullable only for direct in-process callers
// (family unit tests that construct the command without exercising idempotency).

/// <summary>
/// Open the renewed instance (steps 6–8 of the retired monolith): given the CLOSING deposit — which
/// MUST already be Matured (the autonomous maturity leg ran first) — resolve the renewal rate by the
/// closing deposit's policy, settle the rollover debit, and open the NEW stream with
/// <c>DepositConstituted</c> rooted at the closing <c>DepositMatured</c> as its <c>causation_id</c>
/// (plus the ADVANCE upfront-interest triple for an ADVANCE variant). The maturity credit is NOT this
/// command's leg — <see cref="TermDepositConstitutionService.MatureAsync"/> already credited it.
/// </summary>
/// <param name="DepositId">The CLOSING (Matured) deposit's stream id (= the saga's process_id).</param>
/// <param name="NewDepositId">The fresh stream id the renewed instance is constituted under. Caller-
/// supplied (the saga derives it deterministically) so the renewal is a replayable command — the new
/// id is the same on replay and the <c>DepositRenewed</c> link stays stable.</param>
/// <param name="ProductId">The variant id the rate sheet re-prices the new instance against for the
/// SAME_TERM_CURRENT_RATE policy (the position carries only the resolved TAN, never the product/role
/// keys, so the caller supplies them — mirroring the retired <c>RenewDepositCommand</c>).</param>
/// <param name="Role">The pricing role for the re-resolution (e.g. <c>standard</c>).</param>
/// <param name="RenewedAt">The instant the renewal fires: the new sheet is resolved as-of here, and
/// it is the new constitution's valid time. Its DATE is the renewal/new-start date.</param>
/// <param name="FundingAccount">The legacy current account debited the rolled-over principal of the
/// new instance (the renewal_rollover leg).</param>
/// <param name="Actor">The acting principal recorded on the new stream's append.</param>
/// <param name="CommandId">The deterministic command id (the saga_outbox row id) — the
/// Idempotency-Key the new-stream append dedups on (ADR-PC-029 slot 4).</param>
public sealed record ConstituteRenewalCommand(
    Guid DepositId,
    Guid NewDepositId,
    string ProductId,
    string Role,
    DateTimeOffset RenewedAt,
    string FundingAccount,
    string Actor,
    Guid? CommandId = null);

/// <summary>
/// Link the renewal (step 9 of the retired monolith): append <c>DepositRenewed</c> to the CLOSING
/// stream, folding it from Matured to Renewed (terminal). Loads the closing (Matured) deposit and the
/// new deposit (folding its head <c>DepositConstituted</c> for the renewed facts) and calls the pure
/// <see cref="TermDepositDecider.DecideRenewalLink"/>. This append is NOT re-gated against the F.3
/// table for the same reason the retired monolith's step 9 was not — see the service method.
/// </summary>
/// <param name="DepositId">The CLOSING (Matured) deposit's stream id (= the saga's process_id).</param>
/// <param name="NewDepositId">The renewed instance's stream id (opened by
/// <see cref="ConstituteRenewalCommand"/>), whose head <c>DepositConstituted</c> fills the link.</param>
/// <param name="RenewedAt">The valid time recorded on the <c>DepositRenewed</c> append.</param>
/// <param name="Actor">The acting principal recorded on the closing stream's append.</param>
/// <param name="CommandId">The deterministic command id (the saga_outbox row id) — the
/// Idempotency-Key the closing-stream append dedups on (ADR-PC-029 slot 4).</param>
public sealed record LinkRenewalCommand(
    Guid DepositId,
    Guid NewDepositId,
    DateTimeOffset RenewedAt,
    string Actor,
    Guid? CommandId = null);
