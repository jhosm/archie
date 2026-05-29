// Package family is the engine's family-schema constraint language (ADR-PC-006).
//
// These .cue files are the *typed contract* that variant YAML populates
// (feature-design-configuration-authoring §2.2). They are authored by
// engineering / the pack team — never by variant authors — on a quarterly
// cadence, and they are the source of truth for the CUE schemas that a signed
// pack bundles a digest-pinned copy of (ADR-PC-007 §P1; the copy is C.4's job).
//
// common.cue holds the cross-family vocabulary: the version-key shapes, the
// bounded scalar types, the *pack-binding declaration* shapes, and the
// rate-sheet reference shape. A family schema (term-deposit.cue) composes
// these into one closed product-family contract.
//
// "No DSL escape hatch" (ADR-PC-006 Decision; authoring §9.5) is enforced
// structurally: every type below is a CUE *definition* (#Name), and CUE
// definitions are closed — a field the schema does not declare is rejected,
// not silently carried. There is no `extra: {...}` passthrough anywhere.
package family

// #PackId — a regulatory-pack version key, `pt.YYYY.N` (ADR-PC-007 §P2,
// immutable once published). The variant pins the pack it was authored
// against; the engine resolves it through the pack_versions registry
// (ADR-PC-009).
#PackId: =~"^[a-z]{2}\\.[0-9]{4}\\.[0-9]+$"

// #SchemaRef — a family-schema version key, `<family>@YYYY.N`
// (authoring §6). Pinned per instance alongside the pack version so an
// instance keeps running under the schema active at its constitution even
// after a fine-drift split (authoring §3.1).
#SchemaRef: =~"^[a-z_]+@[0-9]{4}\\.[0-9]+$"

// #VariantId — the stable identifier of a variant YAML, snake_case
// (authoring §2.3; the worked examples use e.g. `dpz_pt_12m_flat_juros_venc`).
#VariantId: =~"^[a-z][a-z0-9_]*$"

// #BasisPoints — a non-negative rate or share expressed in basis points
// (1 bp = 0.01%). 10000 bp = 100%. The variant-author surface never uses a
// float for money or rates; everything is integer bp / integer cents,
// matching the engine's Money discipline (ADR-PC-010, financial_concepts §5).
#BasisPoints: int & >=0 & <=10000

// #Cents — a non-negative integer-cents amount.
#Cents: int & >=0

// #PackBoundPrimitive — a *pack-binding declaration*. A field of this type
// names a primitive the engine supplies through the pinned pack (e.g.
// `pt.act_360` for the day-count). The schema declares only that the field is
// pack-bound and namespace-prefixed; whether the name resolves to a primitive
// the pinned pack actually carries is validator depth 2–3 (ADR-PC-006 Context
// table), which needs the pack data and the Go `pack-validate` binary (C.2,
// C.4). At depth 1 we enforce the binding *shape*: a dotted, lower-snake,
// jurisdiction-namespaced reference, never a free string and never an inline
// formula (no DSL escape hatch).
#PackBoundPrimitive: =~"^[a-z]{2}\\.[a-z0-9_]+(\\.[a-z0-9_]+)*$"

// #RateRef — a reference into a rate sheet, resolved at constitution to a
// concrete `tan_basis_points` + `rate_sheet_version_id` (ADR-PC-008;
// surface §2.3). The variant never carries the numeric rate — that lives on
// its own fast cadence in /rate-sheets. `sheet` selects which sheet binding
// (`live` is the active sheet); `role_selector` picks the pricing role
// (e.g. the standard vs new_money split, surface §2.2). Both resolve against
// the rate sheet at deploy/constitution time (depth 2–3).
#RateRef: {
	sheet:         "live" | =~"^[a-z][a-z0-9_]*$"
	role_selector: =~"^[a-z][a-z0-9_]*$"
}
