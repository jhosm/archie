# /orchestrator

The in-house **saga orchestrator** — a Redpanda consumer that drives multi-step
sagas with compensation, persisting saga state as rows in its application database.

- **Build provenance:** in-house estate ("estate by role, in-house by provenance") — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET — the decisive S2 reason in [ADR-IC-003](../docs/product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) is that the orchestrator "speaks the same language as every other service in the stack" (the .NET engine, [ADR-PC-010](../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md))
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

## Saga state-machine substrate (H.1)

`src/Babelstone.Orchestrator/` is the hand-rolled saga substrate (ADR-PC-010 — no
heavyweight framework owns its own tables). It is the foundation H.2 (constitution) and
H.3 (renewal) build their concrete sagas onto; it delivers the machinery, not their full
business logic.

- **`Saga/`** — the state machine. `SagaState` is the `ConstitutionProcess` business-state
  vocabulary (ADR-IC-003 §Context, named for the business situation per §P3). `TableStateMachine`
  is a hand-rolled, table-driven machine where the explicit `(from_state, event_type) →
  (next_state, commands)` table **is** the specification (§P2) — an illegal transition is
  rejected, never silently applied. `ConstitutionProcess` is that table populated with the
  Document 05 happy + compensation + escalation flow.
- **`Saga/SagaStateStore` + `SagaTransitionLog`** — Npgsql persistence. The saga aggregate
  is one `saga_state` row, advanced under **optimistic concurrency** (`WHERE version = ?`,
  §P1 / §Residual "Concurrent writer race"); every accepted move appends an immutable
  `saga_transition` audit row (§F2).
- **`Inbox/SagaAdvanceHandler`** — the **idempotent, inbox-driven advance** (§S2): one
  PostgreSQL transaction dedups on the message id (Document 04 inbox), loads the saga, asks
  the state machine for the transition, applies it, persists the audit row, and emits the
  decided commands through the outbox seam (`ISagaCommandSink`). Effectively-once
  progression. Decoupled from Confluent/Avro so it is testable against a bare PostgreSQL;
  the real consume loop (the engine's `InboxPump`, G.2) plugs onto it via its
  `IInboxMessageHandler` seam.
- **`Migrations/`** — the schema (`Migrations/Sql/0001_saga_state.sql`), with the
  `MigrationRunner`/`MigrationSet` pattern lifted from `engine/Babelstone.EventStore.Migrations`.
  Provisions the `babelstone_orchestrator` runtime role (ADR-PC-001 §P3): UPDATE on
  `saga_state` (the one mutable table), append-only `saga_transition`/`inbox`.

**No PII** (ADR-PC-004 §P2 / no-PII-on-the-durable-bus): every persisted column is a
reference — a process id, a state label, a correlation GUID. A subject's PII never lands
here; the saga carries references and resolves PII internally behind the engine's OpenBao
boundary.

> Status: saga substrate landed (H.1, bd babelstone-mj2i) — states, persisted transitions,
> compensation, idempotent inbox-driven advance. The concrete constitution (H.2,
> babelstone-n55u) and renewal (H.3, babelstone-mtto) sagas build on it. Extraction-ready
> subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
