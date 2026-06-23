// term-deposit.cue — the v1 family schema (ADR-PC-006; v1 scope 02 §2).
//
// One coarse `term_deposit` schema covers every shape of PT term deposit v1
// must handle (authoring §3.1 coarse-start, fine-drift): the three interest
// variants (AT_MATURITY / PERIODIC / ADVANCE, 02 §2.1), the flat-vs-stepped
// rate split (authoring §3.2), the flat-vs-banded early-termination policy
// (02 §2.5), and auto-renewal (02 §2.4.4). Splitting into focused per-shape
// schemas is a later, quarterly-cadence move triggered by union accumulation
// (authoring §3.1) — not done on day one.
//
// Validate a variant with:
//   cue vet -d '#TermDeposit' <variant>.yaml common.cue families/term-deposit.cue
//
// #TermDeposit is a closed definition: a variant carrying a field this schema
// does not declare fails depth 1 (no DSL escape hatch, ADR-PC-006 Decision).
package family

#TermDeposit: {
	// --- version envelope (authoring §6; surface §3.5) -------------------
	// Every variant pins the family-schema version and the pack version it
	// was authored against. The pins travel onto DepositConstituted and never
	// move (02 §2.4.3 envelope), so an instance's governing schema+pack is
	// answerable from its events alone.
	variant_id: #VariantId
	schema:     #SchemaRef // e.g. term_deposit@2026.1
	pack:       #PackId    // e.g. pt.2026.1

	// --- term & day-count ------------------------------------------------
	term_days: int & >0
	// Pack-bound: PT retail deposits use Act/360 (02 §2.2). The schema only
	// declares the binding; depth-4 regulatory coherence (in the pack) is
	// what rejects e.g. Act/365 for a PT deposit.
	day_count: #PackBoundPrimitive

	currency: "EUR" // v1 is EUR-only (02 §4); the field is explicit, not implied.

	// --- interest variant (02 §2.1) -------------------------------------
	interest_variant: "AT_MATURITY" | "PERIODIC" | "ADVANCE"

	// payment_period_months is required for PERIODIC and forbidden otherwise
	// — a cross-field invariant the schema expresses declaratively rather
	// than leaving to the engine (depth-4 shape, authoring §5).
	if interest_variant == "PERIODIC" {
		// v1 periodic interest is monthly or quarterly only (02 §2.1). Wider
		// cadences (semi-annual, annual) are a fine-drift extension if scope
		// expands (authoring §3.1) — not silently permitted here.
		payment_period_months: 1 | 3
	}
	if interest_variant != "PERIODIC" {
		payment_period_months?: _|_ // present ⇒ error
	}

	// --- rate: flat XOR stepped (authoring §3.2) ------------------------
	// Modelled as a disjunction of two closed shapes, so a variant setting
	// both `flat` and `stepped`, or neither, matches no branch and is
	// rejected. The numeric rate is never inline: each shape carries a
	// rate-sheet reference resolved at constitution (surface §2.3).
	rate: #Rate

	// --- commercial-eligibility preconditions (ADR-PC-024) --------------
	// Which upstream-evaluated eligibility verdicts a product REQUIRES to be
	// constituted: new-client-only promotions, new-money requirements, salary
	// domiciliation, mortgage-linked preferential products (ADR-PC-024 §1).
	// The engine OWNS the closed verdict-key taxonomy (#PreconditionKey) and the
	// refusal semantics; the product config OWNS which keys a given product needs
	// (this list); upstream OWNS evaluation (the saga resolves each verdict and
	// passes it on the constitution command). The list is OPTIONAL and defaults
	// to absent: v1 launch products are not eligibility-gated (02 §4), so most
	// variants omit it. A key may not repeat (a set, not a bag); the leading
	// element makes a present list ≥1 entry. The pack restricts which keys are
	// LEGALLY permissible in a jurisdiction — a depth-3 pack-bound check, not
	// expressible here without the pack (ADR-PC-024 §6 Residual risks).
	required_preconditions?: [#PreconditionKey, ...#PreconditionKey]

	// --- early termination: flat XOR banded (02 §2.5) -------------------
	early_termination: #EarlyTermination

	// --- auto-renewal (02 §2.4.4) ---------------------------------------
	auto_renewal_policy: "NONE" | "SAME_TERM_CURRENT_RATE" | "SAME_TERM_SAME_RATE"

	// --- principal bounds (risk corridor, authoring §4 step 4) ----------
	principal_bounds: #PrincipalBounds

	// --- partial-withdrawal policy (F.12; 02 §2.4.1) --------------------
	// Optional. Declares the three gates a partial early withdrawal must clear
	// — a minimum withdrawal amount, a minimum remaining balance, and a lock-up
	// (carência) window after constitution. The block mirrors the engine's
	// PartialWithdrawalPolicy (MinWithdrawalCents / MinRemainingBalanceCents /
	// CarenciaDays); it rides on the config as an explicit decider input
	// resolved at constitution (ADR-PC-008; ADR-PC-021 §D3), never a command
	// input. A variant that OMITS the block permits no F.12-gated partial
	// withdrawals — it resolves to PartialWithdrawalPolicy.Unrestricted (the
	// zero-gate policy), leaving only the structural rules the decider always
	// applies (positive amount; cannot withdraw the whole balance — that is a
	// termination, F.4). Two cross-field coherence invariants
	// (min_remaining_balance_cents < principal_bounds.max_cents; carencia_days <
	// term_days) are depth-4 regulatory checks the Go validator enforces — not
	// expressible element-wise here, the same deferral as #SteppedRate.steps and
	// #BandedPolicy.banded.
	partial_withdrawal?: #PartialWithdrawal

	// A partial withdrawal is forbidden on an ADVANCE (juros antecipados) product
	// (F.12, bd babelstone-emtr): that shape pays the WHOLE term's interest up front
	// on the full principal, so a later withdrawal would strand pre-paid interest with
	// no accrual flow to re-base it (unlike AT_MATURITY/PERIODIC, whose remaining
	// accrual folds over the reduced principal). Unlike the two numeric coherence
	// invariants above, this is a presence-given-enum constraint the schema CAN express
	// declaratively — the same shape as payment_period_months — so a config that
	// declares the block alongside interest_variant: ADVANCE is rejected at the schema
	// layer. The runtime decider (PartialWithdrawalDecider) refuses such a withdrawal
	// as the backstop.
	if interest_variant == "ADVANCE" {
		partial_withdrawal?: _|_ // present ⇒ error
	}

	// --- optional activation date (authoring §4 step 5) -----------------
	effective_from?: =~"^[0-9]{4}-[0-9]{2}-[0-9]{2}$" // ISO-8601 date
}

// #PreconditionKey — the engine-owned CLOSED taxonomy of commercial-eligibility
// verdict keys a product may require (ADR-PC-024 §1, §6). The engine owns this set
// and the refusal semantics; a config may only pick from it (a key the schema does
// not declare fails depth 1, the same closed-struct guarantee as everywhere here).
// Each key names a product-specific predicate evaluated UPSTREAM (CRM for new-client
// / relationship, Core Banking for fund provenance / salary domiciliation, the credit
// system for a linked mortgage) and resolved into the constitution command as an opaque
// { satisfied, evidence_ref, evaluated_at } verdict — the engine never re-evaluates it
// (ADR-PC-024 §2). Adding a predicate is a pack/config addition with zero generic-engine
// diff; widening THIS taxonomy is the only engine change (ADR-PC-024 §6).
#PreconditionKey: "is_new_client" | "is_new_money" | "salary_domiciled" | "mortgage_linked"

// #Rate — exactly one of flat / stepped. Each branch is closed.
#Rate: #FlatRate | #SteppedRate

#FlatRate: {
	flat: {
		rate_ref: #RateRef
	}
}

#SteppedRate: {
	stepped: {
		rate_ref: #RateRef
		// Step boundaries by elapsed day; each names a pricing band resolved
		// against the rate sheet. The leading element makes ≥1 step structural;
		// ascending from_day ordering is a depth-4 obligation (not expressible
		// element-wise in CUE), not enforced here.
		steps: [#RateStep, ...#RateStep]
	}
}

#RateStep: {
	from_day:     int & >=0
	pricing_band: =~"^[a-z][a-z0-9_]*$"
}

// #EarlyTermination — flat policy (a degenerate one-band schedule) XOR a
// banded schedule (02 §2.5). The PT pack restricts which penalty bases are
// legally permissible; that restriction is a pack-bound depth-3 check, not
// expressible here without the pack.
#EarlyTermination: #FlatPolicy | #BandedPolicy

#FlatPolicy: {
	flat: #Band
}

#BandedPolicy: {
	// (window, penalty) pairs the engine evaluates first-match against elapsed
	// term (02 §2.5). The leading element makes ≥1 band structural. Ascending
	// up_to_days order and the single open (null) tail are a depth-4 obligation
	// — not expressible element-wise in CUE, so not enforced here.
	banded: [#Band, ...#Band]
	// Optional minimum payout floor; the depositor's net never falls below it.
	floor_cents?: #Cents
}

#Band: {
	// null is the open-ended tail (02 §2.5: `up_to_days: null`).
	up_to_days: (int & >0) | null
	// Penalty as a share in basis points of the chosen basis (10000 = 100%).
	penalty_basis_points: #BasisPoints
	basis:                "ACCRUED_INTEREST" | "PRINCIPAL" | "BOTH"
}

// #PrincipalBounds (the principal risk corridor, authoring §4 step 4) is shared
// cross-family vocabulary defined once in common.cue (bd babelstone-5r9n.11),
// which every family schema already unifies against — so a bounds-semantics
// change is one edit, not one per family. `principal_bounds` above references it.

// #PartialWithdrawal — the F.12 partial-withdrawal policy (02 §2.4.1). A closed
// definition (ADR-PC-006): an unknown field inside the block fails depth 1, the
// same no-escape-hatch guarantee as every other #Name here. Field names mirror
// the engine's PartialWithdrawalPolicy record one-for-one. All amounts are
// integer cents (#Cents); carencia_days is a non-negative day count — a
// duration, not money, so it is declared inline as `int & >=0`, not a #Cents.
// A 0 on any gate means "no minimum / no lock-up" (the degenerate-policy
// semantics of PartialWithdrawalPolicy with that field zero). The two cross-field
// coherence invariants that relate this block to term_days and
// principal_bounds.max_cents are depth-4 Go checks, not expressed here.
#PartialWithdrawal: {
	min_withdrawal_cents:        #Cents
	min_remaining_balance_cents: #Cents
	carencia_days:               int & >=0
}
