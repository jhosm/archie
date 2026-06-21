// credito-pessoal.cue — the v1 credito_pessoal (closed-end personal loan) family schema
// (ADR-PC-006; ADR-PC-030 roadmap item 2 — the closed-end ASSET, mirroring the term-deposit
// liability).
//
// One coarse `credito_pessoal` schema covers the v1 shape of a PT personal loan: a fixed
// principal disbursed as a lump sum and amortized over `term_months` equal monthly installments
// on the French (constant-installment) schedule (fin-math §4.1), with a
// legally-capped early repayment (fin-math §7.5). Splitting into focused
// per-shape schemas is a later, quarterly-cadence move triggered by union accumulation
// (authoring §3.1) — not done on day one.
//
// ORIGINATION stays UPSTREAM (ADR-PC-030 §P1 / ADR-PC-024): the engine receives an
// already-approved, already-priced loan; the schema models the PRODUCT, not the underwriting.
//
// Validate a variant with:
//   cue vet -d '#CreditoPessoal' <variant>.yaml common.cue families/credito-pessoal.cue
//
// #CreditoPessoal is a closed definition: a variant carrying a field this schema does not
// declare fails depth 1 (no DSL escape hatch, ADR-PC-006 Decision).
package family

#CreditoPessoal: {
	// --- version envelope (authoring §6; surface §3.5) -------------------
	// Every variant pins the family-schema version and the pack version it was
	// authored against. The pins travel onto LoanDisbursed and never move, so an
	// instance's governing schema+pack is answerable from its events alone.
	variant_id: #VariantId
	schema:     #SchemaRef // e.g. credito_pessoal@2026.1
	pack:       #PackId    // e.g. pt.2026.1

	currency: "EUR" // v1 is EUR-only; the field is explicit, not implied.

	// --- term (closed-end amortization grid) ----------------------------
	// A personal loan amortizes on a MONTHLY grid over a whole number of months
	// (fin-math §2.2 — the periodic rate is the proportional TAN / 12). v1 prices
	// monthly installments only; other cadences are a fine-drift extension.
	term_months: int & >0

	// --- purpose (research/credito-pessoal/02 §2) ----------------------
	// The loan PURPOSE selects the legal TAEG ceiling bucket: lower-cap purposes
	// (education / health / renewables / equipment) price under a lower legal
	// ceiling than a general-purpose loan (no stated purpose). A closed
	// enum — the engine owns the taxonomy; the pack restricts which a jurisdiction
	// permits (a depth-4 pack-bound check, not expressible here without the pack).
	purpose: #Purpose

	// --- rate (resolved at disbursement, never inline) ------------------
	// The numeric TAN is never inline: the variant carries a rate-sheet reference
	// resolved at disbursement (surface §2.3), exactly like the term deposit. v1
	// loans are FIXED-rate (PT short/medium-term convention) — a single rate_ref,
	// not a stepped vector. Variable-rate reset is a later extension.
	rate: #FixedRate

	// --- early repayment: the capped early-repayment commission (fin-math §7.5) -
	// The commission the product charges on an early repayment, in basis points.
	// The PT consumer-credit STATUTORY ceiling (0.50% with >1y remaining, 0.25%
	// with ≤1y) is enforced engine-side by the decider (it caps `min(charged,
	// statutory)`), so the schema only bounds the CHARGED rate to a legal maximum
	// here (≤50 bps = the >1y statutory cap); whether a tighter ≤1y cap binds is a
	// runtime, remaining-term-dependent decision the decider owns.
	early_repayment: #EarlyRepayment

	// --- commercial-eligibility preconditions (ADR-PC-024) --------------
	// Which upstream-evaluated verdicts a product REQUIRES to be disbursed. For a
	// loan these are ORIGINATION-shaped (the solvency / CRC checks ADR-PC-030 keeps
	// UPSTREAM, recorded as opaque verdicts only). The engine OWNS the closed
	// verdict-key taxonomy (#LoanPreconditionKey) and the refusal semantics; the
	// product config OWNS which keys a product needs; upstream OWNS evaluation. The
	// list is OPTIONAL and defaults to absent: v1 launch products are not gated.
	required_preconditions?: [#LoanPreconditionKey, ...#LoanPreconditionKey]

	// --- principal bounds (risk corridor, authoring §4 step 4) ----------
	principal_bounds: #PrincipalBounds

	// --- optional activation date (authoring §4 step 5) -----------------
	effective_from?: =~"^[0-9]{4}-[0-9]{2}-[0-9]{2}$" // ISO-8601 date
}

// #Purpose — the engine-owned CLOSED taxonomy of loan-purpose categories
// (research/credito-pessoal/02 §2). `general` is the higher-cap, no-stated-purpose
// bucket; the rest are the lower-cap eligible purposes (DL 133/2009 art. 28). A value
// the schema does not declare fails depth 1.
#Purpose: "general" | "education" | "health" | "renewables" | "equipment"

// #FixedRate — a single rate-sheet reference resolved at disbursement (v1 loans are
// fixed-rate). Closed: a variant adding a stepped vector here fails depth 1.
#FixedRate: {
	fixed: {
		rate_ref: #RateRef
	}
}

// #EarlyRepayment — the early-repayment commission the product charges. The
// CHARGED rate is bounded to the >1y statutory ceiling (≤50 bps); the runtime decider
// applies the tighter remaining-term cap (0.25% ≤1y) and the lost-interest ceiling
// (fin-math §7.5). 0 ⇒ the product charges no early-repayment commission.
#EarlyRepayment: {
	commission_basis_points: int & >=0 & <=50
}

// #PrincipalBounds — the loan's risk corridor (authoring §4 step 4): the minimum and
// optional maximum principal a variant prices. Defined here (not shared) so the
// credito_pessoal schema is self-contained — the check-script vets each family schema
// against common.cue alone. Shape mirrors the term-deposit family's #PrincipalBounds.
#PrincipalBounds: {
	min_cents:  #Cents
	max_cents?: int & >0
	// max, when present, is not below min.
	if max_cents != _|_ {
		max_cents: >=min_cents
	}
}

// #LoanPreconditionKey — the engine-owned CLOSED taxonomy of loan commercial-eligibility
// verdict keys a product may require (ADR-PC-024 §1, §6). For a loan these are
// ORIGINATION-shaped: the upstream solvency assessment and CRC (Central Credit Register)
// consultation that ADR-PC-030 / ADR-PC-024 keep UPSTREAM, resolved into the
// disbursement command as opaque { satisfied, evidence_ref, evaluated_at } verdicts the engine
// never re-evaluates. The engine RECORDS the decision for audit; it never MAKES it
// (ADR-PC-030 §P1: origination is upstream).
#LoanPreconditionKey: "solvency_assessed" | "crc_consulted"
