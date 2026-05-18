# product_concepts/ — Core Banking Product Engine

A documentation series defining a configurable core banking product engine — a product brief, not a system design. It sits between the other two series in this repository:

- [financial_concepts/](../financial_concepts/banking_products_financial_mathematics.md) answers **what math is correct** — cash flows, present value, the unified equation that every retail banking product obeys.
- **product_concepts/ (this series)** answers **what configurable product implements that math** — the engine, the configuration surface, the regulatory pack, the v1 slice, the roadmap.
- [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) answers **how that product integrates with the bank** — events, sagas, ACL, CQRS, the operational backbone.

The engine takes the cash-flow primitive from financial_concepts §9.2 as its single architectural insight and uses it to collapse every product family — deposits, credits, mortgages, current accounts, cards — into one engine with a swappable configuration surface and a swappable regulatory pack. The integration backbone is inherited from integration_concepts/, not redefined here.

The customer is an incumbent Portuguese bank modernising on a strangler-fig adoption path: a single product line moves onto the new engine while the legacy core continues to run the rest. Geography expands PT → ES → EU. Deployment is SaaS multi-tenant and self-hosted from a single codebase. The v1 slice is *depósito a prazo* (Portuguese term deposit) — the smallest surface that exercises both the engine and the PT regulatory pack end-to-end.

The series is short by design: a vision one-pager, an architectural thesis, a concrete v1 slice, a sequenced roadmap, and a register of deferred decisions. The discipline of the brief depends on staying out of GL, IFRS 9, channels, payments rails, KYC, fraud, and onboarding — those are explicitly someone else's problem.

---

## Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./00-product-vision.md) | Product Vision | The one-pager: customer, problem, wedge, in/out of scope, strategic frame |
| [01](./01-product-architecture.md) | Product Architecture | The architectural thesis: cash-flow primitive, configuration surface, two families, regulatory pack, integration seam |
| [02](./02-v1-scope-term-deposits.md) | v1 Scope — Term Deposits | Why term deposits first, in-scope features, PT regulatory features, subledger outputs, event contract, coexistence with legacy DDA |
| [03](./03-roadmap.md) | Roadmap | Sequenced product-family + geography expansion: PT term deposits → PT credit → PT mortgage → PT current accounts/cards → ES → EU |
| [04](./04-open-questions.md) | Open Questions | Deferred decisions with context and unblocking notes — competitive positioning, pricing, licensing, coexistence targets, multi-tenancy, IFRS 9 signal boundary, time-travel, configurability depth, primary economic buyer, founding team credibility, split-brain reconciliation |

### Design notes (companion to the brief, not part of the numbered series)

| File | What It Covers |
|---|---|
| [feature-design-configuration-surface](./feature-design-configuration-surface.md) | Deepens §01 §2 and §01 §4: rate sheets as a price layer separate from product structure, and the pack vocabulary (T1) as a typed jurisdiction-scoped vocabulary. Resolves two sub-questions of Open Question 8 and opens nine new ones. |
