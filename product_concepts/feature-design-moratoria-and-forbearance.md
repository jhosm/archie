# Feature Design — Moratoria and Forbearance

> Companion to the brief. Specifies how the engine handles **payment moratoria** (Portuguese *moratória*; plural moratoria) — temporary, legally-permitted suspensions of payment obligations on active credit instances — and the closely-related case of bank-initiated **forbearance** arrangements (EBA *forborne exposures*). Both are v2-relevant (PT personal credit on the engine) and v3-relevant (PT mortgage); v1 (term deposits) does not exercise them, because term deposits have no scheduled outflows from the customer to the bank.
>
> The financial mathematics of moratoria is in [financial_concepts §7.6](../financial_concepts/banking_products_financial_mathematics.md); this note covers the engine, schema, pack, lifecycle, event, and operational concerns.
>
> Reading order: §1 frame · §2 taxonomy · §3 where it lands in the configuration model · §4 lifecycle and state machine · §5 event payloads · §6 PT pack vocabulary · §7 bitemporal retroactivity · §8 bulk application · §9 interactions · §10 open questions.

---

## 1. Frame

A **payment moratorium** is a temporary, legally-permitted suspension of payment obligations on an active credit instance. The canonical recent Portuguese example is *Decreto-Lei* 10-J/2020 — the COVID-era moratorium covering mortgages and SME credits, published on 26 March 2020 with operative effect from earlier in the month, applied to instances on bank request with a programmatic eligibility check, and ending in stages through 2021.

Two governance shapes use the same engine mechanics:

- **Government / regulator-mandated moratoria.** Triggered by a legal instrument (PT *Decreto-Lei*, EU regulation, BdP *Aviso*). Bulk-applied: a single operator command applies the moratorium to every instance matching an eligibility filter. Auditable against the legal basis. Examples: DL 10-J/2020 (COVID); future natural-disaster moratoria (floods, fires, earthquakes) under sector-specific decrees.

- **Bank-initiated forbearance** (EBA *forborne exposures*). Per-instance concession by the lender to a borrower in financial difficulty, under the EBA framework. Not bulk; not legally mandated; reviewed individually under the bank's credit policy. The engine mechanics are identical — the same `MoratoriumApplied` event, the same lifecycle, the same schedule recomputation — but the legal basis is the bank's own policy rather than a public decree, and the eligibility check is bank-discretionary.

This note treats both under the umbrella "moratorium," with the legal-basis distinction carried as a field on the moratorium type in the pack and on the event payload. Where the mechanics genuinely differ (bulk vs per-instance application, programmatic vs committee eligibility), the difference is named.

Moratoria are not exercised at v1 because term deposits have no payment schedule from the customer to suspend — the engine pays the customer, not the other way around. The natural-disaster case for deposits ("government allows early withdrawal without penalty") is a separate concession governed by the early-termination policy ([02 §2.5](./02-v1-scope-term-deposits.md)), not by the moratorium machinery; the pack can express it through an emergency-window override on the early-termination schedule.

---

## 2. Taxonomy

### 2.1 Three flavours by what is suspended

| Flavour | Capital amortization | Interest accrual |
|---|---|---|
| **Full moratorium** | Suspended | Suspended OR accrued (pack-defined per legal basis) |
| **Interest-only moratorium** | Suspended | Continues to accrue |
| **Capital-only moratorium** | Suspended | Paid as scheduled |

The full-moratorium row carries two sub-shapes depending on whether interest accrues during the window: most common variant accrues interest and capitalises; alternative suspends interest entirely, with the bank absorbing the foregone-interest loss.

### 2.2 Sub-flavours of interest treatment (for full and interest-only)

| Interest treatment | What happens | Where it lands at moratorium-end |
|---|---|---|
| **Capitalised** | Interest accrues during the window and is added to principal at end | `S(end) = S(start) × (1 + r)^g` — principal grows; subsequent payments larger |
| **Deferred** | Interest accrues during the window and is paid in a separate flow at moratorium-end (lump sum) or spread over the remaining schedule | One additional cash flow at end-of-window; principal unchanged |
| **Paid as scheduled** | Interest is paid each period during the window; only capital is suspended | Cash flow continues; principal unchanged at end |
| **Suspended** | Interest does not accrue during the window; bank absorbs the foregone interest | `S(end) = S(start)` — no change to principal; subsequent payments smaller than they would have been without the window |

The COVID DL 10-J/2020 default was *interest-only moratorium with capitalisation*: capital pause, interest accrues, capitalises at end, term extends by duration. The pack catalogue (§6) declares which combinations are permissible per legal basis.

Full mathematical treatment of every combination: [financial_concepts §7.6](../financial_concepts/banking_products_financial_mathematics.md).

### 2.3 Two governance shapes, same engine mechanics

| Property | Government moratorium | Bank-initiated forbearance |
|---|---|---|
| Legal basis | Public decree (DL, regulation, *Aviso*) | Bank's own credit policy |
| Application | Bulk (single command, instance filter) | Per-instance |
| Eligibility check | Non-discretionary (programmatic against legal criteria) | Bank-discretionary (credit-committee review) |
| Reporting hook | Pack-declared per-legal-basis | EBA *forborne exposures* reporting |
| Engine mechanics | Identical | Identical |

The engine does not distinguish at the mechanics layer; the legal-basis field on the moratorium type carries the distinction, and the application path (bulk endpoint vs per-instance endpoint, §8) is chosen at command time.

---

## 3. Where It Lands in the §9 Model

[authoring §9.2](./feature-design-configuration-authoring.md) places product configurations at four positions on the in-scope spectrum. Moratoria require coordinated work at three of the four:

| Position | Layer | What | Cadence |
|---|---|---|---|
| **C — Primitive release** | Engine code | One or more schedule-modification primitives: `extend_schedule_with_capitalisation`, `extend_schedule_with_deferred_interest`, `extend_schedule_with_suspension`. Could be one parameterised primitive or three. Plus event-handler logic for `MoratoriumApplied` / `MoratoriumInterestAccrued` / `MoratoriumEnded`. | Months |
| **B — Family-schema evolution** | Credit family schema | `IN_MORATORIUM` lifecycle state; instance fields `original_maturity_date`, `current_maturity_date`, `current_moratorium_id`; the three new event types | Quarterly |
| **Pack vocabulary** | PT pack release | Catalogue of valid moratorium types with legal basis, eligibility check, max duration, capital + interest treatment, term-extension rule (§6) | Per regulatory release |
| **A — Variant** | Variant YAML | *Not used.* Applying a moratorium is a command, not a configuration change | N/A |

Position A is intentionally absent: once the engine, schema, and pack support moratoria, *applying* one to a real instance is a runtime command, not a new product configuration. This is consistent with the [authoring §9.3](./feature-design-configuration-authoring.md) boundary policy — instance-level events are not configuration.

Position D (roadmap-layer decline) does not apply: moratoria are in-scope from v2.

The work is contained: a few primitives, one credit-family-schema chunk, and a pack vocabulary section. None of it threatens the agility wedge ([authoring §7.1](./feature-design-configuration-authoring.md)), because operational application is a command, not a configuration change.

---

## 4. Lifecycle and State Machine

The credit family schema's lifecycle gains one state and three transitions:

```
        ┌───────────────────────────────────┐
        │                                   │
        ▼                                   │
   ┌────────┐   MoratoriumApplied    ┌──────┴────────┐
   │ ACTIVE │ ─────────────────────▶ │ IN_MORATORIUM │
   └────┬───┘                        └──────┬────────┘
        │                                   │
        │ (normal lifecycle:                │ MoratoriumEnded
        │  paid, prepaid, terminated)       │
        ▼                                   │
   ┌─────────┐                              │
   │ CLOSED  │ ◀───── (normal end) ─────────┘
   └─────────┘
```

While `IN_MORATORIUM`:

- Scheduled-payment events (`InstallmentDue`, `InstallmentPaid`) do not fire for installments inside the moratorium window. The schedule is paused at moratorium-start; resumes at moratorium-end.
- Interest accrual events (`MoratoriumInterestAccrued`) fire each accrual period if the moratorium type accrues interest, carrying the treatment (`CAPITALISED` | `DEFERRED` | `PAID_AS_SCHEDULED` | `SUSPENDED`).
- Other lifecycle events remain valid: the customer can still exit early; the bank can still apply a court order (`FundsHeld`); the IFRS 9 staging signal continues to be emitted.

At `MoratoriumEnded`:

- Schedule recomputation runs using the [financial_concepts §7.6](../financial_concepts/banking_products_financial_mathematics.md) mechanics: new outstanding principal (with or without capitalisation), new term (with or without extension), new installment.
- A `ScheduleRecomputed` event captures the new schedule with the new installment, new maturity date, and new TAEG. Downstream consumers (CRM, channels, GL) react.
- TAEG re-disclosure is triggered if the legal basis requires it (§9.1).

A nested moratorium (a second `MoratoriumApplied` while `IN_MORATORIUM`) is rejected at the command layer; the second application must wait for the first to end, or revoke the first via `MoratoriumEnded(LEGAL_BASIS_REVOKED)` and re-apply. See Q-AT below.

---

## 5. Event Payloads

Three new family-specific events on the credit family schema:

### `MoratoriumApplied`

A moratorium begins on an instance.

```
instance_id,
moratorium_id,                 -- unique to this application; an instance may have multiple moratoria over its lifetime
moratorium_type,               -- references the pack-declared type, e.g. "moratorium.dl_10j_2020"
legal_basis,                   -- e.g. "DL 10-J/2020 art. 4.º"
applied_start_date,            -- valid_time of the moratorium start
applied_end_date,              -- valid_time of the scheduled moratorium end (subject to MoratoriumEnded reason)
capital_treatment,             -- SUSPENDED (always for moratoria)
interest_treatment,            -- CAPITALISED | DEFERRED | PAID_AS_SCHEDULED | SUSPENDED
term_extension_policy,         -- EQUAL_TO_DURATION | NONE | COMPRESS_REMAINING
eligibility_evidence_ref,      -- pointer to the evidence record (customer declaration, automated check result, committee minute)
applied_via,                   -- BULK_COMMAND | INDIVIDUAL_COMMAND
operator_actor                 -- who initiated (bank operator, customer self-service via authorised channel)
```

### `MoratoriumInterestAccrued`

An accrual computation runs during the moratorium window. Distinct from the standard `InterestAccrued` event so projections can distinguish moratorium-period accruals from contractual accruals — important for IFRS 9 staging, EBA reporting, and the customer-facing rate-history view.

```
instance_id,
moratorium_id,
accrual_period_start,
accrual_period_end,
principal_cents,               -- balance the accrual was computed against
gross_interest_cents,
treatment                      -- CAPITALISED | DEFERRED | PAID_AS_SCHEDULED | SUSPENDED
```

### `MoratoriumEnded`

The moratorium concludes — either at its scheduled end, early by customer request, or early by bank/legal-basis decision.

```
instance_id,
moratorium_id,
ended_at,                      -- valid_time of the end
ended_reason,                  -- SCHEDULED | EARLY_BY_CUSTOMER | EARLY_BY_BANK | LEGAL_BASIS_REVOKED
principal_after_capitalisation_cents,  -- S(end) including any capitalised interest
deferred_interest_cents,       -- if treatment was DEFERRED; the lump-or-spread amount to be paid
new_maturity_date,             -- after any term extension
new_installment_cents,         -- after schedule recomputation
new_taeg_basis_points,         -- the recomputed TAEG
schedule_id                    -- pointer to the new amortization-schedule projection
```

A subsequent `ScheduleRecomputed` event carries the full new amortization schedule for projections; this event carries only the new headline figures.

Convention inheritance: all three events ride the standard envelope from [02 §2.4.3](./02-v1-scope-term-deposits.md) (CloudEvents 1.0; Avro payloads; `correlation_id` propagated from the bulk-command initiation; `causation_id` chained to the application event).

---

## 6. PT Pack Vocabulary

The PT pack ships a catalogue of valid moratorium types. Each type binds a legal basis to a specific combination of treatment parameters. Adding a new moratorium type (e.g., a future natural-disaster decree) is a pack release per [surface §3](./feature-design-configuration-surface.md), not an engine release — new legal regimes do not move the engine.

Example, the COVID moratorium:

```yaml
moratorium.dl_10j_2020:
  legal_basis: "DL 10-J/2020"
  applicable_families: [personal_credit, mortgage]
  governance: GOVERNMENT_MANDATED
  max_duration_months: 18
  min_duration_months: 1
  capital_treatment: SUSPENDED
  interest_treatment: ACCRUED_AND_CAPITALISED
  term_extension: EQUAL_TO_DURATION
  attached_contracts_policy:
    life_insurance_continues: true
    fire_insurance_continues: true
  eligibility_check: dl_10j_2020_eligibility
  reporting_hooks:
    - ifrs9_restructuring_signal
    - bdp_moratoria_register
    - eba_forborne_exposures
  reissue_documents: [secci, fine]   # re-disclosure on schedule recomputation
```

A second example, for bank-initiated forbearance:

```yaml
moratorium.bank_forbearance:
  legal_basis: "Bank credit policy v2026.1"
  applicable_families: [personal_credit, mortgage]
  governance: BANK_INITIATED
  max_duration_months: 12
  min_duration_months: 1
  capital_treatment: SUSPENDED
  interest_treatment: ACCRUED_AND_CAPITALISED   # or one of the pack-permitted alternatives
  term_extension: EQUAL_TO_DURATION
  attached_contracts_policy:
    life_insurance_continues: true
    fire_insurance_continues: true
  eligibility_check: bank_forbearance_committee_decision
  reporting_hooks:
    - ifrs9_restructuring_signal
    - eba_forborne_exposures
  reissue_documents: [secci, fine]
```

The `eligibility_check` field names a primitive the engine executes against the instance and customer data — for DL 10-J/2020 it might verify residency, instance status, payment-delinquency state, product family, and outstanding balance against the legal criteria. The primitive is a regular engine primitive on the months cadence ([authoring §2.1](./feature-design-configuration-authoring.md)); the catalogue entry binds a named primitive to the legal regime. For bank-initiated forbearance the eligibility check is a credit-committee decision recorded as evidence, not a programmatic test — the same field carries the evidence-pointer primitive instead.

---

## 7. Bitemporal Retroactivity

Government moratoria are routinely declared retroactively — DL 10-J/2020 was published on 26 March 2020 with operative effect from earlier in the month. The engine's bitemporal projection model from [event-store](./feature-design-event-store-projections.md) handles this without new mechanisms.

### Worked example

A mortgage in `ACTIVE` state on 1 March 2020. Customer pays installment €1,012.30 on 25 March 2020.

On 26 March 2020 DL 10-J/2020 is published with operative date 22 March 2020. The bank applies the moratorium to the instance on 27 March 2020 via the bulk-command path (§8). The engine emits:

```
MoratoriumApplied(
  moratorium_id:   m-2020-03-27-xyz,
  moratorium_type: moratorium.dl_10j_2020,
  applied_start_date: 2020-03-22,    # valid_time
  applied_end_date:   2020-09-22,    # scheduled end (6 months later)
  ...
  envelope:
    valid_time:       2020-03-22T00:00:00+00:00,
    transaction_time: 2020-03-27T14:23:01+00:00,
)
```

Two state views co-exist:

- **As-known on 2020-03-26** (before `MoratoriumApplied`). Instance is `ACTIVE`; the March installment was paid on March 25; nothing unusual.
- **As-known on 2020-03-27** (after `MoratoriumApplied`). Instance has been `IN_MORATORIUM` since March 22; the March 25 installment was paid *during* the moratorium window. The corrected projection marks it as a reversal candidate — the bank refunds it to the customer's current account, or (per the legal basis) credits it forward against the post-moratorium schedule.

Reconciliation: a `ScheduleReconciliation` projection compares pre-correction events against post-correction validity windows and emits a `RefundDue` or `CreditForward` per affected payment. Downstream channels notify the customer.

This is exactly the case the bitemporal model was designed for. No new architectural primitives needed.

### Boundaries

Retroactive application has natural bounds:

- The moratorium's operative date cannot precede the instance's constitution date.
- The moratorium's operative date cannot precede any `ACCEPTED` IFRS 9 staging-change events without also reversing them; if it does, those events are re-evaluated under the moratorium-aware staging logic (§9.2).
- A moratorium cannot be applied retroactively to an instance already `CLOSED` — closed instances are immutable except via an explicit re-opening flow, which is out of scope for this note.

---

## 8. Bulk Application

A government moratorium is bulk-applied: a single operator command applies it to every instance matching an eligibility filter. The engine has the pattern already from `PackVersionMigrated` ([surface §3.6](./feature-design-configuration-surface.md)) and `SchemaVersionMigrated` ([authoring §6](./feature-design-configuration-authoring.md)).

The bulk-command endpoint:

```
POST /v1/moratorium-applications
{
  moratorium_type: "moratorium.dl_10j_2020",
  applied_start_date: "2020-03-22",
  applied_end_date:   "2020-09-22",
  instance_filter: {
    product_family: [personal_credit, mortgage],
    currently_active: true,
    customer_meets:  ["dl_10j_2020_eligibility"]   # the pack-declared check
  },
  legal_evidence_ref: "DL-10-J-2020-DR-58-2020",
  operator_actor: "operator-bank-compliance-2020-03-27",
  dry_run: false
}
```

Semantics:

- The engine evaluates `instance_filter` over the current instance population; each matching instance gets a `MoratoriumApplied` event.
- The eligibility check (per `customer_meets`) runs per-instance; instances that fail are recorded in the dry-run report and skipped in the real run.
- The operation is auditable: a single `BulkMoratoriumInitiated` envelope event carries the command, the resolved instance list, the actor, and the legal-evidence pointer.
- Reversibility: a corresponding `POST /v1/moratorium-revocations` can undo the bulk operation if (rarely) the legal basis is revoked — emits `MoratoriumEnded(ended_reason: LEGAL_BASIS_REVOKED)` per affected instance.

Per-instance application (bank-initiated forbearance) uses a dedicated endpoint `POST /v1/instances/{id}/moratoria` that bypasses the eligibility filter — the eligibility decision is the credit committee's, not a programmatic check. The evidence pointer carries the committee minute reference.

Authorisation is *not* the standard operator token — a bulk command can affect thousands of instances and tens of millions of euros of expected cash flow. The required scheme (two-person approval, legal-evidence verification, dry-run gate) is operating-bank policy but the engine must enforce *some* scheme. See Q-AQ.

---

## 9. Interactions

### 9.1 TAEG re-disclosure

Applying a moratorium mutates the realized cash-flow vector and therefore the TAEG (per [financial_concepts §6.2 and §7.6](../financial_concepts/banking_products_financial_mathematics.md)). PT/EU consumer-credit rules require re-disclosure: an updated SECCI for personal credit, an updated FINE for mortgages. The pack carries `reissue_documents` per moratorium type (§6); the engine emits the document-trigger signal at `MoratoriumEnded` (or at `MoratoriumApplied`, depending on the legal regime — Q-AS); channels render and deliver.

### 9.2 IFRS 9 signal

A moratorium is a restructuring event under IFRS 9. Depending on circumstances it may trigger Stage 1 → Stage 2 (significant increase in credit risk) or be classified as a non-distressed concession (no stage change). The engine emits the operational fact; the downstream IFRS 9 system makes the staging call.

This interacts with the still-open [Open Question §2 (IFRS 9 Signal Boundary)](./04-open-questions.md) — moratoria are a forcing function for that signal-contract design. Specifically:

- The engine emits `MoratoriumApplied` with structured fields (legal basis, capital/interest treatment, eligibility evidence). The IFRS 9 system reads these signals plus the pre-existing days-past-due tracker.
- Whether the engine ALSO emits a derived staging signal (`IFRS9StagingHinted`) is exactly the §2 decision: compositional (engine emits facts, IFRS 9 derives) vs synthetic (engine emits stage transitions). Moratoria favour the compositional option because the staging call is jurisdiction-and-circumstance-dependent (DL 10-J/2020 explicitly carved out a no-stage-change rule for eligible applications; future moratoria may not).

### 9.3 Attached contracts

Mortgages typically have life insurance (*seguro de vida*) and property insurance (*seguro multirriscos*) — separate premium-paying contracts. The pack decides whether the moratorium covers their premiums; the PT default per DL 10-J/2020 was that insurance premiums continue (the customer keeps paying the insurance even while in moratorium on the loan). The `attached_contracts_policy` field on the moratorium type carries this.

When a mortgage is `IN_MORATORIUM`, attached insurance contracts may need state coordination — e.g. if the customer fails to pay an insurance premium and the policy lapses, the bank's collateral position weakens. This is operational coordination, not engine math; the engine emits the moratorium event and the insurance state, the operations function reconciles.

### 9.4 Customer-initiated exit

A customer can exit a moratorium early. Mechanics:

- Customer notifies the bank (typically with a notification window of N days, pack-defined per legal basis).
- The engine receives a `MoratoriumEndRequest` command.
- After the notification window, the engine emits `MoratoriumEnded(ended_reason: EARLY_BY_CUSTOMER, ended_at: <notified_date + N>)`.
- Schedule is recomputed from the early-end date; the customer resumes the schedule earlier than the originally-scheduled end.

If the customer wants to pay back missed-during-moratorium amounts in a lump sum at exit, that is a separate *prestação extraordinária* flow at the same moment (per [financial_concepts §7.3](../financial_concepts/banking_products_financial_mathematics.md)); the two events are linked by `correlation_id`.

### 9.5 Coexistence with legacy

v2 brings PT personal credit onto the engine; mortgages (v3) and current accounts (v4) remain on legacy at v2-time. A moratorium applied during v2 affects only engine-side personal-credit instances; legacy mortgages are handled by the legacy core under its own moratorium-application mechanism.

The reconciliation surface from [coexistence §7](./feature-design-strangler-fig-coexistence.md) does not need new mechanics — moratorium events flow through the same outbox; settlement events to legacy current accounts continue to use the ACL. A `MoratoriumApplied` on a v2 engine-side credit reduces the cash flow into the customer's legacy current account; the legacy core sees a normal-looking gap in expected debits, with no need to understand the moratorium itself.

For v3 (mortgages on engine), the moratorium machinery is re-exercised on the new family. The pack catalogue and event types carry over; family-schema evolution covers the mortgage-specific fields (attached insurances, variable rate). v3-time work, not v2-time.

---

## 10. Open Questions Opened in This Thread

- **Q-AP. Moratorium catalogue in PT pack v2.** Which historical and live moratoria does the v2 pack ship with? Candidates: DL 10-J/2020 (COVID, expired but useful for testing and audit-trail completeness), DL 22-C/2021 (extension regime), and a generic disaster-moratorium template that future *Decreto-Lei*s bind to as they ship. Decision depends on the operating bank's audit-trail requirements for expired moratoria and on the desired template-vs-specific ratio.
- **Q-AQ. Bulk-command authorisation and approval model.** A bulk moratorium command can affect thousands of instances and millions of euros of expected cash flow. Authorisation requires more than the standard operator token — probably a two-person rule with explicit legal-basis evidence and a mandatory dry-run gate. Specifics are operating-bank policy, but the engine must enforce *some* scheme by default.
- **Q-AR. Eligibility-check primitive ownership.** Eligibility checks (e.g. `dl_10j_2020_eligibility`) are pack-bound primitives but encode legal interpretation. Are they authored by the engine team alone, or by the engine team plus internal regulatory counsel (the pack-authorship model from Q-M)? Same shape as Q-M, surfaced from the eligibility angle.
- **Q-AS. TAEG re-disclosure timing.** When the moratorium ends and the schedule is recomputed, re-disclosure of TAEG via SECCI/FINE has a timing question — is the customer disclosed *before* the new schedule takes effect (giving an opt-out window) or *at* the moment it takes effect? Pack-defined per legal basis; PT default needs an explicit choice.
- **Q-AT. Cross-moratorium handling.** An instance receives a second moratorium before the first ends (e.g. flood after pandemic). Engine semantics: extend the current moratorium, or stack them as overlapping events, or revoke-and-replace? Section 4 currently rejects nested application at the command layer (revoke-and-replace is the path); the pack-and-policy-level question is whether the legal-basis combination supports it. Probably pack-defined per pair of bases.

These are tracked in [04-open-questions.md](./04-open-questions.md) under the lettered register.
