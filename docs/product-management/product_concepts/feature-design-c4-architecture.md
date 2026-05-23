# C4 Architecture View — Core Banking Product Engine

> **Status: DRAFT.** A C4 model of the engine described in [01 — Product Architecture](./01-product-architecture.md), rendered as PlantUML. Three levels — **System Context**, **Container**, **Component** — built top-down. Class diagrams are deliberately excluded (the engine's domain types are specified in the event contract [02 §2.4](./02-v1-scope-term-deposits.md) and the `events`-table contract [ADR-PC-001 §P1](./adrs/ADR-PC-001-event-store-technology.md); a UML class layer would duplicate them and rot).
>
> This document is a **view onto decisions already made**, not a new decision. Every box and line traces to a concept doc, a feature-design note, or an ADR; where it does, the source is cited. If this view and a cited source disagree, the source wins and this document is the bug.

---

## How to read this document

[C4](https://c4model.com) describes software with a hierarchy of diagrams at four levels of zoom. This document uses the top three:

| Level | Question it answers | Audience |
|---|---|---|
| **1 — System Context** | How does the engine fit into the bank's estate? Who uses it; which systems does it exchange with? | Everyone — including non-technical stakeholders |
| **2 — Container** | What are the separately-deployable/runnable pieces *inside* the engine's world, and what shared integration-estate runtimes does it plug into? | Engineers, ops |
| **3 — Component** | What are the major code-level building blocks inside a single container (the engine process)? | Engineers working in the codebase |
| ~~4 — Code/Class~~ | *(omitted — see header)* | — |

### The system-boundary decision (load-bearing)

The brief is explicit that the engine does **not** redesign the integration backbone — broker, gateway, anti-corruption layer, saga orchestrator, MCP server, observability — its architecture is fixed in [integration_concepts/adrs/](../integration_concepts/adrs/README.md) ([01 §6](./01-product-architecture.md)). That is a statement about **design authority**, not **build provenance**: several estate components are nonetheless *built in-house* (the saga orchestrator, the outbox, the ACL, the MCP server, the notification service) and co-located in the monorepo per [ADR-IC-013](../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md), while others (broker, gateway, observability) are consumed third-party images. The C4 model honours the *role* split:

- **Level 1 (Context)** draws the engine as the single system in focus and the bank's other systems as external neighbours. The shared transport (Redpanda, Kong, the ACL service) is intentionally **not** drawn here — at context level we care *which systems exchange what*, not *through which pipe*. Drawing the broker at context level would mis-state it as a peer business system.
- **Level 2 (Container)** is where the surrounding estate appears, tagged as integration-estate runtimes (distinct shading), so the seam between the product engine and the estate is visible. *Build provenance is orthogonal to this role split:* some estate runtimes we build in-house, some we consume as third-party images — see the legend and [ADR-IC-013](../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).

"External system" in C4 is **not** a synonym for "out of scope". Per [00 §4](./00-product-vision.md), GL / IFRS 9 / channels / payments / fraud / KYC are out-of-scope *products*, but the integration *to* them is the in-scope, load-bearing asset of the build ([00 §1.5](./00-product-vision.md)). C4 captures exactly this: those products are `System_Ext` (grey) neighbours; the **relationships** crossing the boundary are the asset this view documents.

### PlantUML + C4-PlantUML, and how diagrams are committed

Diagrams use the [C4-PlantUML](https://github.com/plantuml-stdlib/C4-PlantUML) macro set. Because **GitHub does not render PlantUML** (it renders only Mermaid natively), each diagram is **pre-rendered to SVG and committed** next to its source, and the Markdown embeds that SVG so it displays on github.com. The workflow:

- **Source of truth** is a `.puml` file under [`diagrams/`](./diagrams/). The include uses the PlantUML standard-library short form (`!include <C4/...>`), which needs no network access on a recent PlantUML; a commented remote-include fallback is kept in each `.puml` for renderers lacking the bundled stdlib.
- **Committed artefact** is the rendered `.svg` beside it. Regenerate after any edit:
  ```bash
  plantuml -tsvg docs/product-management/product_concepts/diagrams/*.puml
  ```
- **Prerequisites** (one-time): a JDK, Graphviz (`dot`), and PlantUML. Full setup in [`INSTALL.md`](../../../INSTALL.md) — e.g. `brew install graphviz plantuml`.

The SVG is rendered output, never hand-edited; if an SVG and its `.puml` disagree, re-render. Drift between the two is guarded by the [`.githooks/pre-commit`](../../../.githooks/pre-commit) hook, which re-renders any staged `.puml` and stages the resulting SVG (activation in [`INSTALL.md`](../../../INSTALL.md)).

---

## Level 1 — System Context

![System Context — Core Banking Product Engine](./diagrams/c4-l1-system-context.svg)

<sub>Source: [`diagrams/c4-l1-system-context.puml`](./diagrams/c4-l1-system-context.puml) · regenerate with `plantuml -tsvg docs/product-management/product_concepts/diagrams/c4-l1-system-context.puml`</sub>

### Narrative

**The system in focus** is the three-part deliverable from [00 §3](./00-product-vision.md): the product-engine runtime, the event store + bitemporal projections, and the swappable regulatory pack. They are one cohesive system; operating any two without the third is half a product, so they sit inside one box at this zoom.

**The people are the configuration-surface authors plus operations.** The surface splits three ways by owner and cadence ([01 §3](./01-product-architecture.md)): the **Product Manager** commits declarative product configs (family schemas + variants, CUE-validated per [ADR-PC-006](./adrs/ADR-PC-006-cue-schema-language.md)); **Treasury/ALM** publishes rate sheets on a daily–weekly beat; the **Engine team + Regulatory counsel** ship and version-pin the pack ([ADR-PC-007](./adrs/ADR-PC-007-signed-yaml-oci-pack.md)). Modelling these as three distinct relationships — not one "admin" — is the whole point of the split: the cheapest change (a rate) must not inherit the most expensive approval (a product redesign). The **Bank Operations Clerk** reaches the engine through channels for corrections (`DepositCorrected`, [02 §2.4.1](./02-v1-scope-term-deposits.md)) and operational queries.

**End customers are not drawn as direct users** — they act *through* Channels, which is the system that talks to the engine. **AI / LLM Agents** are drawn as an external actor because the chat-agent strategy ([integration_concepts §11](../integration_concepts/11-chat-agent-channel-strategy.md), [ADR-IC-010](../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) makes them a first-class, *untrusted* caller of the same command/query surface, gated by the same IAM.

**Coexistence is the one bidirectional, asymmetric relationship.** A PT term deposit is constituted from and matures into a *conta à ordem* that — in the v1 strangler-fig slice — still lives in the **legacy Core Banking** system ([02 §3](./02-v1-scope-term-deposits.md)). The engine debits/credits that account through the anti-corruption layer (idempotent, indeterminate-state-aware) and never shadows its balance. In the other direction, the legacy core's **daily batch extract** is the engine's only feed of legacy state, surfacing as `LegacyInstanceObserved` ([feature-design-strangler-fig-coexistence](./feature-design-strangler-fig-coexistence.md)). The ACL and the broker that carry these exchanges are integration-estate; they appear at Level 2.

**Constitution is a saga, so several neighbours are saga participants, not request/response dependencies.** CRM, Workflow, and Notifications each take part in the constitution flow ([01 §6](./01-product-architecture.md), [integration_concepts §05](../integration_concepts/05-constitution-saga-walkthrough.md)). The engine **participates as a step**; it does not run the saga (the orchestrator is [ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md), an estate element shown at Level 2). **Compliance/AML is *not* an in-saga participant:** AML/KYC clearance is adjudicated upstream and enforced as an edge precondition ([ADR-PC-013](./adrs/ADR-PC-013-aml-kyc-upstream-precondition.md)) — the engine only emits lifecycle events to it for monitoring and receives freezes / legal holds as commands. KYC sits upstream of CRM — a customer exists before they hold a product ([00 §4](./00-product-vision.md)).

**Downstream consumers receive events and pack-defined signals, never queries against the store.** GL, IFRS 9, and Regulatory Reporting are consumers of the engine's emissions ([01 §2](./01-product-architecture.md)): the engine guarantees the signals are present, correct, and timely; the reports/postings are built downstream. The PG `events` table is not a public interface — consumers see published topics ([ADR-PC-001 Consequences](./adrs/ADR-PC-001-event-store-technology.md)).

### Scope boundary at this level

| In scope (drawn inside `engine`) | Out of scope as products, in scope as integration (drawn as `System_Ext`) |
|---|---|
| Engine runtime; event store + bitemporal projections; regulatory pack | Channels, Core Banking, Compliance/AML/Fraud, CRM, Workflow, KYC, GL, IFRS 9, Regulatory Reporting, Notifications, IAM |

Out-of-scope-entirely for v1 (no relationship drawn): structured/FX deposits, secondary-market trading, non-resident & joint-holder deposits, the assembled FGD *return* itself ([02 §4](./02-v1-scope-term-deposits.md)). Payments rails (SEPA/TARGET2) sit behind Core Banking — how the *conta à ordem* moves money is a payments concern downstream of the legacy core ([00 §4](./00-product-vision.md)), so they do not appear at the engine's context level.

---

## Level 2 — Container

One container diagram would bloat: the engine's world is ~11 runtime containers plus the ~11 external systems from Level 1. So this level is **four flow-focused diagrams** — each tells one story end-to-end and is readable alone — plus a cross-cutting observability note.

**Reading the colours** (legend on each diagram):

The colours encode **architectural role**, *not* build provenance — the two are orthogonal ([ADR-IC-013](../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)):

- **Blue** — the **product engine** the team builds (the deliverable): the engine process, its PostgreSQL, the CUE validator binary.
- **Teal** — the surrounding **integration estate**, whose architecture is fixed in [integration_concepts/adrs/](../integration_concepts/adrs/README.md): Kong, Redpanda + SR, the ACL service (+ its DB), the MCP server, the notification service, the saga orchestrator, observability. By **provenance** this splits two ways: *in-house-built* — the saga orchestrator ([IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)), the outbox ([IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)), the ACL ([IC-012](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)), the MCP server ([IC-010](../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)), the notification service ([IC-011](../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)) — code we write, co-located in the monorepo per [ADR-IC-013](../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md); and *consumed third-party* — Kong, Redpanda + SR, Grafana LGTM — images/SDKs we run but do not author.
- **Grey** — external systems the engine *integrates with* (out-of-scope products per [00 §4](./00-product-vision.md)).
- The dashed **system boundary** marks the product-engine deliverable. Per [01 §6](./01-product-architecture.md) the engine does not *redesign* the estate's architecture; building the in-house estate is nonetheless in scope ([ADR-IC-013](../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)).

**Container inventory** (each appears in one or more of the four diagrams):

| Container | Class | Tech | Source |
|---|---|---|---|
| Engine process | build | C# / .NET 9, single deployable | [ADR-PC-010](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) |
| Engine PostgreSQL | build | PostgreSQL: events + outbox + projections + saga + `pack_versions` | [ADR-PC-001](./adrs/ADR-PC-001-event-store-technology.md), [ADR-IC-005](../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) |
| CUE validator (`pack-validate`) | build | Go static binary | [ADR-PC-006](./adrs/ADR-PC-006-cue-schema-language.md) |
| Kong Gateway CE | estate | Kong / nginx, DB-less | [ADR-IC-006](../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) |
| Redpanda CE + Schema Registry | estate | Kafka API + Confluent SR | [ADR-IC-001](../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) / [002](../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) |
| ACL service + ACL PostgreSQL | estate | dedicated service per bounded context, own DB | [ADR-IC-012](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) |
| MCP server | estate | Python, Streamable HTTP | [ADR-IC-010](../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) |
| Notification service + its PostgreSQL | estate | service + delivery store | [ADR-IC-011](../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md) |
| OCI registry | estate | artefact registry (cosign-signed packs) | [ADR-PC-007](./adrs/ADR-PC-007-signed-yaml-oci-pack.md) |
| OTel Collector → Grafana LGTM | estate | observability pipeline | [ADR-IC-007](../integration_concepts/adrs/ADR-IC-007-observability-stack.md) |

### L2.1 — Runtime write/read path & legacy integration

![Container — L2.1 Runtime write/read path & legacy integration](./diagrams/c4-l2-runtime-write-read.svg)

<sub>Source: [`diagrams/c4-l2-runtime-write-read.puml`](./diagrams/c4-l2-runtime-write-read.puml)</sub>

The heart of the engine. A command enters through **Kong**, which has already done JWT validation, PSD2 SCA enforcement, rate-limiting, and payload validation at the edge and forwards over mTLS ([ADR-IC-006](../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)). Inside the **engine process**, aggregates fold the command into events via the cash-flow handlers, and the hand-rolled event-sourcing core appends the event rows **and** the outbox row in **one local PostgreSQL transaction** — the atomic seam ([ADR-PC-001 §P2](./adrs/ADR-PC-001-event-store-technology.md), [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) that makes the outbox pattern correct without a dual write. The **outbox-relay worker** (a polling publisher, [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) relays committed rows to **Redpanda** as Avro. Queries are served from read-model projections in the same database ([ADR-IC-005](../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)).

The legacy seam is deliberately indirect: the engine never calls Core Banking directly. It sends domain commands over mTLS to the dedicated **ACL service** ([ADR-IC-012 D1](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)), which owns its own PostgreSQL and its own outbox ([D5](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)), translates to the Core's SOAP/REST/MQ surface, enforces per-operation idempotency, and models the `INDETERMINATE` state explicitly so a lost confirmation never becomes a double debit. Core confirmations (and the daily DDA batch) arrive through the ACL's pluggable inbound adapter and are published back through Redpanda, which the engine's saga consumes via its inbox to advance. The current account stays *read-through, not owned* ([02 §3](./02-v1-scope-term-deposits.md)).

### L2.2 — Event backbone & downstream consumers

![Container — L2.2 Event backbone & downstream consumers](./diagrams/c4-l2-event-backbone-consumers.svg)

<sub>Source: [`diagrams/c4-l2-event-backbone-consumers.puml`](./diagrams/c4-l2-event-backbone-consumers.puml)</sub>

Once an event is committed, the outbox relay puts it on **Redpanda** — the **only** public interface to the engine's history; the `events` table itself is private ([ADR-PC-001](./adrs/ADR-PC-001-event-store-technology.md)). Payloads are Avro, validated against the built-in **Schema Registry** at publish ([ADR-IC-002](../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)). The fan-out is pure choreography: the **General Ledger** consumes posting signals, **IFRS 9** consumes lifecycle events, and **Regulatory Reporting** consumes the pack-defined reporting-hook signals (FGD eligible balances, BdP rate statistics). The engine's contract is that those signals are present, correct, and timely; the postings and returns are built downstream ([01 §2](./01-product-architecture.md), [02 §2.2](./02-v1-scope-term-deposits.md)). The **ACL** appears here too — as a *producer* of Core-confirmation events.

The **notification service** is a pure choreography consumer of saga terminal events ([ADR-IC-011 D5](../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)): it owns a PostgreSQL store (subscriptions, delivery log, dead-letter) and delivers HMAC-SHA256-signed, at-least-once callbacks to *pre-registered* subscriber endpoints with exponential backoff. Event contracts are governed offline by EventCatalog from source control ([ADR-IC-008](../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)) — not drawn, because it is not on the runtime path.

### L2.3 — Configuration & regulatory-pack plane

![Container — L2.3 Configuration & regulatory-pack plane](./diagrams/c4-l2-config-pack-plane.svg)

<sub>Source: [`diagrams/c4-l2-config-pack-plane.puml`](./diagrams/c4-l2-config-pack-plane.puml)</sub>

The configuration surface is three artefacts with three owners and cadences ([01 §3](./01-product-architecture.md)): **product configs** (PM), **rate sheets** (Treasury/ALM), and the **regulatory pack** (engine team + regulatory counsel). Authoring flows through Git/CI, where the **CUE validator** (`pack-validate`, a single Go binary, [ADR-PC-006](./adrs/ADR-PC-006-cue-schema-language.md)) runs validation depths 1–4 (syntax, type, pack-compliance, regulatory coherence). On pass, CI cosign-signs the pack — auditor-readable YAML data plus `.cue` schemas — and `oras`-pushes it to the **OCI registry** ([ADR-PC-007](./adrs/ADR-PC-007-signed-yaml-oci-pack.md)).

At pack-load the **engine** pulls by digest, verifies the cosign signature — which *attests CUE validation already passed*, so the engine only structurally re-parses rather than re-running `cue vet` ([ADR-PC-006 §P3](./adrs/ADR-PC-006-cue-schema-language.md)) — caches, and resolves primitives/parameters in memory. Every constituted instance **pins** its `pack_version` and `schema_version` for life, carried on every event ([ADR-PC-007 §P3](./adrs/ADR-PC-007-signed-yaml-oci-pack.md), [ADR-PC-001 §P1](./adrs/ADR-PC-001-event-store-technology.md)). The CUE validator is the engine's *one accepted out-of-process seam* ([ADR-PC-010](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)) — invoked only at commit/CI and pack-load, never on the request path. Rate-sheet storage and its deploy API are still open ([ADR-PC-008](./adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md), shown abstractly).

### L2.4 — Agent channel & async completion

![Container — L2.4 Agent channel & async completion](./diagrams/c4-l2-agent-channel.svg)

<sub>Source: [`diagrams/c4-l2-agent-channel.puml`](./diagrams/c4-l2-agent-channel.puml)</sub>

The agent surface adds two estate containers, not a rewrite. An **AI/LLM agent** first obtains an OAuth 2.1 token from the **existing IAM** — reused, not a second authorization server ([ADR-IC-010 Area 4](../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) — with PKCE and an RFC 8707 `resource` binding to the MCP server's canonical URI. It then calls the **MCP server** (Python SDK, Streamable HTTP) through the *same* **Kong** route family as REST, so JWT/SCA/rate-limit are uniform; Kong validates the token signature against the IAM's JWKS. The MCP server maps `tools→commands` and `resources→CQRS read models` onto the engine.

Because an MCP session may end before a long-running saga finishes ([ADR-IC-011](../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)), completion is pushed: the engine emits `SagaTerminated` → Redpanda → the **notification service** → an HMAC-signed callback to the agent's pre-registered endpoint. Owned web/mobile channels that *can* hold a connection use **SSE** for live progress instead; the two completion paths coexist ([ADR-IC-011 D6](../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)).

### Observability (cross-cutting — stated, not drawn)

Every container above — engine, ACL, MCP server, notification service, plus Kong and Redpanda — emits OpenTelemetry logs, metrics, and traces to the **OTel Collector**, which fans out to **Grafana LGTM** (Loki + Grafana + Tempo + Prometheus, [ADR-IC-007](../integration_concepts/adrs/ADR-IC-007-observability-stack.md)). Kong injects `traceparent` at the edge so one trace spans gateway → engine → saga → downstream consumer ([ADR-IC-006 §P6](../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)). It is a note rather than a diagram because every container points at the collector — drawing it produces an N-to-1 hairball that buries the flows the four diagrams exist to show.

## Level 3 — Component

Zooms into the **Engine process** container. Like Level 2, it is split by path into four component diagrams — **L3.1 Write core · L3.2 Projection & query · L3.3 Messaging & saga · L3.4 Loading & config** — so each stays readable.

**Two colour additions at this level** (legend on each diagram): **gold** marks components *loaded from the family schema, not engine code* — the engine-vs-family separation ([event-store §3](./feature-design-event-store-projections.md)) that makes "one engine, many families" structural; **grey "ref"** marks a component drawn in detail in another L3 diagram (so a shared component can appear without being re-specified).

### Where the financial mathematics lives

A natural question: the [financial_concepts](../financial_concepts/banking_products_financial_mathematics.md) functions — balance evolution, day-count, compounding, accrual, TAE, PV/IRR — have to sit *somewhere*. They split into three homes, and conflating them would break the unification thesis:

| Thing | Example | Home | Source |
|---|---|---|---|
| **Math kernel** (executable primitives) | `S(t+Δt)=S(t)(1+r·Δt)−pay+draw`, Act/360, compounding, `J=ΣS·r·Δt`, TAE, TANB/TANL split, PV/IRR | **Engine** — one generic component | [01 §1](./01-product-architecture.md) "one balance-evolution function, invoked with different parameters"; [00 §3](./00-product-vision.md) "the engine ships the executable primitives" |
| **Orchestration** | "accrue at maturity / periodically / in advance, then withhold" | **Family-schema handlers** (pure, loaded) | [event-store §3, §5](./feature-design-event-store-projections.md) — handlers *call* primitives, never re-implement them |
| **Parameters** | day-count = Act/360, withholding = 2800 bps, the TAN value | **Pack + variant config + rate sheet** (declarative data) | [00 §3](./00-product-vision.md) "the pack is declarative data, not executable code"; [ADR-PC-007](./adrs/ADR-PC-007-signed-yaml-oci-pack.md) |

The license to put the math in the engine is the unification proof itself ([financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md)): because one equation governs deposits, credits, current accounts, and cards, the kernel is **one** family-agnostic engine component — not per-family math. The kernel is drawn in L3.1 (where handlers invoke it) and reused by the accrual-schedule projector in L3.2.

### L3.1 — Write core: command → handlers → append

![Component — L3.1 Write core](./diagrams/c4-l3-write-core.svg)

<sub>Source: [`diagrams/c4-l3-write-core.puml`](./diagrams/c4-l3-write-core.puml)</sub>

A command arrives from **Kong** already authenticated and SCA-checked; the **command API** hands it to the **command dispatcher**, which rehydrates the instance (snapshot + events via `load`) and invokes the relevant **family handler**. The handlers are gold because they are *loaded from the family schema, not engine code* — the engine dispatches to them but contains none ([event-store §3](./feature-design-event-store-projections.md)). A handler orchestrates the family's lifecycle and delegates every calculation to the **financial-math kernel** — the one generic balance-evolution function and its day-count / compounding / accrual / withholding / PV-IRR primitives.

The kernel is a **pure function**: the same inputs always produce the same outputs — no reads, no I/O, no clock. That property is what makes replay correct, so the kernel never fetches its own parameters. Instead the dispatcher calls the **parameter resolver** — the *only* component that touches the version cache — to turn the instance's **pinned** `pack_version` + `schema_version` + `rate_sheet_version` into an immutable **calculation context** (day-count, rate, compounding, withholding), and passes that context to the handler and kernel as an explicit argument (the functional-core / imperative-shell split). Because those versions are immutable — pack pinned by OCI digest, rate sheet by id — replaying a 2026 event re-resolves the identical 2026 context years later. The cache read is **in-process**; the *out-of-process* work that fills it (OCI pull, cosign verify, the CUE subprocess) is in L3.4. The kernel computes in full-precision `decimal` and rounds each result exactly once through the **Money / decimal boundary** (HALF_EVEN at the decimal→cents boundary, [ADR-PC-010 §P1–P2](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)).

Before the new events are written, the **PII crypto-shredding envelope** encrypts the PII payload fields per subject so key destruction is GDPR-Article-17 erasure ([event-store §6.2](./feature-design-event-store-projections.md)). Finally the **event-store access** layer performs the load-bearing `append(stream, expectedVersion, events, outbox_rows)` — event rows and the outbox row in **one local PostgreSQL transaction**, with optimistic concurrency on `(stream_id, sequence_number)` ([ADR-PC-001 §P2](./adrs/ADR-PC-001-event-store-technology.md), [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)). Two build-time disciplines guard this path and so are not drawn as components: a Roslyn analyser bans raw `decimal` rounding outside `Money.FromCents`, and a CI determinism gate rejects any handler that reads the clock, calls out, or otherwise breaks replayability ([event-store §5.3, §10.3](./feature-design-event-store-projections.md)).

### L3.2 — Projection & query: event log → bitemporal read models

![Component — L3.2 Projection & query](./diagrams/c4-l3-projection-query.svg)

<sub>Source: [`diagrams/c4-l3-projection-query.puml`](./diagrams/c4-l3-projection-query.puml)</sub>

The read side derives state from the log. The **projection runtime** reads the event log and dispatches each event to the **family projections** (gold — loaded from the family schema, like handlers): deposit position, accrual schedule, maturity calendar, withholding ledger ([02 §2.3](./02-v1-scope-term-deposits.md)). Projections are pure folds carrying both time dimensions — `valid_time` (when the fact was true) and `transaction_time` (when we recorded it) — so a retroactive `DepositCorrected` leaves *both* "what we thought" and "what we now know" queryable ([event-store §6](./feature-design-event-store-projections.md)). Each projection updates either **synchronously** (inline with the append transaction) or **asynchronously** (a catch-up reader of the log), per projection ([01 §4](./01-product-architecture.md)).

The **accrual-schedule projector** is where the kernel reappears: for a with-a-plan family it computes the ex-ante schedule by calling the *same pure kernel* with a resolved calc-context — so a schedule rebuilt by replay is identical to the original. The **query API** serves as-of / point-in-time reads (and the SSE status stream) from the read models behind Kong. The **snapshot machinery** is performance-only: it triggers per-N-events / at lifecycle boundaries / at calendar boundaries, stamps each snapshot with the last `event_id` it covers for hash-verification, and is discarded-and-rebuilt in the monthly drill — a snapshot is advisory until it has survived that drill ([event-store §8](./feature-design-event-store-projections.md)).

### L3.3 — Messaging & saga: outbox, inbox, orchestration

![Component — L3.3 Messaging & saga](./diagrams/c4-l3-messaging-saga.svg)

<sub>Source: [`diagrams/c4-l3-messaging-saga.puml`](./diagrams/c4-l3-messaging-saga.puml)</sub>

The asynchronous edges and orchestration. The **outbox-relay worker** is the [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) custom polling publisher: it claims rows with `SELECT … FOR UPDATE SKIP LOCKED` and publishes them to **Redpanda**, emitting a publish-lag SLI. Inbound, the **inbox consumer** reads ACL confirmations and saga events off Redpanda and dedups by `message_id` before handing them to the **saga dispatcher** — a small in-process state-machine dispatcher ([ADR-PC-010 §P4](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)) that advances the **saga state machines** (renewal, moratorium application, legacy-SoR transition, and the engine's participation as a *step* in the integration constitution saga, [ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)).

Saga progress is durable the same way state is: `saga_state`, the emitted events, and the outbox rows commit in **one local transaction** (the same `append` from L3.1), so a saga can never advance without its events being durably queued for publication. Outbound domain commands to the **ACL** go over mTLS; their confirmations return asynchronously through Redpanda, closing the loop the inbox started.

### L3.4 — Loading & config: pack/schema load → immutable cache

![Component — L3.4 Loading & config](./diagrams/c4-l3-loading-config.svg)

<sub>Source: [`diagrams/c4-l3-loading-config.puml`](./diagrams/c4-l3-loading-config.puml)</sub>

The genericity and configuration machinery — and the engine's out-of-process boundary. At startup the **family-schema loader** registers a family's event types, pure handlers, projections, and lifecycle state machine into the runtime (the L3.1/L3.2 components) — the loading mechanism that keeps the engine generic ([event-store §3](./feature-design-event-store-projections.md)). The **pack loader/verifier** pulls a pack from the **OCI registry** by digest, verifies its cosign signature (which attests CUE validation already passed in CI, so load is a structural re-parse rather than a full re-validation, [ADR-PC-006 §P3](./adrs/ADR-PC-006-cue-schema-language.md)), records the `pack_version → digest` mapping, and **fails loud** on any mismatch ([ADR-PC-007 §P4](./adrs/ADR-PC-007-signed-yaml-oci-pack.md)). The **rate-sheet resolver** does the same for version-pinned rate sheets ([ADR-PC-008](./adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

All of this populates the **immutable, version-keyed cache** that L3.1's parameter resolver reads *in-process*. That is what isolates every out-of-process call — the OCI pull, the cosign verification, the [CUE validator](./adrs/ADR-PC-006-cue-schema-language.md) Go subprocess — to load time, off the deterministic compute path. This diagram is the answer to "where does the out-of-process work go": here, never in the kernel.
