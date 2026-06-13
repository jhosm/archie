# Load-Bearing Commitment Catalogue — Seed

The **seed** of the architecture fitness-function catalogue defined by
[ADR-PC-020 §D2 / §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
(Open Action #4). It binds each of the **~8 load-bearing invariants** the engine
must not silently drift from to the gate that proves it and a stable Test ID the
[§P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) coverage checker
resolves to a running test.

**This catalogue is the single source of truth.** Each governing ADR carries a
`## Verifiable commitments` section that *references* its rows here (by Test ID)
rather than restating the claim, gate, and status — so the mutable fields live in
exactly one place and cannot drift between an ADR and the catalogue. The reference
is one-way: ADR → catalogue.

This is deliberately the *load-bearing few*, seeded **before broad engine work
begins** so the spec-first loop ([ADR-PC-020 §P10](./ADR-PC-020-llm-toolchain-and-conformance-governance.md))
has concrete targets. The full per-ADR backfill across the rest of the ADR-PC and
in-house ADR-IC corpus is incremental — [ADR-PC-020 Open Action #7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md),
tracked separately — and is **not** the job of this seed.

## How to read this

- **Most rows are `Planned`; a row flips to `Live` as its gate lands.** `Planned`
  means *the gate is named and the Test ID reserved; the test is written before (or
  with) the decision's implementation*
  ([ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)).
  A row becomes `Live` only when its test exists, passes, **and runs in CI** — the
  [§P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) criterion is a
  commitment that "resolves to ≥1 test that exists (and runs in CI)". A test that is
  green on a dev machine but excluded from the CI lane (e.g. a Testcontainers
  integration tier not yet wired) stays `Planned` until that lane runs it — otherwise
  a regression would not fail the build. A row with no intended gate would be `Gap` (a
  deliberate, visible hole) — there are none here.
- **Test ID** is a stable `UPPER_SNAKE_CASE` identifier (the convention the
  [ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
  illustration uses) and the join key between this catalogue and the referencing
  ADR. It never changes once a gate is written against it; renaming it is a
  catalogue migration, not a free edit.
- **Gate (pyramid level)** maps onto the
  [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) pyramid
  (`unit` / `integration` / `contract` / `saga`), or `analyser` / `benchmark` /
  `acceptance` for build-time, timing, and whole-system gates — fitness functions
  live where the work already is ([ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)),
  not in a parallel suite.
- **Governing source** links the ADR section (or, for the two non-ADR rows, the
  doc) the commitment derives from. The two non-ADR commitments (replay budgets,
  zero-engine-code-per-variant) have **no per-ADR home** — their governing sources
  are a feature-design note and a concept doc, which the
  [ADR-PC-000 amendment §A2](./ADR-PC-000-namespace-and-contract-shape-framework.md)
  exempts from the template slot — so this catalogue is their only home.

## The seed

| # | Commitment | Governing source | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|---|
| 1 | `append` writes event rows **and** outbox rows in one local PostgreSQL transaction (no event without its outbox row, and vice versa). | [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md) | integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX` | Live |
| 2 | Money rounds **HALF_EVEN exactly once** at the `Decimal → Cents` boundary, proven against a sealed golden-fixture corpus. | [ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md) | unit + analyser | `MONEY_BOUNDARY_FIXTURES` | Planned |
| 3 | A handler that reads the clock, does I/O, or uses randomness **fails the build**; event evolution is additive-only. | [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) | analyser / CI determinism gate | `DETERMINISM_GATE` | Live |
| 4 | Replay reads the pack/schema pin **off each event**, not the clock; a migration at sequence `M` splits the stream's pin, and a rebuild re-derives the identical per-event pin whenever it runs. | [ADR-PC-009 §P1–§P2](./ADR-PC-009-per-instance-version-pinning.md) | integration (Testcontainers) | `REPLAY_PIN_PER_EVENT` | Planned |
| 5a | **Post-flag, never gated** — a GL-side reject never blocks or unwinds the producing business flow. | [ADR-PC-012 slot 5](./ADR-PC-012-gl-posting-signal-contract.md) | contract / saga | `GL_POST_FLAG_NEVER_GATES` | Live |
| 5b | **Post-flag, never gated** for `EVENT_DRIVEN` notifications; the `PRE_CONTRACTUAL` (FIN) case is the synchronous saga carve-out. *(Per [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) — the clean reissue of [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) — `SCHEDULED` is no longer engine-emitted; its purity claim is `NO_CLOCK_DRIVEN_ENGINE_SIGNAL`, row 17.)* | [ADR-PC-025 slot 5](./ADR-PC-025-customer-notification-emit-contract.md) | contract / saga | `NOTIFY_POST_FLAG_NEVER_GATES` | Live |
| 5c | **Post-flag, never gated — unconditionally**; IFRS 9 is downstream and has no gating claim. *(No signal emitted in v1; gate built before the v2 credit scope.)* | [ADR-PC-015 slot 5](./ADR-PC-015-ifrs9-signal-contract.md) | contract / saga | `IFRS9_POST_FLAG_NEVER_GATES` | Planned |
| 7 | A re-ingested legacy batch file produces **no duplicate `LegacyInstanceObserved` events** (engine-side dedupe on `(legacy_instance_id, fact_kind, fact_date)` + natural key). | [ADR-PC-017 slot 4](./ADR-PC-017-legacy-batch-ingest-contract.md) | integration (Testcontainers) | `BATCH_INGEST_IDEMPOTENT` | Planned |
| 8 | **Cold replay budgets** are met: ≤ 5 s for a with-a-plan instance, ≤ 30 s for an irregular one. *(v1 5 s half Live via D.5; the v4 30 s irregular-family half stays with L.3's load harness — see the D.5 reconciliation note.)* | [event-store §8.2](../feature-design-event-store-projections.md) | benchmark (CI Integration lane) | `REPLAY_BUDGET_5S_30S` | Live |
| 9 | **Zero engine code per new variant** — adding a family/variant produces **zero `/engine` diff**. | [01 §3](../01-product-architecture.md) | acceptance | `ZERO_ENGINE_DIFF_PER_VARIANT` | Planned |
| 10 | **`pack-validate` depths 1–4 meet budget** synchronously at variant/pack-commit and on every PR — syntactic < 1 s, type < 5 s, pack-compliance < 10 s, regulatory-coherence < 10 s, aggregate < 30 s; a depth-N failure rejects the commit. | [ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md) | benchmark (per-PR) | `PACK_VALIDATE_DEPTH_BUDGETS` | Live |
| 11 | **Depth-5 simulation meets budget** — the sealed pack test-corpus, appended through the engine's hand-rolled append/replay substrate against a session-scoped Testcontainers PostgreSQL fixture, reproduces the expected event sequence in < 30 s in CI. | [ADR-PC-006 §P4](./ADR-PC-006-cue-schema-language.md) | benchmark (CI) | `PACK_SIM_DEPTH5_BUDGET` | Planned |
| 12 | The generic engine spine (`Babelstone.Engine`, `Babelstone.EventStore`, `Babelstone.RateSheets`, `Babelstone.Packs`, `Babelstone.FinancialMath`, `Babelstone.FinancialTypes`, `Babelstone.Engine.Avro`, `Babelstone.OutboxPublisher`) carries **no `ProjectReference` to any `families/**` project** — the `family → engine` arrow is one-way. | [ADR-PC-021 §P2 / §D2](./ADR-PC-021-application-layer-family-owned-deciders.md) | architecture / dependency assertion (CI) | `ENGINE_FAMILY_AGNOSTIC` | Live |
| 12a | The engine **event-store migration set** carries **no family-named table** — the entire engine `MigrationSet.All` is scanned for a family-typed table/column/FK, and an inverse positive guard RED-fails if a `read_model` schema or `deposits`-named object re-appears in the engine set. The schema-level twin of row 12: 12 guards the `family → engine` arrow at the `.csproj` level, 12a at the migration-schema level. | [ADR-PC-021 §A5–§A7](./ADR-PC-021-application-layer-family-owned-deciders.md) | architecture / dependency assertion (CI) | `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` | Live |
| 13 | **Exactly one currently-believed projection row** per `(stream_id, projection_kind)`; a correction supersedes-then-inserts atomically and never overwrites or deletes the prior belief. | [ADR-PC-002 §P1 / §P2](./ADR-PC-002-application-level-bitemporality.md) | integration (Testcontainers) | `PROJECTION_ONE_CURRENT_BELIEF` | Planned |
| 14 | **A cold projection rebuild reproduces byte-identical current-belief rows** — every stamp is event-derived (`recorded_at` = the event's transaction-time), never wall-clock. | [ADR-PC-002 §P4](./ADR-PC-002-application-level-bitemporality.md), [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) | integration (Testcontainers) | `PROJECTION_REBUILD_DETERMINISM` | Planned |
| 15 | **A projection folded synchronously vs asynchronously yields identical rows**; the mode is declared per projection, not hardcoded into the engine. | [ADR-PC-002 §P4](./ADR-PC-002-application-level-bitemporality.md) | integration (Testcontainers) | `PROJECTION_MODE_EQUIVALENCE` | Planned |
| 16 | A **required precondition** that is absent or `satisfied: false` yields `DepositConstitutionFailed`, computed as a **pure function of the command's verdicts** — no in-engine evaluation, no compensation. | [ADR-PC-024 slot 5](./ADR-PC-024-constitution-precondition-contract.md) | contract / saga | `CONSTITUTION_PRECONDITION_REFUSAL` | Planned |
| 17 | **No engine-emitted event is produced by a clock/scheduler** — every emitted signal traces to a causing domain event, and no family schema declares a clock-driven "about-to-happen" event type. *(Gate is "analyser + contract": the contract half is Live via the emit-contract fitness tests, and the build-time analyser half is now Live too — `NoClockDrivenEngineSignalAnalyzer` (BENG004) flags a `DomainEvent`/`ScheduledEffect` emit whose construction a clock/scheduler/timer read flows into, catching the off-list clock-driven type name the lexical scan misses.)* | [ADR-PC-023 slot 1](./ADR-PC-023-temporal-signals-projection-derived.md) | analyser + contract | `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` | Live |
| 18 | The deposit read surface is **one canonical resource** (`GET /v1/deposits/{id}`, no storage-named sibling); it serves the denormalized read model by default and **folds the event stream as an internal read-your-writes fallback** when a caller's `If-Min-Sequence` token (a command's `commit_sequence`) outruns the projection — both paths fill the same `DepositResponse`. | [ADR-PC-027 slot 2/3](./ADR-PC-027-deposit-read-surface-canonical-resource.md) | integration (Testcontainers) | `READ_YOUR_WRITES_FOLD_ON_TOKEN` | Live |
| OBS-1 | Every Babelstone .NET host stamps its tracer's **resource** with `service.name`, `service.namespace == "babelstone"`, and a non-blank `deployment.environment` — so every trace is attributable to a service, the estate, and an environment. | [ADR-IC-007 §P1](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit | `OBS_RESOURCE_ATTRS` | Live |
| OBS-2 | The product-semantic spans (`accrual.computed`, `withholding.applied`) are emitted in the **impure runtime shell** (`AggregateRuntime.AppendAsync`'s span hook / the host endpoint), **never** in the pure decider/fold, and carry the structural `babelstone.partition_key` + `babelstone.product_code`. | [ADR-IC-007 §P2–§P3](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit | `OBS_SPAN_PRODUCT_SEMANTICS` | Live |
| OBS-3 | **No PII in any telemetry signal** — span/log attributes carry only structural identifiers (the `babelstone.*` operational tier), never NIF/IBAN/account/name/email; money rides as integer cents. | [ADR-IC-007 §P4](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit / analyser | `OBS_NO_PII_ATTRS` | Planned |
| OBS-4 | **W3C `traceparent` propagates across every process boundary**, including the durable bus (carried as an envelope/outbox header), so a `correlation_id` resolves a complete cross-process trace. | [ADR-IC-007 §P1 (Layer 1)](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | integration | `OBS_TRACEPARENT_PROPAGATION` | Planned |

The "~8 load-bearing" of [ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
counts post-flag-never-gates (rows 5a–5c) as one invariant realised across the
three signal contracts; the catalogue lists the three test IDs the coverage
checker resolves individually.

Rows 10–11 (the `pack-validate` depth budgets) were added under the growth
provision below, identified in the Epic C readiness review: they are load-bearing
to the authoring loop's sub-30 s feedback premise ([ADR-PC-006 §P3–§P4](./ADR-PC-006-cue-schema-language.md)).
They are **toolchain** fitness functions rather than engine invariants, so they
sit alongside the original ~8 rather than recounting it.

**Epic A reconciliation (2026-05-30).** `DETERMINISM_GATE` (row 3) flipped
`Planned → Live`: A.7 (`archie-k03q`) shipped both halves of the gate — the
build-time `BENG001/002/003` handler-purity analysers (referenced by
`Babelstone.Engine` and built warnings-as-errors) and the runtime fixture-replay
determinism test — and both run in CI's `engine` job (the non-Integration tier of
`.github/workflows/ci.yml`). `ES_ATOMIC_APPEND_OUTBOX` (row 1) deliberately stays
`Planned`: A.2 (`archie-2m49`) shipped the atomic append+outbox and its integration
test is green on PostgreSQL 18, but that test is `[Trait("Category","Integration")]`
and CI runs `--filter "Category!=Integration"`, so the lane that would run it is
deferred to **E.6 (`archie-2jum`)** (see the `engine` job's TODO in
`.github/workflows/ci.yml`). It flips to `Live` when that Testcontainers lane is
wired — per the "runs in CI" rule above and the [Epic A PR #34](https://github.com/jhosm/babelstone/pull/34)
follow-up that scopes this reconciliation.

**E.6 reconciliation (2026-05-31).** The deferral recorded above is now resolved:
`ES_ATOMIC_APPEND_OUTBOX` (row 1) flips `Planned → Live`. E.6 (`archie-2jum`) wired
the Testcontainers Integration tier — a second `--filter "Category=Integration"`
step in the `engine` job of `.github/workflows/ci.yml` — so `AtomicAppendIntegrationTests`
(green on PostgreSQL 18 since A.2) now runs in CI, meeting the "test exists, passes,
**and runs in CI**" bar for `Live`. The same lane runs the Redpanda outbox round-trip
(E.4) and the constitute→mature API E2E (`DepositsApiIntegrationTests`), the
acceptance test that exercises `ZERO_ENGINE_DIFF_PER_VARIANT` (row 9 stays `Planned`
— it is "enforced across **E/F**", so it flips when the full family content lands in
Epic F, not at E.6).

**D.2 reconciliation (2026-05-31).** Three projection-runtime rows
(`PROJECTION_ONE_CURRENT_BELIEF` row 13, `PROJECTION_REBUILD_DETERMINISM` row 14,
`PROJECTION_MODE_EQUIVALENCE` row 15) were added under the growth provision as D.2
(`babelstone-zkr1`) implements [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md)
§P1/§P2/§P4 — the spec-first loop ([ADR-PC-020 §P10](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)),
and the ADR's matching `## Verifiable commitments` section is added in the same change
(its first, per the [ADR-PC-000 §A3](./ADR-PC-000-namespace-and-contract-shape-framework.md)
incremental backfill). All three start `Planned`. Rows 13–14 ship as Testcontainers
integration tests in `Babelstone.EventStore.Tests` (`PostgresProjectionStoreTests`
forced-correction round-trip + partial-UNIQUE assertions; `ProjectionRuntimeIntegrationTests`
rebuild byte-identity) — green on PostgreSQL 18 — but stay `Planned` until confirmed running
in the CI Integration lane (the `ES_ATOMIC_APPEND_OUTBOX` precedent: a green Testcontainers
test that the lane runs is the `Live` bar). Row 15 cannot be exercised until the v4 sync path
is turned on (every v1 projection is async; the post-commit hook is wired no-op), so its gate
is built before the path it guards — the `IFRS9_POST_FLAG_NEVER_GATES` shape. The
forced-correction *acceptance* drill is deferred to D.5 (`babelstone-m9n2`).

**D.5 reconciliation (2026-06-07).** `REPLAY_BUDGET_5S_30S` (row 8) flips `Planned → Live`,
but **only its v1 half is proven**. D.5 (`babelstone-m9n2`) ships
`ColdReplayBudgetTests.REPLAY_BUDGET_5S_30S_v1_cold_replay_of_one_instance_is_under_5s`
(in `Babelstone.Families.TermDeposit.Application.Tests`): a 262-event with-a-plan instance
(constitute → accrue×260 → mature, the TOP of the §8.2 ~24-260 range) cold-replays via
`AggregateRuntime.LoadAsync` with no snapshots in **~13 ms locally**, far inside the 5 s
budget — and it runs in CI's `Category=Integration` lane (the `ES_ATOMIC_APPEND_OUTBOX`
precedent: a green Testcontainers test the lane runs is the `Live` bar). The **v4 30 s
irregular-family half** (~250-1000 events, sustained v4-scale traffic) is **NOT** proven
here: it belongs to L.3's in-house load harness ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md),
the backlog's "D.5 / L.3" split), which builds the v4-scale corpus and the sustained-throughput
rig D.5 deliberately does not. The row is `Live` because the commitment now resolves to ≥1
running test; the second half is tracked under L.3 (`babelstone-2e6q`) and tightens the same
Test ID when its rig lands. The gate column is **CI Integration lane** rather than the
originally-reserved "benchmark (nightly)": the test rides the existing per-push Integration tier
(no separate nightly lane exists yet), which is the stricter "runs in CI" home.

D.5 also ships the **reconciliation runtime** the §7.1 patterns need —
`engine/src/Babelstone.Engine/ProjectionReconciler.cs` (per-instance state checksum,
event-count gap-vs-skip, and the §7.2 full-rebuild drill over `ProjectionDrainer.RebuildAsync`),
exercised by `ProjectionReconcilerIntegrationTests` (synthetic family) — and the
forced-correction round-trip **acceptance** drill on the real term-deposit family
(`ForcedCorrectionRoundTripTests`), the spike-criterion-#1 deliverable ADR-PC-002's D.2
note deferred to D.5. Those land under the existing `PROJECTION_ONE_CURRENT_BELIEF` (row 13)
and `PROJECTION_REBUILD_DETERMINISM` (row 14) commitments — D.5 adds no new catalogue row for
them; it is the acceptance/operational layer over D.2's already-catalogued plumbing.

**Epic K reconciliation (K.1).** The four `OBS-*` rows land with K.1 (`babelstone-rzcl`),
which ships the shared `ActivitySource` + `babelstone.*` attribute contract
(`Babelstone.Telemetry`), the product-semantic spans in the impure runtime shell, and
the OTLP + resource wiring on both .NET hosts. `OBS_RESOURCE_ATTRS` (OBS-1) and
`OBS_SPAN_PRODUCT_SEMANTICS` (OBS-2) are `Live`: their Docker-free fitness tests
(`ResourceAttributeTests`, `TelemetrySpanTests` in `Babelstone.Engine.Tests`) exist,
pass, and run in CI's `engine` job (the non-Integration tier). `OBS_NO_PII_ATTRS`
(OBS-3) stays `Planned`: the structural no-PII assertion rides inside `TelemetrySpanTests`
today, but its dedicated build-time analyser gate (the cultural→mechanical control of
ADR-IC-007 §P4) is not yet written, so the row does not yet resolve to its own gate.
`OBS_TRACEPARENT_PROPAGATION` (OBS-4) stays `Planned`: cross-process `traceparent`
propagation over the durable bus is documented here but deferred (the K.1 SCOPE-OUT) to
the bus-relay work, and its lane is the deferred Testcontainers Integration tier.

**Term-deposit scope review (2026-06-03).** Two rows were added under the growth
provision from the §B in/out-of-scope review, both `Planned` (the work is **v1.x** /
downstream, not v1): `CONSTITUTION_PRECONDITION_REFUSAL` (row 16,
[ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)) and
`NO_CLOCK_DRIVEN_ENGINE_SIGNAL` (row 17,
[ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)). Row 17 extends the
`DETERMINISM_GATE` purity stance from the *fold* (no clock in handlers — already `Live`)
to the *emit path* (no clock-driven engine event), and its analyser half is the natural
gate against a family schema introducing a `DepositMaturityApproaching`-style non-fact
event type. Row 5b's claim was narrowed in the same change: per
[ADR-PC-014 Amendment A1](./retired/ADR-PC-014-customer-notification-emit-contract.md) the engine
no longer emits `SCHEDULED` notifications, so `NOTIFY_POST_FLAG_NEVER_GATES` now covers
`EVENT_DRIVEN` (+ the `PRE_CONTRACTUAL` carve-out) only.

**Emit-contract fitness reconciliation (2026-06-10).** Three emit-path rows flip
`Planned → Live` (bd `babelstone-2eo0`), each now resolving to a tight pure-reflection /
disk-scan fitness test in `EmitContractFitnessTests` (`Babelstone.Engine.Tests`) — same
no-container, no-saga-infra idiom as `EngineFamilyAgnosticTests`, and run in CI's `engine`
job (the non-Integration tier of `.github/workflows/ci.yml`, the `ES_ATOMIC_APPEND_OUTBOX`
"runs in CI" bar). The tests are the spec-first loop ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md))
realised on the emit surface:

- `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` (row 17, [ADR-PC-023 slot 1](./ADR-PC-023-temporal-signals-projection-derived.md)):
  three structural assertions — no Avro event schema (`contracts/avro/**`) and no family
  `DomainEvent` type (`families/**/Events.cs`) names a clock-driven "about-to-happen" signal
  (the `DepositMaturityApproaching` / `PaymentDue` forbidden shape), and the `Babelstone.Engine`
  emit spine runs no scheduler/timer (a clock tick cannot fire an engine signal; the runtime
  stamps `transaction_time` from the injected `TimeProvider` at append but owns no timer). This
  extends `DETERMINISM_GATE`'s purity stance from the *fold* to the *emit path* — the contract
  half is now Live; the dedicated build-time analyser the ADR's "analyser + contract" gate column
  also names is a separate layer (the analyser catches a clock-driven type at *compile* time
  rather than test time) that, at this reconciliation, was a tracked follow-up — and the row
  already resolved to ≥1 running CI test, meeting the `Live` bar. *(Update 2026-06-11, bd
  `babelstone-52zq`: that analyser layer is now Live — `NoClockDrivenEngineSignalAnalyzer`
  (BENG004) in `Babelstone.Engine.Analyzers` flags a `DomainEvent`/`ScheduledEffect` emit whose
  construction a clock/scheduler/timer read flows into, the semantic proof the lexical name-scan
  cannot give. Both halves of the "analyser + contract" gate are now in place.)*
- `GL_POST_FLAG_NEVER_GATES` (row 5a, [ADR-PC-012 slot 5](./ADR-PC-012-gl-posting-signal-contract.md))
  and `NOTIFY_POST_FLAG_NEVER_GATES` (row 5b, [ADR-PC-025 slot 5](./ADR-PC-025-customer-notification-emit-contract.md)):
  the EMIT-side structural proof (delivery is DEF-2 deferred). The family decide/append path injects
  only the sanctioned post-flag collaborators (a closed-world allowlist over the append drivers'
  primary-constructor dependencies; the pure `TermDepositDecider` is `static`, holding no port) — a
  GL-posting or notification port under ANY name is by construction absent, so a GL reject or a
  notification failure cannot gate or unwind the producing flow; and
  `AggregateRuntime.AppendAsync` emits only through the outbox (event + `OutboxRow` in one sink
  transaction, no inline synchronous broker publish), so emission is post-commit fire-and-forget.
  The `PRE_CONTRACTUAL` (FIN) synchronous-saga carve-out lives in the constitution saga, off this
  path; `SCHEDULED` is no longer engine-emitted ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)).
  The "~8 load-bearing" still counts post-flag-never-gates (5a–5c) as ONE invariant: 5a/5b are
  now Live; 5c (`IFRS9_POST_FLAG_NEVER_GATES`) stays `Planned` — no IFRS 9 signal is emitted in
  v1, so there is no emit path to gate (the gate is built before the v2 credit scope, the
  build-before-the-path-it-guards posture).

The no-PII-on-the-bus assertion lands in the same file (`No_event_schema_field_carries_pii`),
reusing the `TelemetrySpanTests` `OBS_NO_PII_ATTRS` structural key-fragment detection over the
emitted-event schema surface — it does **not** flip a row: `OBS-3` (`OBS_NO_PII_ATTRS`) stays
`Planned` because its `Live` bar is the dedicated build-time *analyser* gate (ADR-IC-007 §P4),
not yet written. This is the bus-surface companion to that telemetry-surface check, not the
analyser the row reserves; it honours [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) /
[ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) Decision 1 (the envelope carries
no PII; references are resolved internally) without claiming the OBS-3 gate.

**AML withdrawal (2026-06-03).** Row 6 (`AML_EDGE_PRECONDITION`, governed by
ADR-PC-013) was **removed**: AML/KYC is out of scope for the product engine
([00 §4](../00-product-vision.md)), and [ADR-PC-013](./retired/ADR-PC-013-aml-kyc-upstream-precondition.md)
is `Withdrawn`. The product engine has no AML commitment to gate; if AML clearance is
enforced at the edge, that is an integration-estate fitness function, not a product-engine
one. Row numbers are display indices, not the join key (Test IDs are) — the gap left at 6 is
harmless and rows 7–17 keep their identifiers.

**Schema-level family-agnosticism reconciliation (2026-06-13).** Row 12a
(`EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC`) was added under the growth provision as the
schema-level twin of row 12 (`ENGINE_FAMILY_AGNOSTIC`). Row 12 guards the `family → engine`
arrow at the `.csproj`-reference level; 12a guards it at the migration-**schema** level — the
engine event-store migration set may carry no family-named table. It lands `Live` with bd
`babelstone-2t16.18`, which **relocated** the term-deposit CQRS read model
(`read_model.deposits`, formerly engine migration `0013_read_model.sql`) into a family-owned
migration set so the engine migrations carry zero family-named tables
([ADR-PC-021 §A5–§A7](./ADR-PC-021-application-layer-family-owned-deciders.md), amended
2026-06-13). The gate is `EventStoreSchemaFamilyAgnosticTests` (`Babelstone.Engine.Tests`): it
parses the entire engine `MigrationSet.All` (no read-side carve-out, since the engine now owns
zero family tables), runs three deny scans (no family-typed table name, column name, or FK
target), and adds an inverse positive guard that RED-fails if a `read_model` schema or
`deposits`-named object re-appears in the engine set. It is infrastructure-free and runs in CI's
`engine` job (the non-Integration tier — same `ENGINE_FAMILY_AGNOSTIC` "runs in CI" bar), so it
meets the `Live` criterion. The relocated read model's own schema/role assertions move to the
family's Testcontainers integration tier (`ReadModelMigrationSchemaIntegrationTests` in
`Babelstone.Families.TermDeposit.Application.Tests`).

## Coverage by pyramid level

This is the shape [ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
predicts — most invariants already have a home on the existing pyramid, none
spawns a parallel suite:

| Level / mechanism | Test IDs |
|---|---|
| Unit | `OBS_RESOURCE_ATTRS`, `OBS_SPAN_PRODUCT_SEMANTICS` |
| Unit + analyser | `MONEY_BOUNDARY_FIXTURES`, `OBS_NO_PII_ATTRS` |
| Analyser / CI gate | `DETERMINISM_GATE`, `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` (analyser + contract) |
| Architecture / dependency assertion (CI) | `ENGINE_FAMILY_AGNOSTIC`, `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` |
| Integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX`, `REPLAY_PIN_PER_EVENT`, `BATCH_INGEST_IDEMPOTENT`, `OBS_TRACEPARENT_PROPAGATION`, `READ_YOUR_WRITES_FOLD_ON_TOKEN` |
| Contract / saga | `GL_POST_FLAG_NEVER_GATES`, `NOTIFY_POST_FLAG_NEVER_GATES`, `IFRS9_POST_FLAG_NEVER_GATES`, `CONSTITUTION_PRECONDITION_REFUSAL` |
| Benchmark (CI Integration lane) | `REPLAY_BUDGET_5S_30S` (v1 half; v4 30 s half deferred to L.3) |
| Benchmark (per-PR / CI) | `PACK_VALIDATE_DEPTH_BUDGETS`, `PACK_SIM_DEPTH5_BUDGET` |
| Acceptance | `ZERO_ENGINE_DIFF_PER_VARIANT` |

## What consumes this

- The **coverage checker + auditor** ([ADR-PC-020 §P6 / §P3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md))
  — built as `.github/scripts/spec-coverage-check.sh` (per-push, authoritative) and
  `spec-coverage-audit.sh` (nightly sweep), wired in `.github/workflows/spec-coverage.yml`
  and mirrored at edit time by the `surface-spec-coverage` hook (archie-bhq.4) —
  validates this catalogue's integrity, enforces ADR↔catalogue Test-ID consistency
  in both directions, and (once engine source lands) asserts every `Live` Test ID
  resolves to a running test and that every code anchor points to a live, non-superseded
  ADR.
- The **spec-first loop** ([ADR-PC-020 §P10](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):
  implementing one of these decisions starts by writing the named Test ID as a
  *failing* fitness function, then implementing until green and flipping the row to
  `Live` **here** (the single place status lives).
- The **incremental backfill** ([ADR-PC-020 Open Action #7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md))
  grows the per-ADR `## Verifiable commitments` reference sections across the rest
  of the corpus (and the in-house ADR-IC entries, per [§P11](./ADR-PC-020-llm-toolchain-and-conformance-governance.md));
  new load-bearing rows land here as they are identified.
