# /families

The loaded **family schemas**: event types, pure handlers, projections, and
lifecycle state machines. `term_deposit` is the v1 family.

- **Build provenance:** in-house (product engine, "blue")
- **Runtime / stack:** loaded by `/engine` (.NET 9) — see feature-design event-store §3
- **CODEOWNERS:** engine team (the typed schema is engine code; product-team *variants* live in `/product-configs`)
- **Path-scoped CI:** built and unit-tested as part of the engine pipeline

> Status: skeleton — no source yet. Layout governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
