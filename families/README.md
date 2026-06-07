# /families

The loaded **family schemas**: event types, pure handlers, projections, and
lifecycle state machines. `term_deposit` is the v1 family.

- **Build provenance:** in-house (product engine, "blue")
- **Runtime / stack:** loaded by `/engine` (.NET 10) — see feature-design event-store §3
- **CODEOWNERS:** engine team (the typed schema is engine code; product-team *variants* live in `/product-configs`)
- **Path-scoped CI:** built and unit-tested as part of the engine pipeline

> Status: `term-deposit` landed (E.1) — the four AT_MATURITY events, their pure fold
> handlers, and the deposit-position projection (`term-deposit/src/`). The full eleven-event
> set (F.2) and the lifecycle state machine (F.3 — `LifecycleTransitions.cs`, the one
> auditable transition-legality table the decider consults) are in; periodic/early-termination
> variants and the remaining projections are the rest of Epic F. Layout governed by
> [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
