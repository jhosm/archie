// Package pack is the constraint schema for a pack's own data files
// (ADR-PC-007). A pack is auditor-readable YAML data + bundled `.cue` family
// schemas, distributed as a cosign-signed OCI artefact pulled by digest
// (ADR-PC-007 §P1–P2). These definitions describe the *shape of the pack's
// YAML data* — the manifest, primitives, parameters, rate-sheet refs, and
// sealed test corpus — so the publish pipeline can `cue vet` each file before
// `oras push` (ADR-PC-007 Residual risks: "the publish CI runs cue vet of
// pack.yaml against the manifest schema").
//
// This is distinct from the *family* schema (../families/term-deposit.cue),
// which validates variant YAML. This validates the pack data itself.
//
// Layout note: ADR-PC-007 §P1 is the authoritative file layout — pack.yaml is
// the manifest (identity, metadata, deps) and primitives/parameters/etc. are
// separate files. (feature-design-configuration-surface §3.4 illustrates an
// inline-primitives manifest; §P1 post-dates it and splits them for
// auditor `cat`+`diff` readability — §P1 wins.)
package pack

// ---------------------------------------------------------------------------
// pack.yaml — the manifest (ADR-PC-007 §P1; surface §3.4)
// ---------------------------------------------------------------------------

#Manifest: {
	// Identity. The composite version key on the event envelope / registries
	// is `<pack_id>.<pack_version>` = `pt.2026.1` (ADR-PC-009; matches the
	// family schema's #PackId in ../common.cue). pack_version is immutable
	// once published (ADR-PC-007 §P1).
	pack_id:      =~"^[a-z]{2}$"          // ISO-3166-ish jurisdiction, e.g. "pt"
	pack_version: =~"^[0-9]{4}\\.[0-9]+$" // YYYY.N, e.g. "2026.1"
	namespace:    =~"^[a-z]{2}$"          // pack-bound reference namespace, e.g. "pt"

	// The manifest's *own* shape version — a forward-only contract
	// (ADR-PC-007 Consequences). Bumped only on a pack-format major change.
	manifest_schema_version: int & >=1

	publisher:           =~"^[^@[:space:]]+@[^@[:space:]]+$" // pack_signed_by identity
	pack_effective_from: #Date

	// The pack this one supersedes in its line; null for the first.
	based_on_pack_version: =~"^[0-9]{4}\\.[0-9]+$" | null

	// Human-readable changelog of what changed vs based_on_pack_version.
	delta_summary: string & !=""

	// Non-empty ⇒ adoption requires explicit operator acknowledgement
	// (surface §3.6, Q-N: no silent pack upgrades).
	breaking_changes: [...#BreakingChange]

	dependencies: {
		// Semver range of engine versions this pack is compatible with
		// (surface §3.8 compatibility matrix). A formula_ref the engine in
		// range does not implement is rejected at deploy (surface §3.4).
		engine_compatible_versions: string & !=""
	}

	// Which family-schema version this pack's bundled schemas/ carries
	// (authoring §6 pinning). Keyed by family.
	schema_pins: [string]: #SchemaRef

	// Names of the rate-sheet-refs/<name>.yaml files this pack ships.
	rate_sheet_refs: [...(=~"^[a-z][a-z0-9-]*$")]

	// OCI ref of the sealed test-corpus artefact (surface §3.9).
	test_corpus_ref: =~"^oci://"

	// Q-P reserve: multi-pack composition overlays, no-op in v1 (surface §3.11).
	primitive_overlays: [...] | *[]
}

#BreakingChange: {
	id:          =~"^[a-z][a-z0-9_]*$"
	description: string & !=""
}

#SchemaRef:   =~"^[a-z_]+@[0-9]{4}\\.[0-9]+$" // <family>@YYYY.N
#Date:        =~"^[0-9]{4}-[0-9]{2}-[0-9]{2}$"
#BasisPoints: int & >=0 & <=10000

#Cents: int & >=0

// ---------------------------------------------------------------------------
// primitives/*.yaml — pack-bound primitives the family schema references.
// `formula_ref` is the bridge to an engine-implemented primitive
// (surface §3.4). Each primitive category is a map keyed by the
// reference id used in variants (e.g. day_count.act_360 ← `pt.act_360`).
// ---------------------------------------------------------------------------

#FormulaRef: =~"^engine\\.[a-z0-9_]+(\\.[a-z0-9_]+)*$"

// `permitted_for` declares the product families a day-count is regulatorily
// permitted for — pack-declared regulatory law, not validator-encoded. PT
// retail term deposits require Act/360 (02 §2.2), so only act_360 lists
// `term_deposit`; the others declare an empty set and depth-4 rejects them for
// a term-deposit variant. (Previously the permitted set was a hardcoded Go map
// keyed on namespace; moving it here makes the rule auditor-visible in the pack
// — `cat` + `diff`, no tooling.) The family ids match the family-schema names
// (registry in pack-validate; e.g. term_deposit).
#DayCounts: [string]: {
	formula_ref: #FormulaRef
	permitted_for: [...(=~"^[a-z][a-z0-9_]*$")] | *[]
}

#Withholding: [string]: {
	formula_ref:       #FormulaRef
	rate_basis_points: #BasisPoints
	basis:             "gross_interest" | "principal"
	timing:            "at_credit" | "at_maturity" | "at_constitution"
	exemptions: [...{id: =~"^[a-z][a-z0-9_]*$", evidence: =~"^[a-z][a-z0-9_]*$"}]
	reporting: [string]: {required: bool, frequency: "annual" | "monthly" | "quarterly"}
}

// Keyed by scheme id (e.g. deposit_guarantee), consistent with the other
// primitive categories. Coverage ceiling per depositor (EU DGS = €100k).
#Fgd: [string]: {
	coverage_ceiling_cents: #Cents
	scheme:                 string & !=""
}

#Reporting: [string]: {
	active:    bool
	frequency: "annual" | "monthly" | "quarterly"
	regulator: string & !=""
}

// primitives/renewal-policies.yaml — auto-renewal-policy RESTRICTIONS (02 §2.4.4).
// Keyed by the lower_snake policy id (e.g. same_term_same_rate ← the
// SAME_TERM_SAME_RATE enum the family schema's auto_renewal_policy declares).
// `permitted_for` is the SAME pack-declared regulatory permitted-set the day-count
// primitive uses: the product families that MAY use this restricted policy. 02
// §2.4.4 calls SAME_TERM_SAME_RATE "less common, pack-restricted"; this file is
// where that restriction lives, auditor-visible in the signed pack rather than a
// hardcoded validator map. Only RESTRICTED policies appear here — NONE and
// SAME_TERM_CURRENT_RATE are unrestricted and carry no entry. (F.5 follow-up,
// bd babelstone-k6r8.6 / the babelstone-k4yr restriction the renewal decider
// recorded as a missing pack primitive.)
#RenewalPolicies: [string]: {
	description: string & !=""
	permitted_for: [...(=~"^[a-z][a-z0-9_]*$")] | *[]
}

// ---------------------------------------------------------------------------
// parameters/constants.yaml — pack-level scalar constants the schema's
// depth-3 checks resolve against (e.g. tan_basis_points <= max_consumer_rate_bps,
// ADR-PC-006 Context). Values pending Epic 0 regulatory sign-off are marked
// in the data file.
// ---------------------------------------------------------------------------

// Closed: an unknown/misspelled constant key (max_consuer_rate_bps) must fail
// here, not bind to nothing at depth 3. New pack constants are added by an
// explicit, additive edit to this definition — the same no-DSL-escape-hatch
// discipline as the family schema (ADR-PC-006), applied to the governed,
// signed pack artefact where closedness matters most.
#Parameters: {
	max_consumer_rate_bps:           #BasisPoints
	auto_renewal_optout_window_days: int & >0
}

// ---------------------------------------------------------------------------
// rate-sheet-refs/<name>.yaml — version-pinned refs to ADR-PC-008-stored
// sheets (the pack carries refs only; bodies live in the rate_sheets table, C.6).
// ---------------------------------------------------------------------------

#RateSheetRefs: {
	refs: [...{
		product_family:        =~"^[a-z][a-z0-9_]*$"
		rate_sheet_version_id: =~"^[a-z][a-z0-9._-]*$"
	}]
}

// ---------------------------------------------------------------------------
// families.yaml — the family-manifest (ADR-PC-007 §P1; bd babelstone-9w2k.3).
// Pins the FAMILY SET a deployment carrying this pack is allowed to run, the
// same way #Manifest.schema_pins pins each family's SCHEMA version. The host's
// HostModuleLoader cross-checks each scanned IFamilyHostModule against THIS list
// at load and FAILS CLOSED on a family/schema-version skew or a declared family
// with no loadable module (ADR-PC-009 §P1: the pinned pack is the authoritative
// per-deployment family set; every module stamps schema_version onto every
// EventEnvelope, so skew is an audit/replay hazard). The schema_version here
// equals the family module's IFamilyModule.SchemaVersion (e.g.
// term_deposit@2026.1) and MUST be consistent with the SAME family's
// #Manifest.schema_pins entry — a closed cross-pin, auditor-visible by `cat`.
// `aggregate_type` is the event-envelope aggregate_type / bus topic the family
// writes under (the engine's documented convention aggregate_type == family_name
// == topic, ADR-IC-004 §Consequences); `plugin_assembly` names the .NET assembly
// carrying the family's IFamilyHostModule, so a skew message can name the box.
#FamilyManifest: {
	families: [...{
		family_name:     =~"^[a-z][a-z0-9_]*$"
		aggregate_type:  =~"^[a-z][a-z0-9_]*$"
		schema_version:  #SchemaRef // <family>@YYYY.N, the SAME shape as schema_pins
		plugin_assembly: =~"^[A-Za-z][A-Za-z0-9_.]*$"
	}]
}

// ---------------------------------------------------------------------------
// test-corpus/canonical-instances.yaml — sealed regression inputs (surface
// §3.9). expected-events.yaml is engine-GENERATED (ADR-PC-007 §P5), never
// hand-authored, so it has no input-side schema here.
// ---------------------------------------------------------------------------

#CanonicalInstances: {
	tests: [...{
		test_id:           =~"^[a-z][a-z0-9_]*$"
		pack:              =~"^[a-z]{2}\\.[0-9]{4}\\.[0-9]+$"
		variant_id:        =~"^[a-z][a-z0-9_]*$"
		principal_cents:   #Cents & >0
		constituted_at:    #Date
		rate_basis_points: #BasisPoints
	}]
}
