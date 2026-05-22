# ADR-PC-010: Engine Implementation Language and Framework — .NET 9 + Marten + Wolverine

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-05-22 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store; library deferral filled here with Marten), [ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md) (JSON Schema + NJsonSchema), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (pack manifest format), [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) (Redpanda), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro + Confluent SR), [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) (saga orchestrator — **amended 2026-05-22** to permit in-process saga libraries including Wolverine `Saga`), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (outbox pattern), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (PostgreSQL read model), [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) (OpenTelemetry / Grafana LGTM), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers), [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) (Python MCP — engine remains polyglot at system level) |
| Resolves | bd `archie-10r.11` (ADR-PC-010: Engine implementation language and process model) |

---

## Context

The engine described in [01 product-architecture](../01-product-architecture.md) is a single deployable, event-sourced, configuration-driven runtime. The integration estate is fixed (Redpanda broker, PostgreSQL state, Kong gateway, OpenTelemetry observability, Avro+Confluent SR at the bus boundary, Python MCP sibling service). The brief commits to "one codebase, one set of images" ([00 §5](../00-product-vision.md)) and to integer-cents EUR decimal math ([02 §2.2](../02-v1-scope-term-deposits.md)). The team is 1–2 people with moderate event-sourcing experience, and "no in-house event-store build" ([feature-design-event-store-projections §10.4](../feature-design-event-store-projections.md)) is a binding constraint.

This ADR picks the engine implementation **language** *and* the **ecosystem framework stack** together, recognising that for an event-sourced engine the framework choice (event-sourcing library, outbox library, saga library) is as consequential as the language choice itself. The decision was reached via a three-round adversarial workup with full round-robin rebuttals (advocates per stack, mutual cross-examination using `context7` MCP for version-sensitive verification, integrator synthesis) — workup outputs at `~/.claude/work/archie-10r-stack/` document the full evidence trail.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **C# (.NET 9) + Marten 7.x + Wolverine 3.x + NJsonSchema** | Stack-shaped framework consolidator on PostgreSQL; Marten = event store + projections + snapshots; Wolverine = transactional outbox + saga orchestration |
| B | **Elixir 1.18 (OTP 27) + Commanded 1.4.x + EventStore-postgres + Broadway** | BEAM with Commanded as Marten-class consolidator; EventStore-postgres ships its own PG-backed event store; Broadway for Redpanda |
| C | **Go 1.23 + pgx/v5 + Watermill + CUE + hallgren/eventsourcing (R3 pivot)** | No Marten-class consolidator; hand-rolled on pgx with Watermill integration glue; CUE as schema language (separate ADR-PC-006 path); R3 pivots to `hallgren/eventsourcing` library to satisfy §10.4 |

Rust and TypeScript on Node were excluded from the workup roster (thin event-sourcing ecosystems relative to the contenders). JVM languages are excluded per the brief's scope.

---

## Evaluation

### F1 · Cost / licensing

| Candidate | Licence (load-bearing libs) | Verdict |
|---|---|---|
| .NET 9 + Marten + Wolverine | .NET 9: MIT. Marten 7.x: MIT (`/jasperfx/marten`, verified context7 2026-05-22). Wolverine 3.x: MIT (`/jasperfx/wolverine`, verified context7 2026-05-22). NJsonSchema: MIT. Confluent.Kafka .NET: Apache 2.0. YamlDotNet: MIT. OpenTelemetry .NET SDK: Apache 2.0. Testcontainers .NET: MIT. | **Pass (conditional)** — JasperFx Software owns both Marten and Wolverine on the same commit boundary. Mitigation: pre-committed internal fork branch + pinned versions + manual S4 audit before production hardening (per `csharp-counter.md §2.1`). |
| Elixir + Commanded + EventStore-postgres + Broadway | All Apache 2.0 / MIT. | **Pass** |
| Go + pgx + Watermill + CUE + hallgren/eventsourcing | All MIT / Apache 2.0 / BSD. | **Pass** |

All three pass F1. The conditional pass on (A) is the JasperFx correlated single-vendor risk — not a licence concern but a related concentration concern carried into Consequences.

### F2 · Regulatory fit (GDPR / DORA / PSD2)

The engine's regulatory obligations are identical across all three candidates — PII crypto-shredding via per-subject field encryption at event-payload level ([event-store §6.2](../feature-design-event-store-projections.md)), DORA RTO/RPO inherited from PostgreSQL backups + replication, PSD2 audit trail from append-only event log. None of the three is structurally disqualified.

| Candidate | F2 verdict | Notes |
|---|---|---|
| .NET 9 + Marten + Wolverine | **Pass** | Marten's `ISerializer` extension hook supports custom Avro+crypto envelope; OpenTelemetry .NET SDK is first-party; PostgreSQL backup/PITR inherited |
| Elixir + Commanded + EventStore-postgres + Broadway | **Pass (conditional)** | Stacked custom layers on `avro_ex` for crypto envelope; mechanism works, cost real |
| Go + pgx + Watermill + CUE + hallgren/eventsourcing | **Pass** | Application-level AES-GCM envelope; OTel Go first-class |

---

### Soft criteria

#### .NET 9 + Marten 7.x + Wolverine 3.x — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Single .NET 9 process, single `appsettings.json`, single Dockerfile. CLR image weight ~120 MB (real cost but acceptable for non-edge deployment); the engine + MCP polyglot (engine .NET, MCP Python per ADR-IC-010) is structurally identical to any non-Python engine. Testcontainers .NET is mature (PG, Kafka via Redpanda module, WireMock, Toxiproxy all first-party). The engine team is 1–2 people; PT .NET talent pool is the largest of the three candidate languages.

**S2 · Ecosystem coherence.** First-party Confluent.Kafka .NET + Confluent.SchemaRegistry for Avro+SR at the bus boundary (some Decimal logical-type lag vs the Java client — mitigated by pinned version + cross-runtime Decimal round-trip test in CI + Chr.Avro named as fallback). OpenTelemetry .NET SDK is first-party with full LGTM compatibility. YamlDotNet for pack YAML parsing (ADR-PC-007). NJsonSchema for JSON Schema validation (ADR-PC-006). The .NET ecosystem composes cleanly with every integration ADR.

**S3 · Exit cost.** Medium. Marten's `mt_events` table is portable SQL with a documented column shape (`stream_id`, `version`, `data` JSONB, etc.); a hypothetical migration off Marten reads from `mt_events` directly and writes to an alternative event store. Exit cost from Wolverine for the outbox is low (Wolverine's outbox table is a standard `outbox` shape compatible with ADR-IC-004's polling publisher contract). Exit cost from Wolverine for **sagas** is materially higher — Wolverine `Saga` is a coupling point. Mitigation: keep saga business logic in pure methods that can be called from a hypothetical non-Wolverine dispatcher; Wolverine handles only message routing, persistence, retry.

**S4 · Community and longevity.** Marten and Wolverine are both maintained by JasperFx Software (a small consultancy / commercial-OSS company); they share contributors and release cadence. ADR-IC-000's ≥25 trailing-12-month external-commit threshold cannot be verified from context7 alone for either library; **manual `git log --since=` verification is required before production hardening** (carried as Residual Risk #1). Correlated single-vendor risk is real — if JasperFx pivots, both libraries are affected simultaneously. Mitigation: internal fork branch pre-commitment (the engine team's repository forks `JasperFx/marten` and `JasperFx/wolverine` at the pinned major versions, ready to take over maintenance if the upstream becomes unmaintained).

NJsonSchema's commit cadence likewise requires manual `git log` verification; Corvus.JsonSchema is named as a fallback if NJsonSchema activity drops below threshold during the v1 cycle.

#### Elixir + Commanded + EventStore-postgres + Broadway

**S1.** BEAM single deployable via `mix release`; OTP supervision tree is genuine operational advantage for stateful processes — but the engine is deterministic and stateless on the request path (`event-store §5.1–§5.3`), reducing the supervision differentiator. PT BEAM talent pool is 20–30× smaller than .NET (`go-rebuts-elixir.md §2.6`, conceded in R3).

**S2.** Phoenix/Bandit/Ecto/Telemetry compose cleanly. **Testcontainers Elixir** is materially thinner than .NET/Go (`csharp-rebuts-elixir.md §2.3`): no first-party Redpanda or WireMock or Toxiproxy modules; the engine team would need to build them (~600–1000 LOC) as v1 deliverables. Pact-Elixir maturity is similarly thin.

**S3.** EventStore-postgres table shape is portable SQL; exit cost moderate.

**S4.** **Seven below-threshold load-bearing dependencies** (`commanded/eventstore` 7 commits/12mo, `dashbitco/broadway` 10, `broadway_kafka` 4, `beam-community/avro_ex` 5, `ex_json_schema` 11, `ericmj/decimal` 10, `commanded_ecto_projections` not enumerated). Largest S4 surface in the field. Each dep has a named mitigation (pin-and-absorb, named-contributor commitment, drop-to-brod fallback) but the compounding risk is real.

**Decisive reason for not choosing:** R3 §1 retracted Pattern A (`EventStore.append_to_stream/4` joining external `Postgrex.transaction/2` via `shared_connection_pool`) as fabricated after context7 verification against `/commanded/eventstore`. Pivoted to Pattern B (EventStore `events` table is the outbox via subscription) which **triggers ADR-PC-001 Case-B supersession** — major scope expansion across two ADR namespaces. The integrator rejected Elixir partly because Case B is a scope expansion this decision avoids by choosing .NET.

#### Go + pgx + Watermill + CUE + hallgren/eventsourcing

**S1.** Single static binary; no CLR or BEAM runtime; lightest deployment footprint of the three. But no OTP-equivalent supervision — application code implements restart, backoff, circuit breakers (layered Kubernetes pod-restart + Watermill `Recoverer` + bespoke `for { select {} }` loops per `go-counter.md §2.5`).

**S2.** OpenTelemetry Go SDK is mature; Testcontainers Go is mature (first-party Redpanda + PG modules). Avro+SR Go client requires `confluent-kafka-go` (cgo dependency on librdkafka, complicates the static-binary claim) or pure-Go alternatives (`twmb/franz-go` + `hamba/avro/v2` + `riferrei/srclient`); R3 pivoted to the pure-Go path to preserve the static-binary thesis. CUE evaluator is in Go natively; embeds in three lines (`cuecontext.New() → CompileString() → Unify()`).

**S3.** Lowest exit cost in the field. The engine speaks plain SQL, plain Avro, plain HTTP, plain OTLP — no vendor data formats anywhere.

**S4.** pgx and Watermill comfortably above the ≥25 trailing-12-month threshold; CUE is CNCF Sandbox with active development. **`hallgren/eventsourcing` (R3 pivot library for §10.4 compliance) is a small community library not in context7's high-reputation index** — Go's own R3 surfaces this honestly (`go-counter.md §1`) and acknowledges that "if the integrator weighs §10.4 as the hardest constraint, the Elixir/Commanded path is the safer pick."

**Decisive reason for not choosing:** §10.4 "no in-house event-store build" is binding. Go's R1 path-(a) reading ("hand-rolled on pgx isn't an in-house build because PG is the artefact") was retracted in R3 as rhetorical. The R3 pivot to `hallgren/eventsourcing` satisfies §10.4 only conditionally — a single-point-of-failure on a small-community library that fails the same ≥25-commit threshold the Elixir stack fails on multiple dependencies. Marten avoids this single-point-of-failure cleanly (it is the canonical PG event-sourcing framework with multi-year track record and commercial backing). When the binding constraint is weighted, .NET+Marten moves ahead of Go.

A secondary reason: HALF_EVEN boundary-rounding safety. The engine's money substrate is **`long Cents` (integer cents)** per [02 §2.2](../02-v1-scope-term-deposits.md), not a decimal type — every stored Money value, every event payload field, every projection row is integer cents. Decimal arithmetic enters only at the **boundary** where fractional cents arise from `principal × rate × time` accrual computations and must round to integer cents. The rounding-mode question is therefore narrower than "decimal precision throughout the engine" — it is "what API rounds fractional cents to integer cents at the call site?"

All three stacks can implement integer-cents storage equivalently (every language has 64-bit signed integer). The differentiation is at the boundary-rounding API:

- **.NET (chosen):** `MidpointRounding.ToEven` is a language-level enum on `Math.Round(decimal, int, MidpointRounding)`; the typed `Money` record wraps `long Cents` and exposes `Money.FromEuros(decimal)` as the only Decimal→Cents conversion path, calling `ToEven` internally. A Roslyn analyser bans raw `Math.Round(decimal, ...)` calls in domain code (compiler error). Type-level safety at the boundary.
- **Go (apd, R3 pivot):** Context-level safety via `apd.Context{Rounding: apd.RoundHalfEven}`. The Context object is threaded through call sites; if a call site forgets the Context or constructs a fresh one with default rounding, the safety is lost.
- **Elixir (Decimal):** Boot-time CI assertion sets process-global `Decimal.Context` precision and `:half_even` rounding.

The difference is marginal — all three work; .NET's is type-level rather than instance-level. This is **a tie-breaker, not a load-bearing reason for .NET**. The load-bearing reasons remain §10.4 and the Marten+Wolverine outbox seam.

---

## Decision

**Chosen: .NET 9 + Marten 7.x + Wolverine 3.x.**

Three load-bearing reasons:

1. **§10.4 is honoured cleanly.** Marten 7.x is the canonical battle-tested third-party event-sourcing library on PostgreSQL — multi-year track record, commercial backing via JasperFx Software, multi-thousand-snippet documentation surface (verified context7 2026-05-22 against `/jasperfx/marten`). The §10.4 binding constraint is satisfied without conditional caveats on library maturity (which both Elixir's EventStore-postgres and Go's `hallgren/eventsourcing` carry).

2. **The Marten + Wolverine transactional outbox seam is unrefuted and load-bearing.** `IMartenOutbox.PublishAsync(event)` followed by `session.SaveChangesAsync()` commits event-append + outbox-write in **one** local PostgreSQL transaction with a framework-level guarantee (verified context7 2026-05-22 against `/jasperfx/wolverine` "Marten Integration" + "Longhand Marten Outbox" snippets). Both adversaries conceded this in R2 (`elixir-rebuts-csharp.md` §4 concession; `go-rebuts-csharp.md` §4 row 4 "two-table coordination cost concede"). This is the single strongest cell in the entire adversarial field, and it materially eases ADR-PC-001 §P2 compliance.

3. **Wolverine sagas restore the absorbed-concerns claim.** With ADR-IC-003 amended (per user input 2026-05-22) to permit in-process saga libraries, Wolverine `Saga` becomes the saga orchestrator: framework-managed dispatch, retry, compensation, durable saga state as a Marten document in the same PG database — saga state commits transactionally with event-append and outbox-write. This is a materially cleaner expression of `ADR-IC-003 §P1–§P7` than either Go's hand-rolled state machines (revised in R3 to 300–500 LOC per saga + ~1k LOC shared infra) or Elixir's Commanded process managers (which are themselves clean but tied to the rejected Pattern B / PC-001 supersession path). The collapsed-ADR set is **PC-001 (event store) + PC-003 (snapshot) + IC-003 (saga) + IC-004 (outbox) = 4 ADRs absorbed by Marten + Wolverine**, with PC-002 (bitemporal projection) honestly framed as "application code on a Marten substrate" rather than absorbed.

The fitness heuristic (Go 11.0 > C# 8.5 > Elixir 3.0) ranked Go first on raw counts, but the heuristic is a tally and does not weight constraints by criticality. §10.4 is binding for the team's moderate-experience profile; Marten satisfies it without conditional caveats; `hallgren/eventsourcing` satisfies it only conditionally with a single-point-of-failure S4 risk. When the binding constraint is weighted, .NET+Marten wins on judgement.

The .NET stack carries six conditional passes (enumerated in Residual Risks below) — they are real, named, and mitigated. The thesis "five ADRs collapsed into one transactional boundary" was over-claimed in R1; the **surviving thesis** is "four ADRs collapsed (PC-001 event store + PC-003 snapshot + IC-003 saga + IC-004 outbox), one ADR satisfied by application code on the same substrate (PC-002 bitemporal projection)." Still meaningful; no longer dazzling.

---

**Rejected: Elixir 1.18 + Commanded + EventStore-postgres + Broadway**

The decisive reason is the **R3 retraction of Pattern A and the subsequent pivot to Pattern B**, which triggers ADR-PC-001 Case-B supersession. R3 §1 conceded that the R1 claim — `EventStore.append_to_stream/4` joining an external `Postgrex.transaction/2` via `shared_connection_pool: BabelstoneRepo` — was fabricated; context7 verification against `/commanded/eventstore` showed the documented `append_to_stream/4` signature has no transaction-join hook and `shared_connection_pool` is a boot-time pool config, not runtime transaction inheritance. The pivot to Pattern B (EventStore `events` table is the outbox via subscription) eliminates the separate `outbox` table contract from ADR-PC-001 §P1 and ADR-IC-004, requiring supersession of both ADRs across two namespaces. This is a major scope expansion the .NET+Marten path avoids.

Secondary reasons: seven below-threshold load-bearing S4 dependencies (each individually mitigable, compounding in aggregate); Testcontainers Elixir thinness vs ADR-IC-009's mandate (~600–1000 LOC of in-house Testcontainers modules + Toxiproxy client + Pact engagement as v1 deliverables); PT BEAM talent pool 20–30× smaller than .NET.

The Elixir advocate's strongest surviving claim — Commanded's process-manager idiom for saga orchestration — is genuinely cleaner than either competitor's saga code pre-user-input. **With ADR-IC-003 amended to permit Wolverine sagas, Wolverine `Saga` is structurally similar enough to Commanded process managers that this Elixir advantage closes.** Both are framework-managed dispatch with durable state in the same DB; Wolverine sits in a vendor ecosystem (.NET) with materially larger ecosystem coherence for this engine's other commitments (Confluent.Kafka .NET, Testcontainers .NET, OpenTelemetry .NET).

**Rejected: Go 1.23 + pgx/v5 + Watermill + CUE + hallgren/eventsourcing**

The decisive reason is **§10.4 conditional compliance** — Go has no Marten-class consolidator framework. R1's path-(a) reading ("hand-rolled on pgx isn't an in-house build") was retracted in R3 as rhetorical; the honest answer pivots to `hallgren/eventsourcing` (a small-community Go event-sourcing library with single-point-of-failure S4 risk). Marten avoids this by being the canonical, multi-year, commercially-backed PG event-sourcing library on .NET. §10.4 is binding for the moderate-experience team profile; Go cannot satisfy it without conditional caveats; .NET+Marten can satisfy it cleanly.

Secondary reasons: HALF_EVEN boundary-rounding safety is Context-level in Go (apd) vs type-level in .NET (typed `Money` wrapping `long Cents` + analyser + `MidpointRounding.ToEven` enum) — marginal differentiation, not decisive; saga orchestration is hand-rolled in Go (300–500 LOC/saga + ~1k shared) vs framework-managed in Wolverine; OTP-equivalent process supervision is bespoke in Go vs implicit in CLR + Kubernetes; PT talent pool is smaller than .NET (though larger than Elixir).

Go's strongest surviving claims — lowest exit cost, single static binary, no vendor data formats — are real and remain real after this decision. The engine could be re-implemented in Go later if .NET ever becomes untenable, and the migration would be tractable because the events table contract (ADR-PC-001 §P1) and the YAML pack format (ADR-PC-007) are language-agnostic.

---

## Consequences

**What this choice makes easier:**

1. **One-transaction commit** for event-append + outbox-write + saga-state-update. `IMartenOutbox.PublishAsync(event)` + `session.Store(sagaState)` + `session.SaveChangesAsync()` is one PG transaction. ADR-PC-001 §P2, ADR-IC-004 polling-publisher contract, ADR-IC-003 saga durability all satisfied in one atomic boundary.
2. **Marten's projection runtime** provides inline (synchronous with event append) and async projection modes, declarable per-projection (`feature-design-two-modes-asymmetry §5.4`). Bitemporal `valid_time` is hand-rolled as additional projection columns (Path C per `feature-design-event-store-projections §6.1`); `transaction_time` is Marten-native (`mt_events.created`).
3. **Snapshot mechanism** — Marten `SnapshotLifecycle.Inline` / `.Async` per aggregate type. Hash-and-verify is a thin wrapper around Marten snapshot retrieval + recomputation against the event stream; monthly drill harness is engine-team-owned (~150–300 LOC) but built on Marten primitives.
4. **Saga orchestration** — Wolverine `Saga` subclasses provide framework-managed dispatch (`Start`/`Handle` methods routed by saga state correlation), durable state as Marten documents, retry policies, OpenTelemetry spans per saga step. Term-deposit constitution saga, renewal saga, moratorium-application saga, legacy-SoR-transition saga all expressible in ~50–100 LOC of method bodies per saga (versus 300–500 LOC + shared infra per the Go alternative).
5. **Testcontainers .NET** with first-party Redpanda, PG, WireMock, Toxiproxy modules. Integration test fidelity is highest of the three candidate stacks.
6. **CLR ecosystem coherence** — Confluent.Kafka .NET + Confluent.SchemaRegistry, OpenTelemetry .NET SDK, YamlDotNet, NJsonSchema, AspNetCore for the Kong-fronted REST/SSE endpoints. No bespoke glue for any integration ADR.
7. **LLM-codability** — .NET has the largest training corpus of the three candidates; LLM-authored configuration tooling and operator scripts benefit from this.
8. **PT talent pool** — largest of the three candidates, easing future scaling beyond 1–2 person team.

**What this choice makes harder or impossible:**

1. **CLR runtime image weight** — Engine container image is ~120 MB (versus Go ~10–20 MB, Elixir ~30–50 MB). Acceptable for non-edge deployment.
2. **AOT compilation foreclosed for Marten paths** — Marten relies on reflection for event-type-to-class mapping; native AOT compilation (which limits reflection) is not viable for the event-store paths. The engine ships as JIT-compiled CLR, not as a native binary.
3. **System-level polyglot** — Engine in .NET, MCP server in Python (per ADR-IC-010), ACL service language-flexible. The bank operates ≥2 runtimes at the system level. Single-runtime ops within the engine is preserved; the operating bank takes the polyglot cost at the deployment-platform layer.
4. **Saga exit cost** — Wolverine `Saga` is a coupling point. Migrating off Wolverine for sagas requires rewriting saga handler dispatch + durable state shape. Mitigated by keeping saga business logic in pure methods callable from a hypothetical non-Wolverine dispatcher.
5. **Wolverine + ADR-IC-003 amendment** — ADR-IC-003 had to be amended to permit in-process saga libraries. The amendment narrows the "no third-party saga engine" rejection to external workflow services (Camunda, Temporal, Axon-style centralised orchestrators) and explicitly permits in-process saga libraries that run in the engine process against the engine's own DB. This is a real ADR amendment, not a status-line note.

---

## Residual Risks

1. **JasperFx correlated single-vendor risk (S4).** Marten and Wolverine share commits boundary, contributors, release cadence. If JasperFx Software pivots (commercial-tier shift, acquisition, founder retirement), both libraries are affected simultaneously. **Mitigation:** the engine team's repository forks `JasperFx/marten` and `JasperFx/wolverine` at the pinned major versions; a documented internal-maintenance procedure is in place to take over upstream support if the public projects become unmaintained. Manual `git log --since=` audit of both libraries' trailing-12-month external commit count is performed at every minor-version bump and recorded in the ADR-PC change log. ADR-IC-000's ≥25 trailing-12-month-external-commits threshold cannot be verified from context7 alone — manual `git log` is the verification step (carried as Open Action #1 below).

2. **HALF_EVEN boundary-rounding universality is mitigated, not guaranteed by language.** Money in the engine is `long Cents` (integer cents per [02 §2.2](../02-v1-scope-term-deposits.md)), so the substrate-level rounding concern does not arise — `long` arithmetic is exact. The rounding question arises only at the boundary where fractional cents from `principal × rate × time` accrual computations must round to integer cents. The .NET answer: `Math.Round(d, 0, MidpointRounding.ToEven)` is correct since .NET Core 3.0; but third-party serialization paths (Marten JSON, Avro Decimal logical types, EF Core integration if introduced later) may round differently. **Mitigation:** typed `Money` record (`record Money(long Cents)`) is the only money carrier in domain code; Roslyn analyser bans raw `Math.Round` calls on `decimal` (compiler error in CI). All Decimal→Cents conversions go through `Money.FromEuros(decimal)`, which calls `MidpointRounding.ToEven` explicitly. Cross-runtime CI test compares engine event payloads against expected byte equality with a fixture corpus to catch logical-type lag. Alternative architectures considered: pure-integer arithmetic with explicit scale factors avoids decimal entirely; rejected for v1 as higher implementation discipline burden for the moderate-experience team; revisit for v2 if rounding-mode discipline bites. See §Implementation Principles P1 and P2.

3. **Marten upcasters and forward-only event evolution (§5.4).** Marten upcasters permit lambdas that rename / re-type / drop fields — risky operations forbidden by `feature-design-event-store-projections §5.4`. **Mitigation:** Roslyn analyser bans `UpcastAsync` lambdas entirely; only `Upcast<T1, T2>` declarative form is permitted, and every upcaster carries a sign-off comment naming the §5.4 forward-only-compatible transformation it performs. Raw JSONB `mt_events.data` is queryable for audit forensics regardless of upcaster definitions.

4. **Confluent.Kafka .NET Avro Decimal logical-type lag.** Confluent's documentation flags "non-Java implementations less mature" for Avro Serde, particularly around logical types (Decimal, Date, Timestamp). **Mitigation:** pin `Confluent.Kafka` to a verified version; add a cross-runtime Decimal round-trip CI test that publishes an event via .NET and consumes it via a JVM Kafka consumer (or vice versa) asserting byte equality. Chr.Avro named as a fallback Avro library if Confluent.SchemaRegistry .NET's Decimal handling degrades.

5. **NJsonSchema S4 audit gap.** Trailing-12-month external commit count for NJsonSchema cannot be verified from context7 alone. **Mitigation:** manual `git log --since=` verification at every minor-version bump; if NJsonSchema drops below threshold during the v1 cycle, switch to Corvus.JsonSchema (named fallback).

6. **Wolverine saga exit cost concentration.** Saga business logic should remain in pure methods callable from a hypothetical non-Wolverine dispatcher (see §Consequences #4). The discipline is enforced by code review; CI lint that flags Wolverine API calls inside saga business-logic methods (versus saga dispatch methods) is a v1 deliverable.

7. **NJsonSchema vs JSON Schema 2020-12 spec coverage.** Some JSON Schema 2020-12 features (specifically `dynamicRef`/`dynamicAnchor`) have partial implementation in NJsonSchema. **Mitigation:** the family-schema dialect deliberately avoids these features (per ADR-PC-006 §Implementation Principles); the validator dialect surface is the subset NJsonSchema fully supports.

---

## Implementation Principles

### P1 — `Money = long Cents` is the storage type; decimal is a boundary computation type only

Money in domain code, event payloads, projection rows, snapshot state, saga state, rate-sheet resolution outputs, and reconciliation logs is carried as the typed record `record Money(long Cents)`. The underlying storage is `long` (signed 64-bit integer), holding the value as integer cents. Maximum representable value is ~€92 quintillion — well beyond any plausible banking commitment.

`System.Decimal` is **not** the money substrate. It enters the codebase only at three boundary call sites, all of which must use `MidpointRounding.ToEven`:

1. **External rate input → internal rate representation.** Rate sheets express rates in basis points (integer); pack-level percentages may arrive as decimals from authoring tooling. Conversion happens at pack-load time using `decimal → bps` HALF_EVEN rounding.
2. **Accrual computation.** `principal_cents × rate_bps × days_elapsed / (basis_days × 10000)` is performed in `decimal` (or pure integer with explicit scale — engine team's choice per individual handler, documented in code). The Decimal result rounds to `long Cents` at the projection write or event emission via `Money.FromCents(decimal d) => new Money((long)Math.Round(d, 0, MidpointRounding.ToEven))`.
3. **Display / UI / regulatory report.** External-facing decimal representations (e.g., €1,234.56 in a notification template or BdP return) format `Money.Cents / 100m` for display. Read-only; no money math at display time.

A Roslyn analyser enforces this discipline:
- Bans `Math.Round(decimal, ...)` calls in domain assemblies, except inside `Money.FromCents` itself.
- Bans `decimal` field types on any class or record outside the `Babelstone.Money` namespace.
- Flags any operator-overload that returns `decimal` from `Money`-typed inputs.

### P2 — HALF_EVEN rounds at the Decimal → Cents boundary, nowhere else

The HALF_EVEN (banker's rounding) discipline applies at the conversion from `decimal` (intermediate computation) to `long Cents` (storage). No intermediate rounding is performed inside the `decimal` arithmetic itself — accumulating multiple HALF_EVEN roundings in sequence creates drift. The pattern is: compute the full expression in `decimal` at maximum available precision; round once at the final `Money.FromCents` call site.

Tests verify boundary rounding behaviour against a fixture corpus of canonical (input, expected output) pairs covering: midpoint values (e.g., 100.5 cents → 100 cents per HALF_EVEN; 101.5 cents → 102 cents per HALF_EVEN), large magnitudes (€1e8+ principals over multi-year terms), and small magnitudes (sub-cent daily accruals over 1-day periods). The corpus is sealed and replayable per [feature-design-configuration-surface §3.9](../feature-design-configuration-surface.md).

### P3 — Marten `mt_events` table maps to ADR-PC-001 §P1 envelope contract via custom event metadata

Marten's native `mt_events` table provides `stream_id`, `version`, `data` (JSONB), and event metadata columns. The engine maps these to the ADR-PC-001 §P1 contract columns:

- ADR-PC-001 columns mapped via Marten's native projection: `event_id` (`mt_events.id`), `stream_id` (`mt_events.stream_id`), `sequence_number` (`mt_events.version`), `event_type` (`mt_events.type`), `transaction_time` (`mt_events.timestamp`).
- ADR-PC-001 columns added via Marten event metadata: `event_schema_version`, `family`, `partition_key`, `pack_version`, `schema_version`, `valid_time`, `causation_id`, `correlation_id`, `actor`, `payload_schema_id`. These are stored either as Marten `Headers` on the event (Marten persists as additional JSONB columns) or as explicit table-extension columns added via Marten schema migration.
- The Avro-serialised payload bytes live in `mt_events.data`; the underlying SQL contract from ADR-PC-001 §P1 — that `payload` is the binary serialised form with `payload_schema_id` separately tracked — is preserved.

The contract column shape is verifiable by a SQL-level check at engine startup. Marten-internal columns (`mt_dotnet_type`, `tenant_id`) are tolerated per ADR-PC-001 §P1's explicit allowance for library-internal additions.

### P4 — Wolverine sagas persist state as Marten documents; `IMartenOutbox` is the outbox

Saga state is a typed `record` (e.g. `record ConstitutionSagaState(...)`) registered with Wolverine as a `Saga` document type. Wolverine stores saga state via Marten in the same `IDocumentSession`; saga progression commits transactionally with `IMartenOutbox.PublishAsync(event)` and `session.SaveChangesAsync()` in one local PG transaction. The atomicity guarantee from ADR-PC-001 §P2 extends to saga state.

Saga business logic lives in `private` instance methods on the `Saga` subclass and in pure helper methods callable independently of Wolverine; the Wolverine `Handle(MessageX)` methods are thin dispatch shims that delegate to the pure logic. This preserves saga-business-logic exit cost (see §Residual Risks #6).

### P5 — JSON Schema validator runs in-process; no out-of-process schema toolchain

NJsonSchema 11.x validates variant YAML files against family-schema JSON Schemas per [ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md). The validator is loaded as a .NET assembly into the engine process; no `Process.Start` shell-out to a separate validator binary. Depths 1–4 run synchronously at variant-commit time within the 30s budget; depth-5 simulation runs in CI against the sealed pack test corpus from ADR-PC-007.

---

## Open Actions

1. **Manual `git log` audit of Marten + Wolverine + NJsonSchema trailing-12-month external commits** — perform before any production hardening; record results in `/Users/joaomiranda/dev/babelstone/docs/product-management/product_concepts/adrs/` as a dated audit file or in this ADR's change log.
2. **Internal fork branch setup** — fork `JasperFx/marten` and `JasperFx/wolverine` at the v1-pinned versions into the engine team's repository; document the internal-maintenance procedure.
3. **Roslyn analyser implementation** — three analysers: (a) ban raw `Math.Round` on `decimal`; (b) ban `Marten.UpcastAsync` lambdas; (c) flag Wolverine API calls inside non-dispatcher saga methods.
4. **Cross-runtime Avro Decimal round-trip CI test** — publish event in .NET, consume in JVM (or vice versa), assert byte equality.

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL event store; library deferral filled here with Marten. Amendment block dated 2026-05-22 appended to PC-001 per `pc-001-revisit-gate.md`. Status remains `Accepted`.
- [ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md) — JSON Schema (Draft 2020-12) validated by NJsonSchema 11.x as the family-schema language. This ADR depends on PC-006 for schema validation; PC-006 depends on this ADR for runtime language.
- [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) — pack manifest format (signed YAML in OCI artefact). This ADR depends on PC-007 for pack loading; PC-007 depends on this ADR for engine runtime.
- [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — saga orchestrator; **amended 2026-05-22** to permit in-process saga libraries including Wolverine `Saga`. The amendment is collateral to this ADR's adoption of Wolverine.
- [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) — outbox pattern; Wolverine's transactional outbox is the implementation of the polling-publisher contract.
- [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — MCP server stays Python; engine + MCP polyglot at system level acknowledged.
- [feature-design-event-store-projections §10.4](../feature-design-event-store-projections.md) — "no in-house event-store build"; Marten satisfies.
- [feature-design-event-store-projections §6.1](../feature-design-event-store-projections.md) — bitemporal projection paths; Marten is Path C (application-level `valid_time` columns).
- [feature-design-two-modes-asymmetry §5.4](../feature-design-two-modes-asymmetry.md) — per-projection sync/async; Marten inline vs async.
- [feature-design-two-modes-asymmetry §5.6, §8](../feature-design-two-modes-asymmetry.md) — Q-AK synthetic v4-scale load test; pgx+goroutines and CLR+Marten both have known-good banking precedent at the 250-TPS-sustained / 1000-TPS-burst profile; empirical validation is part of v1 acceptance.
- [02 §2.2](../02-v1-scope-term-deposits.md) — integer-cents EUR ledger contract; `Money = long Cents` is the storage type (see §Implementation Principles P1); `System.Decimal` is the boundary computation type only; HALF_EVEN rounds Decimal→Cents at boundary call sites via `MidpointRounding.ToEven`.

---

*Decided 2026-05-22 by jhosm, integrating user input on ADR-IC-003 viability (2026-05-22) with the three-round adversarial workup's survival matrix.*
