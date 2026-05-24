# ADR-PC-010: Engine Implementation Language and Framework — .NET 9 + Hand-Rolled Event-Sourcing Core

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store; the deferred library question is filled here — *hand-rolled module*), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) (CUE + Go validator), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (pack manifest format), [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) (Redpanda), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro + Confluent SR), [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) (in-house event-driven orchestrator — **honoured directly**, no amendment), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (custom polling publisher — **the outbox the engine implements**), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (PostgreSQL read model), [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) (OpenTelemetry / Grafana LGTM), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers), [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) (Python MCP — engine remains polyglot at the system level) |
| Resolves | bd `archie-10r.11` (ADR-PC-010: Engine implementation language and process model) |

---

## Context

The engine ([01 product-architecture](../01-product-architecture.md)) is a single deployable, event-sourced, configuration-driven runtime over a PostgreSQL source of truth ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)). The integration estate is fixed (Redpanda broker, PostgreSQL state, Kong gateway, OpenTelemetry, Avro+Confluent SR at the bus boundary, Python MCP sibling service). The brief commits to "one codebase, one set of images" ([01 §6 Deployment](../01-product-architecture.md)) and to integer-cents EUR math ([02 §2.2](../02-v1-scope-term-deposits.md)). The team is 1–2 people with moderate event-sourcing experience.

This ADR makes **two coupled decisions**:

1. **Language** — the engine's implementation language and process model (the deliverable of [bd archie-10r.11](../04-open-questions.md), whose criteria foreground LLM-codability, native decimal correctness, event-store-client maturity, operability at 1–2 people, and the PT talent pool).
2. **Framework approach** — whether the event-sourcing concerns (event store, outbox, projections, snapshots, saga orchestration) are delegated to a consolidator framework (e.g. Marten + Wolverine on .NET) or **hand-rolled** as thin, fully-owned modules on PostgreSQL. [ADR-PC-001 §Decision](./ADR-PC-001-event-store-technology.md) explicitly deferred this "library or framework on top of PostgreSQL — hand-rolled module, Marten (if .NET), eventsourcing-pg (Python), or equivalent" choice to this ADR.

**Language candidates** ([bd archie-10r.11](../04-open-questions.md)): C# (.NET 9), Go 1.23, Elixir 1.18. Rust is excluded (the bd criteria flag borrow-checker friction against the LLM-codability requirement); TypeScript-on-Node is excluded (float-coercion risk against the decimal-correctness requirement and a thin event-sourcing ecosystem); JVM languages are out of scope per the brief.

**Framework-approach options**: (i) hand-rolled thin module on PostgreSQL; (ii) Marten 7.x + Wolverine 3.x consolidator (the .NET event-sourcing framework pair).

---

## Evaluation — Language

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence (runtime + load-bearing libs) | Verdict |
|---|---|---|
| C# (.NET 9) | .NET 9: MIT. Npgsql (PG driver): PostgreSQL/BSD-style. Confluent.Kafka .NET: Apache 2.0. OpenTelemetry .NET SDK: Apache 2.0. YamlDotNet: MIT. Testcontainers .NET: MIT. *No event-sourcing framework is load-bearing — see Framework decision.* | **Pass** |
| Go 1.23 | Go: BSD-3. pgx: MIT. Confluent/franz-go + hamba/avro: MIT/BSD. OTel Go: Apache 2.0. | **Pass** |
| Elixir 1.18 | Elixir/OTP: Apache 2.0. Ecto/Postgrex/Broadway: Apache 2.0. | **Pass** |

All three pass F1. Note that by hand-rolling the core (Framework decision below), the engine carries **no** event-sourcing-framework licence surface in any language — the only data-tier dependency is the PostgreSQL driver.

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Regulatory obligations are identical across languages: per-subject field-level crypto-shredding inside the Avro payload ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md), [event-store §6.2](../feature-design-event-store-projections.md)); DORA RTO/RPO inherited from PostgreSQL backups + replication; PSD2 audit trail from the append-only event log. None is structurally disqualified.

| Candidate | Verdict | Note |
|---|---|---|
| C# (.NET 9) | **Pass** | OpenTelemetry .NET first-party; AES-GCM per-subject envelope is application code; PG PITR/replication inherited. |
| Go 1.23 | **Pass** | OTel Go first-class; application-level AES-GCM envelope. |
| Elixir 1.18 | **Pass** | OTel via `:telemetry`; application-level crypto envelope. |

---

### Soft criteria — Language

#### C# (.NET 9) — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Single .NET 9 process, single `appsettings.json`, single Dockerfile. CLR image weight (~120 MB) is a real but acceptable cost for non-edge deployment. Testcontainers .NET is mature (PostgreSQL, Redpanda, WireMock, Toxiproxy modules all first-party), giving the highest integration-test fidelity of the three. The PT .NET talent pool is the largest of the candidates, easing eventual human hand-off.

**S2 · Ecosystem coherence.** First-party Confluent.Kafka .NET + Confluent.SchemaRegistry for the Avro+SR bus boundary ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)); OpenTelemetry .NET SDK with full LGTM compatibility ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)); Npgsql for the PostgreSQL source of truth; AspNetCore for the Kong-fronted REST/SSE endpoints. The one out-of-ecosystem dependency is the CUE validator subprocess ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)) — a contained seam, not pervasive glue.

**S3 · Exit cost.** Low-to-medium and, critically, **lower because the core is hand-rolled** (see Framework decision). The engine speaks plain SQL (the [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) `events` table), plain Avro, plain HTTP, plain OTLP, and plain YAML/CUE — no framework data formats. A future re-implementation in another language reads the same `events` table and the same language-agnostic pack format.

**S4 · Community and longevity.** .NET 9 is MIT, foundation-governed (.NET Foundation), with a multi-decade track record and the largest training-corpus footprint of the three candidates (material for the LLM-codability criterion). With no event-sourcing framework load-bearing, there is no single-vendor library-longevity risk to carry.

**Decisive language reasons:** (1) **LLM-codability** — the engine is authored primarily by an LLM ([bd archie-10r.11](../04-open-questions.md)); .NET has the deepest training-corpus representation, strong compile-time guardrails, convention-heavy idioms, and tight lint/format/test tooling. (2) **Native decimal correctness** — C# is the only candidate shipping a built-in 128-bit `System.Decimal`; the Decimal→Cents boundary rounding (§P1–§P2) is type-level rather than library- or context-level. (3) **Ecosystem coherence** with the fixed integration estate.

#### Go 1.23

**S1.** Single static binary; lightest footprint. No OTP-equivalent supervision — restart/backoff/circuit-breaking is application code (Kubernetes pod-restart + bespoke loops). **S2.** OTel Go and Testcontainers Go are mature; Avro+SR needs `confluent-kafka-go` (cgo) or a pure-Go path (`franz-go` + `hamba/avro`). **CUE is Go-native** — the [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) validator would embed in-process in three lines, eliminating the one out-of-process seam the .NET choice accepts. **S3.** Lowest exit cost of the field (no vendor formats anywhere). **S4.** pgx, OTel-Go above threshold; large corpus though smaller than .NET.

**Decisive reason for not choosing:** Go's genuine advantage *grew* once CUE was adopted (the validator is Go; an all-Go engine would have zero validator seam). But the language decision rests on the [bd archie-10r.11](../04-open-questions.md) criteria as weighted: **LLM-codability** (deepest .NET corpus and strongest compile-time guardrails) and **native `System.Decimal`** (Go relies on `shopspring/decimal` or `apd` — library- or context-level rounding discipline, not type-level). The CUE seam the .NET choice pays for is contained to commit-time and pack-load-time invocations ([ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md)), which does not outweigh the LLM-codability and decimal-type advantages. Go remains the cleanest re-implementation target if .NET ever becomes untenable, and the `events`-table + pack contracts keep that path open.

#### Elixir 1.18

**S1.** BEAM `mix release`; OTP supervision is a genuine advantage for stateful processes — but the engine's request path is deterministic and stateless ([event-store §5.1–§5.3](../feature-design-event-store-projections.md)), muting the differentiator. PT BEAM talent pool is far smaller than .NET. **S2.** Phoenix/Ecto/Broadway compose, but Testcontainers Elixir is materially thinner (no first-party Redpanda/WireMock/Toxiproxy modules) against the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) mandate. **S3.** Moderate. **S4.** `ericmj/decimal` is solid but decimal is library-level, not built-in.

**Decisive reason for not choosing:** thinner LLM training corpus (a named drag in the [bd archie-10r.11](../04-open-questions.md) criteria), Testcontainers thinness against the testing mandate, and the smallest PT talent pool — with no offsetting advantage now that the saga is hand-rolled (Elixir's Commanded/process-manager idiom is no longer in play).

---

## Decision

### Language: **C# (.NET 9)**, single deployable.

LLM-codability, native `System.Decimal` for type-level boundary rounding, and coherence with the fixed integration estate are the decisive forces. The engine is one process, one image, one configuration surface ([01 §6](../01-product-architecture.md)); the system stays polyglot only at the boundary (Python MCP per [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md); the Go CUE-validator binary per [ADR-PC-006](./ADR-PC-006-cue-schema-language.md)), never inside the engine.

### Framework: **hand-rolled thin event-sourcing core on PostgreSQL.** No Marten, no Wolverine as runtime dependencies.

The engine's source of truth, outbox, projections, snapshots, and saga orchestration are implemented as small, explicit, fully-owned modules against the contracts the existing ADRs already specify. The decisive reasons:

1. **A source-of-truth core for a regulated banking product should be fully controlled.** Hand-rolling means the team owns the append/load/replay/outbox/projection/snapshot/saga code outright — no library that "manages its own tables," no framework migration daemon, no upcaster machinery, no per-vendor lifecycle. For the most load-bearing component in the system, control and legibility outweigh the convenience of a consolidator.

2. **Hand-rolling is the path of *least* friction through the existing ADRs, not the most.** [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) already chose a **custom polling publisher** (`SELECT … FOR UPDATE SKIP LOCKED` → Redpanda) — the outbox was always going to be hand-rolled; a framework outbox would *replace* that decision. [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) already chose an **in-house event-driven application orchestrator, no third-party engine** — hand-rolling the saga is its literal intent. [ADR-PC-001 §P1–§P5](./ADR-PC-001-event-store-technology.md) already specify the entire module: the `events` table column contract, the one-transaction `append(stream_id, events, outbox_rows)` operation, append-only-by-role-privilege, the two indices, and the upgrade drill. The hand-rolled module *is* the implementation of a spec that already exists. By contrast, adopting Marten + Wolverine would have required **amending ADR-IC-003** (to permit a third-party saga library) and **re-interpreting ADR-IC-004** (Wolverine's outbox table vs the custom polling publisher) — net new ADR churn the hand-rolled path avoids entirely.

3. **`§10.4` is honoured under its actual reading.** [event-store §10.4](../feature-design-event-store-projections.md) ("battle-tested event store; no in-house build") draws its line at *infrastructure correctness* vs *pattern discipline*: "the team's moderate experience cannot absorb both event-sourcing-pattern discipline AND event-store-infrastructure correctness simultaneously." **PostgreSQL is the battle-tested, bought infrastructure** — durability, MVCC, crash recovery, WAL, replication, PITR are not hand-rolled. What is hand-rolled is the thin event-sourcing *application module* (append in a transaction, optimistic concurrency on `sequence_number`, ordered load, projection apply, outbox insert + poll) — which is *pattern discipline*, the thing §10.4 says the team can own. [ADR-PC-001](./ADR-PC-001-event-store-technology.md) already encodes this reading: its Candidate A is "library **or hand-rolled** implementation chosen at v1 build time," and its S3 notes the hand-rolled path carries the **lowest exit cost** ("no application-layer framework lock-in if the implementation is hand-rolled … if a heavier framework (Marten) is chosen, exit cost rises"). Hand-rolling is squarely within §10.4 as PC-001 interpreted it.

4. **The scope is small and bounded.** The hand-rolled core is not an event-store *engine*; it is a few hundred lines of SQL-and-dispatch against a fully-specified table. The risk surface is bounded by [ADR-PC-001 §P1–§P5](./ADR-PC-001-event-store-technology.md), exercised by the mandatory projection-rebuild drills ([event-store §7.2](../feature-design-event-store-projections.md), [§10.2](../feature-design-event-store-projections.md)), and validated against the synthetic v4-scale Q-AK load test that [ADR-PC-001](./ADR-PC-001-event-store-technology.md) already makes a v1 acceptance gate.

**Marten 7.x and Wolverine 3.x are retained as working reference implementations, not dependencies.** They are the canonical .NET expressions of exactly the patterns the engine hand-rolls — Marten's `mt_events` table shape and inline/async projection model; Wolverine's `IMartenOutbox.PublishAsync(event) + SaveChangesAsync()` one-transaction outbox seam; Wolverine `Saga` state-machine dispatch. The engine team studies these as the proven designs to mirror, and reimplements the minimal subset the engine needs against the [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) contract. They may also be used freely in throwaway spikes and learning prototypes. They do not ship in the engine.

**Rejected: Marten + Wolverine as the runtime framework.** Their genuine strength — the one-transaction event-append + outbox-write + saga-state seam — is real and is exactly what the hand-rolled module reproduces in ~one `append(...)` method against [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md). Adopting them would (a) introduce libraries that manage their own tables and lifecycles into the system's source-of-truth core, (b) carry the JasperFx correlated single-vendor S4 risk (both libraries on one commit boundary), and (c) require amending ADR-IC-003 and re-interpreting ADR-IC-004. The convenience does not justify ceding control of the core or incurring the cross-ADR churn.

**Rejected language alternatives: Go** (LLM-codability and native-decimal lose to .NET despite Go's now-stronger CUE-native advantage; kept as the cleanest future re-implementation target) and **Elixir** (thinner corpus, Testcontainers thinness, smallest talent pool, no remaining saga-idiom advantage once the saga is hand-rolled).

---

## Consequences

**What this choice makes easier:**

1. **Full control of the source of truth.** Every byte of the append/load/replay/outbox/projection/snapshot/saga path is engine-team code against a known SQL contract. No framework upgrade can change the storage shape; no library daemon runs unsupervised against the `events` table.
2. **One-transaction commit, owned outright.** `append(stream_id, events, outbox_rows)` ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)) writes the event rows and the outbox rows in one local PostgreSQL transaction via Npgsql — the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) polling publisher reads the outbox. No dual-write, no framework intermediary.
3. **No cross-ADR churn.** ADR-IC-003 and ADR-IC-004 stand unamended; the engine implements them rather than substituting a framework for them.
4. **Lowest exit cost** ([ADR-PC-001 §S3](./ADR-PC-001-event-store-technology.md)) — no framework lock-in; a future migration reads the plain `events` table.
5. **Type-level money correctness** — `System.Decimal` + a typed `Money` record + a Roslyn analyser (§P1–§P2) give boundary rounding a compile-time guardrail.
6. **Highest-fidelity tests** — Testcontainers .NET (real PostgreSQL, Redpanda, WireMock, Toxiproxy) exercises the hand-rolled core directly; the projection-rebuild drills run against the same fixture.

**What this choice makes harder or impossible:**

1. **The team owns event-sourcing correctness.** No framework provides the inline/async projection runtime, snapshot lifecycle, or saga dispatch for free — each is engine code. Mitigation: the scope is bounded by [ADR-PC-001 §P1–§P5](./ADR-PC-001-event-store-technology.md); Marten/Wolverine are the reference designs; the rebuild drills ([event-store §7.2](../feature-design-event-store-projections.md)) and the Q-AK load test are the correctness gates.
2. **No free projection daemon / upcaster machinery.** Projection update modes ([ADR-PC-002](../04-open-questions.md)) and snapshot lifecycle ([ADR-PC-003](../04-open-questions.md)) are explicit engine modules (their own ADRs). This is a feature for control and a cost for effort.
3. **CLR image weight (~120 MB)** vs Go (~10–20 MB). Acceptable for non-edge deployment.
4. **One accepted out-of-process seam** — the CUE validator subprocess ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)). The engine itself is single-runtime .NET; the system is polyglot only at the boundary (MCP Python, Go validator binary).
5. **§10.4 prose tension to reconcile in the docs.** [event-store §10.4](../feature-design-event-store-projections.md) reads, in isolation, as "no in-house build." This ADR honours it under the infrastructure-vs-pattern reading that [ADR-PC-001](./ADR-PC-001-event-store-technology.md) already adopted; a one-line clarification to §10.4 (naming the hand-rolled-module path explicitly, as PC-001 does) is carried as Open Action #4 so a future reader does not trip on the literal phrasing.

---

## Implementation Principles

### P1 — `Money = long Cents` is the storage type; `decimal` is a boundary computation type only

Money in domain code, event payloads (`principal_cents`, `gross_interest_cents`, … per [02 §2.4](../02-v1-scope-term-deposits.md)), projection rows, snapshot state, saga state, and rate resolution is the typed record `record Money(long Cents)` over a signed 64-bit integer (max ~€92 quintillion). `System.Decimal` is **not** the money substrate; it enters only at boundary call sites, each using `MidpointRounding.ToEven` (HALF_EVEN per [pack §3.3 currency](../feature-design-configuration-surface.md), [02 §2.2](../02-v1-scope-term-deposits.md)):

1. **External rate input → internal representation** (decimal percentages from authoring tooling → integer basis points) at pack-load.
2. **Accrual computation** — `principal_cents × rate_bps × days / (basis_days × 10000)` computed in `decimal` (or pure-integer with explicit scale), rounded once to `long Cents` at event emission via `Money.FromCents(decimal d) => new Money((long)Math.Round(d, 0, MidpointRounding.ToEven))`.
3. **Display / regulatory report** — `Money.Cents / 100m` formatting; read-only, no money math.

A Roslyn analyser bans raw `Math.Round(decimal, …)` outside `Money.FromCents`, bans `decimal` fields outside the `Babelstone.Money` namespace, and flags operator overloads returning `decimal` from `Money` inputs. Because serialization is Avro ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) and the engine controls it (no framework JSON path), there is no third-party serializer rounding to police.

### P2 — HALF_EVEN rounds once, at the Decimal → Cents boundary

Compute the full expression in `decimal` at maximum precision; round exactly once at the final `Money.FromCents` call site. No intermediate rounding inside `decimal` arithmetic (accumulating roundings drifts). A sealed fixture corpus of `(input, expected)` pairs — midpoints (100.5¢→100¢, 101.5¢→102¢), large magnitudes (€1e8+ over multi-year terms), small magnitudes (sub-cent daily accruals) — is the boundary-rounding test, replayable per [surface §3.9](../feature-design-configuration-surface.md).

### P3 — The hand-rolled event store implements the ADR-PC-001 §P1 contract directly

The `events` table is created with exactly the [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) columns (`event_id`, `stream_id`, `sequence_number`, `event_type`, `event_schema_version`, `family`, `partition_key`, `pack_version`, `schema_version`, `valid_time`, `transaction_time`, `causation_id`, `correlation_id`, `actor`, `payload`, `payload_schema_id`) — no `mt_*` library columns, no library-internal additions. The data-access layer exposes exactly two public operations: `append(stream_id, expectedVersion, events, outboxRows)` (one transaction, optimistic concurrency on the unique `(stream_id, sequence_number)`, [§P2](./ADR-PC-001-event-store-technology.md)) and `load(stream_id)` (ordered read). Append-only is enforced by the application role lacking `UPDATE`/`DELETE` ([ADR-PC-001 §P3](./ADR-PC-001-event-store-technology.md)); the two indices are created per [§P4](./ADR-PC-001-event-store-technology.md). **Reference:** Marten's `mt_events` shape is the studied design; the engine reproduces the minimal contract subset, not Marten's table set.

### P4 — Sagas are hand-rolled state machines persisted in the engine's PostgreSQL

The engine's internal orchestrations (renewal, moratorium application, legacy-SoR-transition; and its participation as a step in the integration constitution saga per [01 §6](../01-product-architecture.md) and [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) are explicit state machines whose state is a row in a `saga_state` table in the same database as `events` and `outbox`. Saga progression commits transactionally with the event append and outbox write in one local transaction ([§P3](#p3--the-hand-rolled-event-store-implements-the-adr-pc-001-p1-contract-directly), [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)). Compensations are explicit states ([ADR-IC-003 §P-series](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)); the identity trio (`correlation_id`, `causation_id`, `message_id`) rides every emitted message. **Reference:** Wolverine `Saga` dispatch is the studied design; the engine reproduces a minimal in-process dispatcher, not Wolverine.

### P5 — Determinism and forward-only discipline are CI-enforced engine code

Side-effect-free handlers ([event-store §5.3](../feature-design-event-store-projections.md), [§10.3](../feature-design-event-store-projections.md)) and forward-only event schemas ([event-store §5.4](../feature-design-event-store-projections.md), [§10.1](../feature-design-event-store-projections.md)) are enforced by CI gates the engine team owns. Because there is no framework upcaster facility, event evolution is additive-only by construction (new event types, new optional fields) — there is no `UpcastAsync`-style lambda surface to ban, which removes a class of risk the framework path carried.

---

## Open Actions

1. **Hand-rolled-core test harness** — the projection-rebuild drill ([event-store §7.2](../feature-design-event-store-projections.md)) and the Q-AK synthetic v4-scale load test ([ADR-PC-001](./ADR-PC-001-event-store-technology.md), [two-modes §5.6](../feature-design-two-modes-asymmetry.md)) are v1 acceptance gates for the hand-rolled append/replay path.
2. **Reference-study capture** — document the specific Marten (`mt_events`, projection modes) and Wolverine (`IMartenOutbox` one-transaction seam, `Saga` dispatch) patterns the engine mirrors, so the reference link is auditable.
3. **Roslyn analysers** — (a) ban raw `Math.Round` on `decimal`; (b) ban `decimal` fields outside `Babelstone.Money`.
4. **§10.4 clarification** — propose a one-line amendment to [event-store §10.4](../feature-design-event-store-projections.md) naming the hand-rolled-module-on-PostgreSQL path explicitly (consistent with [ADR-PC-001](./ADR-PC-001-event-store-technology.md) Candidate A), so the literal "no in-house build" phrasing does not mislead. (Doc-edit deferred pending owner sign-off — not made unilaterally in this ADR.)

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `MONEY_BOUNDARY_FIXTURES` — HALF_EVEN rounds once at the `Decimal → Cents` boundary, against a sealed golden corpus (§P1–§P2).
- `DETERMINISM_GATE` — a handler that reads the clock / does I/O / uses randomness fails the build (§P5).

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL event store; the deferred library question is filled here as *hand-rolled module*. Amendment block dated 2026-05-23 appended to PC-001. Status remains `Accepted`; the four invariants are preserved (they are the hand-rolled module's spec).
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — CUE schema language + Go validator; the one accepted out-of-process seam.
- [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) — pack manifest format (signed YAML in OCI, CUE-validated).
- [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — in-house event-driven orchestrator; **honoured directly** by §P4. No amendment.
- [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) — custom polling publisher; the outbox the engine's `append` writes and the publisher reads.
- [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — MCP stays Python; engine + MCP + Go validator polyglot at the system level.
- [event-store §10.4](../feature-design-event-store-projections.md) — "no in-house build"; honoured under the infrastructure-vs-pattern reading (see Decision reason 3, Open Action #4).
- [02 §2.2, §2.4](../02-v1-scope-term-deposits.md) — integer-cents EUR ledger; `Money = long Cents`; HALF_EVEN at the Decimal→Cents boundary.

---

*Decided 2026-05-23 by jhosm. Supersedes the prior .NET 9 + Marten + Wolverine iteration of ADR-PC-010 (removed before acceptance); Marten and Wolverine retained as working reference implementations, not runtime dependencies.*
