# ADR-PC-035: Bulk-Operations Execution Pattern — A Register→Drain→Complete Runner over a Postgres Work-Table, Not a Work-Topic

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-26 |
| Deciders | jhosm |
| Shape | Tool-selection (ADR-PC-000 §D3 residual category — a runtime/operational-discipline posture for *how* the engine applies one operator action across a large population of instances; declared tool-selection per the §D4 default. F1/F2 do not discriminate, the same class as ADR-PC-034, ADR-PC-030, ADR-PC-019, ADR-PC-009.) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — a runtime mechanism, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-021 §P2](./ADR-PC-021-application-layer-family-owned-deciders.md) (the family-agnostic spine the runner lives on), [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md) (the atomic append + outbox the per-instance step rides), [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (the table-drained-by-a-background-service pattern this reuses), [ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) (store-only-by-construction milestones), [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md) (the store-only payload the per-instance event is folded as), [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md) (the receiver-dedupe the deterministic command id reuses), [ADR-PC-009 §A2/§P3](./ADR-PC-009-per-instance-version-pinning.md) (the single-auditable-matched-set principle this preserves under batching) |
| Resolves | bd `babelstone-qpiw.1` (Bulk-ops governance: new ADR-PC for the pattern + ADR-PC-009 §A3); the governance half of epic `babelstone-qpiw` (bulk cross-cutting operations runner) |

---

## In plain English

Some operator actions have to be applied to a *huge* number of product instances at once — a regulator forcing a pack change across every deposit, the engine evolving its own schemas, a court freezing funds on every account tied to an order, a compliance team freezing a set of accounts. Today the only path is a single synchronous HTTP call capped at a small number of instances, which cannot realistically run — or *resume* — across a low-millions population. This ADR records the decision for **how** the engine runs such a job: it **freezes the target set into a database work-table** the moment the job is registered, then a **background worker drains that table in bounded batches** (the same shape as the engine's outbox drainer), appending the right per-instance event one row at a time, marking each item done, retrying just the failures, and reporting accurate progress — until every item is processed. The job survives a host restart because the work-table *is* the to-do list; it resumes from where it left off. The alternative — putting the work onto a message queue/topic — is rejected because the real bottleneck is the event store (a read-then-append per instance), and a log is poor at the mutable per-item status, selective retry, and audit-by-query this job needs. Crucially, **one job owns the whole frozen set**: batching is an internal execution detail of one audited plan, so the "what exactly did this migration touch?" answer stays a single, decidable set — the same guarantee per-instance version pinning already relies on.

## Context

This ADR fills the **execution-mechanism** question behind the bulk cross-cutting operations runner (epic `babelstone-qpiw`). The engine already has four cross-cutting, engine-declared events that must sometimes be applied across a large population of instances at once: `PackVersionMigrated` / `SchemaVersionMigrated` ([ADR-PC-009 §P3](./ADR-PC-009-per-instance-version-pinning.md)) and the planned `FundsHeld` / `AccountFrozen` (bd `babelstone-5w90` / PR #316). Each is a **store-only cross-cutting fact appended once per affected instance**, keyed and deduped by a deterministic per-instance command id.

The current path is a single **synchronous HTTP request capped at *N* instances** (PR #324, bd `babelstone-fk7m.12`). That cap is a legitimate stopgap but it cannot execute — or **resume** — a run over a **low-millions** population: a synchronous request has no place to record per-item progress, no way to survive a host restart mid-run, and no selective-retry surface. The engine needs a generic, **family-agnostic** runner that registers the target universe once, executes it asynchronously in bounded batches, isolates per-item failures, and exposes progress / selective retry / cancel.

The decision content is already settled by the upstream ADRs and the 2026-06-24 design session: the per-instance step is the engine's most native operation (read the instance's head, append one store-only event — [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md)); idempotency is the receiver-dedupe the engine already has, keyed on the deterministic `(action_id, instance_id)` command id ([ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md)); the runner is generic over the operation, so the four events ride it as **thin adapters** (a per-instance event factory + an optional precondition + optional per-item params). **The only open question is the execution substrate** — *where the to-do list of "which instances still need the event" lives and how it is drained* — and that is a runtime/operational-discipline question, not a contract or a math question.

### Why this is a tool-selection ADR (and why F1/F2 degenerate)

Picking the bulk-execution substrate is a **runtime mechanism** decision — the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual "operational discipline" category, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default, the same class as [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) and [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md). As with those, **F1 (cost) and F2 (regulatory fit) do not discriminate**: every candidate is in-house code on infrastructure already in the estate (PostgreSQL — [ADR-PC-001](./ADR-PC-001-event-store-technology.md); Redpanda CE — [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)), none buys a licence, and none changes the regulatory surface (a bulk runner appends the same store-only events the engine already declares; no PII rides any work feed — [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md), and only opaque `instance_id` references are carried). The load-bearing forces are the **soft criteria** S1–S4 plus the workload profile (low-millions population, the event store as the bottleneck, a mutable per-item status, audit-by-query).

**Candidates evaluated (the execution substrate):**

| # | Candidate | Notes |
|---|---|---|
| A | **A Postgres work-table drained by a background service** — registering the job freezes the matched universe into a `bulk_operation_targets` work-table (one row per instance) inside the same transaction as a `bulk_operation_jobs` registration; a `BulkOperationDrainer` `BackgroundService` claims a bounded batch with `FOR UPDATE SKIP LOCKED`, appends the per-instance event, and flips each row's status — the same shape as the [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) `OutboxDrainer`. | Reuses a pattern already in the estate; mutable per-item status, transactional registration, audit-by-query, resumability all fall out of the table; the per-instance append is the engine's native op. |
| B | **A Redpanda work-topic drained by a consumer** — registering the job produces one message per matched instance onto a work-topic; a consumer group reads, appends the event, and commits offsets. | A log is good at fan-out throughput but bad at *mutable per-item status*, *selective retry of an arbitrary subset*, *audit-by-query of "what did this job touch?"*, and *transactional co-registration with a job header*; the bottleneck is the event store, not the work feed. |
| C | **The synchronous capped HTTP request (status quo, PR #324)** — one request reads up to *N* matched instances and appends each in-line, returning when done. | No place for per-item progress, no resumability across a host restart, no selective retry; the cap (*N*) is the population ceiling, not a batching detail. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · Postgres work-table | PostgreSQL ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)); two new tables + a `BackgroundService`. No new component, no licence. | **Pass** |
| B · Redpanda work-topic | Redpanda CE already in estate ([ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)); a work-topic + a consumer group + a side-table for status. No licence. | **Pass** |
| C · synchronous capped HTTP | Reuses the existing command surface. No licence. | **Pass** |

Uniform pass — F1 does not discriminate (a bulk-execution substrate buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A bulk-execution substrate is not itself a regulated artefact, so F2 cannot *fail* a candidate; it carries a directional signal only. **None of the three substrates change the engine's regulatory posture**: each appends the same store-only cross-cutting events the engine already declares; no PII rides any work feed ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) — A and C keep instance references in the database/request, and B would carry only opaque `instance_id` references on the topic, never PII. The DORA/audit lens actually *prefers* A: a work-table makes "exactly which instances did this job touch, and what was the outcome per item?" a single SQL query against an immutable frozen set.

| Candidate | GDPR | DORA / PSD2 (auditability) | Verdict |
|---|---|---|---|
| A · Postgres work-table | No PII (opaque `instance_id` references). | The frozen target set + per-item outcome is auditable-by-query; the matched set is one immutable plan. | **Pass** |
| B · Redpanda work-topic | References only on the topic; no PII. | Per-item status lives in a *separate* side-table the log does not own; "what did the job touch?" is reconstructed across topic + offsets + side-table, not one decidable set. | **Pass (conditional)** — only if a side-table re-introduces the audit-by-query A gives natively; see rejection. |
| C · synchronous capped HTTP | No PII. | No per-item record at all; the audit answer is "up to *N*, no resumable record". | **Pass** |

All clear the hard filters; the decision is in S1–S4 and the workload profile — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

### Soft criteria

#### A · Postgres work-table drained by a background service — **CHOSEN**

**S1 · Operational complexity for 1–2 people — decisive.** A is the smallest delta to a running engine: the **table-drained-by-a-background-service** shape is already in the estate as the [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) `OutboxDrainer` — `FOR UPDATE SKIP LOCKED` claim of a bounded batch, transactional status flips, at-least-once execution made safe by idempotency. The runner is *the same pattern, second instance*: a `bulk_operation_targets` work-table drained by a `BulkOperationDrainer` `BackgroundService`. No new kind of infrastructure, no new failure model to learn. B adds a work-topic + a consumer group + a status side-table (three things to keep coherent, plus a dead-letter story); C cannot run the population at all. For a 1–2-person team, A is the only substrate with **zero new moving parts** and a familiar operational story.

**S2 · Ecosystem coherence — decisive.** The engine **already chose table-drained-by-a-relay for exactly this class of problem** (the outbox, [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)). The bottleneck here is the **event store**: every item is a head-read + a single append per instance ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)), and a few drainers saturate that store long before any work-feed throughput matters — the v1 scale target is low-millions on a single-or-few drainers, not a fan-out problem. A log (B) optimises the dimension that is *not* the bottleneck and is structurally poor at the four things this job actually needs: a **mutable per-item status** (`Pending → Applied | Skipped | Failed`), **selective retry of an arbitrary failed subset**, **audit-by-query** of the frozen universe, and **transactional registration** of the job header with its target set in one commit. All four are native to a relational work-table and awkward-to-impossible on an append-only log. A also keeps the per-instance step on the engine's native append path: each `(action_id, instance_id)` append rides the same atomic append + outbox transaction every other event does ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)).

**S3 · Exit cost — low, and the per-instance contract is substrate-independent.** The work-table is two portable PostgreSQL tables; the per-instance step (read head, append one store-only event, dedupe on the command id) is **transport-independent** — exactly the property that lets the runner be generic over the operation. If a future fan-out profile ever demanded a log feed, only the *claim-a-batch* edge changes; the per-instance append, the idempotency key, the store-only fold, and the adapter contract are untouched. Starting at A neither under-commits (it runs the low-millions target today, resumably) nor bakes in messaging machinery that may never be needed.

**S4 · Longevity.** Inherits PostgreSQL's ([ADR-PC-001 §S4](./ADR-PC-001-event-store-technology.md)) and reuses the longest-lived pattern the engine has (the outbox). No new dependency to outlive.

**Decisive project-specific reason.** A bulk operation is **a frozen universe drained item-by-item with mutable per-item state** — which is *definitionally* a work-queue table, not a stream. The job needs to record, per item, "done / skipped / failed", retry just the failures, survive a restart by resuming from `Pending`, and answer "what did this plan touch?" as one query over an immutable set. A relational work-table gives all of that natively and reuses the outbox pattern the engine already trusts; a log would force a status side-table back in to recover the very properties the table has for free. The substrate the engine already chose for "drain a table of pending work safely" is the substrate for this too.

#### B · Redpanda work-topic drained by a consumer — **rejected**

B's apparent advantage — log throughput and built-in fan-out — is **the wrong axis**: the bottleneck is the event store (read + append per instance), not the work feed, so log throughput buys nothing the table lacks at the v1 scale target. Worse, a log is structurally poor at the job's defining needs — mutable per-item status, selective retry of an arbitrary subset, audit-by-query of the frozen set, and transactional co-registration of the job header with its targets — each of which would force a relational **status side-table** back into the design (the F2 conditional), at which point B is *A plus a redundant log feed*. Rejected on S1 (more moving parts) + S2 (optimises the non-bottleneck; awkward per-item status/retry/audit). A log feed remains a *documented future option* should a genuine fan-out profile ever be measured, exactly the [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) "reserved scale-up path" posture — but it is not bought speculatively now.

#### C · Synchronous capped HTTP request (status quo) — **rejected; superseded by the runner**

C cannot run the population: a synchronous request has no place to record per-item progress, no resumability across a host restart, and no selective-retry surface; its cap *N* is the **population ceiling**, not a batching detail. It is rejected as the bulk substrate. Its **salvageable parts are folded into A** (per the 2026-06-24 design session): the `matched_count` preview, the `LIMIT`-bounded matched-set read, and the reusable tests are reused by the runner's registration path. The cap itself is **not discarded — it is re-homed**: under the runner it becomes the **drainer's batch size**, an internal execution detail of one audited plan (see [ADR-PC-009 §A3](./ADR-PC-009-per-instance-version-pinning.md), authored in this same change).

**Decisive reason for A:** a bulk operation is a frozen universe drained item-by-item with mutable per-item state — definitionally a work-table, not a stream; A reuses the outbox pattern the engine already trusts, optimises the real (event-store) bottleneck, gives per-item status / selective retry / audit-by-query / resumability natively, and keeps the per-instance step on the engine's native append path.

---

## Decision

### The engine runs a bulk cross-cutting operation as a **register → drain → complete** job over a Postgres work-table, drained in bounded batches by a background service — not over a work-topic, and not as a synchronous capped request.

The runner is a **family-agnostic spine component** ([ADR-PC-021 §P2](./ADR-PC-021-application-layer-family-owned-deciders.md)): it names no family and appends store-only cross-cutting events keyed by the generic `family` / `event_type` columns ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md), [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md)). Each cross-cutting operation (`PackVersionMigrated`, `SchemaVersionMigrated`, `FundsHeld`, `AccountFrozen`) rides it as a **thin adapter** — a per-instance event factory, an optional precondition, and optional per-item params — never a bespoke per-operation runner.

The following principles fix the mechanism.

### P1 — Register: freeze the matched universe into a work-table, transactionally

Registering a job **freezes its target set the moment it is registered**: in one PostgreSQL transaction the runner writes a `bulk_operation_jobs` header (the `action_id`, the operation kind, the matched-set predicate/snapshot, the requested batch size, the operator actor) **and** one `bulk_operation_targets` row per matched instance (status `Pending`). The matched-set read is the `LIMIT`-bounded predicate read salvaged from PR #324; the count is its `matched_count` preview. Once registered, **the target set is immutable** — the job owns a single frozen universe, not a re-evaluated predicate that could drift between batches. Transactional registration means the job header and its targets land together or not at all — the same atomicity discipline the outbox append relies on ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)).

### P2 — Drain: a background service claims bounded batches with `FOR UPDATE SKIP LOCKED`

A `BulkOperationDrainer` `BackgroundService` — the same shape as the [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) `OutboxDrainer` — repeatedly claims a bounded batch of `Pending` targets with `FOR UPDATE SKIP LOCKED`, runs the per-instance step for each (P4), and flips each target's status (P5) inside a transaction. `SKIP LOCKED` lets **a few drainers run concurrently without contending** on the same rows; the **batch size is the runner's bounded claim** (P-via-§A3 the salvaged cap). At-least-once execution is made safe by idempotency (P3). Because the work-table *is* the to-do list, a host restart mid-run **resumes from `Pending`** with no lost or double-applied work — resumability is a property of the substrate, not a bolted-on checkpoint.

### P3 — Idempotency: reuse the engine's receiver-dedupe on the deterministic `(action_id, instance_id)` command id

The per-instance step appends its event under a **deterministic command id derived from `(action_id, instance_id)`** — the same id whether the step runs the first time, on a retry, or after a restart. The engine's existing receiver-dedupe ([ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md), `ENGINE_COMMAND_IDEMPOTENT`, catalogue row 19, **Live**) makes a replayed command id a no-op that returns the original `commit_sequence` with **no second append**. So a target re-claimed after a crash, or retried after a transient failure, **cannot append the cross-cutting event twice** — the idempotency the runner needs is the idempotency the engine already has, not a new mechanism.

### P4 — The per-instance step is the engine's native op, appending a store-only event via an adapter

For each target the runner: (1) optionally evaluates the operation's **precondition** (an adapter-supplied verdict — the operation may decline this instance, recorded as `Skipped`); (2) calls the adapter's **per-instance event factory** to build the correct store-only cross-cutting event (`PackVersionMigrated` / `SchemaVersionMigrated` / `FundsHeld` / `AccountFrozen`), with optional per-item params; (3) appends it on the engine's native path (head-read + atomic append + outbox transaction, [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)). The event is **store-only** ([ADR-PC-028](./ADR-PC-028-event-store-payload-format.md)) — folded into instance state and replayable, never on the durable bus by default (P6).

### P5 — Per-item failure isolation: one bad item never fails the job

Each target's outcome is recorded independently: `Applied` (event appended), `Skipped` (precondition declined), or `Failed` (an error appending — recorded with the error, the target row left for selective retry). **A failed item does not abort the batch or the job**: the drainer records the failure on that target and moves on, so a job over a low-millions population is never sunk by a handful of bad instances. The job exposes accurate `{total, applied, skipped, failed, pending}` counts by query over the work-table, and a **selective-retry** surface re-arms an arbitrary `Failed` subset back to `Pending` (the retry re-runs under the same deterministic command id, so a partially-applied-then-failed item is deduped, not double-applied — P3). A **cancel** surface stops the drainer claiming further `Pending` rows; already-applied items stay applied (the frozen-set audit answer is still decidable).

### P6 — Job milestones are store-only by default; publish only when a named consumer exists

The job's own lifecycle milestones (registered, batch-drained, completed) are **store-only by construction** ([ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md)): progress is exposed **by query** over the work-table, not by emitting milestone events onto the bus. A milestone is promoted to a catalogued integration event **only when a named external consumer exists** — the deliberate-promotion discipline [ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) makes the default. (The per-instance cross-cutting events `PackVersionMigrated` etc. are governed by their own catalog status under the same gate; this principle is about the *job's* milestones, not the per-instance facts.)

### P7 — One job owns the whole frozen set; batching is an internal execution detail (the §A3 link)

Because the target set is frozen at registration (P1) and drained as bounded batches *within one job* (P2), **one job owns the entire matched universe**. Batching never splits a migration into separate plans: the batch size is *how* one audited plan is executed, not *what* set it touched. This is what preserves the [ADR-PC-009 §A2/§P3](./ADR-PC-009-per-instance-version-pinning.md) **single-auditable-matched-set** principle when a pack/schema migration runs through the runner — recorded as [ADR-PC-009 §A3](./ADR-PC-009-per-instance-version-pinning.md) in this same change. The cap that PR #324 made the *population ceiling* becomes the *batch size* of one job over one frozen set.

**Rejected: B** (a Redpanda work-topic — optimises the non-bottleneck work feed; structurally poor at mutable per-item status, selective retry, audit-by-query, and transactional registration; reserved as a documented future option only if a measured fan-out profile demands it). **C** (the synchronous capped HTTP request — no per-item progress, no resumability, no selective retry; its cap is the population ceiling, not a batching detail; superseded by the runner, its salvageable matched-set read/preview/tests folded in, its cap re-homed as the batch size per [ADR-PC-009 §A3](./ADR-PC-009-per-instance-version-pinning.md)).

---

## Consequences

**What this choice makes easier:**

1. **Zero new infrastructure.** The runner is the outbox pattern's second instance — a work-table drained by a `BackgroundService` on the existing PostgreSQL store; the idempotency ledger, the atomic append, and the store-only fold are reused as-is.
2. **Resumability and per-item status fall out of the substrate.** A host restart resumes from `Pending`; per-item outcome, selective retry, and `{total, applied, skipped, failed, pending}` progress are native queries, not bolted-on bookkeeping.
3. **One generic runner, four operations as adapters.** `PackVersionMigrated`, `SchemaVersionMigrated`, `FundsHeld`, `AccountFrozen` each ride the same runner as a thin adapter (event factory + optional precondition + optional params) — no bespoke per-operation execution path.
4. **The single-auditable-matched-set guarantee is preserved under scale.** One job owns the whole frozen set; batching is an internal detail, so "what did this migration touch?" stays one decidable query — the [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) audit property survives the move off the synchronous cap.
5. **Audit-by-query.** The frozen target set + per-item outcome is a single SQL answer over an immutable plan — the DORA/PSD2 audit story the synchronous cap could not give.

**What this choice makes harder or locks in:**

1. **The event store is the throughput governor.** A low-millions run is bounded by head-read + append per instance ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)); a few drainers saturate the store, so the runner's pace is the store's pace. This is intended (the store *is* the bottleneck), but it makes [ADR-PC-001](./ADR-PC-001-event-store-technology.md) throughput a bulk-run SLO, not background.
2. **No broker-native fan-out at v1.** A genuine fan-out profile beyond a few drainers would need the reserved log-feed option; until then, scale comes from drainer count + batch size against one store, not from a partitioned topic.
3. **The matched set is frozen at registration.** An instance that becomes eligible *after* a job registers is not picked up by that job — by design (one immutable plan), but it means a "catch the stragglers" run is a *new* job, never a silent re-scan of a live predicate.

## Residual risks

- **This ADR does not commit the work-table schema or the command/query wire surface.** The `bulk_operation_jobs` / `bulk_operation_targets` columns and state machines are the store-migration child (bd `babelstone-qpiw.2`); the register / GET-progress / retry-failed / cancel surface is the command/query child (bd `babelstone-qpiw.4`); the `BulkOperationService` / `BulkOperationDrainer` implementation is bd `babelstone-qpiw.3`; the four adapters are bd `babelstone-qpiw.5`. This ADR commits only the **execution pattern** (register→drain→complete over a work-table, idempotent per-instance append, per-item failure isolation, store-only milestones, single-frozen-set ownership). Those children compose with this ADR; their concrete shapes are theirs to fix.
- **A genuine fan-out profile could reopen the substrate choice.** If load evidence ever shows a few drainers against one store cannot meet a future bulk SLO, the reserved log-feed edge is a transport swap of only the *claim-a-batch* step (the per-instance append, the idempotency key, the store-only fold, and the adapter contract are substrate-independent — S3). That would be a documented amendment under the explicit-drift gate ([ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)), not a silent divergence.
- **Idempotency depends on the deterministic command id staying deterministic.** The whole no-double-append guarantee (P3) rests on `(action_id, instance_id)` producing the *same* command id on every retry/restart. A non-deterministic id (e.g. a random suffix) would silently break dedupe; the implementing child must keep the derivation pure and test it (the `BULK_OP_REGISTER_DRAIN_COMPLETE` gate, below).
- **This ADR does not commit a concrete throughput number.** The low-millions completion target, the restart-resume behaviour, and the per-job SLO are proven when the runner is built (bd `babelstone-qpiw.3`) and exercised against a real PostgreSQL; the numbers are not fixed here.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)).

This pattern decision **reuses** one existing catalogued commitment and **seeds** one new row that the catalogue maintainer adds centrally:

- `ENGINE_COMMAND_IDEMPOTENT` — the receiver-dedupe on the caller's command id this runner relies on for the per-instance step, already **Live** (catalogue row 19, governed by [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md)); the bulk runner inherits it unchanged via the deterministic `(action_id, instance_id)` command id (§P3). This ADR references it but does not own it — no change to that row.

- `BULK_OP_REGISTER_DRAIN_COMPLETE` (§P1–§P5) — a bulk operation registers a **frozen** target universe into the work-table, a background drainer applies it in bounded `SKIP LOCKED` batches with the per-instance event appended **idempotently** on the deterministic `(action_id, instance_id)` command id, **per-item failures are isolated** (one `Failed` item never aborts the job and is selectively retryable as a no-op-safe re-run), and a host restart mid-run **resumes from `Pending`** producing the correct `{total, applied, skipped, failed, pending}` counts with **no double-append**. Status `Planned`, gate `integration (Testcontainers)`, governing source this ADR. The test is written with the runner implementation (bd `babelstone-qpiw.3`). `Planned` is a deliberate, listed hole — visibility is the point. (The row is added to the catalogue centrally by the maintainer, not in this ADR-authoring change; once present, this section's reference is the catalogued back-reference the [ADR-PC-020 §P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) spec-coverage gate checks.)

---

## Cross-references

- [ADR-PC-009 §A2/§P3 + §A3](./ADR-PC-009-per-instance-version-pinning.md) — the single-auditable-matched-set principle this preserves; §A3 (authored in this same change) records that the cap becomes the runner's batch size and a migration becomes a registered job over a frozen set.
- [ADR-IC-004 §P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) — the table-drained-by-a-background-service pattern (the `OutboxDrainer`) this runner is the second instance of; `FOR UPDATE SKIP LOCKED`, transactional status flips, at-least-once-made-safe-by-idempotency.
- [ADR-PC-001 §P1–§P2](./ADR-PC-001-event-store-technology.md) — the family-agnostic event envelope and the atomic append + outbox the per-instance step rides; the event store is the bulk-run throughput governor.
- [ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) — store-only-by-construction; the job's milestones are exposed by query, promoted to the bus only when a named consumer exists.
- [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md) — the store-only payload format the per-instance cross-cutting event is folded as.
- [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md) — the receiver-dedupe (`ENGINE_COMMAND_IDEMPOTENT`) the deterministic command id reuses so a retried/restarted step never double-appends.
- [ADR-PC-021 §P2](./ADR-PC-021-application-layer-family-owned-deciders.md) — the family-agnostic spine the runner lives on; it names no family and the four operations ride it as adapters.
- [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) / [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the sibling [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual-category posture ADRs whose tool-selection shape (F1/F2 degenerate, decision on S1–S4, a reserved scale-up path) this follows.

---

*Proposed 2026-06-26 by jhosm. Resolves bd `babelstone-qpiw.1`; the governance half of epic `babelstone-qpiw`.*
