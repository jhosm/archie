# Open Questions

> A living register of deferred architectural decisions. Each entry has enough context that a future session knows what would unblock it. The point of this document is honesty: the brief in 00–03 is consciously committal where it can be, and consciously open where it cannot. Open decisions deferred deliberately are not the same as decisions overlooked.
>
> Future sessions add to this register, refine entries, and — when one is resolved — fold the resolution into the relevant numbered document and remove (or annotate) the entry here.

---

## 1. Legacy Coexistence Targets

**Context.** The strangler-fig motion in [01-product-architecture §5](./01-product-architecture.md) and [02-v1-scope §3](./02-v1-scope-term-deposits.md) requires first-class coexistence with the operating bank's legacy core. The legacy estate has several integration surfaces that may be relevant:

- **The legacy core banking system** (whatever the operating bank runs — a vendor core such as BANKA / Temenos T24 / Oracle Flexcube, a mainframe / AS400-era system, an internally-built stack, or some combination).
- **Mainframe / AS400-era systems** (integration via fixed-format files or middleware) if present.
- **Internal stacks** (per-system integration shape) if present.

The engine's coexistence story is described abstractly in terms of the ACL in [integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md). The open question is **which specific legacy systems the engine ships first-class adapters for**, vs which are handled bespoke through customer-built adapters on top of the ACL contract.

The decision shapes the v1 engineering effort: a productised connector for the dominant legacy system in the operating bank shortens v1 onboarding; a generic ACL interface keeps the engine portable but pushes more work to the integration side.

**Unblocked by.** An inventory of the operating bank's legacy core systems, the specific integration shape of each (transaction model, idempotency guarantees, batch windows), and a decision on which integrations are first-class vs bespoke. Output: a list (one or two named systems) added to this document and reflected in the engineering roadmap.

---

## 2. IFRS 9 Signal Boundary

**Context.** IFRS 9 implementation is explicitly out of scope ([00-product-vision §4](./00-product-vision.md)). However, the engine *does* feed an external IFRS 9 system, and the signal contract between them is in scope to define. Three signal families are involved:

- **Staging triggers.** Events that move an exposure between IFRS 9 stages (Stage 1 → Stage 2 on significant increase in credit risk; Stage 2 → Stage 3 on default). The engine has the operational data (days past due, restructuring events, watchlist flags); IFRS 9 staging logic consumes them.
- **Days-past-due tracking.** A continuous signal per exposure. The engine maintains it; the IFRS 9 system reads it to drive staging.
- **Restructuring events.** When a contract is modified (rate change, term extension, payment holiday) under financial-difficulty conditions, IFRS 9 has specific treatment. The engine emits a `LoanRestructured` event with the contextual data; the IFRS 9 system interprets the regulatory meaning.

The open question is the **specific schema** of the signal contract. Is it one big event per change (`Stage1To2`, `Stage2To3`)? Or two signals (a continuous days-past-due tracker plus discrete restructuring/forbearance events) from which the IFRS 9 system derives the staging? The latter is more compositional and reusable across IFRS 9 vendors; the former is simpler if the bank uses a single IFRS 9 system that already has a known contract.

The decision interacts with the event catalogue in [integration_concepts/08](../integration_concepts/08-event-catalog-governance.md) — once the signals are named, they are public API and hard to change.

**Unblocked by.** An IFRS 9 SME conversation: ideally a risk-quant or model-validation lead inside the operating bank, or a consultant who has integrated several IFRS 9 vendors. Output: a signal-contract section in [02-v1-scope](./02-v1-scope-term-deposits.md) (or in the v2 / v3 scope documents where credit lands) and corresponding events registered in the catalogue.

---

## 3. Time-Travel / Point-in-Time Correctness

**Context.** Regulated banking products require the ability to reconstruct the state of an account at any past point in time — for audit, for dispute resolution, for regulator inquiries, for IFRS 9 backtesting. Two credible implementation approaches:

- **Event sourcing.** The subledger is rebuildable from the event stream alone; point-in-time queries are answered by replaying events up to the chosen timestamp. Strong audit story by construction; performance characteristics need careful design (snapshots, projections); operational complexity higher.
- **Snapshot and journal.** The subledger stores current state plus a journal of all state changes, each timestamped and immutable. Point-in-time queries are answered by walking the journal backwards from current state. Simpler operational model; weaker reconstructibility guarantee (the journal has to be complete).

Both can satisfy regulatory point-in-time requirements; they differ in operational shape, in cost, and in the failure modes they expose.

The choice has architectural consequences. Event sourcing aligns naturally with the event-emission patterns from [integration_concepts/04](../integration_concepts/04-plumbing-patterns.md) (the outbox) — the events that go onto the bus are the same events that rebuild the state. Snapshot-and-journal is simpler but creates a duality between the subledger's journal and the integration event stream that has to be maintained.

**Unblocked by.** An internal-audit-requirements discussion with the operating bank's audit function, and/or a Banco de Portugal supervisory contact. Output: a decision recorded in [01-product-architecture §1](./01-product-architecture.md) (or a separate subledger-design memo) and corresponding subledger-shape choices in the engineering roadmap.

---

## 4. Configurability Depth

**Context.** The agility wedge ([00-product-vision §2](./00-product-vision.md), [01-product-architecture §2](./01-product-architecture.md)) depends on new products being configuration changes. The open question is the **depth** of the configuration surface — three credible models:

- **Template catalog only.** The engine ships with a bounded catalogue of product templates (term deposit with X variants, Price credit, SAC credit, mortgage, current account, card). New products are template instantiations with parameter overrides. Simplest; safest; tightest scope. Risk: the catalogue is always either too narrow (a product needed is not in it) or too wide (the catalogue is the same complexity the engine was meant to replace).
- **DSL only.** The engine ships with a configuration DSL (cash-flow shape, day-count, compounding, charges, lifecycle hooks) and no templates; every product is composed from primitives. Most flexible; highest learning curve; biggest support surface; risk: the DSL can be used to build products that violate regulatory or commercial constraints the engine is meant to enforce.
- **Both.** Templates for 80% of common products; DSL for the long tail. Probably correct; specific shape needs work. Risks: dual maintenance burden; the boundary between "template" and "DSL extension" is a per-product judgement that may drift.

This is the heart of the wedge, and getting it wrong in either direction kills it. Template-only is too rigid; DSL-only is too unbounded. The "both" answer is correct in shape but undefined in detail.

**Unblocked by.** Prototyping the configuration surface against the v1–v3 product set. The prototype answers: what does the term-deposit configuration look like as a template; what does a "non-standard" deposit (one whose configuration the template cannot express) look like in the DSL; where is the template/DSL boundary. Output: an addendum to [01-product-architecture §2](./01-product-architecture.md) with worked examples of both shapes and a stated boundary policy.

---

## 5. Operational SLA Calibration for Reconciliation

**Context.** [02-v1-scope §3](./02-v1-scope-term-deposits.md) describes the happy-path coexistence with the legacy core's current-account module. The unhappy path (engine and legacy disagree about an instance's state) was the original framing of this question; the architectural answer is now in [feature-design-strangler-fig-coexistence §7](./feature-design-strangler-fig-coexistence.md), which specifies three reconciliation flows (settlements outbox vs legacy journal; engine's view of legacy instances vs daily batch file; engine-internal projection rebuild) and names ownership and cadence.

The residual open question is **operational**, not architectural: **what alert thresholds, escalation paths, and tooling does the operating bank's ops function use to action the reconciliation reports?** Specifically:

- How many engine-side orphans per day cross from "operational noise" to "page on-call"? How many cross from "page on-call" to "freeze new constitutions"? The thresholds require a calibration period under real-data load — they cannot be set in advance.
- What's the decision tree for a single legacy-side orphan (a credit in legacy's journal the engine did not emit)? Investigation paths, ownership, time-to-resolution targets.
- What tooling does the ops team use to drill from a daily reconciliation report into specific records on each side? Existing bank tooling, or new tooling owned by the engine team?
- What is the runbook for the first auto-renewal cycle after cutover (see [feature-design-strangler-fig-coexistence §9.3](./feature-design-strangler-fig-coexistence.md) and Q-AD), where the engine sees an unusual constitution-load spike?

This is operational scope tracked here because v1 cannot enter production without it. A demo can hand-wave; a production deployment cannot.

**Unblocked by.** An operations / reconciliation review with the operating bank's ops function: walk through the daily reconciliation process the bank uses today, identify where the three reconciliation flows from [feature-design-strangler-fig-coexistence §7](./feature-design-strangler-fig-coexistence.md) fit, define the alerting thresholds and escalation paths. Output: an operational runbook (not in this repo — it is operating-bank-specific) plus a confirmation in [02-v1-scope §3](./02-v1-scope-term-deposits.md) that the engine's reconciliation contract is operable as specified. See also Q-AG (reconciliation alert thresholds), Q-AH (legacy batch file contract), Q-AI (channel routing) when folded in.

---

## Adding to This Register

Future sessions are expected to add to this list. The shape of a useful entry is:

- A **named** question (one line summary).
- **Context** — enough that a reader cold-reading the document understands the trade-off space.
- **Unblocked by** — the specific input that would let someone make the decision.

Entries should be removed (or marked **Resolved**, with the resolution noted) when the question is answered and the answer has been folded into the relevant numbered document.
