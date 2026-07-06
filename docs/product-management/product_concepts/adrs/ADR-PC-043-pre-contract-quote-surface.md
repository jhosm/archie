# ADR-PC-043: The Pre-Contract Quote Surface — a Stateless, Family-Agnostic `/v1/quotes` over the Constitution Kernels

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-07-06 |
| Deciders | jhosm |
| Shape | Contract-shape ([ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) — a boundary contract between the engine and the customer-facing channel, not a tool choice) |
| Counterparty | the customer-facing **channel** that renders and distributes the pre-contract disclosure (FIN / SECCI) — and, transitively, the shopping customer who runs the *simulador* |
| Depends on | [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) (the engine is the FIN/SECCI **data source**, not a renderer — this route is where that responsibility lands for the *browse-time* case), [ADR-PC-027](./ADR-PC-027-deposit-read-surface-canonical-resource.md) / [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (storage-opaque reads — a quote is a read-like computation, never a write), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (product/rate resolution as-of a pinned version), [ADR-PC-010 §P1](./ADR-PC-010-dotnet-hand-rolled-engine.md) (money is integer cents), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) (no PII), [ADR-PC-031](./ADR-PC-031-personal-loan-family.md) (the loan `Amortization` kernel + the "already-priced" boundary), [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) (family-agnostic surface / family-owned computation), [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) (the OpenAPI catalogue this public route is governed by) |
| Resolves | — (unfiled; this ADR + its [feature-design companion](../feature-design-pre-contract-quote.md) precede the work) |

---

## Context

**In plain English.** Before someone opens a term deposit or takes a personal loan, they shop around — "if I put in €10 000 for 12 months, what do I actually get back after tax?" or "what's the monthly payment and the APR on €10 000 over 60 months?". In Portugal the bank must hand the customer a standardised pre-contract sheet with those figures — the **FIN** (*Ficha de Informação Normalizada*) for deposits, the **SECCI** for consumer credit. The engine already computes every number those sheets need — it just has no way to *ask it a hypothetical*: today the only way to see a deposit's net return is to actually constitute one (which moves money), and the loan installment is only produced at disbursement. This ADR fixes the shape of a **quote** — a read-only, no-side-effect calculation that runs the *same* math the real contract runs, so the number the customer is shown while shopping is provably the number they will get.

The motivating boundary: **the engine is the calculation authority; the channel is the renderer** ([ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) already fixes this for the in-saga FIN gate). What is missing is a *browse-time, pre-commitment* surface — stateless, repeatable across products, and callable without a customer identity — distinct from the FIN gate that fires *inside* the constitution saga once the customer is already committing.

Two properties make this a contract, not a tool choice: it crosses the engine↔channel boundary, and its whole value rests on the **quote never drifting from the contract** — the same reason the [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) catalogue + drift tests exist.

## Decision

A single **family-agnostic** route, `POST /v1/quotes`, resolves the family from `product_code` (exactly as constitution does) and returns a shared envelope with a family-discriminated `quote` + `disclosure`. It **appends nothing, opens no stream, moves no money**. All six contract slots:

1. **Payload shape.** Request `{ product_code, amount_cents (int, ADR-PC-010 §P1), term?, as_of? }`; response `{ product_code, family, as_of, rate_sheet_version_id, tan_basis_points, quote{…}, disclosure{…} }`. `quote` is family-specific (deposit: gross/withholding/net/payout/TAE; loan: installment/n/total/cost-of-credit/TAEG). The authoritative schema is the OpenAPI artefact `contracts/openapi/specs/engine-quotes.openapi.yaml` (to be authored; catalogued per [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md)). No PII anywhere ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) — the request carries no account, no customer, no NIF.
2. **Semantics.** A **pure, indicative** computation: resolve the product shape + rate *as-of* `as_of` ([ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)), then run the **same kernels constitution/disbursement runs** — the deposit accrual + 28% IRS withholding + TAE (financial_concepts §5), the loan `Amortization` kernel ([ADR-PC-031](./ADR-PC-031-personal-loan-family.md); financial_concepts §7) and, once it ships, the TAEG IRR-with-charges solver (financial_concepts §6.2). The `disclosure` block is the FIN/SECCI figures ([ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)). Indicative, not underwriting: the loan quote prices at a *representative* sheet rate — creditworthiness/CRC/KYC/scoring stay **outside** the engine ([ADR-PC-031](./ADR-PC-031-personal-loan-family.md); a loan arrives "already-priced").
3. **Ordering and delivery.** **N/A** — synchronous request/response over HTTP; nothing rides the durable bus, so there is no ordering or gap-detection concern. (Stated explicitly per §D3: this slot does not apply to a stateless read.)
4. **Idempotency.** **Safe and idempotent by construction** — no state is written, so there is no dedup key and no uniqueness window. A response is a pure function of `(product_code, amount_cents, term, as_of, rate_sheet_version_id)` and is therefore **cacheable** by that tuple; the channel or an edge cache may cache it. Neither side dedupes because there is nothing to dedupe.
5. **Error model.** **Gated at the request boundary, blocks nothing downstream** (there is no downstream — a quote has no flow to compensate): `404` unknown `product_code`; `422` `amount_cents`/`term` outside the product's configured bounds, or `as_of` outside the rate sheet's validity; `200` with `taeg_status: "unavailable"` when the loan APR solver is not yet shipped — the quote still returns the installment (a **post-flag inside the 200**, never a hard failure of the whole quote).
6. **Ownership and versioning.** Owned by the **engine team** (the calculation authority, [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)). Every response carries `rate_sheet_version_id` for lineage. Breaking changes ship through the [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) catalogue (Spectral + oasdiff + Kong-route reconcile) plus a drift contract test (the bd `ax0b.4` pattern) that asserts **the quote kernel is the constitution kernel**. A new family extends the surface by registering an `IQuoteProjector` — additive, never a breaking change to an existing family's shape.

## Consequences

**Easier.** A channel can offer a real deposit/loan *simulador* without touching the write path; the FIN/SECCI numbers have one home (this route's `disclosure`), shared with the in-saga gate so browse-time and commit-time agree; the deposit quote is buildable **today** on existing kernels.

**Harder / impossible.** A *personalized* quote (the rate *you* qualify for) is out of reach here — it needs the pricing/creditworthiness systems the engine deliberately does not own; the channel must orchestrate this math **plus** an external pricing decision. The loan **TAEG** is unavailable until the IRR-with-charges solver lands (roadmap v2), so the loan quote is installment-only until then.

## Residual risks

Open sub-decisions this stub deliberately does **not** close (they are the reviewer's to settle):

- **Verb.** `POST /v1/quotes` (structured, variant-rich body — the proposed form) vs `GET /v1/quotes?…` (maximally cacheable, awkward for a loan charges array). Either way the operation is safe/idempotent.
- **Auth.** Public + rate-limited (a true shopping surface; no PII to protect) vs JWT-gated like the other reads. If public, it is a **deliberate, documented exception** to the per-route-JWT norm (the way the mcp `.well-known` discovery route is a knowing exclusion in the [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) gate).
- **What the contract does NOT commit to:** a personalized/underwritten rate; the FIN/SECCI *rendering* or *distribution* (channel-owned, [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)); the legal *acknowledgement* gate (that stays the in-saga FIN step); loan TAEG before the v2 solver.

## Verifiable commitments

Not yet catalogued (this is `Proposed`); the load-bearing invariants this decision will owe, listed as a deliberate `Planned`/`Gap` seed for the [commitment catalogue](./commitment-catalogue.md) when the work is filed:

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | §Decision 2 — a quote runs the **same** kernel as constitution/disbursement (the simulator cannot promise a number the contract won't honour) | fitness function (contract test, `ax0b.4` pattern) | `QUOTE_KERNEL_EQUALS_CONSTITUTION` | Planned |
| 2 | §Context — a quote **appends no event, opens no stream, moves no money** (pure read path) | fitness function | `QUOTE_IS_STATELESS_NO_APPEND` | Planned |
| 3 | §Decision 1 — a quote request/response carries **no PII** ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) | fitness function | `QUOTE_NO_PII` | Gap |
| 4 | §Decision 6 — the public route is reconciled by the [ADR-IC-020](../../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) FORWARD/REVERSE gate against `kong.yml` | CI gate (`openapi-catalog-validate.sh`) | `OPENAPI_QUOTE_ROUTE_RECONCILED` | Gap |
