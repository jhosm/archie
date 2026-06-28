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
| 1 | `append` writes event rows **and** their outbox rows in one local PostgreSQL transaction — neither half lands without the other. The per-event coupling is **≤ one outbox row per event**: every outbox row has its appended event, but a catalogued event gets an outbox row while an **uncatalogued** event is store-only by construction and gets none (narrowed from "one-per-event, and vice versa" by [ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md); see [ADR-PC-001 §P2 amendment 2026-06-13](./ADR-PC-001-event-store-technology.md)). | [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md) | integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX` | Live |
| 2 | Money rounds **HALF_EVEN exactly once** at the `Decimal → Cents` boundary, proven against a sealed golden-fixture corpus. | [ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md) | unit + analyser | `MONEY_BOUNDARY_FIXTURES` | Planned |
| 3 | A handler that reads the clock, does I/O, or uses randomness **fails the build**; event evolution is additive-only. | [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) | analyser / CI determinism gate | `DETERMINISM_GATE` | Live |
| 4 | Replay reads the per-event pin **off each event**, not the clock; a migration at sequence `M` splits the stream's pin, and a rebuild re-derives the identical per-event pin whenever it runs. The pin family is `pack_version`/`schema_version` (envelope) + `rate_sheet_version_id`/`product_config_version` (payload on `DepositConstituted`, resolved in-transaction — §A2). | [ADR-PC-009 §P1–§P2, §A2](./ADR-PC-009-per-instance-version-pinning.md) | integration (Testcontainers) | `REPLAY_PIN_PER_EVENT` | Planned |
| 5a | **Post-flag, never gated** — a GL-side reject never blocks or unwinds the producing business flow. | [ADR-PC-012 slot 5](./ADR-PC-012-gl-posting-signal-contract.md) | contract / saga | `GL_POST_FLAG_NEVER_GATES` | Live |
| 5b | **Post-flag, never gated** for `EVENT_DRIVEN` notifications; the `PRE_CONTRACTUAL` (FIN) case is the synchronous saga carve-out. *(Per [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) — the clean reissue of [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) — `SCHEDULED` is no longer engine-emitted; its purity claim is `NO_CLOCK_DRIVEN_ENGINE_SIGNAL`, row 17.)* | [ADR-PC-025 slot 5](./ADR-PC-025-customer-notification-emit-contract.md) | contract / saga | `NOTIFY_POST_FLAG_NEVER_GATES` | Live |
| 5c | **Post-flag, never gated — unconditionally**; IFRS 9 is downstream and has no gating claim. *(No signal emitted in v1; gate built before the v2 credit scope.)* | [ADR-PC-015 slot 5](./ADR-PC-015-ifrs9-signal-contract.md) | contract / saga | `IFRS9_POST_FLAG_NEVER_GATES` | Planned |
| 7 | A re-ingested legacy batch file produces **no duplicate `LegacyInstanceObserved` events** (engine-side dedupe on `(legacy_instance_id, fact_kind, fact_date)` + natural key). | [ADR-PC-017 slot 4](./ADR-PC-017-legacy-batch-ingest-contract.md) | integration (Testcontainers) | `BATCH_INGEST_IDEMPOTENT` | Planned |
| 8 | **Cold replay budgets** are met: ≤ 5 s for a with-a-plan instance, ≤ 30 s for an irregular one. *(v1 5 s half Live via D.5; the v4 30 s irregular-family half stays with L.3's load harness — see the D.5 reconciliation note.)* | [event-store §8.2](../feature-design-event-store-projections.md) | benchmark (CI Integration lane) | `REPLAY_BUDGET_5S_30S` | Live |
| 9 | **Zero engine code per new variant** — adding a family/variant produces **zero `/engine` diff**. | [01 §3](../01-product-architecture.md) | acceptance | `ZERO_ENGINE_DIFF_PER_VARIANT` | Planned |
| 10 | **`pack-validate` depths 1–4 meet budget** synchronously at variant/pack-commit and on every PR — syntactic < 1 s, type < 5 s, pack-compliance < 10 s, regulatory-coherence < 10 s, aggregate < 30 s; a depth-N failure rejects the commit. | [ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md) | benchmark (per-PR) | `PACK_VALIDATE_DEPTH_BUDGETS` | Live |
| 11 | **Depth-5 simulation meets budget** — the sealed pack test-corpus, appended through the engine's hand-rolled append/replay substrate against a session-scoped Testcontainers PostgreSQL fixture, reproduces the expected event sequence in < 30 s in CI. *(C.3 + F.8: Live via `PackSimulationDepth5Tests` — drives all seven canonical pt.2026.1 instances through their per-shape lifecycles (constitute→[coupons]→mature for the four maturing shapes, a banded early termination, an F.12 partial withdrawal that stays Active, and an F.12 partial-withdrawal-then-mature re-base leg, bd babelstone-aviw) on the A.3 rehydrate substrate, asserts the per-shape event-type sequence, and gates the < 30 s budget; runs in the engine job's Testcontainers lane, which triggers on `packs`/`contracts` changes. BOTH halves are now gated: the structural EVENT-SEQUENCE assertion AND the full byte-level `expected-events.yaml` corpus — the latter GENERATED store-side from the engine (flow-by-flow withholding) and asserted field-for-field as a HARD gate (bd babelstone-up7t / F.8), retiring the logged-skip placeholder. The bus-Avro array-of-record limit, bd babelstone-vcxq, constrains only the bus payload, not this store-side corpus — see the test's scope note + ADR-PC-006 A2 Revised 2026-06-20.)* | [ADR-PC-006 §P4](./ADR-PC-006-cue-schema-language.md) | benchmark (CI) | `PACK_SIM_DEPTH5_BUDGET` | Live |
| 12 | The generic engine spine (`Babelstone.Engine`, `Babelstone.EventStore`, `Babelstone.RateSheets`, `Babelstone.Packs`, `Babelstone.FinancialMath`, `Babelstone.FinancialTypes`, `Babelstone.Engine.Avro`, `Babelstone.OutboxPublisher`) carries **no `ProjectReference` to any `families/**` project** — the `family → engine` arrow is one-way. | [ADR-PC-021 §P2 / §D2](./ADR-PC-021-application-layer-family-owned-deciders.md) | architecture / dependency assertion (CI) | `ENGINE_FAMILY_AGNOSTIC` | Live |
| 12a | The engine **event-store migration set** carries **no family-named table** — the entire engine `MigrationSet.All` is scanned for a family-typed table/column/FK, and an inverse positive guard RED-fails if a `read_model` schema or `deposits`-named object re-appears in the engine set. The schema-level twin of row 12: 12 guards the `family → engine` arrow at the `.csproj` level, 12a at the migration-schema level. | [ADR-PC-021 §A5–§A7](./ADR-PC-021-application-layer-family-owned-deciders.md) | architecture / dependency assertion (CI) | `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` | Live |
| 13 | **Exactly one currently-believed projection row** per `(stream_id, projection_kind)`; a correction supersedes-then-inserts atomically and never overwrites or deletes the prior belief. | [ADR-PC-002 §P1 / §P2](./ADR-PC-002-application-level-bitemporality.md) | integration (Testcontainers) | `PROJECTION_ONE_CURRENT_BELIEF` | Planned |
| 14 | **A cold projection rebuild reproduces byte-identical current-belief rows** — every stamp is event-derived (`recorded_at` = the event's transaction-time), never wall-clock. | [ADR-PC-002 §P4](./ADR-PC-002-application-level-bitemporality.md), [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) | integration (Testcontainers) | `PROJECTION_REBUILD_DETERMINISM` | Planned |
| 15 | **A projection folded synchronously vs asynchronously yields identical rows**; the mode is declared per projection, not hardcoded into the engine. | [ADR-PC-002 §P4](./ADR-PC-002-application-level-bitemporality.md) | integration (Testcontainers) | `PROJECTION_MODE_EQUIVALENCE` | Planned |
| 16 | A **required precondition** that is absent or `satisfied: false` yields `DepositConstitutionFailed`, computed as a **pure function of the command's verdicts** — no in-engine evaluation, no compensation. | [ADR-PC-024 slot 5](./ADR-PC-024-constitution-precondition-contract.md) | contract / saga | `CONSTITUTION_PRECONDITION_REFUSAL` | Planned |
| 17 | **No engine-emitted event is produced by a clock/scheduler** — every emitted signal traces to a causing domain event, and no family schema declares a clock-driven "about-to-happen" event type. *(Gate is "analyser + contract": the contract half is Live via the emit-contract fitness tests, and the build-time analyser half is now Live too — `NoClockDrivenEngineSignalAnalyzer` (BENG004) flags a `DomainEvent`/`ScheduledEffect` emit whose construction a clock/scheduler/timer read flows into, catching the off-list clock-driven type name the lexical scan misses.)* | [ADR-PC-023 slot 1](./ADR-PC-023-temporal-signals-projection-derived.md) | analyser + contract | `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` | Live |
| 18 | The deposit read surface is **one canonical resource** (`GET /v1/deposits/{id}`, no storage-named sibling); it serves the denormalized read model by default and **folds the event stream as an internal read-your-writes fallback** when a caller's `If-Min-Sequence` token (a command's `commit_sequence`) outruns the projection — both paths fill the same `DepositResponse`. | [ADR-PC-027 slot 2/3](./ADR-PC-027-deposit-read-surface-canonical-resource.md) | integration (Testcontainers) | `READ_YOUR_WRITES_FOLD_ON_TOKEN` | Live |
| 19 | A **replayed command id returns the original `commit_sequence` with no second append** — the engine (receiver) dedupes, keyed on the caller's command id, scoped per aggregate; composes with the `expectedVersion` concurrency guard. *(Live via bd `babelstone-t7o3.6`: the `command_dedup` ledger — migration 0015 — is written in the §P2 append transaction (`PostgresEventStore.AppendAsync`, receipt-before-events so a duplicate id cannot open a second stream) and read by the endpoint pre-check (`PostgresCommandLog`) before any side effect. Gated by `CommandIdempotencyIntegrationTests` — the store backstop, a duplicate id cannot open a second stream and returns the original head — and the host test `DepositsApiIntegrationTests.ENGINE_COMMAND_IDEMPOTENT_a_replayed_idempotency_key_returns_the_original_and_appends_once`.)* | [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md) | integration (Testcontainers) | `ENGINE_COMMAND_IDEMPOTENT` | Live |
| 20 | The **orchestrator dispatcher ↔ engine command-endpoint contract holds** — the consumer↔provider command surface is pinned, so a provider-side break fails the build. *(Stays `Planned` for the FORMAL Pact harness, but the contract is concretely pinned in code by a **Pact-STYLE CDC** shipped with the dispatcher, bd `babelstone-t7o3.3`: a shared `EngineCommandContract` the **consumer** produces against — `EngineCommandPactConsumerTests` drives the real `SagaCommandDispatchDrainer` against a stub asserting the request (POST `/v1/deposits`, mandatory UUID `Idempotency-Key`, JSON body) — and the **provider** verifies against the REAL engine via `WebApplicationFactory<Program>` — `EngineCommandPactProviderTests` (engine API Integration lane) asserts the engine honours the full contract: the 201 + `ConstituteDepositResponse` with snake_case `commit_sequence`, replay (same key → same 201, no second append), and the 400 on an absent/malformed key. Both halves anchor the `ENGINE_COMMAND_PACT` Test ID and run in CI, so a provider-side break already fails the build. What stays open is only the FORMAL PactNet broker round-trip (a `.pact.json` published + verified through a broker): PactNet carries a native Rust FFI that must bundle per-platform on CI, a larger/CI-fragile greenfield change deferred — so the row is honestly `Planned`, not faked Live, until that harness lands.)* | [ADR-PC-029 slot 6](./ADR-PC-029-engine-command-ingress.md) | contract (Pact CDC, [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) | `ENGINE_COMMAND_PACT` | Planned |
| 21 | The relay publishes an event **iff** it is catalogued (an AsyncAPI/`.avsc` entry); an **uncatalogued event is store-only by construction** — appended, folded, replayable, but never on the durable bus. *(Live via the append-side gate: `AggregateRuntime.AppendAsync` ALWAYS writes the events-envelope row but builds an `OutboxRow` — the relay's only publishable artefact — only when the injected family-agnostic `IIntegrationEventCatalog` (the real `AvroSchemaCatalog`, wired in `Program.cs`/`TermDepositHostModule`) catalogues the `event_type`; both halves still commit in the one sink transaction, so `ES_ATOMIC_APPEND_OUTBOX` holds (its lower bound relaxes from "one outbox row per event" to "≤ one per event"). Gated by `CatalogGatedRelayIntegrationTests` (Testcontainers Postgres): a catalogued `DepositConstituted` and an uncatalogued `InterestPaid` appended together — BOTH in the event store, only the catalogued one in the outbox — plus a store-only batch that appends with zero outbox rows.)* | [ADR-IC-017 §P1](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) | integration (relay + Testcontainers) | `INTEGRATION_EVENT_CATALOG_GATED` | Live |
| 22 | **Every relay-publishable `event_type` has an AsyncAPI/`.avsc` entry** — the reverse orphan check that mirrors row 21's runtime rule at build time, making *catalogued ⇔ on the bus* a hermetic biconditional. *(Live two ways: the pure .NET fitness test `CatalogGatedRelayReverseOrphanTests` (default lane) anchors on the RUNTIME event set — every `event_type` a loaded family registers a handler for — and asserts the gate predicate admits it IFF it is catalogued, the biconditional in full; the `contracts`-job shell gate `asyncapi-catalog-validate.sh` adds the schema-layer §P3 leg, asserting every catalogued `.avsc` record name is a real family `DomainEvent` (no phantom promotion). This catches a SCHEMALESS event the forward `.avsc`→catalog orphan check cannot see.)* | [ADR-IC-017 §P3](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) | analyser / CI (`contracts` job) | `NO_UNCATALOGUED_EVENT_ON_BUS` | Live |
| 23 | An **as-of / point-in-time read** of `GET /v1/deposits/{id}?as_of_sequence=N` folds the event stream up to and INCLUDING per-stream sequence `N` and returns the **historical projection at that point, not the current head** — the same pure, deterministic fold the read-your-writes fallback uses (no wall-clock in the fold), generalised with an inclusive upper bound. The axis is the per-stream `commit_sequence` (transaction-time); a malformed (negative) point is a `400` and a point beyond the head is a `422`, never a `500` and never a silent fold-to-head. A wall-clock `valid_time` axis (`?as_of=<timestamp>`) is deferred to the bitemporal projection runtime (Epic D / [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md)). | [ADR-PC-027 slot 2/3](./ADR-PC-027-deposit-read-surface-canonical-resource.md) | integration (Testcontainers) | `READ_AS_OF_SEQUENCE` | Live |
| ORCH-1 | The orchestrator **saga substrate** (`Babelstone.Orchestrator.Substrate`) carries **no `ProjectReference` to any `families/**` project** — the `family → substrate` arrow is one-way; the host composition root (`Babelstone.Orchestrator`) is the standing §D4 exemption. The saga-side cousin of row 12 (`ENGINE_FAMILY_AGNOSTIC`). *(Live via bd `babelstone-t7o3.12`: `OrchestratorFamilyAgnosticTests` (`Babelstone.Orchestrator.Tests`) parses the substrate `.csproj` and fails if it references `families/**`, and a sibling test keeps the substrate-project allowlist in lockstep with the ADR-IC-018 §P1/§P2 enumeration parsed off disk; runs in CI's orchestrator unit lane, `Category!=Integration`.)* | [ADR-IC-018 §D2/§D4/§P2](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) | architecture / dependency assertion (CI) | `ORCHESTRATOR_FAMILY_AGNOSTIC` | Live |
| ORCH-2 | No **family-specific** concrete saga is typed in the orchestrator substrate **assembly** — every `ISagaStateMachine` / `IResultEventBridge` / `ISagaCommandRouter` it defines names **no family** (no `Babelstone.Families.*` reference, no per-family token, no `saga.SagaType == "ConstitutionProcess"` branch). A **family-agnostic** concrete saga (the `settlement` saga, `SettlementProcess`, keyed only on the ADR-PC-032 `Movement` `direction` / opaque `account_ref`) IS substrate-owned — the saga-level analog of a substrate store — and is explicitly allow-listed. The type-level twin of ORCH-1: ORCH-1 guards the `family → substrate` arrow at the `.csproj` level, ORCH-2 catches a *family-named* saga typed INSIDE the substrate even when no `.csproj` reference does (the §Residual-risk the ADR names). *Narrowed 2026-06-24 (bd `babelstone-t7o3.15`, ADR-IC-018 Amendment A1/A2): the gate moved from "no concrete saga" to "no family-named concrete saga" so the substrate-owned `settlement` saga ADR-PC-032 mandates is legal; a family saga in the substrate still fails.* *(Live via bd `babelstone-t7o3.12`, narrowed by bd `babelstone-t7o3.15`: `OrchestratorFamilyAgnosticTests.Substrate_defines_no_family_named_concrete_saga` reflects over the substrate assembly and rejects any concrete saga outside the family-agnostic allow-list or naming a `Babelstone.Families.*` type; runs in CI's orchestrator unit lane.)* | [ADR-IC-018 §D1/§D3/§P3/§P6 + Amendment A1/A2](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) | architecture / type assertion (CI) | `ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA` | Live |
| ORCH-3 | The orchestrator substrate's **saga subscription wiring** (`Babelstone.Orchestrator.Substrate/Inbox/`) names **no per-family topic constant** — the consume topics arrive EXCLUSIVELY from the family module's `ISagaModule.ConsumeTopics` via the `required` `SagaInboxConsumerOptions.Topics` (derived from the AsyncAPI catalogue, bd `babelstone-9w2k.4`). A hardcoded family topic literal (a `"term_deposit"` / `"deposits.process.events"` subscription string) would be the per-family edit the family-count-invariant epic removes — a missed topic is a saga that silently never advances. The subscription-level cousin of ORCH-1/ORCH-2. *(Live via bd `babelstone-9w2k.5`: `OrchestratorFamilyAgnosticTests.Substrate_subscription_wiring_names_no_per_family_topic_constant` scans the substrate `Inbox/` source (comments stripped, literals kept) for a family topic token; runs in CI's orchestrator unit lane.)* | [ADR-IC-018 §D2/§P4](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) / [ADR-IC-003 §A9–§A11](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) | architecture / source assertion (CI) | `ORCHESTRATOR_SUBSTRATE_NO_FAMILY_TOPIC_CONSTANT` | Live |
| NOTIF-1 | The **notification core** (the `notification/` worker host + scheduler + per-service outbox + delivery/subscription stores) carries **no `ProjectReference` to any `families/**` project, and none to an engine-spine project** (`Babelstone.Engine` / `Babelstone.EventStore`, …) — the `family → core` arrow is one-way and the core reaches the engine only across the storage-opaque read contract ([ADR-PC-027](./ADR-PC-027-deposit-read-surface-canonical-resource.md)), never by a compile-time kernel reference; the host composition root is the standing [ADR-PC-021 §A2](./ADR-PC-021-application-layer-family-owned-deciders.md) exemption. The notification-estate cousin of row 12 (`ENGINE_FAMILY_AGNOSTIC`) and ORCH-1 (`ORCHESTRATOR_FAMILY_AGNOSTIC`). *(Live via bd `babelstone-60n8.5`: the skeleton's engine-kernel + term-deposit-family binding was relocated onto the ADR-PC-027 HTTP read contract — `DepositReadClient` over `GET /v1/deposits/{id}` — and `NotificationFamilyAgnosticTests` (`Babelstone.Notification.Tests`) parses the notification core `.csproj` and fails on a `families/**` or engine-spine reference, exactly like `EngineFamilyAgnosticTests` / `OrchestratorFamilyAgnosticTests`; runs in the notification default lane.)* | [ADR-IC-019 §D2/§P2](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md) | architecture / dependency assertion (CI) | `NOTIFICATION_FAMILY_AGNOSTIC` | Live |
| 12b | The **Engine API host's composition code** (`Babelstone.Engine.Api/Program.cs`) names **no concrete family** — no `Babelstone.Families.*` identifier (e.g. `DepositPosition`, `PostgresDepositReadModelStore`, `DepositsEndpoints`) reachable in code. The host is the §D4 composition root and KEEPS its `families/**` `ProjectReference` as the `HostModuleLoader` scan anchor (§A14), so this is NOT the `.csproj` gate row 12 is (that would contradict §A14) — it is **two family-agnostic pattern scans** for the `Babelstone.Families.*` prefix (no per-family token denylist — the high-churn shape the allowlist gates avoid; adding a family never edits this gate): (1) a SOURCE scan of `Program.cs` (comments + string literals stripped) catching a fully-qualified reference or a LOCAL `using`, and (2) a scan of the host's GLOBAL-import surface (csproj `<Using>` items + any `global using` in another host file) catching the one vector that would leave a bare, prefix-less family token in `Program.cs` — which the `.csproj` gate cannot backstop (host is the §A2/§A14 exemption; it checks `ProjectReference`, not `<Using>`; `ImplicitUsings` imports only the SDK set). With family host modules discovered by assembly-scan (bd `babelstone-9w2k.2`) and all per-family wiring relocated into the family's `IFamilyHostModule` (bd `babelstone-9w2k.1/.5`), the host names no family in code (a family named only in a COMMENT is fine). The host-side capstone of the family-count-invariant epic, the cousin of row 12 / ORCH-1. *(Live via bd `babelstone-9w2k.5`: `EngineApiHostFamilyAgnosticTests.Host_Program_cs_names_no_concrete_family_type_in_code` + `.Host_imports_no_family_namespace_globally`; runs in CI's engine default lane.)* | [ADR-PC-021 §P2/§D4 + §A2/§A14](./ADR-PC-021-application-layer-family-owned-deciders.md) | architecture / source assertion (CI) | `ENGINE_API_HOST_FAMILY_AGNOSTIC` | Live |
| 12c | The Engine API host **fails closed at load on a pack/family version skew** — `HostModuleLoader.CrossCheckAgainstPackManifest` cross-checks each discovered `IFamilyHostModule`'s `(FamilyName, AggregateType, SchemaVersion)` against the pinned pack's family-manifest (`families.yaml`, ADR-PC-007 §A1) and throws (the host exits non-zero before serving) on a schema-version/aggregate-type skew, an unpinned discovered family, OR a pinned family with no loadable module. Every module stamps `SchemaVersion` onto every `EventEnvelope` (ADR-PC-009 §P1), so a newer-than-pinned module is an audit/replay hazard — the cross-check is MANDATORY for the assembly-scan discovery to be safe. *(Live via bd `babelstone-9w2k.3`: `HostModuleLoaderTests` (`Babelstone.Engine.Api.Tests`, default lane) exercises the happy path + all four fail-closed directions; `PackParserTests` pins the `families.yaml` structural parse.)* | [ADR-PC-007 §A1](./ADR-PC-007-signed-yaml-oci-pack.md) / [ADR-PC-009 §P1/§A1](./ADR-PC-009-per-instance-version-pinning.md) | architecture / load-time assertion (CI) | `HOST_PACK_FAMILY_MANIFEST_CROSS_CHECK` | Live |
| MCP-1 | **Wrong-resource token is rejected at the MCP boundary** — a request bearing a token whose `aud` claim is not the MCP server's canonical URI receives `401` with code `AUDIENCE_MISMATCH` (and a `WWW-Authenticate` header carrying `resource_metadata`) **before any application/tool code runs** (RFC 8707 audience binding; the token-replay defence). Realised at BOTH layers: the Kong edge pre-function (`scripts/kong-config-check.sh` static contract) and the app-layer audience re-check (`AudienceMiddleware`, pytest). | [ADR-IC-010 §P3](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) | contract (Kong static + app pytest) | `MCP_WRONG_RESOURCE_TOKEN_REJECTED` | Live |
| MCP-2 | **The step-up-SCA gate on the agent money-movers cannot be bypassed client-side** — an irreversible money-mover (`POST /v1/deposits/{id}/maturity`, `…/interest`) refuses to settle on the agent's word: it transitions only on the bank's own signal, the AS-signed `acr`/`auth_time` Kong attests as `X-SCA-Acr`/`X-SCA-Auth-Time` (§A7/§A8), which a courier (the agent) cannot forge. A money-mover with **no** SCA proof or a **stale** `auth_time` is `422 SCA_REQUIRED` **before any side effect** and **does not settle** — the stream still carries only its constitution event; a fabricated elicitation "accept" without a genuinely refreshed token is `422`'d again on the retry (§A9, the §P8 invariant). | [ADR-IC-010 §P8 (§A7–§A9)](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) | integration (Testcontainers) | `MCP_SCA_GATE_CANNOT_BYPASS` | Live |
| OBS-1 | Every Babelstone .NET host stamps its tracer's **resource** with `service.name`, `service.namespace == "babelstone"`, and a non-blank `deployment.environment` — so every trace is attributable to a service, the estate, and an environment. | [ADR-IC-007 §P1](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit | `OBS_RESOURCE_ATTRS` | Live |
| OBS-2 | The product-semantic spans (`accrual.computed`, `withholding.applied`) are emitted in the **impure runtime shell** (`AggregateRuntime.AppendAsync`'s span hook / the host endpoint), **never** in the pure decider/fold, and carry the structural `babelstone.partition_key` + `babelstone.product_code`. | [ADR-IC-007 §P2–§P3](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit | `OBS_SPAN_PRODUCT_SEMANTICS` | Live |
| OBS-3 | **No PII in any telemetry signal** — span tags, structured-log fields, and metric dimensions carry only the admitted `babelstone.*`/semantic-convention operational tier, never NIF/IBAN/account/name/email; money rides as integer cents. Enforced **at emit** by the runtime guard (`AddBabelstonePiiGuard` / `BabelstoneAttributeTierProcessor` + `BabelstoneLogRecordTierProcessor` + the metric-View allowlist, `Babelstone.Telemetry.Hosting`) across all three signals — **the load-bearing leg** (bd njt2.9–2.11), since every real attribute is runtime-valued. Backed by the `TelemetrySpanTests` structural assertion (unit) and, as a **secondary build-time tripwire** for a literal call-site leak only, the BENG005 `NoPiiTelemetryAttributeAnalyzer` (which fires on none of the real sites, so it can never earn `Live` alone, bd njt2.12). | [ADR-IC-007 §P4](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | unit / analyser / runtime | `OBS_NO_PII_ATTRS` | Live |
| OBS-4 | **W3C `traceparent` propagates across every process boundary**, including the durable bus (carried as an envelope/outbox header), so a `correlation_id` resolves a complete cross-process trace. | [ADR-IC-007 §P1 (Layer 1)](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | integration | `OBS_TRACEPARENT_PROPAGATION` | Planned |
| OBS-5 | At the **synchronous HTTP boundary** the engine host joins the inbound trace and hands the trace id back to the caller: `AddAspNetCoreInstrumentation()` makes the request a SERVER span that adopts an inbound `traceparent` (so the `deposit.*` spans nest under it, not as roots), and **every** response carries the active trace id on the `X-Trace-Id` header as an opaque 32-hex id (never PII). A strict subset of OBS-4, scoped to in-process HTTP — bus/orchestrator propagation stays OBS-4·Planned. | [ADR-IC-007 §P1 (Layer 1)](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | integration | `OBS_TRACE_ID_SURFACED_HTTP` | Live |
| OBS-6 | The **projection-reconciliation surface emits live metrics** on the `Babelstone.Engine` meter, so the M.5 alert rules resolve to real series: `reconciliation_checksum_mismatch_total{consumer,projection_kind}` increments on a checksum mismatch, `reconciliation_event_count_drift_total{consumer,projection_kind}` on an event-count **Skip** (a benign Gap is **not** counted — acceptable async lag), `reconciliation_rebuild_drill_divergence_total{projection_kind}` on a diverged §7.2 rebuild drill, and the observable gauge `reconciliation_drill_last_success_timestamp_seconds{projection_kind}` records each clean drill's freshness. Tags are operational-tier references (`consumer` / `projection_kind`) — never PII (ADR-IC-007 §P4). The governing operational contract is the M.5 alert rules; the telemetry-naming and no-PII contract is [ADR-IC-007 §P2/§P4](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md). | [alert-rules.yaml `projection-reconciliation`](../../../../infra/grafana/prometheus/alert-rules.yaml) + [ADR-IC-007 §P2/§P4](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | integration (Testcontainers) | `OBS_RECONCILIATION_METRICS` | Live |
| SEC-1 | **Every Kafka client authenticates with a distinct SASL/SCRAM identity; topic ACLs reject cross-context produce/consume.** The producer (`OutboxDrainer`) and each consumer (`InboxPump`) present a per-service SCRAM credential resolved at the composition root through `ISecretProvider` (never PLAIN, never a span attribute, never on the bus); only the deposit producers may write deposit topics and each consumer reads only the topics it subscribes to (declarative `infra/redpanda/topic-acls.yaml` + `apply-acls.sh`). *(Unit leg Live via bd `babelstone-njt2.1`: `KafkaSaslOptionsTests` (`Babelstone.OutboxPublisher.Tests`, default lane) pins the credential applier — configured ⇒ SCRAM identity on the config, unconfigured ⇒ additive no-op, PLAIN never the default. The broker-side topic-ACL enforcement leg stays integration-Planned until an authenticated cluster runs in CI.)* | [ADR-IC-016 §4–§6 (plane ii)](../../integration_concepts/adrs/ADR-IC-016-service-identity-and-mtls.md) | unit + integration | `KAFKA_SASL_TOPIC_ACL` | Planned |
| SEC-2 | **The observability plane is role-scoped (NOC / compliance / developer); access to financially-attributed traces is logged.** The Grafana LGTM plane is provisioned-as-code with the four ADR-IC-007 §P6 roles (`noc-viewer` / `engineer` / `compliance-viewer` / `admin`); the Tempo (trace) datasource — the financial-restricted tier — is locked to `engineer` + `admin` (NOC + compliance have no trace access), and Grafana dataproxy logging records every trace query with its acting user (`infra/grafana/rbac/`). *(Live via bd `babelstone-njt2.7`: [`scripts/grafana-rbac-check.sh`](../../../../scripts/grafana-rbac-check.sh) stands up the pinned `grafana/otel-lgtm:0.28.0` with the `infra/grafana/rbac/` overlay in CI's `infra` job and asserts the end-to-end enforcement — an anonymous Tempo query is refused (401), a NOC-class token without `datasources:query` is refused the trace read (403) while engineer/admin succeed (200), and the authorised read is recorded user-attributed in Grafana's dataproxy access log; static assertions pin the provisioned `grafana.ini` + `provisioning/{roles,datasource-permissions,teams}.yaml` (bd `babelstone-njt2.4`). OSS Grafana enforces the `datasources:query` action-level gate + the access log + no-anonymous access; the per-datasource Tempo lock with a faithful folder-granular noc/engineer split + a tamper-evident audit log are the Enterprise/upstream-gateway hardening the ADR names, out of OSS scope.)* | [ADR-IC-016 §7 (plane iii)](../../integration_concepts/adrs/ADR-IC-016-service-identity-and-mtls.md) / [ADR-IC-007 §P6](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) | integration | `OBS_PLANE_RBAC` | Live |
| 24 | A **snapshot-accelerated rebuild is byte-identical to the cold fold** over the same deep stream **and demonstrably faster** within the §8.2 budget — the snapshot-equivalence invariant ADR-PC-003 §P4/§122 named as a deliberate, visible gap. A snapshot that diverges from the cold fold FAILS regardless of speed (a fast-but-wrong snapshot is the worst event-sourcing failure mode); correctness gates, performance only qualifies. *(Realises the ADR-PC-003 §122 "no executable commitment yet catalogued … to be added under its growth provision when the snapshot module is built" hole, now built — bd `babelstone-0uau.1`/L.5. Test exists and is green on a real PostgreSQL — `SnapshotReplayRigIntegrationTests` + the `LoadRunner` `--measure snapshot-replay` mode drive `AggregateRuntime.LoadAsync` snapshot-then-tail vs a snapshot-free cold runtime and assert hash-identity + a speedup. Stays `Planned` until the load-gate runs in the required CI lane: today it runs in the `load-gate.yml` RC-cadence workflow (`Category=Integration`), not yet a required check — that wiring is the bd `babelstone-j72w` follow-up, per the §P6 "runs in CI" criterion.)* | [ADR-PC-003 §P3/§P4](./ADR-PC-003-postgresql-snapshots.md) | benchmark (CI Integration / RC lane) | `SNAPSHOT_REPLAY_EQUIVALENCE` | Planned |
| CP-1 | The **personal_loan French-system amortization schedule conserves capital to the principal to the cent and zeroes the balance**, and the **early-repayment commission never exceeds `min(charged, statutory_cap) × capital_repaid` nor the lost-interest ceiling** (the PT consumer-credit caps: 0.50% with >1y remaining, 0.25% with ≤1y, fin-math §7.5). The amortization math runs command-side in the decider, never in a fold; the folds record the already-computed installment split. *(Test exists and is green Docker-free — `AmortizationMathTests` (`Babelstone.Families.PersonalLoan.Application.Tests`) pins the fin-math §4.1 worked example to the cent (C=€10,000 / TAN 6% / 12m ⇒ €860.66 installment), the capital-conservation + zero-balance invariants (locked against rounding drift by the `Schedule_conserves_to_the_cent_for_awkward_inputs` golden fixtures — odd principals like €10,000.01, 60–72-month terms, awkward periodic rates, and a 0% promo, each asserting capital-legs-sum == principal and S(n) == 0 EXACTLY, the balancing-final-row invariant), the interest-decreasing/capital-increasing shape, the zero-rate degenerate case, and the statutory-cap + lost-interest clamps; `PersonalLoanDeciderTests` pins the per-installment split, the full-vs-partial repayment + settlement pairing, and the [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) precondition refusal. Now `Live`: ADR-PC-031 is Accepted, and `AmortizationMathTests` runs Docker-free in CI's default unit lane, so the §P6 coverage checker registers this row's Test ID against the running test.)* | [ADR-PC-031 §D3/§D5](./ADR-PC-031-personal-loan-family.md) | unit (Docker-free) | `CREDITO_PESSOAL_AMORTIZATION_MATH` | Live |
| 25 | The **synchronous-replication append-latency cost is measured**, not assumed — append p50/p99 with `synchronous_commit` on vs off and the §P1 delta, the cost the RPO≈0 guarantee imposes. GATING against a real warm standby (the HA overlay, where `synchronous_commit=on` blocks on the named standby); ADVISORY on the single-node stack (the delta is then a floor, not the production cost). *(Realises the ADR-PC-005 §150 "Known gap, no Test ID yet wired … to be added under the catalogue's growth provision when synchronous replication is implemented and benchmarked" hole — bd `babelstone-2e6q.5`/L.3e. The MEASUREMENT path is built and green on a real PostgreSQL — `SnapshotReplayRigIntegrationTests` + the `LoadRunner` `--measure repl-latency` mode, toggling `synchronous_commit` via pure Npgsql connection-string composition (no engine-core change). Stays `Planned`: the single-node CI lane proves the path runs and returns finite p50/p99 but the GATING production cost needs the HA warm-standby cluster (ADR-PC-005 §P1 Residual Risk 2) — the live-cluster verification is the residual budget, and the required-CI wiring is the bd `babelstone-j72w` follow-up.)* | [ADR-PC-005 §P1](./ADR-PC-005-dr-rto-rpo.md) | benchmark (CI Integration / HA overlay) | `SYNC_REPL_APPEND_COST` | Planned |
| AUTH-1 | A real-time authorization is answered **synchronously** on the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface, and a **replayed** authorization command id returns the **original** verdict (same `hold_id`) with **no second `HoldPlaced` append**. It carries the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) idempotency guarantee onto the payment hot path; the test is written with the conta à ordem family implementation (bd `babelstone-xvcx`). | [ADR-PC-034 §Decision](./ADR-PC-034-realtime-authorization-technique.md) | integration (Testcontainers) | `AUTHORIZATION_SYNC_IDEMPOTENT` | Planned |
| BULK-1 | A **bulk cross-cutting operation registers a frozen target universe, drains it idempotently, and resumes mid-run** — registering a job snapshots its matched set into the work-table (one immutable plan, [ADR-PC-035 §P1](./ADR-PC-035-bulk-operations-execution-pattern.md)); a background drainer applies it in bounded `FOR UPDATE SKIP LOCKED` batches with the per-instance store-only event appended **idempotently** on the deterministic `(action_id, instance_id)` command id (reusing `ENGINE_COMMAND_IDEMPOTENT`, row 19; §P2–§P3); **per-item failures are isolated** (one `Failed` item never aborts the job and is selectively retryable as a no-op-safe re-run, §P5); and a host restart mid-run **resumes from `Pending`** producing the correct `{total, applied, skipped, failed, pending}` counts with **no double-append**. Governs the four cross-cutting operations (`PackVersionMigrated`/`SchemaVersionMigrated`/`FundsHeld`/`AccountFrozen`) that ride the runner as adapters. *(Planned: the gate is named and the Test ID reserved; the integration test is written with the `BulkOperationService`/`BulkOperationDrainer` implementation, bd `babelstone-qpiw.3`, against a real PostgreSQL. The cross-cutting per-instance dedupe rides the already-`Live` `ENGINE_COMMAND_IDEMPOTENT`, row 19.)* | [ADR-PC-035 §P1–§P5](./ADR-PC-035-bulk-operations-execution-pattern.md) | integration (Testcontainers) | `BULK_OP_REGISTER_DRAIN_COMPLETE` | Planned |
| MOVEMENT-2 | An **`Originated` cash leg cannot double-move** — the substrate-owned settlement saga routes it as a downstream saga step, so it inherits the ACL's `(operation_type, …, external_reference)` idempotency key + the stable, process-id-derived external reference (a reissue presents the SAME reference, so even a silently-executed-then-not-executed-clearance worst case is deduped at the Core), and a PUBLISHED leg is never re-sent — NEVER the bypassing eager `SettleAsync` call (ADR-PC-032 §Decision slot 4 / commitment 2). Registered here `Live` as ADR-PC-032's `Planned` commitment 2 flips on its gate landing (bd `babelstone-t7o3.15`). *(Live: `MovementCashLegIdempotentWireMockIntegrationTests` (`Babelstone.Orchestrator.Tests`) stands up a WireMock Core ACL and drives the SettlementProcess debit + credit cash legs through the real `SagaCommandDispatchDrainer` — proving each is delivered ONCE with the row's `message_id` Idempotency-Key, a PUBLISHED leg is never re-sent (no double-move), and a reissued debit presents the IDENTICAL stable `CoreHoldRef` external reference; runs in CI's orchestrator Integration lane.)* | [ADR-PC-032 slot 4 / commitment 2](./ADR-PC-032-money-movement-primitive.md) | integration (gated-settlement, WireMock Core) | `MOVEMENT_CASH_LEG_IDEMPOTENT` | Live |
| MOVEMENT-3 | **The step-up-SCA gate on the `Originated` cash leg cannot be bypassed** — the settlement-leg analogue of `MCP_SCA_GATE_CANNOT_BYPASS` (MCP-2). An irreversible saga-driven money-mover (a maturity / coupon credit, an early-termination payout) refuses to settle on the agent's word: the substrate **attests** — it threads the gateway-attested `acr`/`auth_time` from the Movement-bearing event's CloudEvents headers (the populate hop, ADR-PC-032 §A8) onto the cash leg's `saga_outbox` row and the dispatcher re-emits them as `X-SCA-Acr`/`X-SCA-Auth-Time` — and the **RECEIVER** (the Core ACL settlement leg) is the deny point, re-checking freshness against `SCA_MAX_AGE` (300 s) **at the settlement-dispatch instant** (NOT inherited from saga entry). A cash leg with **no** SCA proof or a **stale** `auth_time` is `422 SCA_REQUIRED` **before** `ConfirmDebit`/`ConfirmCredit` — no cash moves; the leg parks/compensates (ADR-PC-029 slot 5). The substrate never denies (attest-not-deny, ADR-IC-006 §P2 / ADR-PC-019 §P2 / ADR-IC-018 §D2). *(Live: `SettlementLegStepUpScaIntegrationTests` (`Babelstone.Orchestrator.Tests`) auto-starts the `SettlementProcess` off a maturity Movement-bearing event and drives the credit cash leg through the real `SagaCommandDispatchDrainer` against an in-process Core ACL stub reproducing the receiver's fail-closed verdict — proving a no-SCA and a stale-SCA money-mover is refused `422` and terminal FAILED before the credit confirms, the attested claims ARE forwarded on the stale case, and a fresh-SCA leg settles PUBLISHED; runs in CI's orchestrator Integration lane.)* | [ADR-PC-032 §A7/§A8](./ADR-PC-032-money-movement-primitive.md) / [ADR-IC-010 §A11–A12](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) | integration (gated-settlement, WireMock Core) | `SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS` | Live |
| LCD-1 | **A driven recurring lifecycle command is number-pinned idempotent** — the lifecycle-command driver derives each command's idempotency key SERVER-side from `(instance_id, command_kind, stable_occurrence_key)`, never caller-supplied; for a recurring installment the occurrence key is the **stable installment number**, never the due-date, so a re-dated or backfilled retry of occurrence N reuses the same key and appends **one** money leg via `command_dedup` (the legality gate gives a repeatable `PayInstallment` from `Active` no backstop — the key is the only guard). The write-side companion to `ENGINE_COMMAND_IDEMPOTENT` (row 19) for the clock-driven driver. *(Live: the Layer-1 foundation is built — the loan installment endpoint (`PayInstallmentAsync`) no longer takes a caller `Idempotency-Key`; it derives the number-pinned key via `Babelstone.Engine.Hosting.LifecycleCommandKey` (bd `babelstone-6cpq.1`). `LoansApiIntegrationTests.LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT_a_redated_retry_of_an_installment_dedupes_to_one_leg` is green on a real PostgreSQL: firing #1 over HTTP records the command_dedup receipt under EXACTLY `LifecycleCommandKey.Derive(loan, "pay_installment", 1)`, and a re-dated retry of occurrence 1 carrying that same number-pinned key on a DIFFERENT due-date is swallowed by `command_dedup` (`DuplicateCommandException`, the original head, no second `LoanInstallmentPaid`); runs in CI's Engine.Api Integration lane. The automated Layer-2 driver + the deposit-maturity occurrence remain follow-ups.)* | [ADR-PC-036 §Decision 3](./ADR-PC-036-lifecycle-command-driver.md) / [ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md) | integration (Testcontainers) | `LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT` | Live |
| LCD-2 | **The lifecycle driver never outruns de-settled settlement** — for a recurring schedule the driver fires occurrence N+1 only when occurrence N's `Originated` cash leg ([ADR-PC-032](./ADR-PC-032-money-movement-primitive.md)) is not parked in `HUMAN_INTERVENTION_REQUIRED`; since the engine advances a loan's paid-count on the `LoanInstallmentPaid` **event** (not on settled cash), an automated catch-up after an outage cannot advance the paid-count past collected cash. Maturity (one-shot) needs no such gate. | [ADR-PC-036 §Decision 4](./ADR-PC-036-lifecycle-command-driver.md) | integration (Testcontainers) | `LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE` | Planned |

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

**Epic K reconciliation (K.1).** The first four `OBS-*` rows (OBS-1–OBS-4) land with K.1 (`babelstone-rzcl`),
which ships the shared `ActivitySource` + `babelstone.*` attribute contract
(`Babelstone.Telemetry`), the product-semantic spans in the impure runtime shell, and
the OTLP + resource wiring on both .NET hosts. `OBS_RESOURCE_ATTRS` (OBS-1) and
`OBS_SPAN_PRODUCT_SEMANTICS` (OBS-2) are `Live`: their Docker-free fitness tests
(`ResourceAttributeTests`, `TelemetrySpanTests` in `Babelstone.Engine.Tests`) exist,
pass, and run in CI's `engine` job (the non-Integration tier). `OBS_NO_PII_ATTRS`
(OBS-3) is `Live` on its **runtime emit-time guard** (bd njt2.9–2.11), the load-bearing leg:
`AddBabelstonePiiGuard` (`Babelstone.Telemetry.Hosting`) registers a `BaseProcessor<Activity>`
+ `BaseProcessor<LogRecord>` + a metric-View TagKeys allowlist on every host's trace / log /
metric provider, so a span tag, structured-log field, or metric dimension whose key is outside
the admitted `babelstone.*`/semantic-convention tier is stripped AS IT IS EMITTED, before export
(proven by `BabelstonePiiGuardTests`, the `engine` lane). This is the control that earns the row
`Live`, because every real telemetry attribute is RUNTIME-valued — a compile-time check sees none
of them. The runtime structural no-PII assertion in `TelemetrySpanTests` is retained as the unit
companion. The `NoPiiTelemetryAttributeAnalyzer` (BENG005, `Babelstone.Engine.Analyzers`) is a
**secondary build-time tripwire** only: it flags a *literal* PII key/value hard-coded at a span
call site (exercised by `NoPiiTelemetryAttributeAnalyzerTests`), a shape that fires on none of the
real sites — so the analyser can never honestly flip the row on its own (bd njt2.12); it is the
cheap backstop, not the gate. The gate column reads `unit / analyser / runtime` accordingly.
The **span-attribute pseudonymization** half of OBS-3 (ADR-IC-016 plane iii §8 — where a span
must reference a customer, it carries a salted one-way hash under `babelstone.subject_pseudonym`,
never the raw `client_id`) lands with `babelstone-njt2.2`: the `ClientPseudonym.Of(clientId, salt)`
HMAC-SHA-256 derivation in `Babelstone.Telemetry` plus its `ClientPseudonymTests` pins
(deterministic, salt-dependent, one-way, fail-loud on a missing salt). It extends the same
OBS-3 commitment the runtime guard holds `Live`.
`OBS_TRACEPARENT_PROPAGATION` (OBS-4) stays `Planned`: cross-process `traceparent`
propagation over the durable bus is documented here but deferred (the K.1 SCOPE-OUT) to
the bus-relay work, and its lane is the deferred Testcontainers Integration tier.
`OBS_TRACE_ID_SURFACED_HTTP` (OBS-5, added 2026-06-14 with bd `babelstone-2dex`) is `Live`:
the engine host wires `AddAspNetCoreInstrumentation()` so the inbound request is a SERVER
span that adopts an inbound `traceparent` (the `deposit.*` spans nest under it instead of
starting as roots) and returns the active trace id to the caller on the `X-Trace-Id`
response header. Its fitness test (`DepositsApiTracingIntegrationTests` in
`Babelstone.Engine.Api.Tests`) runs in the Testcontainers Integration tier. It is a strict
in-process subset of OBS-4 — the synchronous HTTP boundary only — so OBS-4 stays `Planned`
for the bus/orchestrator legs.

**ACTUAL→INTENDED reconciliation (2026-06-13, PR #163).** Four rows were added under the
growth provision as [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) (engine command
ingress) and [ADR-IC-017](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md)
(integration-event promotion) moved from `Proposed` to `Accepted`, migrating their
load-bearing commitments out of the ADRs' inline tables into this catalogue per the
acceptance rule ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md));
both ADRs' `## Verifiable commitments` sections now *reference* these rows by Test ID.
All four started `Planned` — decision-time gates whose tests are written with the
implementing issues. `ENGINE_COMMAND_IDEMPOTENT` (row 19) is now **Live** — the engine
idempotent command endpoint shipped with bd `babelstone-t7o3.6` (the impl issue refiled from
the `type=decision` `babelstone-t7o3.5`); `ENGINE_COMMAND_PACT` (row 20) stays `Planned` and
lands with the dispatcher (bd `babelstone-t7o3.3`), the consumer-driven Pact's real consumer.
`INTEGRATION_EVENT_CATALOG_GATED` (row 21) and `NO_UNCATALOGUED_EVENT_ON_BUS` (row 22) are now
**Live** — the catalog-gated-relay implementing issue (ADR-IC-017 §P1/§P3) shipped the append-side
gate in `AggregateRuntime` (driven by the injected family-agnostic `IIntegrationEventCatalog`), its
Testcontainers relay test, the runtime-anchored reverse-orphan fitness test, and the `contracts`-job
shell mirror. Rows 21–22 are the first **in-house ADR-IC**
engine-boundary commitments catalogued here (the `OBS-*` rows aside), per the
[§P11](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) in-house-estate reach.

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
emitted-event schema surface — it did **not** itself flip a row: at that 2026-06-10
reconciliation `OBS-3` (`OBS_NO_PII_ATTRS`) stayed `Planned` (its mechanical enforcement was then
unwritten). This is the bus-surface companion to that telemetry-surface check; it honours
[ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) /
[ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) Decision 1 (the envelope carries
no PII; references are resolved internally). OBS-3 has since flipped to `Live` on its **runtime
emit-time guard** (`AddBabelstonePiiGuard`, bd njt2.9–2.11 — the load-bearing leg across
traces/logs/metrics; see the Epic K reconciliation above), with `TelemetrySpanTests` retained as
the unit companion and the BENG005 `NoPiiTelemetryAttributeAnalyzer` as a secondary build-time
tripwire (it could never flip the row alone — it fires on no real, runtime-valued site, bd njt2.12).

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

**MCP edge-boundary reconciliation (2026-06-15, bd babelstone-xma4).** Row MCP-1
(`MCP_WRONG_RESOURCE_TOKEN_REJECTED`) was added under the growth provision and lands
**`Live`**. It closes the deliberate, visible gap [ADR-IC-010 §Verifiable-commitments](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
reserved ("No Test ID is wired yet … to be catalogued when the MCP server is
implemented"): bd `babelstone-e50n` shipped the secured MCP edge, so the wrong-resource
(RFC 8707 audience-binding) refusal now resolves to running tests at both layers. The
**app-layer** leg is `mcp-server/tests/test_auth.py`'s
`test_MCP_WRONG_RESOURCE_TOKEN_REJECTED_wrong_aud_is_rejected_401_audience_mismatch`
(the `AudienceMiddleware` returns `401` + code `AUDIENCE_MISMATCH` + a `WWW-Authenticate`
`resource_metadata` pointer for a wrong-`aud` token), which runs in CI's `mcp-server`
job (`.github/workflows/ci.yml`). The **Kong-edge** leg is the static contract in
`scripts/kong-config-check.sh` (the `AUDIENCE_MISMATCH` / `kong.response.exit(401` /
`resource_metadata` assertions on the `/mcp` route), which runs in CI's `infra` job
(the "Validate Kong edge config" step invokes `scripts/kong-config-check.sh`). The
§P6 coverage checker resolves the `Live` row by literal grep of the Test ID in the renamed
pytest method under `mcp-server/` (the `mcp-server` subtree is in `CODE_DIRS`, `.py` in
`CODE_INCLUDES`); the Kong-layer leg carries the Test ID as a traceability comment. This is
an in-house **ADR-IC** commitment catalogued here (joining the existing ADR-IC estate rows —
e.g. the ADR-IC-007 observability rows and the ADR-IC-017 bus event-promotion rows 21–22),
per the [§P11](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) in-house-estate reach. The companion `outputSchema`-mandatory gap ADR-IC-010 also reserves
stays uncatalogued — out of scope for this change.

**MCP step-up-SCA gate reconciliation (2026-06-23, bd babelstone-u75l).** Row MCP-2
(`MCP_SCA_GATE_CANNOT_BYPASS`) was added under the growth provision and lands **`Live`**.
It catalogues the §P8 invariant the [ADR-IC-010 Verifiable-commitments](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
section noted was "realised by … the saga orchestrator … governed there, not as this ADR's own
catalogue rows" — but the [2026-06-20 §A7–§A9 amendment](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
moved the gate onto the **engine-direct** money-mover path (an engine-returned `422 SCA_REQUIRED`,
refreshed-Bearer re-entry), so the no-client-side-bypass posture is now an engine commitment with a
catalogue home. PR #274 (bd `babelstone-ziu3.5`, hoisted into an endpoint filter by bd `babelstone-45c4`)
shipped the real gate; the **engine** leg resolves to `DepositsApiIntegrationTests`
(`Babelstone.Engine.Api.Tests`): `Mature_without_any_SCA_proof_is_422_SCA_REQUIRED_and_does_not_settle`
(no `X-SCA-Acr`/`X-SCA-Auth-Time` ⇒ `422 SCA_REQUIRED`, stream still carries only `DepositConstituted`)
and `Mature_with_a_stale_SCA_auth_time_is_422_SCA_REQUIRED` (an `auth_time` beyond
`ScaPrecondition.MaxAgeSeconds` ⇒ `422`), with `Mature_with_fresh_attested_SCA_settles_normally`
as the positive control. The class carries `[Trait("Category", "Integration")]`, so the gate runs
in CI's `engine` job Testcontainers Integration lane (`--filter "Category=Integration"`,
`.github/workflows/ci.yml`) — meeting the "exists, passes, **and runs in CI**" bar for `Live`. The
§P6 coverage checker resolves the row by literal grep of the Test ID under `engine/` (in `CODE_DIRS`,
`.cs` in `CODE_INCLUDES`), carried as a traceability comment beside the test block. The MCP
step-up-then-retry half (the agent's elicitation re-entry) is the companion in
`mcp-server/tests/test_server.py`; the load-bearing settlement gate is the engine leg this row
anchors. An in-house **ADR-IC** commitment catalogued here (joining the ADR-IC-007/017 and MCP-1
estate rows), per the [§P11](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
in-house-estate reach. ADR-IC-010's `## Verifiable commitments` references this row by Test ID as
the single source of truth for its status.

**Saga-substrate family-agnosticism reconciliation (2026-06-16, bd babelstone-t7o3.12).** Rows
ORCH-1 (`ORCHESTRATOR_FAMILY_AGNOSTIC`) and ORCH-2 (`ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA`) were
added under the growth provision and land **`Live`** — the orchestrator-estate cousins of the engine's
rows 12/12a. [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) made
the saga orchestrator a family-agnostic SUBSTRATE with concrete sagas as pluggable family
`.Orchestration` modules (operationalising [ADR-IC-003 §P3](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md),
mirroring [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)); bd `babelstone-t7o3.12`
realised it by extracting `ConstitutionProcess` into `Babelstone.Families.TermDeposit.Orchestration` and
adding the two gates. ORCH-1 is the `.csproj`-reference twin (the substrate references no `families/**`);
ORCH-2 is the type-level twin (the substrate assembly defines no concrete
`ISagaStateMachine`/`IResultEventBridge`/`ISagaCommandRouter`), catching a saga typed inside the substrate
that no `.csproj` reference would. Both are `OrchestratorFamilyAgnosticTests`
(`Babelstone.Orchestrator.Tests`), infrastructure-free, running in CI's orchestrator unit lane
(`Category!=Integration`) — meeting the "exists, passes, **and runs in CI**" bar. ADR-IC-018's
`## Verifiable commitments` reference these rows by Test ID as the single source of truth for their
status. An in-house **ADR-IC** commitment catalogued here (joining the ADR-IC-007/017 and MCP-1 estate
rows), per the [§P11](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) in-house-estate reach.

## Coverage by pyramid level

This is the shape [ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
predicts — most invariants already have a home on the existing pyramid, none
spawns a parallel suite:

| Level / mechanism | Test IDs |
|---|---|
| Unit | `OBS_RESOURCE_ATTRS`, `OBS_SPAN_PRODUCT_SEMANTICS` |
| Unit + analyser | `MONEY_BOUNDARY_FIXTURES` |
| Unit + analyser + runtime guard | `OBS_NO_PII_ATTRS` (runtime emit-time guard is the load-bearing leg; analyser is a secondary tripwire) |
| Analyser / CI gate | `DETERMINISM_GATE`, `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` (analyser + contract) |
| Architecture / dependency + type assertion (CI) | `ENGINE_FAMILY_AGNOSTIC`, `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC`, `ORCHESTRATOR_FAMILY_AGNOSTIC`, `ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA` |
| Integration (Testcontainers) | `ES_ATOMIC_APPEND_OUTBOX`, `REPLAY_PIN_PER_EVENT`, `BATCH_INGEST_IDEMPOTENT`, `OBS_TRACEPARENT_PROPAGATION`, `OBS_TRACE_ID_SURFACED_HTTP`, `READ_YOUR_WRITES_FOLD_ON_TOKEN`, `MCP_SCA_GATE_CANNOT_BYPASS`, `BULK_OP_REGISTER_DRAIN_COMPLETE` |
| Contract / saga | `GL_POST_FLAG_NEVER_GATES`, `NOTIFY_POST_FLAG_NEVER_GATES`, `IFRS9_POST_FLAG_NEVER_GATES`, `CONSTITUTION_PRECONDITION_REFUSAL`, `MCP_WRONG_RESOURCE_TOKEN_REJECTED` (Kong static + app pytest) |
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
