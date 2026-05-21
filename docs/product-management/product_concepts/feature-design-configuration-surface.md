# Feature Design — Configuration Surface

> Companion to the brief. Deepens [01 §3](./01-product-architecture.md) and [01 §5](./01-product-architecture.md) by specifying two artefact families the brief names but does not detail: **rate sheets** (the price layer) and the **pack vocabulary** (the jurisdiction-scoped vocabulary).
>
> Does not resolve [04 §4 (Configurability Depth)](./04-open-questions.md); answers two design questions any future resolution will have to live with.
>
> Reading order: skim §1 for the shared frame, then read §2 (rate sheets) and §3 (pack vocabulary) independently.

---

## 1. Frame: One Configuration Surface, Three Artefact Families

[01 §3](./01-product-architecture.md) commits to a configuration surface that is declarative, synchronously validated, and safe-by-default, and names three artefact families with distinct cadences and approvers. This document specifies the artefact families in detail — the rate-sheet split (§2) and the pack vocabulary (§3).

The three artefact families, with their cadences, approvers, and lifecycle semantics:

| Family | What it specifies | Owner | Cadence | Approver shape |
|---|---|---|---|---|
| **Product config** | Cash-flow shape, day-count, compounding, charge structure, lifecycle hooks, references to rate-sheet roles | Product team | Days–weeks per product variant | Product + Compliance |
| **Rate sheet** | Numerical rates indexed by (product, role, principal band), with effective-from timestamps | Treasury / ALM | Daily–weekly | Treasury sign-off |
| **Pack** | Jurisdiction-scoped vocabulary of primitives and their parameters (day-count conventions, withholding rules, disclosure templates, reporting hooks, calendars) | Engine team + internal regulatory counsel | Per regulatory change (months–years) | Engine-team release + operating-bank pinning |

The three are bound by reference. A product config says "for this depósito a prazo, take the rate from the live rate sheet for the *new_money* role, compute accrual under the *act_360* day-count primitive from the *pt* pack, and apply the pack's *irs_juros* withholding at credit time." The rate sheet supplies the numerical value. The pack supplies the primitive and its parameters. The product config composes them.

Treating these as one artefact family is the single largest configuration-surface mistake to avoid. A product redesign and a rate change should not move through the same approval gate; a regulatory change should not require a product redesign. The brief's commitment to "new products are configuration changes" is only operable if the configuration surface is layered so that the *cheapest* change moves through the *cheapest* approval.

---

## 2. Rate Sheets

### 2.1 Premise

In the v1 scope ([02](./02-v1-scope-term-deposits.md)), depósito a prazo has at least these properties that change on different timescales:

- **Cash-flow shape** (juros à cabeça vs juros no vencimento vs juros trimestrais): changes when a new variant is designed. Months.
- **Day-count and compounding**: changes when the regulatory pack changes. Years.
- **Withholding mechanics**: changes when DL or BdP regulation changes. Years.
- **TAN values** for each (product variant, principal band, role): changes weekly, sometimes daily during promotional campaigns.

Embedding rate values in the product config means every weekly rate change becomes a product config deploy — the same approval gate as a structural redesign. That collapses the cadence of the cheapest change to the cadence of the most expensive one, and the agility wedge in [00 §2](./00-product-vision.md) dies on the first promotional campaign.

The split: structure lives in the product config, prices live in the rate sheet, both are versioned, both deploy through CI, but they move at different cadences through different approvers.

### 2.2 Worked example

Product config (excerpt, a v1 *depósito a prazo* with interest at maturity):

```yaml
product_id: dpz_pt_12m_juros_venc
pack: pt.2026.1
parameters:
  interest_variant: AT_MATURITY
  term_days: 365
  day_count: pt.act_360
  rate_ref:
    sheet: live
    role_selector:
      fact: deposit_origin       # fact captured at constitution
      map:
        external_transfer: new_money
        existing_balance: standard
```

Rate sheet (a separate artefact, separate repo path, separate deploy endpoint):

```yaml
rate_sheet_id: dpz_pt_rates_2026_05_19
product_family: term_deposit
pack: pt.2026.1
effective_from: 2026-05-19T00:00:00+01:00
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [50000,    5000000],   tan_basis_points: 300 }
        - { principal_cents: [5000000,  25000000],  tan_basis_points: 325 }
        - { principal_cents: [25000000, null],      tan_basis_points: 350 }
    new_money:
      bands:
        - { principal_cents: [50000, null],         tan_basis_points: 400 }
```

At constitution the engine reads the `deposit_origin` fact about the operation (one of `external_transfer` — principal transferred in from outside the bank — or `existing_balance` — principal already on the bank's balance sheet) and resolves it through `role_selector.map` to a role: `external_transfer → new_money`, `existing_balance → standard`. The rate sheet supplies the rate for each role; the product config supplies the fact-to-role mapping. The rate sheet never sees `deposit_origin`; the product config never sees a `tan_basis_points` value.

A weekly rate-only update is a new rate-sheet version; the product config's `version_id` does not move.

### 2.3 Constitution-time binding

At `DepositConstituted` (or `CreditConstituted`), the engine resolves the rate by:

1. Reading the rate sheet **active at `constituted_at`** — the saga's commit timestamp, deterministic. If two rate sheets share `effective_from`, the validator has rejected the second at deploy time, so no runtime ambiguity is possible.
2. Resolving `(product_id, role, principal_band)` to a `tan_basis_points` value.
3. Storing both `rate_sheet_version_id` and the resolved `tan_basis_points` on the instance.
4. Emitting both on the event.

Every subsequent `InterestAccrued` event references the same `rate_sheet_version_id` so the audit chain is decidable from the event stream alone, with no need to re-resolve.

Storing both is deliberate. The version ID anchors the audit/replay story (the event-sourced model in [01 §2](./01-product-architecture.md) and [event-store](./feature-design-event-store-projections.md)); the resolved value answers the day-2 "what rate is this deposit paying?" query without a re-resolution.

### 2.4 Index sheets — the variable-rate cousin

For variable-rate mortgages (v3 in [03](./03-roadmap.md)), the engine needs an external index (Euribor for PT) and reads it at every revision date. Same shape as a rate sheet, different source:

```yaml
index_sheet_id: euribor_2026_05_19
indices:
  EURIBOR_3M:
    fixing_date: 2026-05-19
    value_basis_points: 273
  EURIBOR_6M:
    fixing_date: 2026-05-19
    value_basis_points: 289
```

A variable-rate mortgage instance binds to an `index_sheet_version_id` **at every revision**, not just at constitution. Each `RateRevised` event carries the index-sheet version it read from. The audit guarantee is identical to fixed-rate.

### 2.5 Validator invariants

At rate-sheet deploy:

- All referenced product IDs exist in active product configs.
- All `(product, role, principal)` combinations referenced by active product configs are covered (no gaps).
- Bands are non-overlapping and exhaustive over the supported principal range.
- Pack-declared bounds are honoured (e.g. PT pack v1 requires `0 ≤ tan_basis_points ≤ 5000`).

At product-config deploy:

- If the config references a `rate_ref`, the active rate sheet must already cover it. Deploying a config that asks for a role the sheet doesn't have is rejected at deploy time, not at the first constitution.

The two artefacts can deploy in either order, but the engine never accepts a state where the two disagree. This is the symmetric invariant.

### 2.6 Lifecycle

Rate sheets are forward-only and versioned. Once a sheet is published, it is never edited; corrections ship as a new version with a new effective-from. The schema supports sub-second timestamp granularity (rare in retail, common in FX-adjacent products) but allows arbitrarily slow cadence at the other end — a bank that publishes one sheet a year is unusual but not violating the model. Typical PT retail cadence is weekly or biweekly at midnight on a published day. Rollback of a wrong-rate publication uses the same forward-only mechanism plus an out-of-band compensation flow for instances that constituted under the bad sheet (see Q-J below).

Rate sheets apply only to products on the new engine. Term deposits booked through the legacy core during the strangler-fig coexistence ([02 §3](./02-v1-scope-term-deposits.md)) continue to draw their rates from whatever the legacy core does — the engine's rate-sheet artefacts have no scope over them. The engine and the legacy core never disagree about a rate because they never read from the same source.

### 2.7 Open questions opened in this thread

- **Q-I. Negative rates.** Schema allows signed bps; pack constrains. PT pack v1 recommends `tan_basis_points >= 0`. Forward-looking: if a EUR retail product ever runs negative again, the pack relaxes the bound; the engine does not change.
- **Q-J. Rate-sheet typo rollback.** Treasury publishes 350 bps instead of 35 bps and some deposits constitute at the wrong rate. The technical shape is forward-only fix plus an out-of-band compensation flow for affected instances. Worth naming because it is a **commercial** risk (someone has to call the affected customers and explain), distinct from the merely-technical rollback mechanics.
- **Q-K. [Retired.]** Previously asked about tenant-scoped rate sheets under SaaS multi-tenancy. Out of scope per the [single-operator framing](./README.md): the engine runs for one operating bank, so there are no tenants in the SaaS sense. The narrower legitimate question — should rate sheets be scopable per business unit inside the bank (retail vs corporate, brand A vs brand B) — is left open as a configuration concern, not a multi-tenancy concern. Letter preserved to keep Q-I through Q-W stable for cross-references.
- **Q-L. Index sheet sourcing.** Who publishes the Euribor fixings into the engine? Direct from ECB? A market-data vendor (Bloomberg, Refinitiv)? Bank-supplied? Probably bank-supplied via the same deploy API, with a pluggable upstream feeder. v3 question, not v1.

---

## 3. Pack Vocabulary

### 3.1 Premise

[01 §5](./01-product-architecture.md) commits to the regulatory pack as "swappable from day one" but does not specify what a pack actually is. The risk in leaving it abstract: engine implementations that promise "swappable pack" routinely arrive at a *de facto* fork (a PT branch, an ES branch) because the abstraction was never load-bearing. This section makes the pack load-bearing.

A **pack** is a versioned, jurisdiction-scoped vocabulary of *primitives*, *parameters*, and *reporting hooks*. It is declarative data, not executable code. The engine ships the executable primitives; the pack binds them to a jurisdiction.

### 3.2 Three layers

| Layer | Artefact | Owner | Versioning | Contains |
|---|---|---|---|---|
| **Engine** | Binary release | Engine team | semver | Primitive implementations (`compute_interest_simple`, `apply_withholding`, …), event bus, persistence, scheduler |
| **Pack** | Signed YAML bundle | Engine team + internal regulatory counsel | `YYYY.N` | Jurisdiction bindings: which primitives apply, with what parameters, with what reporting hooks |
| **Config** | YAML in bank repo | Bank | Content-hash + label | Product composition: "use these primitives via this pack, with these knobs" |

The engine never knows what country it is in. The pack tells it.

### 3.3 PT v1 pack — required primitives

What v1 ships, by category:

| Category | Primitive IDs | Notes |
|---|---|---|
| Day-count (`day_count.*`) | `act_360`, `act_365`, `act_act_isda`, `30_360_european` | PT retail deposits default to `act_360`. |
| Calendars (`calendar.*`) | `pt_national`, `pt_national_plus_lisbon`, `target2` | Business-day conventions: `following`, `modified_following`, `preceding`. |
| Interest (`interest.*`) | `simple`, `compound_periodic`, `act_365_compound` | `simple` is capital × rate × day-count fraction. `act_365_compound` is required for some BdP-mandated TAEG calculations. |
| Withholding (`withholding.*`) | `irs_juros` | 2800 bps on gross interest at credit time. Exemptions: `pme_leader`, `non_resident_treaty`, `jovens_poupanca`. Reporting hook `modelo_39`. The 28% rate is a pack *parameter*, not an engine constant — rate changes ship as a new pack version, the engine binary does not move. |
| Stamp duty (`stamp_duty.*`) | `is_credit`, `is_interest` | `is_credit` reserved for v2; `is_interest` relevant for some deposit structures. Reporting hook `dms_at` (declaração mensal de imposto do selo). |
| Disclosure templates (`disclosure.*`) | `fin`, `fipre`, `fine` | v1 activates `fin` (Ficha de Informação Normalizada for deposits) — required-fields schema in the pack, field-level mapping populated by the engine at constitution. `fipre`, `fine` reserved for v2 / v3. |
| Reporting hooks (`reporting.*`) | `bdp_centralizacao_responsabilidades`, `bdp_estatisticas_taxas_juro`, `ifrs9_staging` | v1 activates `bdp_estatisticas_taxas_juro` only (monthly aggregates). The others reserved for credit (see [Open Question 2 — IFRS 9 Signal Boundary](./04-open-questions.md)). |
| Default interest / mora (`mora.*`) | reserved | v2 credit only; deposits do not enter mora. |
| Currency (`currency.*`) | `eur` | HALF_EVEN rounding to cents. Rounding lives in the pack because it is jurisdiction-influenced (PT rounds differently from CH for some products). |

### 3.4 Pack manifest shape

```yaml
pack_id: pt
pack_version: 2026.1
pack_effective_from: 2026-01-01
pack_signed_by: pt-pack-team@engine.internal
based_on_pack_version: 2025.2
engine_compatibility: ">=1.4.0,<2.0.0"

delta_summary: |
  - Stamp duty IS-credit raised from 4.0% to 4.5% (DL N/2025)
  - Added withholding exemption: jovens-poupança scheme
  - No primitive added or removed

breaking_changes: []

primitives:
  day_count:
    act_360: { formula_ref: engine.day_count.actual_360 }
    act_365: { formula_ref: engine.day_count.actual_365 }
    # ...
  withholding:
    irs_juros:
      formula_ref: engine.withholding.percentage
      rate_basis_points: 2800
      basis: gross_interest
      timing: at_credit
      exemptions:
        - { id: pme_leader, evidence: declaration_pme }
        - { id: non_resident_treaty, evidence: rfi_form }
        - { id: jovens_poupanca, evidence: scheme_enrolment }
      reporting:
        modelo_39: { required: true, frequency: annual }

test_corpus_ref: oci://engine/pt-pack-tests:2026.1
```

`formula_ref` is the bridge between pack and engine. `engine.withholding.percentage` names a primitive the engine implements; the pack supplies its parameters. A pack referencing a `formula_ref` the loaded engine does not know is rejected at deploy time, with a clear error pointing at the engine-compatibility range.

### 3.5 Pack pinning — the stability invariant

Every constituted instance pins to **the pack version active at constitution**. The instance carries `pack_version: pt.2026.1` for its entire life. Pack `pt.2027.1` shipping the next year does not change any in-flight instance.

Concretely: a depósito a prazo constituted on 2024-03-15 under `pt.2024.1` keeps computing accrual under that pack's `act_360` primitive and applying withholding at that pack's `irs_juros` parameters until it matures on 2025-03-15 — even if `pt.2025.1` ships on 2025-01-01 with a different withholding rate. A 12-year mortgage constituted under `pt.2024.1` runs `pt.2024.1` for 12 years.

This is the regulatory-stability guarantee. Regulators expect it; banks rely on it. The retroactive-change story in §3.6 covers the rare cases where the regulator overrides it.

### 3.6 Retroactive change — explicit, audited

Sometimes the regulator *requires* retroactive change ("from 2027-01-01, the new rate applies to all existing instances"). The model handles this without violating pinning:

1. Engine team ships `pt.2027.1` with the new rate.
2. Bank explicitly invokes a **pack migration** for the affected instance set: `POST /v1/pack-migrations { from: pt.2026.1, to: pt.2027.1, instance_filter: { product_family: term_deposit, currently_active: true } }`.
3. Engine emits a `PackVersionMigrated` event per instance.
4. Instances now run under `pt.2027.1`. History prior to the migration remains pinned to `pt.2026.x`.

The migration is auditable (a regulator can ask exactly which instances were re-pinned, when, by whom), reversible-in-principle (replay an instance under the old pack to compute "what would have happened"), and explicit (no silent global rewrite).

### 3.7 Distribution and signing

- Packs ship as OCI artefacts: `oci://engine/pt-pack:2026.1`, signed (cosign).
- Engine verifies signature at load time; rejects unsigned or wrong-signer packs unless an explicit local-development override is configured.
- The operating bank pins pack version in its cluster config; pack updates are an explicit operations action, not a background pull.

### 3.8 Pack ↔ engine compatibility matrix

| Engine version | Compatible pack versions |
|---|---|
| 1.4.x | pt.2026.1, pt.2026.2 |
| 1.5.x | pt.2026.1, pt.2026.2, pt.2027.1 |
| 2.0.x | pt.2027.1+ (breaking primitive rename) |

Published with every engine release. CI in the bank's monorepo can assert "every pinned pack is compatible with every running engine version" — caught at deploy time, never in production.

### 3.9 Pack test corpus

Every pack version ships with a sealed test corpus: canonical instances with expected event sequences over a multi-year horizon. The engine team runs the corpus against every engine release; the operating bank runs it in CI to detect drift from local engine forks.

Example test:

```yaml
test_id: pt_dpz_12m_simple_with_irs
pack: pt.2026.1
input:
  product: dpz_pt_12m_juros_venc
  principal_cents: 1000000
  constituted_at: 2026-01-15
  rate_basis_points: 300
expected_events:
  - { type: DepositConstituted, at: 2026-01-15 }
  - { type: InterestAccrued, at: 2027-01-15, gross_cents: 30000 }
  - { type: WithholdingApplied, at: 2027-01-15, irs_cents: 8400 }
  - { type: InterestPaid, at: 2027-01-15, net_cents: 21600 }
  - { type: DepositMatured, at: 2027-01-15 }
```

This is the canonical "the engine + pack do what the brief claims" evidence — the kind of artefact the operating bank's internal audit, Banco de Portugal supervision, and DORA-style technical due-diligence reasonably ask to see.

### 3.10 Validator interplay

The validator consumes the pack to do its job. Two stages:

1. **Static.** Schema-check the config against the pack version it pins. Every `pt.*` reference must resolve to a primitive that exists. Every parameter must be in-range per pack-declared bounds (e.g. `tan_basis_points ≤ pack.max_consumer_rate`).
2. **Dynamic.** Run the simulator over a sample instance under the pinned pack version and assert outputs match the test-corpus expectations for that product shape.

Without the pack as a typed vocabulary, the validator could only catch syntax errors. With it, the validator catches regulatory misuse.

### 3.11 Open questions opened in this thread

- **Q-M. Pack authorship and sign-off model.** Who within the operating bank authors and signs the canonical pack? Engine team alone (purest, but lacks regulatory accountability)? Engine team plus internal regulatory counsel / compliance function (most likely)? Engine team plus an industry working group the bank participates in (interesting for credibility, slower for change)? Each shape puts a different team on the hook when regulation changes and the pack has to follow within an internal SLA (see Q-Q).
- **Q-N. Breaking-change opt-in mechanics.** When a pack ships with a non-empty `breaking_changes` block, what does adoption look like? Recommend: tenants must call `POST /v1/pack-adoptions` with explicit acknowledgement of each breaking item; engine logs an `OperatorAck` event. No silent pack upgrades.
- **Q-O. Pack overrides for business-unit-specific primitives.** Can a specific business unit within the operating bank define a proprietary primitive override (e.g. a private internal-credit-rating-based stamp-duty calculation the regulator allows the bank to self-determine)? Allowed but discouraged: the engine team maintains only the canonical pack; overrides are owned by the business unit that needs them; overrides are flagged in reporting metadata so regulators can ask about them.
- **Q-P. Multi-pack composition.** A v5 cross-border product wants PT primitives for withholding (because the holder is PT-resident) but ES primitives for the booking entity. Is the resolution "pick one pack and inline what you need from the other," or "real composition with explicit precedence"? v5 question, but the v1 pack schema should reserve a `primitive_overlays` field (no-op in v1) to avoid painting into a corner.
- **Q-Q. Pack-update internal SLA.** When a DL ships with 30 days' notice, the engine team needs to publish the updated pack within (say) 14 days. An internal operational SLA between the engine team and the product organisation, currently unmodelled. Without it, regulatory urgency competes against feature work case-by-case rather than against a named commitment.

