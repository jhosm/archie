# Open Questions

> A living register of deferred architectural decisions. The brief in 00–03 is committal where it can be and open where it cannot; this document is the open side.
>
> Two registers run in parallel:
>
> - **§§1–8** — brief-level decisions. §§1–5 are the original drafting-pass set; §§6–8 surfaced in a later register audit. §§3 and 4 are Resolved; §5 has narrowed and largely folded into Q-AG; §1 has a structured unblocking agenda pending the legacy-inventory meeting; §7 has a recommended v1 position pending DPO confirmation; §§2, 6, 8 remain open.
> - **Q-I through Q-BC** — lettered questions, in a continuing letter sequence. Q-I–Q-AO from the original five design-note companions; Q-AP–Q-AT from the moratoria design note; Q-AU–Q-BC collect cross-cutting and integration-shape gaps that none of the existing design notes own.
>
> Future sessions add lettered entries; when one resolves, fold the resolution into the relevant numbered document and annotate the entry here.

---

## §§1–8: Brief-Level Decisions

### 1. Legacy Coexistence Targets — **AGENDA SPECIFIED; PENDING LEGACY INVENTORY MEETING**

**Context.** The strangler-fig motion in [01 §6](./01-product-architecture.md) and [02 §3](./02-v1-scope-term-deposits.md) requires first-class coexistence with the operating bank's legacy core. The open question is **which specific legacy systems the engine ships first-class adapters for**, vs which are handled bespoke through customer-built adapters on top of the ACL ([integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md)). The decision shapes the v1 engineering effort: a productised connector for the dominant legacy system shortens v1 onboarding; a generic ACL interface keeps the engine portable but pushes work to the integration side. The gap is not closable by architectural reasoning — only the bank knows its estate.

**Unblocking conversation.** Inventory questionnaire — ten dimensions per system (identity, transaction model, idempotency, batch windows, API surface, settlement contract, data export, outage profile, customer-master role, GL coupling), three-way classification per system (first-class adapter / generic ACL-only / out-of-scope at v1), five named decision outputs, and pre-meeting preparation guidance — in [coexistence §12](./feature-design-strangler-fig-coexistence.md). The legacy current-account module is the load-bearing first-class candidate by virtue of [02 §3](./02-v1-scope-term-deposits.md); the commitment is "one or two named systems" first-class, with the rest handled by the generic ACL.

**Output.** A named list (one or two systems) folded into this entry as the position, and a new sub-section of [01 §6](./01-product-architecture.md) declaring the first-class adapters as in-scope for v1 engineering. Interacts with Q-AH (legacy batch file contract — same source system) and Q-AB (GL adapter ownership — see §12.1's GL-coupling dimension).

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

### 4. Configurability Depth — **RESOLVED**

**Resolution.** The three options of the original framing — template-only, DSL-only, or both — are all rejected. The configuration model is **typed family schemas with variants, evolving under coarse-start fine-drift**. The schema is the boundary: what the schema permits is in-scope at the variant layer (weekly cadence); what it does not permit waits for schema fine-drift (quarterly) or a new primitive (months) or is declined at the roadmap layer. There is no DSL escape hatch — that path collapses the cadence-separation invariant on which the agility wedge depends.

A schema with union types, optional fields, range-bounded scalars, and pack-bound primitives is structurally different from a static template catalogue (and is what the PT v1 `term_deposit` schema already is, absorbing the three interest variants from [02 §2.1](./02-v1-scope-term-deposits.md), flat-vs-stepped rates, banded early termination, and the role split from [surface §2.2](./feature-design-configuration-surface.md) in one typed contract). The long tail is future schema territory, not DSL territory.

The four-outcome operational procedure (variant reshape / schema fine-drift / primitive release / roadmap-layer decline), the worked examples that exercise the boundary at four positions, and the reasoning that rejects each of the original three options are in [authoring §9](./feature-design-configuration-authoring.md). The wedge falsifiability claim ([authoring §7.1](./feature-design-configuration-authoring.md)) is preserved: a variant that "needs" engine code reveals a schema gap or a primitive gap, never a DSL bypass.

Two related questions remain open: **Q-R** (the precise signal that triggers schema fine-drift) and **Q-T** (raw-YAML vs form-UI authoring tooling). Both are below the boundary-policy level and do not reopen the depth question.

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

### 6. Customer-Master Ownership During Coexistence

**Context.** [01 §6](./01-product-architecture.md) and [coexistence](./feature-design-strangler-fig-coexistence.md) describe the engine working with opaque customer-IDs owned by the legacy core's customer-master system. This holds for as long as legacy runs. The open question is **what happens at the strangler-fig endpoint** — when the last legacy product family migrates and the operational case for keeping the legacy customer-master service alive disappears.

Three credible answers:

- **Legacy customer-master survives indefinitely as a stub.** Cheapest in the short term; the bank carries a legacy dependency past the point where any product runs on legacy. Each integration that resolves customer IDs (channels, KYC, marketing, AML) still calls a system that has no other purpose.
- **Customer master migrates to a new owning system.** Could be a CRM platform the bank already runs, a new dedicated customer-master service, or absorbed into a CDP. Requires a customer-side cutover analogous to a product-family cutover, with its own coexistence period and its own reconciliation flows.
- **The engine absorbs customer master.** Out of scope as currently framed ([00 §4](./00-product-vision.md) puts KYC and onboarding upstream of the engine). Re-opening expands engine scope materially and unwinds part of the wedge — the engine becomes "core banking + customer master" rather than "product engine."

The decision is structurally peer to [§1](#1-legacy-coexistence-targets): both shape what the engine can unilaterally retire and what dependencies survive past the v4-equivalent cutover. Folding it into one of the existing design notes is premature because none of them claim customer-master as a topic.

**Unblocked by.** A customer-master architecture review with whoever owns customer data today (typically a CRM or CIF function inside the bank), plus a position from the channels function on what they can tolerate. Output: a stated position in [01](./01-product-architecture.md) or a new companion design note on customer-master coexistence, plus a corresponding answer at Q-AJ (end-of-coexistence trigger) and an unblocking input for Q-BA (cutover mechanism).

---

### 7. GDPR Erasure vs Event-Sourcing Immutability — **POSITION; PENDING DPO CONFIRMATION**

**Position.** v1 commits to **crypto-shredding**: PII fields encrypted per data subject under a per-subject key; erasure = key destruction; cipher-text remains in the event log, plaintext is unrecoverable. Structural fields (amounts, dates, lifecycle transitions) remain in the clear, so handlers and projections continue to operate over erased records exactly as they do over live ones — PII fields return null instead of plaintext. The audit trail after erasure shows "an event occurred at this transaction_time; payload PII is unrecoverable due to subject erasure," which is the GDPR-compliant audit state, not a gap. Full treatment, including the per-event PII annotation requirement and the field-level encryption envelope constraint on the §6.1 candidate paths: [event-store §6.2](./feature-design-event-store-projections.md). PT GDPR transposition (Lei 58/2019) is in force at v1; the choice cannot be deferred to a later phase.

**Residual.** DPO and compliance confirmation that crypto-shredding satisfies the operating bank's interpretation of Article 17 in conjunction with PT banking-record retention obligations (10-year accounting, 7-year AML). Conversation agenda — worked retention-vs-erasure scenario, three-path foreclosure reference list, four named decision outputs — in [event-store §6.4](./feature-design-event-store-projections.md). Q-Y below is the same conversation from the bitemporality angle; both are unblocked by the same meeting. If the DPO vetoes crypto-shredding, the v1 fallback is PII off-store; tombstoning is rejected.

---

### 8. Pack-Effective-Date Semantics for In-Flight Contracts

**Context.** [03 §Pack Maintenance](./03-roadmap.md) commits to prospective pack changes: a pack update applies from a `pack_effective_date`, the engine carries the effective pack version per account, and historical reconstructions remain consistent. The unanswered question is **whether "pack version" pins at constitution or floats per flow**.

Concretely: a term deposit constituted 2026-09-01 under PT pack v1.3 (28% IRS withholding) matures 2027-03-01. The 2027 Budget Law (effective 2027-01-01) changes the rate to 27% via PT pack v1.4. The maturity-day withholding flow is computed 2027-03-01 — under which pack version?

- **Pin-at-constitution.** Every flow on the instance uses pack v1.3 for the instance's lifetime. Simple; deterministic; matches the "instance carries its pack version" claim verbatim. Costs: a budget-law rate change does not flow through to existing instances, which is operationally surprising and may not match how the bank's tax obligations actually work.
- **Float-per-flow.** Each flow looks up the pack effective on the flow's value-date. Matches typical regulatory expectation (a withholding event is taxed under the rules in force on the event date). Costs: harder to reproduce historical state; replay must reconstruct pack-version-at-flow-date, which requires the pack registry to be queryable bitemporally as well.
- **Per-primitive policy.** Some primitives pin (cash-flow shape, day-count — these *define* the instrument); others float (withholding rate, regulatory disclosure templates — these track regulation in force). Probably correct in shape; the categorisation is detailed pack-vocabulary design work and the boundary may itself shift under regulator pressure.

This interacts with [§4](#4-configurability-depth) (configurability depth): the pack vocabulary has to be able to *express* which primitives pin and which float, and that's not yet in the surface design. It also interacts with Q-N (breaking-change opt-in mechanics) — a primitive that floats automatically is, in effect, a non-opt-in pack change for affected flows.

**Unblocked by.** A pack-design session with the regulatory and tax leads inside the operating bank, plus a worked example for each PT pack primitive (day-count, withholding rate, TANB/TANL split, BdP reporting schema, disclosure templates). Output: a per-primitive pin-or-float annotation in the PT pack manifest ([surface §3.4](./feature-design-configuration-surface.md)) and an addendum to [03 §Pack Maintenance](./03-roadmap.md) stating the per-primitive policy as part of the pack contract.

---

## Q-I through Q-BC: Lettered Questions

Q-I–Q-AO are opened by the design-notes companions, one block per companion. Q-AP onward collect gaps the design notes do not own — integration shapes the brief declares in-scope without a companion document, plus operational and strategic peers of the existing operational questions. Skim by letter range; drill into the source for the trade-off space.

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

- **Q-X. Bitemporal projection implementation choice.** PostgreSQL temporal extensions vs XTDB / datomic-style vs application-level bitemporality on plain Postgres. Spike specification — 5-day timebox per path, four-deliverable scope (deposit-position projection end-to-end, per-subject PII encryption envelope, cold-replay performance run, operational profile doc), six scoring criteria in priority order (correctness on forced correction round-trip, GDPR erasure compatibility per §7, DR/RTO shape per Q-AY, cold-replay time vs §8.2 target, operational fit, query ergonomics) — in [event-store §6.3](./feature-design-event-store-projections.md). Spike runs only after Q-Y returns: if Q-Y confirms bitemporal is required, scoring proceeds as specified; if not, criteria 1 and 6 fall away and the choice collapses to operational fit. The bitemporal commitment is firm; only the mechanism is deferred.
- **Q-Y. Regulatory bitemporality confirmation.** Confirmation with the operating bank's compliance and internal-audit functions that PT regulators expect retroactive corrections to be queryable in both time dimensions. Conversation agenda — attendees (compliance lead, internal audit lead, DPO, engine technical lead), worked retroactive-correction scenario (€10k-vs-€100k clerk error), retention-vs-erasure scenario, three-path foreclosure reference list, four named decision outputs — in [event-store §6.4](./feature-design-event-store-projections.md). The meeting covers Q-Y and the §7 DPO question simultaneously and unblocks the Q-X spike. If unitemporal is sufficient for v1, the Q-X spike scoring weights shift away from bitemporal-specific criteria; if forbidden, projection schemas simplify materially.
- **Q-Z. Replay performance targets and instrumentation.** Cold-replay budgets (5s for with-a-plan, 30s for irregular). Instrumentation, monitoring dashboards, SLA escalation paths sit with the operations runbook. Refined and operationalised by Q-AK below.
- **Q-AA. Storage growth modelling.** Back-of-envelope estimates suggest 500GB–5TB across 10 years; the engine team should produce a real model based on v1 volume, v2–v3 product velocity, and v4 irregular ingestion.
- **Q-AB. GL adapter ownership and contract.** The engine emits raw business events; the GL system needs a small adapter to consume them and produce postings. Adapter shape, ownership, and consumption contract are coordination work with the GL team.
- **Q-AC. Event-store technology selection — RESOLVED.** PostgreSQL-based event store, decided in [ADR-PC-001](./adrs/ADR-PC-001-event-store-technology.md). The decisive force was outbox co-location with [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) combined with the bitemporal-projections, field-level PII crypto-shredding, and snapshot commitments — Kurrent breaks atomic event-append + outbox-write, and Redpanda-as-event-store breaks bitemporal queries, per-field PII crypto, and transactional snapshots. The Q-AK synthetic v4-scale load test ([two-modes §8](./feature-design-two-modes-asymmetry.md)) remains as the v1 acceptance gate against the chosen PG topology; the technology decision is settled.

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

- **Q-AK. Synthetic v4-scale load test specification — SPECIFIED; pending operator calibration.** Workload pattern (event mix, daily/monthly/annual peak structure, sync/async projection classification), pass/fail thresholds (latency p50/p95/p99 per projection class, sustained 250 TPS for 24h, burst 1000 TPS for 15min, replay budgets, reliability invariants, no projection-rebuild divergence), test infrastructure (engine-team-owned rig on production-shaped hardware, harness through production APIs, standard observability, every-RC cadence), determinism requirements (seeded RNG, injected clock), and three explicit non-goals (Q-AL sharding, Q-AM backpressure, Q-AN cross-mode invariant) — fully laid out in [two-modes §8](./feature-design-two-modes-asymmetry.md). Residual is the operator calibration in §8.1 (active accounts `N_acct`, active cards `N_card`, annual event volume `E_year`); spec shape is independent of absolute size. The test is v1 acceptance, not a future deliverable.
- **Q-AL. Sharding strategy for v4.** Reserved `partition_key` (per [two-modes §5.3](./feature-design-two-modes-asymmetry.md)) leaves the v4 sharding shape open — shard sizing, rebalancing approach, cross-shard transaction handling, parallel shard-level replay. v4-time decision; v1 must not foreclose any credible v4 shape.
- **Q-AM. Real-time projection backpressure.** Sync/async projection support is committed; what happens when an async projection falls behind is not specified. Acceptable lag bounds per projection class, alerting thresholds, recovery procedures (replay, parallel projectors, load shedding). v4 makes this urgent.
- **Q-AN. Cross-mode reconciliation.** A v1 deposit maturing into a v4 current account is a cross-family flow. At v4, runs end-to-end inside the engine; the reconciliation contract between the two family schemas and the cross-mode invariant ("principal lands exactly once") are deferred to v4 design.
- **Q-AO. Operational tooling asymmetry.** With-a-plan tools (manual lookup, batch reports, accrual investigation) vs irregular tools (real-time transaction search, fraud-screening review, overdraft incident triage). The MCP server and admin APIs must accommodate both additively; bifurcating into two tools would break the unification claim.

### Q-AP through Q-AT — from [moratoria](./feature-design-moratoria-and-forbearance.md)

Payment moratoria and EBA forbearance on credit instances (v2+).

- **Q-AP. Moratorium catalogue in PT pack v2.** Which historical and live moratoria the v2 pack ships with. Candidates: DL 10-J/2020 (COVID — expired but useful for testing and audit-trail completeness), DL 22-C/2021 (extension regime), and a generic disaster-moratorium template that future *Decreto-Lei*s bind to as they ship. Decision depends on the operating bank's audit-trail requirements for expired moratoria and on the template-vs-specific ratio.
- **Q-AQ. Bulk-command authorisation and approval model.** A bulk moratorium command can affect thousands of instances and millions of euros of expected cash flow. Authorisation requires more than the standard operator token — probably a two-person rule with explicit legal-basis evidence and a mandatory dry-run gate. Specifics are operating-bank policy; the engine must enforce *some* scheme by default.
- **Q-AR. Eligibility-check primitive ownership.** Eligibility checks (e.g. `dl_10j_2020_eligibility`) are pack-bound primitives but encode legal interpretation. Same shape as Q-M (pack authorship and sign-off), surfaced from the eligibility angle — engine team alone, plus internal regulatory counsel, or plus an industry working group.
- **Q-AS. TAEG re-disclosure timing.** When the moratorium ends and the schedule is recomputed, re-disclosure of TAEG via SECCI/FINE has a timing question — is the customer disclosed *before* the new schedule takes effect (giving an opt-out window) or *at* the moment it takes effect? Pack-defined per legal basis; PT default needs an explicit choice.
- **Q-AT. Cross-moratorium handling.** An instance receives a second moratorium before the first ends (e.g. flood after pandemic). Engine semantics: nested application is rejected at the command layer; revoke-and-replace is the path. The pack-and-policy-level question is whether the legal-basis combination supports it. Probably pack-defined per pair of bases.

### Q-AU through Q-BC — cross-cutting and integration-shape gaps

Gaps the existing design notes do not own. Some are integration shapes the brief declares in-scope (per [00 §4](./00-product-vision.md)) but have no companion design note; others sit at the engine ↔ operator boundary, peer to Q5, Q-AG, Q-AJ.

- **Q-AU. AML / KYC signal contract.** [00 §4](./00-product-vision.md) puts AML and KYC out of scope as products but in-scope as integration shapes. The engine emits a constitution event; an AML system may need to gate or post-flag it. Two credible shapes: (a) constitution completes and AML reviews asynchronously, with a compensation flow if AML rejects; (b) AML pre-gates constitution as a synchronous check on the saga. The two have materially different cancellation semantics and different requirements on the legacy seam ([coexistence](./feature-design-strangler-fig-coexistence.md)). Peer in shape to Q-AB (out-of-scope system, in-scope integration contract).
- **Q-AV. Customer-communications emit contract.** Pack-shipped disclosure templates (FIN, SECCI, FINE, maturity notices, annual IRS withholding statements) are specified in [surface §3.4](./feature-design-configuration-surface.md); the *trigger* and *delivery* contract is not. Candidates: engine emits `NotificationDue` events per template-ref and a separate system renders and delivers; pack-side renderer with channel-side delivery; channel-side both. Choice determines whether the engine carries a notification-state projection (sent/acked/bounced) or treats notifications as fire-and-forget.
- **Q-AW. Tax-engine pluggability vs in-pack rules.** PT pack v1 treats 28% IRS withholding as an in-pack primitive computed inline. Some banks run external tax engines (vendor or in-house) for cross-border, non-resident, or treaty-relief cases. Open question: is tax always an in-pack primitive, or can a pack delegate to an external tax engine through the ACL? Forced earlier than Q-P (multi-pack composition) if any v1–v4 product touches non-resident withholding (offshore depositor, EU resident under DAC6, etc.).
- **Q-AX. Regulatory-reporting inventory per phase.** Q-AE asks who owns *a* reporting application; the prior question — the complete named-report inventory v1–v4 must produce — is unanswered. Candidates so far: modelo 39 (annual IRS withholding statements), BdP Aviso 8/2009 (deposit-rate statistics), BdP central de responsabilidades (credit-bureau reporting, from v2), DGSD 2014/49/EU reporting, FATCA / CRS reporting, BdP estatísticas de taxas de juro. Each needs a per-contract design (signal shape, cadence, completeness guarantees, schema-drift protocol).
- **Q-AY. DR / RTO / RPO and event-store recovery.** [event-store](./feature-design-event-store-projections.md) makes replay routine and treats it as the integrity story; it does not name an RTO for an event-store-volume loss. Backup cadence (continuous, hourly, daily snapshots), off-site replication topology, recovery-time targets, cold-replay budget under a *recovery* scenario (distinct from the benchmark in Q-Z). Production-blocking at v1 cutover; the engine cannot enter production without a named recovery position.
- **Q-AZ. "Non-core core" stopping criteria.** [03 §v4 stance](./03-roadmap.md) explicitly allows the bank to stop at v1–v3 and keep current accounts on legacy DDA. The criteria for that stopping decision are not named. Candidates: aggregate dual-operation operating cost above a stated threshold; legacy DDA support EOL; integration-brittleness incidents per quarter; organisational appetite signalled by senior stakeholders. Without named criteria, "optional in practice" decays into unintentional drift — the bank ends up with a permanent two-core estate by accident rather than by decision.
- **Q-BA. Customer-master cutover mechanism.** If §6 lands a brief-level position, the operational mechanism is a separate design question — same shape as Q-AD (cutover-day load risk) but for customer records rather than product instances. Qualitatively harder because every other system holds customer references; a customer-ID change cascades across channels, payments, marketing, regulatory reporting. Probably needs a long alias-table period during which both IDs resolve.
- **Q-BB. Data-residency policy per pack.** PT pack data hosted where, ES pack data hosted where, each operator's regulator imposing supervisory expectations on regulated-data location, GDPR baseline, operating-bank policy. Touches the [00 §5](./00-product-vision.md) deployment model: single-codebase does not imply single-deployment, and per-pack residency may demand per-pack deployment topology. v5+ urgency; v1 should not foreclose any credible v5 residency shape — specifically, the event store should not assume single-region storage.
- **Q-BC. Build-vs-buy revisit trip-wires.** [00 §1.5](./00-product-vision.md) commits the bank to revisit build-vs-buy at any point, but does not name what fires the conversation. Candidates: aggregate v1–v3 calendar slip beyond a stated multiple of plan; pack-maintenance staffing gap persisting beyond a stated quarter count; integration-asset value re-estimated downward by an external audit; vendor product capability inflection (a vendor ships something materially closer to the engine's wedge). Without trip-wires, the revisit is reactive — a steering-committee mood swing — rather than evidence-driven.

---

## Adding to This Register

Shape of a useful entry:

- A named question (one-line summary).
- **Context** — enough that a cold reader understands the trade-off space.
- **Unblocked by** — the specific input that would let someone make the decision.

Mark an entry **Resolved** (with the resolution noted) when the answer has been folded into the relevant numbered document.
