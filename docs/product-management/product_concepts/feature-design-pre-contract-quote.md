# Feature Design — The Pre-Contract Quote Surface (the *simulador*)

> Companion to [ADR-PC-043](./adrs/ADR-PC-043-pre-contract-quote-surface.md) (the decision: a stateless, family-agnostic `/v1/quotes` that runs the constitution kernels without a write). This document is the **implementation design** that realises it — the surface shape, the two families' projections, where it plugs in, and what proves it can't lie.
>
> In plain English: banks let you *simulate* an offer before you commit — "put in €10 000 for a year, here's your net interest after tax"; "borrow €10 000 over 5 years, here's the monthly payment and the APR". In Portugal the pre-contract sheet (FIN for deposits, SECCI for loans) that carries those numbers is mandatory. The engine already computes every figure; it just has no *hypothetical* endpoint. This adds one — a pure calculation that reuses the exact math a real deposit/loan runs, so the shopping number and the contract number are the same by construction.
>
> Interlocks with [ADR-PC-025](./adrs/ADR-PC-025-customer-notification-emit-contract.md) (engine = FIN/SECCI data source, channel = renderer), [ADR-PC-027](./adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) / [ADR-IC-005](../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (storage-opaque reads), [ADR-PC-009](./adrs/ADR-PC-009-per-instance-version-pinning.md) (as-of product/rate resolution), [ADR-PC-031](./adrs/ADR-PC-031-personal-loan-family.md) (the loan `Amortization` kernel + the "already-priced" boundary), [ADR-IC-018](../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) (family-agnostic surface / family-owned computation), [ADR-IC-020](../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) (the OpenAPI catalogue governing the new public route), and the financial math in [financial_concepts](../financial_concepts/banking_products_financial_mathematics.md) (§5 deposit interest + TAE, §6.2 TAEG, §7 amortisation).
>
> Reading order: §1 frame · §2 the surface · §3 deposit quote · §4 loan quote · §5 the shared computation path · §6 statelessness & boundary guarantees · §7 placement · §8 governance & fitness functions · §9 open decisions and risks.

---

## 1. Frame: a hypothetical, not a contract

Today the engine can tell you a deposit's net return exactly one way — **constitute the deposit**, which moves money and opens a stream. A loan's monthly installment is produced only at **disbursement**. There is no way to ask "what *would* I get?" — yet that hypothetical is precisely the pre-contract duty a Portuguese bank owes a shopping customer: the **FIN** for a *depósito a prazo*, the **SECCI** for *crédito aos consumidores*.

The math is not the gap. The accrual, withholding, TAE, and French amortisation are all built and running. The gap is a **surface**: a read-only, no-side-effect way to run those kernels over a hypothetical `(product, amount, term)` and hand back the FIN/SECCI figures — repeatable across products, callable without a customer identity, and distinct from the in-saga FIN gate ([ADR-PC-025](./adrs/ADR-PC-025-customer-notification-emit-contract.md)) that fires *after* the customer is already committing.

One structural decision drives everything: **the quote runs the identical kernels constitution runs** (§5). That is what makes the simulator honest — the number shown while shopping is the number the contract will honour, proven by a fitness function (§8), not by hand-kept parity.

---

## 2. The surface

One route, family resolved from `product_code` (the same resolution constitution uses):

```
POST /v1/quotes
{
  "product_code": "TD-PT-12M-STD",   // or "PL-PT-60M-STD" → resolves family + shape
  "amount_cents": 1000000,           // shopping knob #1 (integer cents, ADR-PC-010 §P1)
  "term": { "months": 60 },          // knob #2; optional — resolved from product_code if omitted
  "as_of": "2026-07-06"              // optional; the rate-sheet resolution date (defaults today)
}
```

Shared response envelope + a family-discriminated `quote` / `disclosure`:

```
{
  "product_code": "…", "family": "term_deposit" | "personal_loan",
  "as_of": "2026-07-06", "rate_sheet_version_id": "rs-2026-07",  // lineage
  "tan_basis_points": 250,
  "quote": { /* §3 or §4 */ },
  "disclosure": { /* FIN or SECCI figures */ }
}
```

No account, no customer, no NIF in the request → **no PII** ([ADR-PC-004](./adrs/ADR-PC-004-pii-crypto-shredding.md)).

---

## 3. Deposit quote (buildable today)

Reuses the accrual (`J = Σ S(d)·r·Δt`, simple/compound per variant), the 28% IRS withholding, and the TAE formula (`(1+TAN/m)^m − 1`) — the same functions that fill `accrued_gross_interest_cents` / `withholding_to_date_cents` / `net_interest_cents` / `total_payout_cents` on a real deposit ([financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md)).

```
"quote": {
  "principal_cents": 1000000, "term_days": 365,
  "gross_interest_cents": 11500,
  "withholding_cents": 3220,          // 28% IRS on resident interest
  "net_interest_cents": 8280,
  "net_payout_cents": 1008280,
  "tae_basis_points": 250
},
"disclosure": {                        // FIN data (ADR-PC-025: engine is the source)
  "kind": "fin",
  "indicative_net_return_cents": 8280,
  "deposit_guarantee": "FGD €100 000 per depositor per institution"
}
```

---

## 4. Loan quote (installment now; TAEG when the v2 solver lands)

Reuses the `Amortization` kernel ([ADR-PC-031](./adrs/ADR-PC-031-personal-loan-family.md); French/Price, [financial_concepts §7](../financial_concepts/banking_products_financial_mathematics.md)) — the same kernel `PersonalLoanDecider` runs at disbursement to fix the level installment. The **TAEG** is the IRR of the full cash flow including mandatory charges ([financial_concepts §6.2](../financial_concepts/banking_products_financial_mathematics.md)); its solver is roadmap v2, so the quote degrades honestly rather than faking the headline number.

```
"quote": {
  "principal_cents": 1000000, "term_months": 60,
  "installment_cents": 18871,         // level installment from the Amortization kernel
  "n_installments": 60,
  "total_repayment_cents": 1132260,
  "total_cost_of_credit_cents": 132260,
  "taeg_basis_points": 720,
  "taeg_status": "indicative"          // or "unavailable" until the IRR-with-charges solver ships
},
"disclosure": {
  "kind": "secci",
  "representative": true,              // INDICATIVE — not personalized underwriting
  "right_of_withdrawal_days": 14
}
```

**The boundary that makes loans harder than deposits.** A deposit quote is self-contained (rate + amount + term → net return). A loan quote is entangled with *who you are*: DL 133/2009 mandates a creditworthiness assessment, and the rate depends on risk. The engine draws that line **outside** itself — a loan arrives "already-priced" ([ADR-PC-031](./adrs/ADR-PC-031-personal-loan-family.md); the engine "never models solvency / CRC / KYC / scoring"). So this surface serves an **indicative** quote at a representative sheet rate; a **personalized** quote is a channel orchestrating this math *plus* an external pricing/risk decision.

---

## 5. The shared computation path (reuse, don't reinvent)

1. **Resolve family + product config** from `product_code` ([ADR-PC-009](./adrs/ADR-PC-009-per-instance-version-pinning.md)) — the same resolver constitution uses.
2. **Resolve the rate as-of `as_of`** from the rate sheet; stamp `rate_sheet_version_id` for lineage.
3. **Run the family's pure kernel** — deposit accrual + withholding + TAE, or the loan `Amortization` kernel (+ the TAEG solver when present). The only genuinely new math is the loan IRR/TAEG-with-charges solver (already on the v2 roadmap); everything else is a projection over kernels that exist.

The load-bearing property: **the quote calls the identical kernel constitution/disbursement calls.** That is the quote/contract analog of the spec↔code drift tests — and it is what §8 gates.

---

## 6. Statelessness & boundary guarantees

- **No append, no stream, no money.** The handler calls pure kernels + config/rate-sheet *reads*; it never touches the event-store write path. Safe to retry, cacheable by request tuple (§2 / [ADR-PC-043 §Decision 4](./adrs/ADR-PC-043-pre-contract-quote-surface.md)).
- **No PII** → the quote can be **public / unauthenticated** (the real shop-around case), rate-limited for anti-abuse — unlike `GET /v1/deposits/{id}`, which is JWT + ownership-checked because it exposes a real customer's deposit. (Public-vs-authenticated is an open decision — §9.)
- **Indicative, not underwriting** — the loan quote never prices to a customer's risk (§4).
- **One projector, two callers.** The in-saga FIN gate ([ADR-PC-025](./adrs/ADR-PC-025-customer-notification-emit-contract.md)) and this route call the **same** projector, so the browse-time figure and the commit-time disclosure are provably equal.

---

## 7. Placement: family-agnostic surface, family-owned computation

The route lives in the engine host as **one** family-agnostic endpoint; each family registers an `IQuoteProjector` that, given `(product config, amount, term, resolved rate)`, returns its `quote` + `disclosure`. This is the same substrate-owns-the-surface / family-owns-the-numbers split already used for deciders, projections, and saga modules ([ADR-IC-018](../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md); the family-agnostic-core covenant of [ADR-PC-040](./adrs/ADR-PC-040-family-agnostic-substrate-covenant.md)). The math kernels already exist per family; the projector only wraps them for the quote shape.

---

## 8. Governance & fitness functions

The new public route is governed exactly like the rest of the REST surface ([ADR-IC-020](../integration_concepts/adrs/ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md)):

- **OpenAPI spec** `contracts/openapi/specs/engine-quotes.openapi.yaml` + a **Kong route**, reconciled by `openapi-catalog-validate.sh` (FORWARD/REVERSE). If the route is public, that is a documented auth exception (the mcp `.well-known` precedent).
- **Drift test** (the bd `ax0b.4` pattern) with the extra, decisive assertion: **the quote kernel == the constitution kernel** — `QUOTE_KERNEL_EQUALS_CONSTITUTION`. Plus `QUOTE_IS_STATELESS_NO_APPEND` and `QUOTE_NO_PII` (all `Planned`/`Gap`; see [ADR-PC-043 §Verifiable commitments](./adrs/ADR-PC-043-pre-contract-quote-surface.md)).

---

## 9. Open decisions and risks

- **Verb** — `POST /v1/quotes` (structured body; proposed) vs `GET` (cacheable; awkward for loan charges). Either way, safe/idempotent.
- **Auth** — public + rate-limited vs JWT-gated. A security/product call; no PII either way.
- **Sequencing** — the deposit quote ships now on existing kernels; the loan quote ships installment-only and gains `taeg` when the v2 IRR-with-charges solver lands. `taeg_status` is the honest bridge.
- **Not committed** — a personalized/underwritten rate; FIN/SECCI *rendering* and *distribution* (channel-owned); the legal *acknowledgement* gate (stays the in-saga FIN step).
