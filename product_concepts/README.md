# product_concepts/ — Core Banking Product Engine

A documentation series defining a configurable core banking product engine — a product brief, not a system design. It sits between the other two series in this repository:

- [financial_concepts/](../financial_concepts/banking_products_financial_mathematics.md) answers **what math is correct** — cash flows, present value, the unified equation that every retail banking product obeys.
- **product_concepts/ (this series)** answers **what configurable product implements that math** — the engine, the configuration surface, the regulatory pack, the v1 slice, the roadmap.
- [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) answers **how that product integrates with the bank** — events, sagas, ACL, CQRS, the operational backbone.

The engine takes the cash-flow primitive from financial_concepts §9.2 as its single architectural insight and uses it to collapse every product family — deposits, credits, mortgages, current accounts, cards — into one engine with a swappable configuration surface and a swappable regulatory pack. The integration backbone is inherited from integration_concepts/, not redefined here.

The operating organization is an incumbent Portuguese bank modernising on a strangler-fig adoption path: a single product line moves onto the new engine while the legacy core continues to run the rest. Geography expands PT → ES → EU. The v1 slice is *depósito a prazo* (Portuguese term deposit) — the smallest surface that exercises both the engine and the PT regulatory pack end-to-end.

The series is short by design: a vision one-pager, an architectural thesis, a concrete v1 slice, a sequenced roadmap, and a register of deferred decisions. The discipline of the brief depends on staying out of GL, IFRS 9, channels, payments rails, KYC, fraud, and onboarding — those are explicitly someone else's problem.

---

## Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./00-product-vision.md) | Product Vision | The one-pager: problem, wedge, in/out of scope, strategic frame |
| [01](./01-product-architecture.md) | Product Architecture | The architectural thesis: cash-flow primitive, event store and projections, configuration surface, two families, regulatory pack, integration seam |
| [02](./02-v1-scope-term-deposits.md) | v1 Scope — Term Deposits | Why term deposits first, in-scope features, PT regulatory features, projections, event contract (family-specific + cross-cutting), coexistence with legacy DDA |
| [03](./03-roadmap.md) | Roadmap | Sequenced product-family + geography expansion: PT term deposits → PT credit → PT mortgage → PT current accounts/cards → ES → EU |
| [04](./04-open-questions.md) | Open Questions | Deferred architectural decisions with context and unblocking notes — legacy coexistence targets, IFRS 9 signal boundary, configurability depth, operational SLA calibration for reconciliation (time-travel is resolved). Carries a lettered question series Q-I through Q-AO accumulated from the design-notes companions, each entry pointing back at its source document. |

### Design notes (companion to the brief, not part of the numbered series)

Each design note deepens specific sections of the numbered brief. Where a design note's conclusions are load-bearing for the brief, the brief itself carries the commitment and points back here for the full treatment. The lettered open questions opened by these documents live in [04-open-questions](./04-open-questions.md).

| File | What It Covers |
|---|---|
| [feature-design-configuration-surface](./feature-design-configuration-surface.md) | Deepens §01 §3 and §01 §5: rate sheets as a price layer separate from product structure (constitution-time binding, index-sheet variant for variable-rate products, validator invariants, lifecycle); the pack vocabulary as a typed jurisdiction-scoped vocabulary (three layers — engine / pack / config; pack manifest shape; pinning invariant; retroactive-change mechanics; distribution and signing; engine-pack compatibility matrix; sealed test corpus). Opens Q-I through Q-Q. |
| [feature-design-configuration-authoring](./feature-design-configuration-authoring.md) | Deepens §01 §3 from the authoring angle: the three authoring layers (engine primitives, family schemas, variants), the variant authoring/review workflow, the validator's five depths, schema-version pinning parallel to pack pinning, the falsifiable agility wedge (zero engine code per variant; ≤ 5 working days PM commit to production), and the parallel-ES-pack-track roadmap consequence. Opens Q-R through Q-W. |
| [feature-design-event-store-projections](./feature-design-event-store-projections.md) | Deepens §00 §3, §01 §2, and §02 §2.3: specifies the event store + bitemporal projections as the engine's source-of-truth model — the four time-dimensional capabilities, strict engine-vs-family separation, the cross-cutting vs family-specific event taxonomy (5 generic engine events + 3 additional family-specific deposit events beyond the v1 happy path), handler discipline, replay reconciliation, snapshot strategy, GL coupling, and the risk mitigations for moderate event-sourcing experience. Resolves Open Question 3 (time-travel). Opens Q-X through Q-AC. |
| [feature-design-strangler-fig-coexistence](./feature-design-strangler-fig-coexistence.md) | Deepens §01 §6 and §02 §3: specifies coexistence as a multi-year period with start/middle/end phases, the seven dimensions of dual operation (SoR map, settlement plumbing, legacy emission, unified read surface, reconciliation, regulatory reporting, SoR transitions), the cutover mechanics, and the end state. Commits to three shapes: legacy emits a daily batch file (24h staleness asymmetry); regulatory reporting aggregates downstream via a named reporting application; renewal of a legacy in-flight deposit creates a new engine-native instance linked by causation_id. Narrows Open Question 5 to operational SLA calibration. Opens Q-AD through Q-AJ. |
| [feature-design-two-modes-asymmetry](./feature-design-two-modes-asymmetry.md) | Deepens §01 §4 and §03 §v4: operationalises the irregular-profile-as-upper-bound design constraint. Quantifies the with-a-plan vs irregular asymmetry across seven dimensions and specifies the six non-negotiable v1 commitments that follow from **Approach C — interfaces for v4, implementations for v1** (event store with scale path; no batch-only assumptions; reserved `partition_key`; per-projection sync/async; snapshot infrastructure in v1; synthetic v4-scale load tests as v1 acceptance), plus the four event-store selection criteria. Refines Q-AC and Q-Z; opens Q-AK through Q-AO. |
