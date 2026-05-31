# Reading path — Pack author / compliance

**You author and audit a `pt.YYYY.N` regulatory [pack](../reference/glossary.md#pack-regulatory-pack)** — the declarative YAML of primitives, parameters, and [rate-sheet](../reference/glossary.md#rate-sheet) refs that pins what the engine is regulatorily allowed to do. Follow this sequence and you'll know what a pack declares, which schema constrains it, and how it ships signed and pulled by digest — all without writing a line of CUE handler logic. It links and sequences only — every claim lives once, in the spine ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)).

1. [Feature design — Configuration Surface](../product_concepts/feature-design-configuration-surface.md) — what is configurable and where each knob lives; the lay of the land.
2. [Feature design — Configuration Authoring](../product_concepts/feature-design-configuration-authoring.md) — how an author actually composes and pins those knobs.
3. [ADR-PC-006 — CUE Schema Language](../product_concepts/adrs/ADR-PC-006-cue-schema-language.md) — why constraints are CUE, the language your pack is validated against.
4. [ADR-PC-007 — Signed YAML OCI Pack](../product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) — the signing, packaging, and pull-by-digest discipline your pack ships under.
5. [ADR-PC-008 — Rate-Sheet Storage and Deploy API](../product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — how the rate sheets your pack references are stored and deployed.
6. [reference/pack-format/](../reference/pack-format/README.md) — the generated manifest schema every field of your pack must satisfy.
7. [reference/family-schemas/](../reference/family-schemas/README.md) — the family contracts your pack's primitives and parameters bind to.

**When you're ready to DO something:** author and load a real pack with [Tutorial 02 — author and load a PT pack](../guides/tutorials/02-author-and-load-a-pt-pack.md).
