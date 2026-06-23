# /families

The loaded **family schemas**: event types, pure handlers, projections, and
lifecycle state machines. Two families live here: `term_deposit` (the v1 liability
family) and `personal_loan` (the closed-end asset family, ADR-PC-031 / ADR-PC-030
roadmap item 2).

- **Build provenance:** in-house (product engine, "blue")
- **Runtime / stack:** loaded by `/engine` (.NET 10) — see feature-design event-store §3
- **CODEOWNERS:** engine team (the typed schema is engine code; product-team *variants* live in `/product-configs`)
- **Path-scoped CI:** built and unit-tested as part of the engine pipeline

> Status: `term-deposit` landed (E.1) — the four AT_MATURITY events, their pure fold
> handlers, and the deposit-position projection (`term-deposit/src/`). The full eleven-event
> set (F.2) and the lifecycle state machine (F.3 — `LifecycleTransitions.cs`, the one
> auditable transition-legality table the decider consults) are in; periodic/early-termination
> variants and the remaining projections are the rest of Epic F. `personal-loan` is the second,
> asset-side family (ADR-PC-031): six events (`LoanDisbursed`…`LoanWrittenOff`), its own
> `LifecycleTransitions.cs`, projection, and decider, registered in `engine/Babelstone.slnx`.
> Layout governed by
> [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
