# v1 Scope — Portuguese Term Deposits

> The v1 product slice. Smallest surface that exercises both the engine and the PT regulatory pack end-to-end. Aligned with the running example in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md), so the integration backbone is already proven on the same product.

---

## 1. Why Term Deposits as v1

Three reasons, each independently sufficient.

**Simplest cash-flow math.** A term deposit has at most three cash flows in the basic case: a negative principal at constitution, optional periodic interest payments, and a positive principal + interest at maturity. The entire math (per [financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md)) is simple interest (`M = C × (1 + TAN × days/360)`), compound interest (`M = C × (1 + TAN/m)^(m·n)`), and three interest-payment variants. No amortisation table, no Euribor revision, no insurance, no balloon.

**Narrowest PT regulatory surface.** Term deposits draw on a small, fixed subset of the PT pack: Act/360 day-count, 28% IRS withholding on interest paid to resident individuals, the TANB/TANL split (per [financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)), TAE computation, and BdP deposit-information disclosure and reporting hooks. The pack abstraction is exercised on a real geography without absorbing the much wider surface of consumer credit or mortgage (DL 133/2009, DL 74-A/2017, FINE, SECCI, mandatory insurance). Pack-as-architecture is proven; pack-as-coverage is built out in later v's.

**Aligned with the running example.** [integration_concepts §00](../integration_concepts/00-introduction-and-decisions.md) uses a Portuguese term deposit management system as the running example throughout. The [constitution saga walkthrough](../integration_concepts/05-constitution-saga-walkthrough.md) is already a deposit-constitution saga, with concrete IDs, timings, and compensation paths. The integration seam from [01 §6](./01-product-architecture.md) is not theoretical for term deposits; it is documented and worked out.

Validating v1 validates the engine. Subsequent products in the [roadmap](./03-roadmap.md) are configuration on top of a known-working runtime.

---

## 2. In-Scope Features

### 2.1 The product: *depósito a prazo*

A Portuguese term deposit. Resident-individual depositor; EUR-denominated; fixed TAN over a fixed term; principal returned at maturity. Three interest-payment variants (per [financial_concepts §5.3](../financial_concepts/banking_products_financial_mathematics.md)), all in scope for v1:

- **Interest at maturity** (*juros no vencimento*). The most common Portuguese variant. Cash flows: `CF(0) = -C`, `CF(maturity) = +C + J`. Single accrual computation at maturity; single withholding event; single payout.
- **Periodic interest** (*juros periódicos*). Monthly or quarterly interest payments to the depositor's current account, principal returned at maturity. Cash flows: `CF(0) = -C`, `CF(k) = +J_k` for `k = 1..n-1`, `CF(n) = +C + J_n`. Each interest payment is its own accrual, withholding, and payout cycle.
- **Interest paid in advance** (*juros antecipados*). Interest paid up front at constitution; principal returned alone at maturity. Cash flows: `CF(0) = -C + J`, `CF(n) = +C`. Withholding applies at t=0 to the upfront interest payment.

All three variants share the same product configuration apart from the interest-payment schedule. The configuration surface from [01 §3](./01-product-architecture.md) handles them with one parameter change, not three product types.

### 2.2 PT regulatory features

The v1 PT pack covers exactly what depósito a prazo requires:

- **TANB/TANL split with 28% IRS withholding.** The product configuration carries the gross rate (TANB). Events and projections show both TANB and TANL where appropriate. Withholding is applied flow-by-flow to each interest payment as it accrues — never by scaling the rate (per [financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md): rate-level scaling is only exact for single-period deposits with interest at maturity). Periodic-interest deposits compound their net-of-tax interest from each payment going forward, which is what the depositor experiences.
- **Act/360 day-count.** Default for PT retail deposits. A pack parameter, not a hardcoded constant.
- **TAE — effective annual rate with compounding.** `TAE = (1 + TAN/m)^m - 1` (per [financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)). Shown on the information sheet and on every event that quotes a rate. For interest-at-maturity deposits with no intra-period capitalisation, `TAE = TAN`; for compound deposits the gap is the compounding effect and grows with `m`.
- **Banco de Portugal reporting hooks.** The engine emits signals; reports are downstream. Two signals in scope for v1: deposit-guarantee-fund reportable balances (per-customer aggregate, eligible vs ineligible), and the BdP retail-deposit interest-rate statistics return (new deposits constituted in period, by term and rate band). The engine guarantees the signals are present, correct, and timely; the reports themselves are built by a downstream reporting application.
- **Ficha de Informação Normalizada (FIN).** The pre-contractual information sheet required by BdP for retail deposits. The engine produces the data (rate, term, fees if any, TAE, indicative net return, deposit-guarantee statement); rendering and customer-facing distribution are in channels.

Out of the v1 PT pack: anything credit-related (DL 133/2009, DL 74-A/2017, FINE, SECCI), non-resident depositor withholding, FX deposits, structured deposits.

### 2.3 Projections

Per-account derived state, built bitemporally from the event store (see [01 §2](./01-product-architecture.md); full treatment [event-store](./feature-design-event-store-projections.md)):

- **Deposit position.** Current principal, accrued-but-not-yet-paid interest, paid-but-not-yet-matured interest, withholding accumulated to date. Updated by every event; queryable as a point-in-time snapshot.
- **Accrual schedule.** The ex-ante schedule of interest payments and principal repayment. Two lines for interest-at-maturity; n lines for periodic-interest.
- **Maturity calendar.** Per-tenant view of deposits maturing by date band. Drives operational planning — liquidity, renewal campaigns, customer outreach.
- **Withholding ledger.** Per-deposit and per-customer record of withholding tax applied. Feeds the bank's IRS reporting downstream. The BdP retail-rate-statistics signal is a pack-defined reporting hook (`reporting.bdp_estatisticas_taxas_juro` per [surface §3.3](./feature-design-configuration-surface.md)), not a separate engine module.

The event store is the source of truth; these projections are derived state, rebuildable from the log at any time. The integration architecture (per [integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md)) puts read models on top of the projections.

### 2.4 Event contract

Events emitted by the engine onto the bank's event backbone. Names follow the [integration_concepts §08](../integration_concepts/08-event-catalog-governance.md) convention: `<Entity><PastParticipleVerb>`, factually true about the moment it happened.

#### 2.4.1 Family-specific events (term-deposit family schema)

##### `DepositConstituted`

A new deposit is created and funded. Also fires on engine-native auto-renewal, where `causation_id` points at the `DepositMatured` of the previous instance.

```
deposit_id, customer_id, current_account_id (debited for principal),
principal_cents, currency, tan_basis_points, rate_sheet_version_id,
term_days, start_date, maturity_date,
interest_variant ∈ {AT_MATURITY, PERIODIC, ADVANCE},
payment_period_months  -- if PERIODIC
auto_renewal_policy,
originating_legacy_id  -- optional; set when the instance renews a legacy
                          deposit per [coexistence §9]
```

##### `DepositConstitutionFailed`

The constitution saga compensated before completion.

```
deposit_id  -- the reserved ID that will not be used
customer_id, current_account_id, attempted_principal_cents,
failure_reason ∈ {INSUFFICIENT_FUNDS, COMPLIANCE_REJECTED,
                  LIMIT_EXCEEDED, CORE_UNAVAILABLE,
                  TIMEOUT, CUSTOMER_CANCELLED},
compensation_completed_at
```

##### `InterestAccrued`

An accrual computation runs — daily or at payment date.

```
deposit_id, accrual_period_start, accrual_period_end,
principal_cents, gross_interest_cents (TANB-based), day_count_basis
```

##### `WithholdingApplied`

IRS withholding is computed on an interest payment.

```
deposit_id, interest_payment_id,
gross_interest_cents,
withholding_rate_basis_points  -- 2800 for the PT 28%
withholding_cents, net_interest_cents
```

##### `InterestPaid`

A periodic coupon (or the ADVANCE up-front interest) is accrued, withheld, and settled —
the self-contained per-coupon flow.

```
deposit_id,
gross_interest_cents, withholding_tax_cents, net_interest_cents,
payment_date
```

The engine models `InterestPaid` as the **single, self-contained record for a coupon /
advance flow** — it carries gross, withholding, *and* net so one event folds the whole
flow's accrual + withholding + net tallies exactly once. (Emitting a separate
`InterestAccrued` + `WithholdingApplied` + `InterestPaid` triple per coupon would
double-count, because all three folds accumulate the same running tallies.) AT_MATURITY
uses the `InterestAccrued` + `WithholdingApplied` + `DepositMatured` triple and emits no
`InterestPaid`; the PERIODIC intermediate coupons and the ADVANCE up-front flow emit
`InterestPaid` alone. The credit's `target_current_account_id` is resolved by the ACL
(see §2.4 note 2) and kept out of the structural event. The Avro wire contract for
`InterestPaid` is authored at G.3 (bd `babelstone-c3bq`).

##### `DepositMatured`

A deposit reaches its maturity date.

```
deposit_id, principal_cents,
final_gross_interest_cents, final_net_interest_cents,
total_payout_cents, target_current_account_id
```

##### `DepositRenewed`

Linking event between the matured deposit and the engine-native instance constituted by auto-renewal. Not used for the cross-SoR (legacy → engine) renewal path — that links via `causation_id` and `originating_legacy_id` (per [coexistence §9](./feature-design-strangler-fig-coexistence.md)).

```
previous_deposit_id, new_deposit_id, renewal_date
```

##### `DepositTerminatedEarly`

A deposit is closed before maturity at the depositor's request.

```
deposit_id, termination_date,
principal_returned_cents,
accrued_interest_cents (gross),
withholding_cents, net_payout_cents,
early_termination_reason
```

##### `DepositPartiallyWithdrawn`

A pack-conditional partial early withdrawal (PT pack permits this for some products). Preserves the deposit's historical link rather than terminating + reconstituting.

```
deposit_id, withdrawn_principal_cents,
withholding_on_withdrawn_cents,
remaining_principal_cents, withdrawal_date
```

##### `DepositCorrected`

Clerk-data-entry correction (wrong principal, wrong rate, wrong term). Required for bitemporal correctness — distinguishes *what we thought* from *what we now know* (per [event-store §6](./feature-design-event-store-projections.md)).

```
deposit_id, correction_id,
corrected_fields: { field: { old, new } },
correction_reason, corrected_by
```

##### `DepositTransferredToHeirs`

Succession on death of holder. A lifecycle terminator that is neither maturity nor early termination.

```
deposit_id, transfer_id,
from_holder_id, to_heirs: [{ heir_id, share }],
succession_evidence_ref
```

#### 2.4.2 Cross-cutting generic events (engine-declared)

Five events apply to any instance regardless of family. The engine declares these; family schemas do not. Full treatment: [event-store §4.1](./feature-design-event-store-projections.md).

##### `PackVersionMigrated`

Operator-initiated retroactive pack migration (per [surface §3.6](./feature-design-configuration-surface.md)).

```
instance_id, from_pack_version, to_pack_version,
migration_id, operator_actor
```

##### `SchemaVersionMigrated`

Operator-initiated family-schema migration (per [authoring §6](./feature-design-configuration-authoring.md)).

```
instance_id, from_schema_version, to_schema_version,
migration_id, operator_actor
```

##### `LegacyInstanceObserved`

Daily batch file arrives from legacy DDA (per [coexistence §5](./feature-design-strangler-fig-coexistence.md); ingest contract in [ADR-PC-017](./adrs/ADR-PC-017-legacy-batch-ingest-contract.md)).

```
legacy_instance_id, observed_at,
legacy_state_snapshot, batch_file_id,
fact_kind, family-specific payload
```

##### `FundsHeld`

Court order, garnishment, or external hold instruction.

```
instance_id, hold_id, held_amount_cents,
legal_reference, hold_expires_at (optional)
```

##### `AccountFrozen`

Compliance hold (fraud, AML, sanctions screening).

```
instance_id, freeze_id, freeze_reason,
compliance_actor, freeze_expires_at (optional)
```

#### 2.4.3 Envelope and conventions

Every event — family-specific or cross-cutting — wraps the engine's event envelope (per [event-store §4.3](./feature-design-event-store-projections.md)):

```
event_id, event_type, event_schema_version,
instance_id, family, pack_version, schema_version,
partition_key  -- typically instance_id; reserved for v4 sharding
                  per [two-modes §5.3]
valid_time, transaction_time,
causation_id, correlation_id,
actor, payload
```

Conventions inherited from [integration_concepts §08](../integration_concepts/08-event-catalog-governance.md):

- CloudEvents 1.0 envelope; Avro payloads with Confluent wire format; schema registry contracts.
- IDs as strings; monetary values as integer cents; dates ISO-8601 UTC; ISO-4217 currencies (always `EUR` here, always explicit).
- `correlation_id` and `causation_id` propagated from the originating constitution saga (per [integration_concepts §05](../integration_concepts/05-constitution-saga-walkthrough.md)).
- Outbox emission per [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md): event-store append and outbox-row write commit in one local transaction; the publisher relays from the outbox to Redpanda.

#### 2.4.4 Auto-renewal semantics

The `auto_renewal_policy` field on `DepositConstituted` is one of:

- `NONE` — deposit terminates at maturity and settles to the current account.
- `SAME_TERM_CURRENT_RATE` — auto-renew for the same term at the bank's then-current standard rate for that product.
- `SAME_TERM_SAME_RATE` — auto-renew at the original rate. Less common, pack-restricted.

On the renewal date the engine emits three events in order:

1. `DepositMatured` for the closing deposit.
2. `DepositConstituted` for the new engine-native instance, with `causation_id` set to the `DepositMatured` event_id, the new rate in `tan_basis_points`, and a fresh `rate_sheet_version_id` resolved at the renewal moment.
3. `DepositRenewed` linking the old and new `deposit_id`s for direct old↔new lookup.

The depositor's opt-out window (a PT pack parameter, typically the final 14 days before maturity) is enforced by the engine: a customer-initiated termination during the window prevents auto-renewal without penalty. Termination outside the window is governed by the early-termination policy in §2.5.

Schemas for these events are registered before v1 launch and governed under [integration_concepts §09](../integration_concepts/09-long-term-schema-evolution.md). Backward-compatible evolution keeps the event name; incompatible evolution publishes a `V2` event in parallel with a sunset plan.

### 2.5 Early-termination policies

The operating bank runs banded early-termination penalty schedules on at least some deposit products, so v1 has to express them. A v1 that supports only a single flat policy cannot run those product lines.

The configuration shape, attached to the product config:

- **Flat policy.** One rule applied to any early termination — a fixed haircut on accrued interest, or on principal, or "lose all accrued interest." Supported as a degenerate one-band schedule.
- **Banded schedule.** An ordered list of (window, penalty) pairs evaluated against the elapsed term. Example: `[ { up_to_days: 30, penalty: 100% of accrued }, { up_to_days: 90, penalty: 50% of accrued }, { up_to_days: null, penalty: 25% of accrued } ]`. The engine picks the first band whose `up_to_days` is not yet reached; `up_to_days: null` is the open-ended tail.
- **Penalty basis.** Each band declares whether the penalty applies to accrued interest, to principal, or both. The PT pack restricts which bases are legally permissible.
- **Floor.** Optional minimum payout (typically principal less any pack-permitted principal haircut). The depositor's net payout never falls below the floor.

On a `DepositTerminatedEarly` event, the engine applies the configured policy at the termination moment, emits the event with gross accrued interest and resolved penalty/withholding/net payout, and settles to the depositor's current account. The bank's pricing team owns the per-product schedules; the engine enforces them.

Out of v1: policies whose penalty depends on movements between constitution and termination (revenue-share clauses tied to portfolio performance), policies indexed to a market rate at termination, and policies derived from product-specific optionality. Listed in §4.

---

## 3. Coexistence with Legacy DDA

A Portuguese term deposit is constituted from, and matures into, a *conta à ordem* (current account / demand-deposit account). In the v1 strangler-fig motion the current account lives in the legacy core, not in the new engine. The bank moves term deposits onto the new engine first; current accounts stay where they are. The integration has to make this work without double-counting and without split-brain ledgers.

Three concrete commitments:

1. **The current account is read through, not owned.** When `DepositConstituted` fires, the engine debits the depositor's current account on the legacy core through the [anti-corruption layer](../integration_concepts/02-anti-corruption-layer.md). The engine does not maintain a shadow balance — that would be the double-counting failure mode. The ACL handles the legacy core's transactional semantics, including the indeterminate-state problem (per [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md): the legacy debit succeeds but our confirmation is lost).

2. **Interest payments settle through the ACL, not directly.** `InterestPaid` triggers a credit to the legacy current account through the same ACL, with the same idempotency guarantees and compensation paths. The event means the engine has finalised the payment from its perspective; the legacy core's receipt is downstream and observed via the ACL's confirmation flow.

3. **Settlement and maturity events are observable by the legacy core.** When `DepositMatured` fires, the engine settles principal + final net interest into the legacy current account. The legacy core sees a credit event with a correlation ID and reconciles against its own books at end of day. The bank's reconciliation process compares the engine's outbox (matured deposits, target current accounts, amounts) against the legacy core's incoming credit journal; mismatches are operational alerts, not silent drift.

These are direct consequences of the ACL (per [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md)) and the outbox (per [integration_concepts §04](../integration_concepts/04-plumbing-patterns.md)). The settlement-command contract and the first-class-adapter decision for the current-account module are fixed in [ADR-PC-016](./adrs/ADR-PC-016-legacy-current-account-adapter.md). Later v's that bring more product families onto the engine adopt the same approach until the legacy core is replaced — or until the bank decides to run hybrid indefinitely.

---

## 4. Explicit Out-of-Scope for v1

Each item below is a v1 omission, not a never-built omission. Each has a specific later home.

- **Structured deposits.** Returns linked to indices, baskets, or derivatives. Need optionality math v1 does not implement.
- **FX deposits.** Non-EUR-denominated. The engine is currency-aware (events carry `currency`), but v1 only exercises EUR; multi-currency tax treatment and FX accounting are additional surface.
- **Secondary-market trading.** Negotiable certificates of deposit and similar. Different lifecycle, different regulatory treatment.
- **FGD coverage return.** v1 emits the signals (eligible-balances-per-customer per §2.2); assembly and submission of the return to the Fundo de Garantia de Depósitos is built downstream.
- **Early-termination policies beyond flat and banded.** Policies that depend on movements between constitution and termination (revenue-share, portfolio-performance), policies indexed to a market rate at termination, and policies derived from product-specific optionality.
- **Non-resident depositors.** Different withholding rules (typically lower with a tax-residency certificate). v1 assumes resident individuals.
- **Joint-holder deposits.** Held by two or more *co-titulares*. Different consent flows, different withholding allocation. v1 is single-holder.

Re-opening any of these widens v1. Each has a clear later home; the brief stays disciplined by leaving them there.
