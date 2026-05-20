# Open Questions

> A living register of deferred architectural decisions. The brief in 00–03 is committal where it can be and open where it cannot; this document is the open side.
>
> Two registers run in parallel:
>
> - **§§1–5** — the original brief-level decisions, opened in the first drafting pass. §3 is now Resolved; §5 has narrowed; §§1, 2, 4 remain open.
> - **Q-I through Q-AO** — questions opened by the design-notes companions, in a continuing letter sequence. Each entry points at its source document.
>
> Future sessions add lettered entries; when one resolves, fold the resolution into the relevant numbered document and annotate the entry here.

---

## §§1–5: Brief-Level Decisions

### 1. Legacy Coexistence Targets

**Context.** The strangler-fig motion in [01 §6](./01-product-architecture.md) and [02 §3](./02-v1-scope-term-deposits.md) requires first-class coexistence with the operating bank's legacy core. The legacy estate has several integration surfaces that may be relevant:

- **The legacy core banking system** (whatever the operating bank runs — a vendor core such as BANKA / Temenos T24 / Oracle Flexcube, a mainframe / AS400-era system, an internally-built stack, or some combination).
- **Mainframe / AS400-era systems** (integration via fixed-format files or middleware) if present.
- **Internal stacks** (per-system integration shape) if present.

The engine's coexistence story is described abstractly in terms of the ACL (per [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md)). The open question is **which specific legacy systems the engine ships first-class adapters for**, vs which are handled bespoke through customer-built adapters on top of the ACL contract.

The decision shapes the v1 engineering effort: a productised connector for the dominant legacy system in the operating bank shortens v1 onboarding; a generic ACL interface keeps the engine portable but pushes more work to the integration side.

**Unblocked by.** An inventory of the operating bank's legacy core systems, the specific integration shape of each (transaction model, idempotency guarantees, batch windows), and a decision on which integrations are first-class vs bespoke. Output: a list (one or two named systems) added to this document and reflected in the engineering roadmap.

---

### 2. IFRS 9 Signal Boundary

**Context.** IFRS 9 implementation is explicitly out of scope (per [00 §4](./00-product-vision.md)). However, the engine *does* feed an external IFRS 9 system, and the signal contract between them is in scope to define. Three signal families are involved:

- **Staging triggers.** Events that move an exposure between IFRS 9 stages (Stage 1 → Stage 2 on significant increase in credit risk; Stage 2 → Stage 3 on default). The engine has the operational data (days past due, restructuring events, watchlist flags); IFRS 9 staging logic consumes them.
- **Days-past-due tracking.** A continuous signal per exposure. The engine maintains it; the IFRS 9 system reads it to drive staging.
- **Restructuring events.** When a contract is modified (rate change, term extension, payment holiday) under financial-difficulty conditions, IFRS 9 has specific treatment. The engine emits a `LoanRestructured` event with the contextual data; the IFRS 9 system interprets the regulatory meaning.

The open question is the **specific schema** of the signal contract. Is it one big event per change (`Stage1To2`, `Stage2To3`)? Or two signals (a continuous days-past-due tracker plus discrete restructuring/forbearance events) from which the IFRS 9 system derives the staging? The latter is more compositional and reusable across IFRS 9 vendors; the former is simpler if the bank uses a single IFRS 9 system that already has a known contract.

The decision interacts with the event catalogue (per [integration_concepts §08](../integration_concepts/08-event-catalog-governance.md)) — once the signals are named, they are public API and hard to change.

**Unblocked by.** An IFRS 9 SME conversation: ideally a risk-quant or model-validation lead inside the operating bank, or a consultant who has integrated several IFRS 9 vendors. Output: a signal-contract section in [02](./02-v1-scope-term-deposits.md) (or in the v2 / v3 scope documents where credit lands) and corresponding events registered in the catalogue.

---

### 3. Time-Travel / Point-in-Time Correctness — **RESOLVED**

**Resolution.** The architectural answer is **event sourcing with bitemporal projections**. The engine's source of truth is the event store; state is derived by deterministic, side-effect-free handlers; projections are bitemporal (every row carries `valid_time` and `transaction_time`); replay is routine, not a recovery scenario. The four time-dimensional capabilities the engine commits to — as-of queries, audit trails, counterfactual replay, forward projection — are properties of the event-sourced model, not features bolted on. Full treatment: [event-store](./feature-design-event-store-projections.md); architectural commitment summarised in [01 §2](./01-product-architecture.md).

The implementation choice (PostgreSQL temporal extensions vs XTDB / datomic-style vs application-level bitemporality on plain Postgres) remains open and is tracked as **Q-X** in the lettered questions section below. The bitemporal-vs-unitemporal commitment is firm; only the mechanism is deferred.

---

### 4. Configurability Depth

**Context.** The agility wedge (per [00 §2](./00-product-vision.md), [01 §3](./01-product-architecture.md)) depends on new products being configuration changes. The open question is the **depth** of the configuration surface — three credible models:

- **Template catalog only.** The engine ships with a bounded catalogue of product templates (term deposit with X variants, Price credit, SAC credit, mortgage, current account, card). New products are template instantiations with parameter overrides. Simplest; safest; tightest scope. Risk: the catalogue is always either too narrow (a product needed is not in it) or too wide (the catalogue is the same complexity the engine was meant to replace).
- **DSL only.** The engine ships with a configuration DSL (cash-flow shape, day-count, compounding, charges, lifecycle hooks) and no templates; every product is composed from primitives. Most flexible; highest learning curve; biggest support surface; risk: the DSL can be used to build products that violate regulatory or commercial constraints the engine is meant to enforce.
- **Both.** Templates for 80% of common products; DSL for the long tail. Probably correct; specific shape needs work. Risks: dual maintenance burden; the boundary between "template" and "DSL extension" is a per-product judgement that may drift.

This is the heart of the wedge, and getting it wrong in either direction kills it. Template-only is too rigid; DSL-only is too unbounded. The "both" answer is correct in shape but undefined in detail.

**Unblocked by.** Prototyping the configuration surface against the v1–v3 product set. The prototype answers: what does the term-deposit configuration look like as a template; what does a "non-standard" deposit (one whose configuration the template cannot express) look like in the DSL; where is the template/DSL boundary. Output: an addendum to [01 §3](./01-product-architecture.md) with worked examples of both shapes and a stated boundary policy.

---

### 5. Operational SLA Calibration for Reconciliation

**Context.** [02 §3](./02-v1-scope-term-deposits.md) describes the happy-path coexistence with the legacy core's current-account module. The unhappy path (engine and legacy disagree about an instance's state) was the original framing of this question; the architectural answer is now in [coexistence §7](./feature-design-strangler-fig-coexistence.md), which specifies three reconciliation flows (settlements outbox vs legacy journal; engine's view of legacy instances vs daily batch file; engine-internal projection rebuild) and names ownership and cadence.

The residual open question is **operational**, not architectural: **what alert thresholds, escalation paths, and tooling does the operating bank's ops function use to action the reconciliation reports?** Specifically:

- How many engine-side orphans per day cross from "operational noise" to "page on-call"? How many cross from "page on-call" to "freeze new constitutions"? The thresholds require a calibration period under real-data load — they cannot be set in advance.
- What's the decision tree for a single legacy-side orphan (a credit in legacy's journal the engine did not emit)? Investigation paths, ownership, time-to-resolution targets.
- What tooling does the ops team use to drill from a daily reconciliation report into specific records on each side? Existing bank tooling, or new tooling owned by the engine team?
- What is the runbook for the first auto-renewal cycle after cutover (see [coexistence §9.3](./feature-design-strangler-fig-coexistence.md) and Q-AD), where the engine sees an unusual constitution-load spike?

This is operational scope tracked here because v1 cannot enter production without it. A demo can hand-wave; a production deployment cannot.

**Unblocked by.** An operations / reconciliation review with the operating bank's ops function: walk through the daily reconciliation process the bank uses today, identify where the three reconciliation flows from [coexistence §7](./feature-design-strangler-fig-coexistence.md) fit, define the alerting thresholds and escalation paths. Output: an operational runbook (not in this repo — it is operating-bank-specific) plus a confirmation in [02 §3](./02-v1-scope-term-deposits.md) that the engine's reconciliation contract is operable as specified. See also Q-AG (reconciliation alert thresholds), Q-AH (legacy batch file contract), Q-AI (channel routing) when folded in.

---

## Q-I through Q-AO: Design-Note Questions

Each design-notes companion opens its own questions in a continuing letter sequence. Skim by letter range; drill into the source for the trade-off space.

### Q-I through Q-Q — from [surface](./feature-design-configuration-surface.md)

Rate sheets and pack vocabulary.

- **Q-I. Negative rates.** Schema allows signed bps; pack constrains. PT pack v1 recommends `tan_basis_points >= 0`. If a EUR retail product runs negative again, the pack relaxes the bound; the engine does not change.
- **Q-J. Rate-sheet typo rollback.** Treasury publishes 350 bps instead of 35 bps and deposits constitute at the wrong rate. Forward-only fix plus an out-of-band compensation flow for affected instances. Commercial risk (customer calls), distinct from the technical rollback mechanics.
- **Q-K. [Retired.]** Previously a SaaS-tenancy question; out of scope under the single-operator framing. The narrower legitimate question — should rate sheets be scopable per business unit (retail vs corporate, brand A vs brand B) — is left as a configuration concern. Letter preserved for cross-reference stability.
- **Q-L. Index sheet sourcing.** Who publishes the Euribor fixings into the engine? Direct from ECB, a market-data vendor, or bank-supplied via the same deploy API with a pluggable upstream feeder. A v3 question, not v1.
- **Q-M. Pack authorship and sign-off model.** Who within the operating bank authors and signs the canonical pack? Engine team alone, engine team plus internal regulatory counsel, or engine team plus an industry working group. Each shape distributes accountability differently when regulation changes and the pack has to follow.
- **Q-N. Breaking-change opt-in mechanics.** When a pack ships with `breaking_changes`, adoption mechanics. Recommend explicit `POST /v1/pack-adoptions` with operator acknowledgement; no silent pack upgrades.
- **Q-O. Pack overrides for business-unit-specific primitives.** Can a business unit define a proprietary primitive override (e.g. a private internal-credit-rating-based stamp-duty calculation). Allowed but discouraged; the engine team maintains only the canonical pack.
- **Q-P. Multi-pack composition.** A v5 cross-border product wants PT primitives for some fields and ES primitives for others. Pick-one-and-inline vs real composition with explicit precedence. v5 question; v1 pack schema reserves a `primitive_overlays` field as no-op.
- **Q-Q. Pack-update internal SLA.** Engine-team-to-product-organisation operational SLA for shipping pack updates within a stated window of a regulatory change. Currently unmodelled.

### Q-R through Q-W — from [authoring](./feature-design-configuration-authoring.md)

Configuration authoring workflow.

- **Q-R. Family-schema split threshold.** What concrete signal triggers a fine-drift split of a bloated family schema? Number of mutually-exclusive sub-blocks, validator error-message length, or explicit quarterly review decision.
- **Q-S. Variant deployment cadence beyond weekly.** Some variants (promotional rate campaigns) plausibly want daily activation windows. Separate flag, separate track, or shorten the deployment train to daily across the board. Sits with the deployment pipeline.
- **Q-T. PM-authored YAML vs PM-driven form UI.** YAML is the variant artefact; a form UI producing YAML is a plausible long-term path. Engine-team-shipped (on engine-release cadence) or product-organisation-owned (independent).
- **Q-U. Risk-review automation.** Risk corridors (rate-band caps, principal-exposure ceilings) are largely encodable. Move some portion of risk review into validator depth 6 (automated risk policy) with human review reserved for flagged exceptions.
- **Q-V. Schema split-merge tooling.** Fine-drift splits a bloated schema; tooling for authoring the split, mapping existing variants to one side, and supporting parallel schemas through the transition is unspecified.
- **Q-W. Multi-pack variant composition.** A variant for a cross-border product needs PT-pack disclosure and ES-pack withholding. Same shape as Q-P, reached from the authoring side. Defer to the same v5-era resolution.

### Q-X through Q-AC — from [event-store](./feature-design-event-store-projections.md)

Event store and bitemporal projections.

- **Q-X. Bitemporal projection implementation choice.** PostgreSQL temporal extensions vs XTDB / datomic-style vs application-level bitemporality on plain Postgres. Decision deferred to a small spike per path; the bitemporal commitment is firm.
- **Q-Y. Regulatory bitemporality confirmation.** Confirmation with the operating bank's compliance and internal-audit functions that PT regulators expect retroactive corrections to be queryable in both time dimensions. If unitemporal is sufficient for v1, projection schemas simplify materially.
- **Q-Z. Replay performance targets and instrumentation.** Cold-replay budgets (5s for with-a-plan, 30s for irregular). Instrumentation, monitoring dashboards, SLA escalation paths sit with the operations runbook. Refined and operationalised by Q-AK below.
- **Q-AA. Storage growth modelling.** Back-of-envelope estimates suggest 500GB–5TB across 10 years; the engine team should produce a real model based on v1 volume, v2–v3 product velocity, and v4 irregular ingestion.
- **Q-AB. GL adapter ownership and contract.** The engine emits raw business events; the GL system needs a small adapter to consume them and produce postings. Adapter shape, ownership, and consumption contract are coordination work with the GL team.
- **Q-AC. Event-store technology selection.** Kurrent / EventStoreDB, Postgres-based, or Redpanda-as-event-store. A small spike per candidate against the synthetic v4-scale load test (see Q-AK) is the proposed path. Refined by Q-AK and [two-modes §6](./feature-design-two-modes-asymmetry.md).

### Q-AD through Q-AJ — from [coexistence](./feature-design-strangler-fig-coexistence.md)

Strangler-fig coexistence (multi-year period of dual operation).

- **Q-AD. Cutover-day load risk.** On the first auto-renewal cycle after cutover, every legacy deposit that renews on a given date lands on the engine as a new constitution. Day-1 load could spike if the legacy book has date clustering. Needs a load-smoothing strategy or explicit cutover scheduling against the actual legacy renewal distribution.
- **Q-AE. Reporting application identity and ownership.** Whether the operating bank already has a downstream reporting application that can consume engine events and legacy batch facts, or whether one has to be built. Shapes the engine's reporting-hook contracts (`bdp_estatisticas_taxas_juro`, `modelo_39`).
- **Q-AF. Read-model latency contract per channel.** Per-channel tolerances for 24-hour-stale legacy-sourced data and where per-channel refresh paths back to legacy are needed. Requires a channel-by-channel review with the channel teams.
- **Q-AG. Reconciliation alert thresholds.** Calibration of "how many mismatches per day cross from noise to incident" for the three reconciliation flows (settlements outbox, legacy state, engine-internal). Requires a calibration period under real-data load. The narrowed scope of Q5 (above) lives here.
- **Q-AH. Legacy batch file contract.** Schema, cutoff times, completeness guarantees, schema-drift coordination protocol. Depends on what the operating bank's legacy core can produce; unblocked by a legacy-extract audit.
- **Q-AI. Channel routing for state-changing operations.** Three credible locations for the routing logic (channel, unified API gateway, read model); the choice should align with the operating bank's existing channel architecture, not introduce a new pattern.
- **Q-AJ. End-of-coexistence trigger for the full bank.** For term deposits alone the answer is mechanical (last instance matured); for the full bank it requires named operational criteria across families.

### Q-AK through Q-AO — from [two-modes](./feature-design-two-modes-asymmetry.md)

Approach C: interfaces for v4, implementations for v1.

- **Q-AK. Synthetic v4-scale load test specification.** Exact workload patterns, exact pass/fail thresholds (sustained TPS, p99 latency per projection type, replay-time per snapshot-coverage scenario), and exact test infrastructure. The test is v1 acceptance, not a future deliverable.
- **Q-AL. Sharding strategy for v4.** Reserved `partition_key` (§5.3 of the source) leaves the v4 sharding shape open — shard sizing, rebalancing approach, cross-shard transaction handling, parallel shard-level replay. v4-time decision; v1 must not foreclose any credible v4 shape.
- **Q-AM. Real-time projection backpressure.** Sync/async projection support is committed; what happens when an async projection falls behind is not specified. Acceptable lag bounds per projection class, alerting thresholds, recovery procedures (replay, parallel projectors, load shedding). v4 makes this urgent.
- **Q-AN. Cross-mode reconciliation.** A v1 deposit maturing into a v4 current account is a cross-family flow. At v4, runs end-to-end inside the engine; the reconciliation contract between the two family schemas and the cross-mode invariant ("principal lands exactly once") are deferred to v4 design.
- **Q-AO. Operational tooling asymmetry.** With-a-plan tools (manual lookup, batch reports, accrual investigation) vs irregular tools (real-time transaction search, fraud-screening review, overdraft incident triage). The MCP server and admin APIs must accommodate both additively; bifurcating into two tools would break the unification claim.

---

## Adding to This Register

Shape of a useful entry:

- A named question (one-line summary).
- **Context** — enough that a cold reader understands the trade-off space.
- **Unblocked by** — the specific input that would let someone make the decision.

Mark an entry **Resolved** (with the resolution noted) when the answer has been folded into the relevant numbered document.
