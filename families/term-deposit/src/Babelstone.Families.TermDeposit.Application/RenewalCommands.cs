namespace Babelstone.Families.TermDeposit.Application;

// The renewal-saga command surface (bd babelstone-mtto PR B). The monolithic, un-idempotent
// RenewAsync (which matured + constituted + linked in one cross-stream call) is decomposed into
// TWO idempotent engine operations the renewal saga drives, with the MATURITY leg dropped:
// maturity is now autonomous (MatureAsync runs first and the closing deposit is already Matured
// when the saga fires). Each command carries only the per-step facts the engine needs; EVERY
// renewal fact — the rate, term, variant, cadence, policy AND (bd babelstone-mtto.5) the product
// code, pricing role and funding-account token — is read off the (Matured) closing deposit's folded
// state, NOT supplied here. So the orchestrator carries no product-family knowledge (ADR-IC-003
// §A7): the engine resolves product facts in-tx from the closing deposit it already loads. The
// optional CommandId is the ADR-PC-029 slot 4 idempotency key (the saga's
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
/// <remarks>
/// <b>The command is MINIMAL (bd babelstone-mtto.5): it carries NO product / role / funding.</b> The
/// engine now resolves ALL renewal facts — <c>product_code</c>, <c>role</c>, <c>funding_account</c> —
/// from the CLOSING deposit's folded state (now that <c>DepositConstituted</c> persists role + funding
/// alongside the already-persisted product code), so the orchestrator carries NO product-family
/// knowledge (the #200 / ADR-IC-003 §A7 principle "the engine resolves product facts in-tx"). The
/// closing id stays in the URL path; the body becomes <c>{ new_deposit_id, renewed_at?, actor }</c>.
/// </remarks>
/// <param name="DepositId">The CLOSING (Matured) deposit's stream id (= the saga's process_id). Its
/// folded state is the SINGLE source of the renewed instance's product / role / funding.</param>
/// <param name="NewDepositId">The fresh stream id the renewed instance is constituted under. Caller-
/// supplied (the saga derives it deterministically) so the renewal is a replayable command — the new
/// id is the same on replay and the <c>DepositRenewed</c> link stays stable.</param>
/// <param name="RenewedAt">The instant the renewal fires: the new sheet is resolved as-of here, and
/// it is the new constitution's valid time. Its DATE is the renewal/new-start date.</param>
/// <param name="Actor">The acting principal recorded on the new stream's append.</param>
/// <param name="CommandId">The deterministic command id (the saga_outbox row id) — the
/// Idempotency-Key the new-stream append dedups on (ADR-PC-029 slot 4).</param>
public sealed record ConstituteRenewalCommand(
    Guid DepositId,
    Guid NewDepositId,
    DateTimeOffset RenewedAt,
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
