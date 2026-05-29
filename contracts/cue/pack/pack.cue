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
// pack.yaml — the manifest (ADR-PC-007 §P1 line 132; surface §3.4)
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
// (surface §3.4 line 219). Each primitive category is a map keyed by the
// reference id used in variants (e.g. day_count.act_360 ← `pt.act_360`).
// ---------------------------------------------------------------------------

#FormulaRef: =~"^engine\\.[a-z0-9_]+(\\.[a-z0-9_]+)*$"

#DayCounts: [string]: {formula_ref: #FormulaRef}

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

// ---------------------------------------------------------------------------
// parameters/constants.yaml — pack-level scalar constants the schema's
// depth-3 checks resolve against (e.g. tan_basis_points <= max_consumer_rate_bps,
// ADR-PC-006 Context). Values pending Epic 0 regulatory sign-off are marked
// in the data file.
// ---------------------------------------------------------------------------

#Parameters: {
	max_consumer_rate_bps:           #BasisPoints
	auto_renewal_optout_window_days: int & >0
	...
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
