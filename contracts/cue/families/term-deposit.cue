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

	// --- early termination: flat XOR banded (02 §2.5) -------------------
	early_termination: #EarlyTermination

	// --- auto-renewal (02 §2.4.4) ---------------------------------------
	auto_renewal_policy: "NONE" | "SAME_TERM_CURRENT_RATE" | "SAME_TERM_SAME_RATE"

	// --- principal bounds (risk corridor, authoring §4 step 4) ----------
	principal_bounds: #PrincipalBounds

	// --- optional activation date (authoring §4 step 5) -----------------
	effective_from?: =~"^[0-9]{4}-[0-9]{2}-[0-9]{2}$" // ISO-8601 date
}

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

#PrincipalBounds: {
	min_cents:  #Cents
	max_cents?: int & >0
	// max, when present, is not below min.
	if max_cents != _|_ {
		max_cents: >=min_cents
	}
}
