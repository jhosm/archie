# product_concepts/ — Core Banking Product Engine

A configurable core banking product engine. A product brief, not a system design.

## Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./00-product-vision.md) | Product Vision | The one-pager: problem, wedge, in/out of scope, strategic frame |
| [01](./01-product-architecture.md) | Product Architecture | Cash-flow primitive, event store and projections, configuration surface, two families, regulatory pack, integration seam |
| [02](./02-v1-scope-term-deposits.md) | v1 Scope — Term Deposits | Why term deposits first, in-scope features, PT regulatory features, projections, event contract, coexistence with legacy DDA |
| [03](./03-roadmap.md) | Roadmap | PT term deposits → PT credit → PT mortgage → PT current accounts/cards → ES → EU |
| [04](./04-open-questions.md) | Open Questions | Deferred architectural decisions with context and unblocking notes |

### Design notes (companion to the brief, not part of the numbered series)

Each note deepens specific sections of the numbered brief. Where a note's conclusions are load-bearing for the brief, the brief carries the commitment and points back here for the full treatment. The short name in the *Cite as* column is the form used in cross-references throughout the series.

| File | Cite as | What It Covers |
|---|---|---|
| [feature-design-configuration-surface](./feature-design-configuration-surface.md) | `surface` | Deepens [01 §3](./01-product-architecture.md) and [01 §5](./01-product-architecture.md): rate sheets as a price layer separate from product structure; the pack as a typed jurisdiction-scoped vocabulary (three layers — engine / pack / config). Opens Q-I through Q-Q. |
| [feature-design-configuration-authoring](./feature-design-configuration-authoring.md) | `authoring` | Deepens [01 §3](./01-product-architecture.md) from the authoring angle: three authoring layers (engine primitives, family schemas, variants), the variant authoring/review workflow, schema-version pinning, the falsifiable agility wedge. Resolves Open Question 4 (configurability depth) in §9. Opens Q-R through Q-W. |
| [feature-design-event-store-projections](./feature-design-event-store-projections.md) | `event-store` | Deepens [00 §3](./00-product-vision.md), [01 §2](./01-product-architecture.md), and [02 §2.3](./02-v1-scope-term-deposits.md): the event store + bitemporal projections as the engine's source of truth — four time-dimensional capabilities, engine-vs-family separation, event taxonomy, handler discipline, replay reconciliation, snapshots, GL coupling. Resolves Open Question 3. Opens Q-X through Q-AC. |
| [feature-design-strangler-fig-coexistence](./feature-design-strangler-fig-coexistence.md) | `coexistence` | Deepens [01 §6](./01-product-architecture.md) and [02 §3](./02-v1-scope-term-deposits.md): coexistence as a multi-year period with start/middle/end phases, the seven dimensions of dual operation, cutover mechanics, end state. Narrows Open Question 5 to operational SLA calibration. Opens Q-AD through Q-AJ. |
| [feature-design-two-modes-asymmetry](./feature-design-two-modes-asymmetry.md) | `two-modes` | Deepens [01 §4](./01-product-architecture.md) and [03 §v4](./03-roadmap.md): operationalises irregular-profile-as-upper-bound. Commits to **Approach C — interfaces for v4, implementations for v1** and specifies the six non-negotiable v1 commitments. Refines Q-AC and Q-Z; opens Q-AK through Q-AO. |
| [feature-design-moratoria-and-forbearance](./feature-design-moratoria-and-forbearance.md) | `moratoria` | Deepens [03 v2/v3](./03-roadmap.md): payment moratoria (Portuguese *moratória*) and EBA forbearance as a lifecycle event on credit instances. Three flavours, sub-flavours on interest treatment, the §9 four-position map, lifecycle state, event payloads, PT pack vocabulary, bitemporal retroactivity, bulk application, IFRS 9 / TAEG / insurance / customer-exit / coexistence interactions. Cites [financial_concepts §7.6](../financial_concepts/banking_products_financial_mathematics.md) for the math. Opens Q-AP through Q-AT. |

---

## Orientation

This series sits between the other two in the repository:

- [financial_concepts/](../financial_concepts/banking_products_financial_mathematics.md) — **what math is correct**: cash flows, present value, the unified equation every retail banking product obeys.
- **product_concepts/** (this series) — **what configurable product implements that math**: the engine, the configuration surface, the regulatory pack, the v1 slice, the roadmap.
- [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) — **how that product integrates with the bank**: events, sagas, ACL, CQRS, the operational backbone.

The engine takes the cash-flow primitive from [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) as its single architectural insight and collapses every product family — deposits, credits, mortgages, current accounts, cards — into one engine with a swappable configuration surface and a swappable regulatory pack. The integration backbone is inherited from integration_concepts/, not redefined here.

The operating organization is an incumbent Portuguese bank modernising on a strangler-fig adoption path: a single product line moves onto the new engine while the legacy core continues to run the rest. Geography expands PT → ES → EU. The v1 slice is *depósito a prazo* (Portuguese term deposit) — the smallest surface that exercises both the engine and the PT regulatory pack end-to-end.

The series is short by design. The discipline of the brief depends on staying out of GL, IFRS 9, channels, payments rails, KYC, fraud, and onboarding — those are explicitly someone else's problem.

---

## Cross-references

Three styles, signalling how to use each link.

**Inline link** — `[link](url)` in running prose. The default. Used for pointers and most citations. The reader judges by context whether to click.

> The configuration surface has three load-bearing properties ([01 §3](./01-product-architecture.md)).

**Parenthetical citation** — `(see ...)`, `(per ...)`, or `([link])`, rendered inside parentheses. Pure attribution: the claim was just made, the link points at where it is proven or detailed. The reader does not need to click; the link exists so a sceptic can verify.

> Withholding is applied flow-by-flow, never by scaling the rate (per [financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)).

**Required reading** — `Full treatment: [link]` or `Mechanics: [link]` at the end of a paragraph or section. Used when the brief gave the summary and the linked document carries the load-bearing detail. The reader can defer the click but cannot defer it indefinitely.

> Full treatment: [event-store](./feature-design-event-store-projections.md).

### Short forms

| Target | Form | Notes |
|---|---|---|
| Same document section | `§N.M` | No link |
| Sibling numbered brief | `[NN §N.M]` | e.g. `[01 §3]`, `[02 §2.4]`, `[04 §5]` |
| Open question by letter | `[Q-AC]` | Always linked to [04](./04-open-questions.md) |
| Sister series | `[financial_concepts §9.2]`, `[integration_concepts §03]`, `[ADR-004]` | Series name + section |
| Design note | `[surface §3.4]`, `[authoring §6]`, `[event-store §4.1]`, `[coexistence §9]`, `[two-modes §5.3]` | Topic word from the *Cite as* column above |

The URL preserves the full filename; only the link text uses the short form.

The discipline: when a link is just attribution, push it into parentheses and let the reader skip it; when it carries the argument, put it inline; when the reader genuinely needs to read it, mark it with `Full treatment:` or `Mechanics:`. A reader who has learned the convention can triage five links per paragraph without losing signal.
