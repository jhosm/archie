# ADR-PC-034: Real-Time Authorization Technique — Synchronous Idempotent Call, with Async Kept as a Reversible Scale-Up Path

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-24 |
| Deciders | jhosm |
| Shape | Tool-selection (ADR-PC-000 §D3 residual category — a runtime/operational-discipline posture for *how* the engine answers authorization in real time, declared tool-selection per the §D4 default; F1/F2 do not discriminate, the same class as ADR-PC-030, ADR-PC-031, and ADR-PC-019) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — a runtime mechanism, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) (§P3 + Open Action 4 — the *commitment* this picks the *mechanism* for), [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) (the synchronous idempotent command surface this extends to the authorization regime), [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (the event store now on the payment hot path), [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) (the load harness that must prove the technique under the v4 burst profile), [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) (the append-first `Movement` the verdict appends), [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) (the account/hold fold the authorization decider reads and appends) |
| Resolves | bd `babelstone-mz33` (Real-time authorization technique ADR — sync vs async); discharges [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) Open Action 4's "real-time authorization technique ADR (sync vs async)" |

---

## In plain English

[ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) already decided **what** the engine does on a card-or-debit attempt: it is the authoritative balance and it must answer "is this authorized?" **in real time**. It deliberately left **how** the engine is *called* to answer for a later decision — this one. The choice is between a **synchronous** call (the caller waits on the wire for the verdict, the way the engine's existing command surface already works) and an **asynchronous/reactive** design (the request goes onto a queue and the verdict comes back as a separate message). This ADR picks **synchronous** for v1, because it reuses the command path the engine already has and the verdict is exactly the kind of question that *needs* a blocking yes/no answer. It does **not** burn the async bridge: if the load harness ever shows the synchronous path can't hold the burst targets, the same pure decider can be moved behind a fast async round-trip — that migration is named, kept reversible, and tracked, not silently foreclosed.

## Context

This ADR fills the **last open mechanism question** the product-scope decision left behind. [ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) fixed the *commitment* — "*The engine is a live dependency of the authorization path; its latency and availability become payment-path concerns. The **technique** (synchronous vs asynchronous/reactive) is a deferred runtime ADR; the **commitment** (real-time answer, engine authoritative) is fixed here.*" — and boxed it explicitly:

> **Real-time, technique deferred.** This ADR fixes the *commitment* … not the *mechanism*. Whether that is a synchronous call ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) shape) or an asynchronous/reactive design with a fast round-trip is a runtime question deferred to a future ADR.

[ADR-PC-030 Open Action 4](./ADR-PC-030-product-scope-and-boundary.md) names this ADR as the discharge of that deferral ("*the **real-time authorization technique ADR** (sync vs async)*"). This entry owns the mechanism.

### What "authorization" is, mechanically, after the upstream ADRs

The decision content is already settled; only the *call regime* is open. On a debit/authorization attempt the engine runs a pure decider ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)) that:

1. folds the **available balance** = `accounting balance − Σ active holds` over the [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) `Movement`s and the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) hold ledger (both pure, rebuildable folds — no stored mutable number);
2. applies pack rules (limits, *descoberto autorizado*) at stage 4 ([ADR-PC-030 §P1/§P3](./ADR-PC-030-product-scope-and-boundary.md));
3. appends `HoldPlaced` (the `authorized` verdict + the earmark) **or** a refusal — exactly the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) gated `HoldPlaced → HoldCaptured | HoldExpired` lifecycle.

This is the engine's most native operation (a read-state-and-append decider, [ADR-PC-030 §44](./ADR-PC-030-product-scope-and-boundary.md)), and stages 3–5 are the only ones the engine owns; instrument validation, SCA, fraud, and the rails sit *outside* and call *in* ([ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md)). The only undecided thing is **how the external authorization pipeline gets the verdict back** — and that is a transport/runtime question, not a contract or a math question.

### Why this is a tool-selection ADR (and why F1/F2 degenerate)

Picking sync vs async is a **runtime mechanism** decision — the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual "operational discipline" category, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default, the same class as [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) and [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md). As with those, **F1 (cost) and F2 (regulatory fit) do not discriminate**: neither regime buys a licence (both are in-house code on the existing [ADR-PC-001](./ADR-PC-001-event-store-technology.md) store and [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) bus), and neither changes the regulatory surface (the engine stays a ledger/decision component, not a PSP — [ADR-PC-030 §F2](./ADR-PC-030-product-scope-and-boundary.md); no PII on the bus either way, [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)). The load-bearing forces are the **soft criteria** S1–S4 plus the non-functional latency/availability profile the real-time commitment imposes.

**Candidates evaluated (call regimes):**

| # | Candidate | Notes |
|---|---|---|
| A | **Synchronous idempotent call** — the authorization pipeline (the external authorizer / edge) calls the engine's [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface and **blocks** on the wire for the verdict (`authorized` + `hold_id`, or `declined`), under a heavier load profile than the constitution path. | Reuses the tested, idempotent command surface; the verdict *is* a synchronous request/response question; one ingress; the de-settled stance ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)/[ADR-PC-032](./ADR-PC-032-money-movement-primitive.md)) holds unchanged. |
| B | **Asynchronous / reactive with a fast round-trip** — the authorization request is published to a topic; the engine consumes it and publishes the verdict to a reply topic the authorizer awaits (a request/reply over the [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) broker, or an in-process reactive pipeline). | Temporal decoupling + backpressure for the v4 burst targets; but contradicts [Primitive 1](../../integration_concepts/01-the-six-primitives.md) (commands point-to-point, the bus events-only), adds a reply-correlation + timeout layer, and a *blocking yes/no on the hot path* is the wrong fit for a fire-and-forget bus. |
| C | **Engine-internal balance cache / read-replica fast path** — answer the funds check from a cached available-balance read, append the hold asynchronously afterwards. | Fastest read, but the hold append is what makes concurrent authorization safe **without locking** ([ADR-PC-030 §48](./ADR-PC-030-product-scope-and-boundary.md)); deferring it reopens the double-spend window and breaks the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) "`HoldPlaced` lowers available balance on the append-only log before the next is evaluated" guarantee. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · synchronous call | Reuses the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) surface; no new component, no licence. | **Pass** |
| B · async / reactive | A reply topic + correlation/timeout layer (new build), but no licence (Redpanda CE already in estate). | **Pass** |
| C · cache fast path | A cache tier (more infra), no licence. | **Pass** |

Uniform pass — F1 does not discriminate (a runtime regime buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A call regime is not itself a regulated runtime artefact, so F2 cannot *fail* a candidate; it carries a directional signal only. **None of the three regimes change the engine's regulatory posture**: it remains the ledger/decision component of [ADR-PC-030 §F2](./ADR-PC-030-product-scope-and-boundary.md) (no rails, no SCA, no card/scheme credentials, not a PSD2 PSP); no PII rides the bus under any regime ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) — A and C keep the authorization payload point-to-point, and B carries only references, never PII, on the broker. DORA's availability lens is the same non-functional concern S1/§Residual-risks already weigh.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A · synchronous call | Point-to-point, no PII on the bus. | Engine is a real-time dependency — an availability concern, not a regulated activity. | **Pass** |
| B · async / reactive | References only on the broker; no PII. | Same availability concern, re-shaped as broker liveness. | **Pass** |
| C · cache fast path | As A. | Adds a cache-coherence failure mode (stale balance → wrong verdict). | **Pass (conditional)** — only if the hold append stays synchronous; see rejection. |

All clear the hard filters; the decision is in S1–S4 and the latency/availability profile — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

### Soft criteria

#### A · Synchronous idempotent call — **CHOSEN**

**S1 · Operational complexity for 1–2 people — decisive.** A is the smallest delta to a running v1: the engine *already* exposes a synchronous, idempotent command surface ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)) with the receiver-dedupe ledger Live (`ENGINE_COMMAND_IDEMPOTENT`, catalogue row 19). Authorization is that same surface under a heavier load profile — an extension, not a new kind of component. [ADR-PC-030 §S1](./ADR-PC-030-product-scope-and-boundary.md) reached the identical conclusion when it sized posture B: "*the synchronous answer regime is the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface under a heavier load profile — an extension, not a new kind of component.*" B adds a reply-topic + correlation + timeout layer (two messaging legs to keep coherent, plus a dead-letter story for unanswered authorizations); C adds a cache tier with its own coherence and invalidation discipline. For a 1–2-person team, A is the only regime with **zero new moving parts**.

**S2 · Ecosystem coherence — decisive.** The estate already says *commands are point-to-point with a known destination; the durable bus carries events (facts), not instructions* ([Primitive 1](../../integration_concepts/01-the-six-primitives.md), [ADR-PC-029 §30/slot 3](./ADR-PC-029-engine-command-ingress.md)). An authorization request is a **command** ("authorize this debit") expecting a blocking verdict — exactly the point-to-point command shape, not a fire-and-forget event. B would put a request/reply RPC *onto* the broker — the "bus-as-RPC" smell [doc 01](../../integration_concepts/01-the-six-primitives.md) warns of and [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) explicitly declined for the constitution command path. A also leaves the [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) append-first / de-settled stance and the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) gated-hold-fold untouched: the verdict appends `HoldPlaced` synchronously inside the event's outbox transaction, so the available-balance fold drops before the next authorization is evaluated — the no-locking guarantee survives unchanged.

**S3 · Exit cost — the heart of this ADR.** A is **deliberately reversible into B**. The decision content (the pure decider, the fold, the hold append) is transport-independent — exactly the property [ADR-PC-029 slot 6](./ADR-PC-029-engine-command-ingress.md) engineered ("*slots 1/2/4/5 are transport-independent and survive the migration unchanged … swap the dispatcher's sink for a command topic and add an engine `InboxConsumer` that calls the **same application service***"). The async migration here is the *same low-regret move* the constitution path already keeps open (bd `babelstone-ne1m`): if the load harness proves the synchronous authorization leg is a measured bottleneck under the v4 burst profile, move the authorization request behind a fast async round-trip that calls the **same decider** — no change to the math, the fold, or the hold lifecycle. Starting at A neither under-commits (it satisfies the real-time commitment today) nor bakes in messaging machinery that may never be needed.

**S4 · Longevity.** Neutral-to-positive: the synchronous request/response idiom outlives any single family and is the one the existing architecture already carries; the reversibility seam keeps the door open without paying for it now.

**Decisive project-specific reason.** A real-time authorization verdict is **definitionally a synchronous question**: an external authorizer cannot let a card transaction proceed until it knows `authorized`/`declined`, so *somewhere* a caller blocks for the answer regardless of regime. A makes that blocking explicit at the transport the estate already uses for blocking commands, with idempotency already Live, and keeps the only genuine advantage of async (decoupling/backpressure at extreme scale) as a measured, reversible upgrade rather than a speculative v1 cost. The async advantage is **unmeasured today** — the same standard [ADR-PC-029 §30](./ADR-PC-029-engine-command-ingress.md) applied — and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) is exactly the instrument that will measure it.

#### B · Asynchronous / reactive with a fast round-trip — **rejected (reserved as the load-driven scale-up path)**

B's real advantage — temporal decoupling and broker backpressure under the v4 burst targets — is genuine but **unmeasured**, and buying it now means building a request/reply-over-broker correlation layer, a timeout/dead-letter policy for authorizations that never get answered, and a *second* engine ingress to keep coherent with the synchronous one — against [Primitive 1](../../integration_concepts/01-the-six-primitives.md) and the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) precedent. A blocking yes/no on the payment hot path is the wrong fit for a fire-and-forget event bus. Rejected on S1 + S2 for v1; **explicitly preserved** as the reversible scale-up path (§Residual risks), gated on [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) load data, sharing bd `babelstone-ne1m`'s migration shape.

#### C · Engine-internal balance cache / read-replica fast path — **rejected**

C answers the funds check fastest but breaks the guarantee that makes concurrent authorization correct: the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) hold append is what lowers the available balance on the append-only log *before* the next authorization is evaluated ([ADR-PC-030 §48](./ADR-PC-030-product-scope-and-boundary.md), "*the first debit appends a `HoldPlaced` that lowers available balance before the second is evaluated*"). Deferring the hold to answer from a cache reopens the double-spend window and reintroduces the locking C was meant to avoid; answering from a possibly-stale cached balance can authorize against funds another in-flight hold already earmarked. Rejected on correctness (S2). The legitimate slice — a read-optimised projection for *non-authorizing* balance reads — already exists as the [ADR-PC-027](./ADR-PC-027-deposit-read-surface-canonical-resource.md) read surface and does **not** gate the authorization path.

**Decisive reason for A:** authorization is a blocking command whose verdict the estate is already built to answer synchronously and idempotently ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)); A satisfies the real-time commitment with zero new components, preserves Primitive 1 and the hold-fold no-locking guarantee, and keeps the only real async advantage (scale decoupling) as a measured, reversible upgrade rather than a speculative v1 cost.

---

## Decision

### The engine answers real-time authorization **synchronously**, on the ADR-PC-029 idempotent command surface under a heavier load profile — with the async/reactive regime kept as a measured, reversible scale-up path.

**Technique (A).** The external authorization pipeline ([ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) stages 1–2, 6–7) calls the engine's **synchronous, idempotent command surface** ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)) to answer the funds-and-rules stage (3–5). The caller **blocks** for the verdict: the engine runs the pure authorization decider, appends `HoldPlaced` (verdict `authorized`, with the `hold_id` earmark) **or** a refusal — inside the event's outbox transaction — and returns the verdict synchronously. This extends the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command path to the authorization regime; it does **not** add a new ingress.

**The five [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command-surface properties carry over unchanged** to the authorization command:

1. **Idempotency is reused, not re-invented.** The caller supplies a command id (`Idempotency-Key`); a replayed authorization returns the **original** verdict (the same `hold_id` / `commit_sequence`) with no second `HoldPlaced` — the `ENGINE_COMMAND_IDEMPOTENT` guarantee ([ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md), catalogue row 19, **Live**), now load-bearing on the payment hot path. A retried authorization can never place a second hold.
2. **De-settled.** The authorization append never moves money on the wire; capture/settlement is the separate, gated leg ([ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) `Originated` cash leg → the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) settlement command; the `Observed` capture feed arrives as a `HoldCaptured`). The HTTP `2xx` confirms the *verdict was decided and appended*, not that cash moved.
3. **Gated for the verdict, never for downstream.** A refusal (insufficient available balance, a pack-rule/limit breach) is a terminal `declined` the caller must honour — the authorization decision is gated ([ADR-PC-033 slot 5](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)). GL/notification stay post-commit and never gate ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) / [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)).
4. **Per-aggregate ordering + optimistic concurrency.** Concurrent authorizations against one account are serialised by `expectedVersion` on `(stream_id, sequence_number)`; the `HoldPlaced` append lowers the available-balance fold before the next is evaluated — concurrent authorization is safe **without locking** ([ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md), [ADR-PC-030 §48](./ADR-PC-030-product-scope-and-boundary.md)).
5. **One ingress.** Authorization shares the engine's single command ingress with the constitution/lifecycle commands; the durable bus stays **events-only** ([Primitive 1](../../integration_concepts/01-the-six-primitives.md)) — no authorization-request topic at v1.

**The real-time commitment is now met by a concrete mechanism.** [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)'s "real-time answer, engine authoritative" is satisfied by the synchronous verdict on the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) surface; the engine's latency and availability are the payment-path SLO, making [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (store throughput on the hot path) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) (the load harness that must prove it) load-bearing exactly as [ADR-PC-030 §Consequences](./ADR-PC-030-product-scope-and-boundary.md) anticipated.

**Reversibility is the explicit point of choosing A now.** Because the authorization decider, the available-balance fold, and the hold lifecycle are all transport-independent ([ADR-PC-029 slot 6](./ADR-PC-029-engine-command-ingress.md), [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)), the engine may later answer authorization behind a **fast async round-trip** — a broker request/reply or an in-process reactive pipeline that calls the **same application service** — without changing the math, the fold, or the verdict semantics. That migration is a low-regret swap of the *transport*, identical in shape to the constitution path's open Kafka-command-inbox move (bd `babelstone-ne1m`), and is taken only if [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) load data proves the synchronous leg is a measured bottleneck under the v4 burst profile.

**Rejected: B** (async/reactive — unmeasured advantage, against Primitive 1, a second ingress; reserved as the load-driven scale-up path). **C** (cache/read-replica fast path — defers the hold append, reopening the double-spend window and breaking the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) no-locking guarantee).

---

## Consequences

**What this choice makes easier:**

1. **Zero new components for v1.** Authorization is the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface under load; the idempotency ledger, the de-settled stance, and the single ingress are reused as-is.
2. **The hold-fold no-locking guarantee is preserved exactly.** The synchronous `HoldPlaced` append lowers available balance before the next authorization is evaluated — concurrent authorization stays correct without distributed locks.
3. **A clean, measured upgrade path.** Async is not foreclosed; it is the named, transport-only migration taken on load evidence — the lowest-regret way to keep the option.
4. **One coherent command idiom.** The estate's "commands point-to-point, bus events-only" rule ([Primitive 1](../../integration_concepts/01-the-six-primitives.md)) holds for authorization too — no bus-as-RPC.

**What this choice makes harder or locks in:**

1. **The engine is a synchronous dependency on the payment hot path.** Its latency and availability are now hard payment-path SLOs; a slow or unavailable engine *blocks* authorizations. [ADR-PC-001](./ADR-PC-001-event-store-technology.md) throughput and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) load proof are load-bearing, not background.
2. **No broker backpressure at v1.** A burst beyond the synchronous capacity surfaces as caller-side timeouts/retries (idempotency makes the retries safe), not as queued smoothing — until the async migration, if ever taken.
3. **The decider must stay fast and pure.** The hot-path latency budget makes the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) purity + the fold cost of available-balance/hold computation ([ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)) a performance concern, not only a correctness one (snapshots, [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md), become relevant on long hold ledgers).

## Residual risks

- **The v1 choice is provisional pending load data.** If the load harness ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)) shows the synchronous authorization leg is a measured bottleneck under the v4 burst targets — or a "no synchronous in-estate calls on the payment path" rule is later adopted — migrate the authorization request to a fast async round-trip that calls the same decider. This shares the constitution path's reversible-transport seam and is tracked alongside bd `babelstone-ne1m` (deferred, post-v1). Divergence would be acknowledged per the explicit-drift gate ([ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)); the reversibility means it is low-regret.
- **This ADR does not commit the authorization command's wire payload.** The `authorize`-style command request/response shape (the field set, the `hold_id` return, the `declined` reason taxonomy) is owned by the **conta à ordem** transactional-account family ADR (bd `babelstone-xvcx`) and the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) hold contract; this ADR commits only the **call regime** (synchronous, on the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) surface). The two compose: the family ADR fills the payload, this ADR fixes the transport.
- **This ADR does not commit availability targets.** The concrete authorization-path latency/availability SLOs and the DR posture under hot-path load are an operational concern adjacent to [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md); they are named as load-bearing here but their numbers are set when the conta à ordem family is built and the load harness runs.
- **The hold append cannot be made eventually-consistent without reopening this decision.** Candidate C is rejected precisely because the verdict and its earmark must be one synchronous atomic append; any future "answer fast, hold later" optimisation would be a supersession, not an amendment.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)).

This technique decision **reuses** one existing catalogued commitment and **seeds** one new row that the catalogue maintainer adds centrally:

- `ENGINE_COMMAND_IDEMPOTENT` — the receiver-dedupe guarantee this ADR relies on, already **Live** (catalogue row 19, governed by [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md)); the authorization path inherits it unchanged. This ADR references it but does not own it — no change to that row.

The **new** row this ADR seeds is `AUTHORIZATION_SYNC_IDEMPOTENT` (status `Planned`, gate `integration (Testcontainers)`, governing source this ADR): *a real-time authorization is answered **synchronously** on the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface, and a **replayed** authorization command id returns the **original** verdict (same `hold_id`) with **no second `HoldPlaced` append**.* It carries the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) idempotency guarantee onto the payment hot path; the test is written with the conta à ordem family implementation (bd `babelstone-xvcx`). `Planned` is a deliberate, listed hole — visibility is the point. (The row is added to the catalogue centrally by the maintainer, not in this ADR-authoring change; once present, this section's reference becomes the catalogued back-reference the [ADR-PC-020 §P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) spec-coverage gate checks.)

The hold-fold and balance-fold determinism (the `ACCOUNT_BALANCE_IS_A_FOLD` / `HOLD_LIFECYCLE_PURE` commitments, both `Planned`) are governed by [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md), not duplicated here; this ADR's only new gate is the synchronous-idempotent call regime.

---

## Cross-references

- [ADR-PC-030 §P3 + Open Action 4](./ADR-PC-030-product-scope-and-boundary.md) — fixes the real-time *commitment* and explicitly defers this *mechanism*; this ADR discharges that deferral.
- [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) — the synchronous idempotent command surface this extends to the authorization regime; its slot-6 transport-independence is what makes the async migration low-regret.
- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — the event store, now on the payment hot path: its throughput becomes a payment-path SLO.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the load harness that must prove the synchronous technique under the v4 burst profile and would trigger the async migration.
- [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) — the append-first, de-settled `Movement` the verdict appends; the authorization never settles synchronously.
- [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) — the account/hold fold the authorization decider reads and appends; the synchronous `HoldPlaced` is what keeps concurrent authorization lock-free.
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) / [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) — the sibling [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual-category posture ADRs whose tool-selection shape (F1/F2 degenerate, decision on S1–S4) this follows.
- [Primitive 1](../../integration_concepts/01-the-six-primitives.md) — commands point-to-point, the durable bus events-only; the principle that keeps authorization a synchronous command, not a bus RPC.

---

*Proposed 2026-06-24 by jhosm. Resolves bd `babelstone-mz33`; discharges [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) Open Action 4.*
