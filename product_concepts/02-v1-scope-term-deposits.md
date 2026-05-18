# v1 Scope — Portuguese Term Deposits

> The v1 product slice. Smallest surface that exercises both the engine and the PT regulatory pack end-to-end. Term deposits chosen because they have the simplest cash-flow math in retail banking, the narrowest PT regulatory surface, and they align with the running example in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) — so the integration backbone is already proven on the same product.

---

## 1. Why Term Deposits as v1

Three reasons, each independently sufficient:

**Simplest cash-flow math.** A term deposit has at most three cash flows in the basic case: a negative principal at constitution, optional periodic interest payments, and a positive principal + interest at maturity. [financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md) covers the entire math in a few pages: simple interest (`M = C × (1 + TAN × days/360)`), compound interest (`M = C × (1 + TAN/m)^(m·n)`), and three variants of interest payment. No amortisation table, no Euribor revision, no insurance, no balloon. The engine's math layer is exercised end-to-end on a product where every formula has a closed form and a worked example.

**Narrowest PT regulatory surface.** Term deposits draw on a small, fixed subset of the PT regulatory pack: the Act/360 day-count convention, the 28% IRS withholding tax on interest paid to resident individuals, the TANB/TANL split ([financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)), the TAE computation, and Banco de Portugal's deposit-information disclosure and reporting hooks. The product engine's pack abstraction is exercised on a real geography without having to absorb the much wider surface of consumer credit or mortgage (DL 133/2009, DL 74-A/2017, FINE, SECCI, mandatory insurance). Pack-as-architecture is proven; pack-as-coverage is built out in later v's.

**Aligned with the integration_concepts/ running example.** [integration_concepts/00](../integration_concepts/00-introduction-and-decisions.md) uses a Portuguese term deposit management system as the running example throughout the entire series. The [constitution saga walkthrough](../integration_concepts/05-constitution-saga-walkthrough.md) is already a deposit-constitution saga, with concrete IDs, timings, and compensation paths. v1 inherits that work — the integration seam from [01-product-architecture §5](./01-product-architecture.md) is not theoretical for term deposits, it is documented and worked out.

Validating v1 validates the engine. Subsequent products in the [roadmap](./03-roadmap.md) are configuration on top of a known-working runtime.

---

## 2. In-Scope Features

### 2.1 The product: *depósito a prazo*

A Portuguese term deposit. Resident-individual depositor; EUR-denominated; fixed TAN over a fixed term; principal returned at maturity. Three interest-payment variants from [financial_concepts §5.3](../financial_concepts/banking_products_financial_mathematics.md), all in scope for v1:

- **Interest at maturity** (*juros no vencimento*). The most common Portuguese variant. Cash flows: `CF(0) = -C`, `CF(maturity) = +C + J`. Single accrual computation at maturity; single withholding event; single payout.
- **Periodic interest** (*juros periódicos*). Monthly or quarterly interest payments to the depositor's current account, principal returned at maturity. Cash flows: `CF(0) = -C`, `CF(k) = +J_k` for `k = 1..n-1`, `CF(n) = +C + J_n`. Each interest payment is its own accrual, withholding, and payout cycle.
- **Interest paid in advance** (*juros antecipados*). Interest paid up front at constitution; principal returned alone at maturity. Cash flows: `CF(0) = -C + J`, `CF(n) = +C`. Withholding applies at t=0 to the upfront interest payment.

All three variants share the same product configuration apart from the interest-payment schedule. The configuration surface from [01-product-architecture §2](./01-product-architecture.md) handles them with one parameter change, not three product types.

### 2.2 PT regulatory features

The v1 PT pack covers exactly what depósito a prazo requires:

- **TANB/TANL split with 28% IRS withholding.** The product configuration carries the gross rate (TANB); the subledger and event stream show both TANB and TANL where appropriate, but **withholding is applied flow-by-flow to each interest payment as it accrues**, never by scaling the rate ([financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md) is explicit on this — rate-level scaling is only exact for single-period deposits with interest at maturity). Periodic-interest deposits compound their net-of-tax interest from each payment going forward, which is what the depositor experiences.
- **Act/360 day-count convention.** Default for PT retail deposits. The day-count is a pack parameter, not a hardcoded constant.
- **TAE — effective annual rate with compounding.** Computed per [financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md) as `TAE = (1 + TAN/m)^m - 1`. Shown on the product information sheet and on every event that quotes a rate. For interest-at-maturity deposits with no intra-period capitalisation, `TAE = TAN`; for compound deposits the gap is the compounding effect and grows with `m`.
- **Banco de Portugal reporting hooks.** The engine emits signals; reports are downstream. Two signals are in scope for v1: deposit-guarantee-fund reportable balances (per-customer aggregate, eligible vs ineligible), and the BdP retail-deposit interest-rate statistics return (new deposits constituted in period, by term and rate band). The reports themselves are built by a downstream reporting application; the engine guarantees the signals are present, correct, and timely.
- **Ficha de Informação Normalizada (FIN).** The pre-contractual information sheet required by BdP for retail deposits. The engine produces the data the FIN needs (rate, term, fees if any, TAE, indicative net return, deposit-guarantee statement); rendering and customer-facing distribution are out of scope (they live in channels).

What is **not** in the v1 PT pack: anything credit-related (DL 133/2009, DL 74-A/2017, FINE, SECCI), anything for non-resident depositors with different withholding rules, anything for FX deposits, anything for structured deposits.

### 2.3 Subledger outputs

Per-account journal records, in the engine's product subledger:

- **Deposit position.** Current principal, accrued-but-not-yet-paid interest, paid-but-not-yet-matured interest, withholding accumulated to date. Updated by every event affecting the deposit; queryable as a point-in-time snapshot.
- **Accrual schedule.** The ex-ante schedule of interest payments and principal repayment. For interest-at-maturity, a two-line schedule (constitution and maturity); for periodic-interest, an n-line schedule.
- **Maturity calendar.** Per-tenant view of deposits maturing by date band. Drives operational planning (liquidity, renewal campaigns, customer outreach).
- **Withholding ledger.** Per-deposit and per-customer record of withholding tax applied. Feeds the bank's IRS reporting obligations downstream.

The subledger is the engine's source of truth. The integration architecture in [integration_concepts/03](../integration_concepts/03-cqrs-and-read-models.md) puts read models on top of it; the subledger itself is the write model.

### 2.4 Event contract

The events the engine emits onto the bank's event backbone. Names follow the [integration_concepts/08](../integration_concepts/08-event-catalog-governance.md) convention: `<Entity><PastParticipleVerb>`, factually true about the moment that happened.

| Event | When | Carries |
|---|---|---|
| `DepositConstituted` | A new deposit is created and funded | `deposit_id`, `customer_id`, `current_account_id` (debited for principal), `principal_cents`, `currency`, `tan_basis_points`, `term_days`, `start_date`, `maturity_date`, `interest_variant` (`AT_MATURITY` / `PERIODIC` / `ADVANCE`), `payment_period_months` if periodic, `auto_renewal` |
| `InterestAccrued` | An accrual computation runs (daily or at payment date) | `deposit_id`, `accrual_period_start`, `accrual_period_end`, `principal_cents`, `gross_interest_cents` (TANB-based), `day_count_basis` |
| `WithholdingApplied` | IRS withholding is computed on an interest payment | `deposit_id`, `interest_payment_id`, `gross_interest_cents`, `withholding_rate_basis_points` (`2800` for the PT 28%), `withholding_cents`, `net_interest_cents` |
| `InterestPaid` | Net interest is settled to the depositor's current account | `deposit_id`, `interest_payment_id`, `net_interest_cents`, `currency`, `target_current_account_id`, `payment_date` |
| `DepositMatured` | A deposit reaches its maturity date | `deposit_id`, `principal_cents`, `final_gross_interest_cents`, `final_net_interest_cents`, `total_payout_cents`, `target_current_account_id` |
| `DepositRenewed` | A deposit auto-renews into a new term | `previous_deposit_id`, `new_deposit_id`, `renewal_date`, `principal_cents`, `tan_basis_points` (the new rate), `term_days` |
| `DepositTerminatedEarly` | A deposit is closed before maturity at the depositor's request | `deposit_id`, `termination_date`, `principal_returned_cents`, `accrued_interest_cents` (gross), `withholding_cents`, `net_payout_cents`, `early_termination_reason` |

Conventions inherited from [integration_concepts/08](../integration_concepts/08-event-catalog-governance.md):

- CloudEvents 1.0 envelope; Avro payloads with Confluent wire format; schema registry contracts.
- IDs as strings; monetary values as integer cents; dates ISO-8601 UTC; ISO-4217 currencies (always `EUR` here but always explicit).
- `correlationid` and `causationid` propagated from the originating constitution saga ([integration_concepts/05](../integration_concepts/05-constitution-saga-walkthrough.md)).
- Outbox emission per [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md): the subledger write and the event-row write commit in one local transaction; the publisher relays from the outbox table to Redpanda.

Schemas for these events are registered before v1 launch and governed under the long-term evolution rules in [integration_concepts/09](../integration_concepts/09-long-term-schema-evolution.md). Backward-compatible evolution keeps the same event name; incompatible evolution publishes a `V2` event in parallel with a sunset plan.

---

## 3. Coexistence with Legacy DDA

A Portuguese term deposit is constituted from, and matures into, a *conta à ordem* (current account / demand-deposit account). In the v1 strangler-fig motion, **the current account lives in the legacy core**, not in the new engine. The bank moves term deposits onto the new engine first; current accounts stay where they are. The integration architecture has to make this work without double-counting and without split-brain ledgers.

The coexistence story has three concrete commitments:

**1. The current account is read through, not owned.** When `DepositConstituted` fires, the engine debits the depositor's current account on the legacy core through the [anti-corruption layer](../integration_concepts/02-anti-corruption-layer.md). The engine does not maintain a shadow current-account balance — that would be the double-counting failure mode. The ACL handles the legacy core's specific transactional semantics, including the indeterminate-state problem documented in integration_concepts/02 (the case where the legacy debit succeeds but our confirmation is lost).

**2. Interest payments settle through the ACL, not directly.** `InterestPaid` triggers a credit to the depositor's current account on the legacy core. Same ACL, same idempotency guarantees, same compensation paths. The event is "interest paid" — meaning the engine has finalised the payment from its perspective; the legacy core's receipt of the credit is downstream and observed via the ACL's confirmation flow.

**3. Settlement and maturity events are observable by the legacy core.** When `DepositMatured` fires, the engine settles principal + final net interest into the depositor's current account. The legacy core does not have to know what the new engine did internally — it sees a credit event with a correlation ID, and it can reconcile against its own books at end of day. The bank's reconciliation process compares the engine's outbox (deposits matured, with target current accounts and amounts) against the legacy core's incoming credit journal; mismatches are operational alerts, not silent data drift.

The strangler-fig pattern depends on these commitments. They are not new architecture — they are direct consequences of the ACL ([integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md)) and the outbox ([integration_concepts/04](../integration_concepts/04-plumbing-patterns.md)). v1 adopts them; later v's that bring more product families onto the engine adopt the same coexistence approach until the legacy core is finally replaced (or not — some banks may run hybrid indefinitely).

---

## 4. Explicit Out-of-Scope for v1

The discipline of v1 lives here. Each item below is genuinely a v1 omission — not a never-built omission. They are deferred to specific later v's or to specific downstream systems.

- **Structured deposits.** Deposits with returns linked to indices, baskets, or derivatives. Not a configuration of the simple-interest engine; they need optionality math that v1 does not implement.
- **FX deposits.** Non-EUR-denominated term deposits. The engine is currency-aware (events carry `currency`), but v1 only exercises EUR; multi-currency tax treatment and FX accounting are additional surface.
- **Secondary-market trading.** Negotiable certificates of deposit and similar instruments. Different lifecycle, different regulatory treatment.
- **Deposit-guarantee-fund reporting (the full report).** v1 emits the signals (eligible-balances-per-customer); the actual FGD return is built downstream and is not a v1 deliverable.
- **Early-withdrawal penalty schedules beyond the simplest case.** v1 supports a single early-termination policy per product configuration (e.g. "lose all accrued interest" or "lose accrued interest above a floor"); banded penalty schedules ("first 30 days: 100% penalty; 30–90 days: 50%; ...") are v1+ configuration extensions.
- **Non-resident depositors.** Different withholding rules (typically lower with a tax-residency certificate). v1 assumes resident individuals; non-resident handling is a v1+ regulatory-pack extension.
- **Joint-holder deposits.** A deposit held by two or more co-titulares. Different consent and disclosure flows, different withholding allocation. v1 is single-holder.

Re-opening any of these items inside v1 costs the v1 timeline. Each one has a clear later home (a specific v, or a specific downstream system); the brief stays disciplined by leaving them there.
