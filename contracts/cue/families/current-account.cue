// current-account.cue — the v1 current_account (conta à ordem / demand account) family schema
// (ADR-PC-006; ADR-PC-030 roadmap item — the first TRANSACTIONAL family, the first instance of the
// ADR-PC-033 Account abstraction with a real accounting/available split + a hold ledger).
//
// A demand account's product config is deliberately thin: it carries no principal or amortization
// grid (a transactional account is not a fixed-term instrument), only the version envelope, the
// currency, the arranged-overdraft headroom, and the velocity/transaction limits the authorize
// decider reads. The account's balances are NOT config — both are spine-owned folds over the
// movement ledger + hold set (ADR-PC-033), never a declared number.
//
// Validate a variant with:
//   cue vet -d '#CurrentAccount' <variant>.yaml common.cue families/current-account.cue
//
// #CurrentAccount is a closed definition: a variant carrying a field this schema does not declare
// fails depth 1 (no DSL escape hatch, ADR-PC-006 Decision).
//
// The arranged-overdraft and velocity/transaction-limit constructs land here with the family wiring
// (ADR-PC-037). They are DECLARATIVE grammar only: the pack VALUES that populate a concrete limit,
// the overdraft-interest ACCRUAL math (run command-side), and the within-limit / unarranged-overdraft
// authorize decision are the sibling arranged-overdraft change (the ARRANGED_OVERDRAFT_PACK_BOUNDED
// commitment lands its test there). The pack owns the values; the decider evaluates them.
package family

#CurrentAccount: {
	// --- version envelope (authoring §6; surface §3.5) -------------------
	// Every variant pins the family-schema version and the pack version it was authored against. The
	// pins travel onto AccountOpened and never move, so an instance's governing schema+pack is
	// answerable from its events alone.
	variant_id: #VariantId
	schema:     #SchemaRef // e.g. current_account@2026.1
	pack:       #PackId    // e.g. pt.2026.1

	currency: "EUR" // v1 is EUR-only; the field is explicit, not implied.

	// --- arranged overdraft (descoberto autorizado, ADR-PC-037) ----------
	// The pack-declared authorization HEADROOM in integer cents: a debit that overdraws the account
	// within this limit is authorized, extending the available-balance identity to
	// `available = accounting − Σ active holds + arranged_overdraft_limit`; a debit beyond it — an
	// unarranged overdraft (ultrapassagem) — is refused. OPTIONAL and defaults to absent (0-equivalent):
	// a v1 basic account carries no arranged overdraft. The overdraft-interest accrual rate is resolved
	// from the rate sheet, not inline — added with the accrual math (the sibling arranged-overdraft
	// change), so this construct is the declarative LIMIT only.
	arranged_overdraft_limit?: #Cents

	// --- velocity / transaction limits (ADR-PC-037) ---------------------
	// Declarative caps the authorize decider reads at stage 4 (ADR-PC-030), alongside the arranged
	// overdraft. OPTIONAL: an account with no configured caps is unconstrained here (the engine still
	// applies the balance + overdraft gate). Element-wise numeric coherence CUE cannot express (e.g. a
	// per-transaction max above a daily cap) is a depth-4 pack-validate check, not depth 1.
	transaction_limits?: #TransactionLimits

	// --- dormancy horizon (ADR-PC-037) ----------------------------------
	// The inactivity horizon, in days, after which an account is eligible to be marked dormant
	// (AccountMarkedDormant). The dormancy CRITERIA are pack/product policy, not fixed in the engine —
	// this is where a product declares them. OPTIONAL: absent ⇒ the product configures no automatic
	// dormancy horizon.
	dormancy_horizon_days?: int & >0

	// --- optional activation date (authoring §4 step 5) -----------------
	effective_from?: =~"^[0-9]{4}-[0-9]{2}-[0-9]{2}$" // ISO-8601 date
}

// #TransactionLimits — the closed velocity/transaction-limit block (ADR-PC-037). Every cap is integer
// cents (#Cents) and OPTIONAL, so a variant declares only the caps its product enforces; a field this
// block does not declare fails depth 1 (closed-struct, ADR-PC-006 Decision).
#TransactionLimits: {
	per_transaction_max_cents?: #Cents
	daily_velocity_cents?:      #Cents
	monthly_velocity_cents?:    #Cents
}
