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
| 1 | `append` writes event rows **and** outbox rows in one local PostgreSQL transaction (no event without its outbox row, and vice versa). | [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md) | integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX` | Planned |
| 2 | Money rounds **HALF_EVEN exactly once** at the `Decimal → Cents` boundary, proven against a sealed golden-fixture corpus. | [ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md) | unit + analyser | `MONEY_BOUNDARY_FIXTURES` | Planned |
| 3 | A handler that reads the clock, does I/O, or uses randomness **fails the build**; event evolution is additive-only. | [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) | analyser / CI determinism gate | `DETERMINISM_GATE` | Live |
| 4 | Replay reads the pack/schema pin **off each event**, not the clock; a migration at sequence `M` splits the stream's pin, and a rebuild re-derives the identical per-event pin whenever it runs. | [ADR-PC-009 §P1–§P2](./ADR-PC-009-per-instance-version-pinning.md) | integration (Testcontainers) | `REPLAY_PIN_PER_EVENT` | Planned |
| 5a | **Post-flag, never gated** — a GL-side reject never blocks or unwinds the producing business flow. | [ADR-PC-012 slot 5](./ADR-PC-012-gl-posting-signal-contract.md) | contract / saga | `GL_POST_FLAG_NEVER_GATES` | Planned |
| 5b | **Post-flag, never gated** for `EVENT_DRIVEN`/`SCHEDULED` notifications; the `PRE_CONTRACTUAL` (FIN) case is the synchronous saga carve-out. | [ADR-PC-014 slot 5](./ADR-PC-014-customer-notification-emit-contract.md) | contract / saga | `NOTIFY_POST_FLAG_NEVER_GATES` | Planned |
| 5c | **Post-flag, never gated — unconditionally**; IFRS 9 is downstream and has no gating claim. *(No signal emitted in v1; gate built before the v2 credit scope.)* | [ADR-PC-015 slot 5](./ADR-PC-015-ifrs9-signal-contract.md) | contract / saga | `IFRS9_POST_FLAG_NEVER_GATES` | Planned |
| 6 | Absent/invalid AML clearance is a `403` at the **edge** (orchestrator never starts); the engine has **no eligibility step, no AML gate, no AML-reject compensation**. | [ADR-PC-013 slot 5](./ADR-PC-013-aml-kyc-upstream-precondition.md) | contract / saga | `AML_EDGE_PRECONDITION` | Planned |
| 7 | A re-ingested legacy batch file produces **no duplicate `LegacyInstanceObserved` events** (engine-side dedupe on `(legacy_instance_id, fact_kind, fact_date)` + natural key). | [ADR-PC-017 slot 4](./ADR-PC-017-legacy-batch-ingest-contract.md) | integration (Testcontainers) | `BATCH_INGEST_IDEMPOTENT` | Planned |
| 8 | **Cold replay budgets** are met: ≤ 5 s for a with-a-plan instance, ≤ 30 s for an irregular one. | [event-store §8.2](../feature-design-event-store-projections.md) | benchmark (nightly) | `REPLAY_BUDGET_5S_30S` | Planned |
| 9 | **Zero engine code per new variant** — adding a family/variant produces **zero `/engine` diff**. | [01 §3](../01-product-architecture.md) | acceptance | `ZERO_ENGINE_DIFF_PER_VARIANT` | Planned |
| 10 | **`pack-validate` depths 1–4 meet budget** synchronously at variant/pack-commit and on every PR — syntactic < 1 s, type < 5 s, pack-compliance < 10 s, regulatory-coherence < 10 s, aggregate < 30 s; a depth-N failure rejects the commit. | [ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md) | benchmark (per-PR) | `PACK_VALIDATE_DEPTH_BUDGETS` | Live |
| 11 | **Depth-5 simulation meets budget** — the sealed pack test-corpus, appended through the engine's hand-rolled append/replay substrate against a session-scoped Testcontainers PostgreSQL fixture, reproduces the expected event sequence in < 30 s in CI. | [ADR-PC-006 §P4](./ADR-PC-006-cue-schema-language.md) | benchmark (CI) | `PACK_SIM_DEPTH5_BUDGET` | Planned |

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

## Coverage by pyramid level

This is the shape [ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
predicts — most invariants already have a home on the existing pyramid, none
spawns a parallel suite:

| Level / mechanism | Test IDs |
|---|---|
| Unit + analyser | `MONEY_BOUNDARY_FIXTURES` |
| Analyser / CI gate | `DETERMINISM_GATE` |
| Integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX`, `REPLAY_PIN_PER_EVENT`, `BATCH_INGEST_IDEMPOTENT` |
| Contract / saga | `GL_POST_FLAG_NEVER_GATES`, `NOTIFY_POST_FLAG_NEVER_GATES`, `IFRS9_POST_FLAG_NEVER_GATES`, `AML_EDGE_PRECONDITION` |
| Benchmark (nightly) | `REPLAY_BUDGET_5S_30S` |
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
