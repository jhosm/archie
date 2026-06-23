# /orchestrator

The in-house **saga orchestrator** — a Redpanda consumer that drives multi-step
sagas with compensation, persisting saga state as rows in its application database.
Since I.1 it also hosts the **edge-over-saga front door** (ADR-IC-006 §P4): the `202` +
`process_id` + SSE stream that STARTS a saga and follows it to a terminal state.

- **Build provenance:** in-house estate ("estate by role, in-house by provenance") — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET — the decisive S2 reason in [ADR-IC-003](../docs/product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) is that the orchestrator "speaks the same language as every other service in the stack" (the .NET engine, [ADR-PC-010](../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md))
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

## Saga state-machine substrate (H.1)

`src/Babelstone.Orchestrator.Substrate/` is the hand-rolled, **family-agnostic** saga substrate
(ADR-PC-010 — no heavyweight framework owns its own tables; ADR-IC-018 §P1/§P2 — it carries no
`families/**` reference). It is the foundation the concrete sagas build onto; it delivers the
machinery, not their full business logic. The composition root that wires the substrate to a
concrete family module is the host project `src/Babelstone.Orchestrator/` (a `Microsoft.NET.Sdk.Web`
exe — see `Program.cs` and `Edge/` below).

- **`Saga/`** — the state-machine machinery. `TableStateMachine`
  is a hand-rolled, table-driven machine where the explicit `(from_state, event_type) →
  (next_state, commands)` table **is** the specification (§P2) — an illegal transition is
  rejected, never silently applied. The substrate defines only this abstract machinery (plus the
  `ISagaModule` plug-in seam); a **concrete** saga such as `ConstitutionProcess` — that table
  populated with the Document 05 happy + compensation + escalation flow — lives in its family
  module at `families/term-deposit/src/Babelstone.Families.TermDeposit.Orchestration/ConstitutionProcess.cs`
  (ADR-IC-018 §D1/§D4), never in the substrate.
- **`Substrate/Saga/SagaStateStore` + `SagaTransitionLog`** — Npgsql persistence. The saga aggregate
  is one `saga_state` row, advanced under **optimistic concurrency** (`WHERE version = ?`,
  §P1 / §Residual "Concurrent writer race"); every accepted move appends an immutable
  `saga_transition` audit row (§F2).
- **`Substrate/Inbox/SagaAdvanceHandler`** — the **idempotent, inbox-driven advance** (§S2): one
  PostgreSQL transaction dedups on the message id (Document 04 inbox), loads the saga, asks
  the state machine for the transition, applies it, persists the audit row, and emits the
  decided commands through the outbox seam (`ISagaCommandSink`). Effectively-once
  progression. Decoupled from Confluent/Avro so it is testable against a bare PostgreSQL;
  the real consume loop (the engine's `InboxPump`, G.2) plugs onto it via its
  `IInboxMessageHandler` seam.
- **`Substrate/Migrations/`** — the schema (`Substrate/Migrations/Sql/0001_saga_state.sql`, through
  `0007_saga_outbox_fifo_guard.sql`), with the
  `MigrationRunner`/`MigrationSet` pattern lifted from `engine/Babelstone.EventStore.Migrations`.
  Provisions the `babelstone_orchestrator` runtime role (ADR-PC-001 §P3): UPDATE on
  `saga_state` (the one mutable table), append-only `saga_transition`/`inbox`.
- **`Edge/` (I.1)** — the **edge-over-saga front door** (ADR-IC-006 §P4 / Document 05 §Step 0).
  In plain terms: a client hits the edge, the edge STARTS the saga and immediately returns
  `202 Accepted` with a `process_id` and an SSE `stream_url`; the stream follows the saga to a
  terminal state. The orchestrator is the application BEHIND the Kong gateway (Boundary 1), so it
  now hosts its own Kestrel HTTP surface (`Microsoft.NET.Sdk.Web`, a FRAMEWORK reference — NOT an
  engine-kernel `ProjectReference`, so it stays extraction-ready per ADR-PC-019 §P2) ALONGSIDE the
  consume loop + dispatcher. `EdgeSagaStarter` creates the `ConstitutionProcess` STARTED row and
  drives the first transition IN-PROCESS in one transaction (emitting the parallel commands to
  `saga_outbox` — **nothing on the bus**; the bus stays events-only). `ProcessApiEndpoints` maps
  `POST /api/v1/deposits/constitute` and the SSE `GET /api/v1/processes/{id}/stream`, which streams
  the structural saga state (never PII) and enforces **per-process authz** — the requester's
  `client_id` must match the process's owning client (`process_id` is NOT a capability token).

**No PII** (ADR-PC-004 §P2 / no-PII-on-the-durable-bus): every persisted column is a
reference — a process id, a state label, a correlation GUID. A subject's PII never lands
here; the saga carries references and resolves PII internally behind the engine's OpenBao
boundary.

> Status: saga substrate landed (H.1, bd babelstone-mj2i) — states, persisted transitions,
> compensation, idempotent inbox-driven advance. The concrete constitution (H.2,
> babelstone-n55u) and renewal (H.3, babelstone-mtto) sagas build on it. Extraction-ready
> subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
