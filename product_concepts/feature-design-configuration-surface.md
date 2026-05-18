# Feature Design — Configuration Surface

> A design-notes companion to the brief, not a numbered member of the series. Deepens [§01-product-architecture §2](./01-product-architecture.md) ("The Configuration Surface") and [§01-product-architecture §4](./01-product-architecture.md) ("The Regulatory Pack") by working through two artefact families that the brief names but does not specify: **rate sheets** (the price layer, separate from product structure) and the **pack vocabulary T1** (the jurisdiction-scoped vocabulary that configs and rate sheets bind to).
>
> The brief is short by design; this document is the load-bearing detail behind a couple of its load-bearing claims. It addresses several entries in [04-open-questions](./04-open-questions.md), most directly Q8 (Configurability Depth), and resolves two sub-questions of that thread while opening nine new ones.
>
> Reading order: skim §1 for the shared frame; read §2 (rate sheets) and §3 (pack vocabulary) on their own merits; §4 collects the consequences for the numbered brief.

---

## 1. Frame: One Configuration Surface, Three Artefact Families

[§01-product-architecture §2](./01-product-architecture.md) commits to a configuration surface that is declarative, synchronously validated, and safe-by-default. It does not commit to *how many distinct artefacts* the surface is composed of. The implicit reading of the brief is "one artefact per product." That implicit reading does not survive contact with the v1 PT pack.

A working configuration surface needs three artefact families, with different cadences, different approvers, and different lifecycle semantics:

| Family | What it specifies | Owner | Cadence | Approver shape |
|---|---|---|---|---|
| **Product config** | Cash-flow shape, day-count, compounding, charge structure, lifecycle hooks, references to rate-sheet roles | Product team | Days–weeks per product variant | Product + Compliance |
| **Rate sheet** | Numerical rates indexed by (product, role, principal band), with effective-from timestamps | Treasury / ALM | Daily–weekly | Treasury sign-off |
| **Pack** | Jurisdiction-scoped vocabulary of primitives and their parameters (day-count conventions, withholding rules, disclosure templates, reporting hooks, calendars) | Vendor + regulatory counsel | Per regulatory change (months–years) | Vendor release + tenant pinning |

The three are bound by reference. A product config says "for the period 1–6, apply a *promotional* rate from the live rate sheet, computed under the *act_360* day-count primitive from the *pt* pack." The rate sheet supplies the numerical value. The pack supplies the primitive implementation parameters.

Treating these as one artefact family is the single largest configuration-surface mistake to avoid. A product redesign and a rate change should not move through the same approval gate; a regulatory change should not require a product redesign. The brief's commitment to "new products are configuration changes" is only operable if the configuration surface is layered so that the *cheapest* change moves through the *cheapest* approval.

---

## 2. Rate Sheets

### 2.1 Premise

In the v1 scope ([§02-v1-scope](./02-v1-scope-term-deposits.md)), depósito a prazo has at least these properties that change on different timescales:

- **Cash-flow shape** (juros à cabeça vs juros no vencimento vs juros trimestrais): changes when a new variant is designed. Months.
- **Day-count and compounding**: changes when the regulatory pack changes. Years.
- **Withholding mechanics**: changes when DL or BdP regulation changes. Years.
- **TAN values** for each (product variant, principal band, role): changes weekly, sometimes daily during promotional campaigns.

Embedding rate values in the product config means every weekly rate change becomes a product config deploy — the same approval gate as a structural redesign. That collapses the cadence of the cheapest change to the cadence of the most expensive one, and the agility wedge in [§00-product-vision §2](./00-product-vision.md) dies on the first promotional campaign.

The split: structure lives in the product config, prices live in the rate sheet, both are versioned, both deploy through CI, but they move at different cadences through different approvers.

### 2.2 Worked example

Product config (excerpt, building on v2 personal credit shape):

```yaml
product_id: cp_pt_60m_promo
pack: pt.2026.1
parameters:
  rate_schedule:
    - { periods: [1, 6],  rate_ref: { sheet: live, role: promotional } }
    - { periods: [7, 60], rate_ref: { sheet: live, role: standard } }
```

Rate sheet (a separate artefact, separate repo path, separate deploy endpoint):

```yaml
rate_sheet_id: cp_pt_rates_2026_05_19
product_family: personal_credit
pack: pt.2026.1
effective_from: 2026-05-19T00:00:00+01:00
products:
  cp_pt_60m_promo:
    standard:
      bands:
        - { principal_cents: [0, 1000000],     tan_basis_points: 850 }
        - { principal_cents: [1000000, null],  tan_basis_points: 800 }
    promotional:
      bands:
        - { principal_cents: [0, null],        tan_basis_points: 600 }
```

A weekly rate-only update is a new rate-sheet version; the product config's `version_id` does not move.

### 2.3 Constitution-time binding

At `DepositConstituted` (or `CreditConstituted`), the engine resolves the rate by:

1. Reading the rate sheet **active at `constituted_at`** — the saga's commit timestamp, deterministic.
2. Resolving `(product_id, role, principal_band)` to a `tan_basis_points` value.
3. Storing both `rate_sheet_version_id` and the resolved `tan_basis_points` on the instance.
4. Emitting both on the event.

Every subsequent `InterestAccrued` event references the same `rate_sheet_version_id` so the audit chain is decidable from the event stream alone, with no need to re-resolve.

Storing both the version ID and the resolved value is deliberate redundancy. The version ID satisfies the audit/replay story (Open Question #7 in [04-open-questions](./04-open-questions.md)); the resolved value satisfies the simpler day-2 query ("what rate is this deposit paying?") without forcing a re-resolution.

**Tiebreaker.** If two rate sheets share `effective_from`, the validator rejects the second one at deploy time. No runtime ambiguity.

### 2.4 Index sheets — the variable-rate cousin

For variable-rate mortgages (v3 in [03-roadmap](./03-roadmap.md)), the engine needs an external index (Euribor, IRPH) and reads it at every revision date. Same shape as a rate sheet, different source:

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

- **Maximum cadence.** Sub-second timestamp granularity. Useful if a bank ever wants intraday rate moves (uncommon for retail; common in some FX-adjacent products).
- **Minimum cadence.** One rate sheet a year is allowed.
- **Typical PT retail.** Weekly or biweekly, midnight on a published day.

Rates are forward-only and versioned. Once a sheet is published, it is never edited; corrections ship as a new version with a new effective-from. Rollback of a wrong-rate publication is the same forward-only mechanism plus an out-of-band compensation flow for instances that constituted under the bad sheet — see Q-J below.

### 2.7 Open questions opened in this thread

- **Q-I. Negative rates.** Schema allows signed bps; pack constrains. PT pack v1 recommends `tan_basis_points >= 0`. Forward-looking: if a EUR retail product ever runs negative again, the pack relaxes the bound; the engine does not change.
- **Q-J. Rate-sheet typo rollback.** Treasury publishes 350 bps instead of 35 bps and some deposits constitute at the wrong rate. Same shape as Q-E (config rollback): forward-only fix plus an out-of-band compensation flow. Worth naming this as a **commercial** risk, distinct from the technical rollback story.
- **Q-K. Tenant-scoped rate sheets.** In SaaS multi-tenant mode, rate sheets are tenant-scoped. No vendor-default sheet; would risk cross-bank leakage and is commercially meaningless.
- **Q-L. Index sheet sourcing.** Who publishes the Euribor fixings into the engine? Direct from ECB? A market-data vendor (Bloomberg, Refinitiv)? Bank-supplied? Probably bank-supplied via the same deploy API, with a pluggable upstream feeder. v3 question, not v1.

---

## 3. Pack Vocabulary (T1)

### 3.1 Premise

[§01-product-architecture §4](./01-product-architecture.md) commits to the regulatory pack as "swappable from day one" but does not specify what a pack actually is. The risk in leaving it abstract: vendors who promise "swappable pack" routinely arrive at a *de facto* fork (a PT branch, an ES branch) because the abstraction was never load-bearing. This section makes the pack load-bearing.

A **pack** is a versioned, jurisdiction-scoped vocabulary of *primitives*, *parameters*, and *reporting hooks*. It is declarative data, not executable code. The engine ships the executable primitives; the pack binds them to a jurisdiction.

### 3.2 Three layers

| Layer | Artefact | Owner | Versioning | Contains |
|---|---|---|---|---|
| **Engine** | Binary release | Vendor | semver | Primitive implementations (`compute_interest_simple`, `apply_withholding`, …), event bus, persistence, scheduler |
| **Pack** | Signed YAML bundle | Vendor + regulatory counsel | `YYYY.N` | Jurisdiction bindings: which primitives apply, with what parameters, with what reporting hooks |
| **Config** | YAML in bank repo | Bank | Content-hash + label | Product composition: "use these primitives via this pack, with these knobs" |

The engine never knows what country it is in. The pack tells it.

### 3.3 PT v1 pack — required primitives

What v1 ships, organised by category:

**Day-count conventions** (`day_count.*`) — `act_360`, `act_365`, `act_act_isda`, `30_360_european`.

**Calendars** (`calendar.*`) — `pt_national`, `pt_national_plus_lisbon`, `target2` (euro payments). Business-day conventions: `following`, `modified_following`, `preceding`.

**Interest computation** (`interest.*`) — `simple` (capital × rate × day-count fraction), `compound_periodic` (explicit compounding frequency), `act_365_compound` (used in some BdP-mandated TAEG calculations).

**Withholding** (`withholding.*`) — `irs_juros` at 2800 bps on gross interest at credit time, with exemptions (`pme_leader`, `non_resident_treaty`, `jovens_poupanca`) and reporting hook `modelo_39`. The 28% rate is a *parameter*, not a constant in the engine; rate changes ship as a new pack version, the engine binary does not move.

**Stamp duty** (`stamp_duty.*`) — `is_credit` (percentage on principal at origination, irrelevant for v1 deposits but shipped now so v2 credit doesn't need pack churn), `is_interest` (percentage on interest paid, relevant for some deposit structures). Reporting hook `dms_at` (declaração mensal de imposto do selo).

**Disclosure templates** (`disclosure.*`) — `fin` (Ficha de Informação Normalizada for deposits, with required-fields schema in the pack and field-level mapping populated by the engine at constitution). `fipre`, `fine` reserved for v2 / v3.

**Reporting hooks** (`reporting.*`) — `bdp_centralizacao_responsabilidades` (reserved, credit only), `bdp_estatisticas_taxas_juro` (active for v1 deposits, emits monthly aggregates), `ifrs9_staging` (reserved, credit only — see Open Question #6 in [04-open-questions](./04-open-questions.md)).

**Default interest / mora** (`mora.*`) — reserved for v2 credit; deposits do not enter mora.

**Currency** (`currency.*`) — `eur` with rounding rule (HALF_EVEN to cents). Per-currency rounding lives in the pack because it is jurisdiction-influenced (PT rounds differently from CH for some products).

### 3.4 Pack manifest shape

```yaml
pack_id: pt
pack_version: 2026.1
pack_effective_from: 2026-01-01
pack_signed_by: vendor-pt-pack-team@vendor.example
based_on_pack_version: 2025.2
engine_compatibility: ">=1.4.0,<2.0.0"

delta_summary: |
  - Stamp duty IS-credit raised from 4.0% to 4.5% (DL N/2025)
  - Added withholding exemption: jovens-poupança scheme
  - No primitive added or removed

breaking_changes: []   # if non-empty, tenants must explicitly opt in

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

test_corpus_ref: oci://vendor/pt-pack-tests:2026.1
```

`formula_ref` is the bridge between pack and engine. `engine.withholding.percentage` names a primitive the engine implements; the pack supplies its parameters. A pack referencing a `formula_ref` the loaded engine does not know is rejected at deploy time, with a clear error pointing at the engine-compatibility range.

### 3.5 Pack pinning — the stability invariant

Every constituted instance pins to **the pack version active at constitution**. The instance carries `pack_version: pt.2026.1` for its entire life. Pack `pt.2027.1` shipping the next year does not change any in-flight instance.

This is the regulatory-stability guarantee. A 12-year mortgage constituted under `pt.2024.1` keeps computing interest and applying withholding per `pt.2024.1` until maturity. Regulators expect it; banks rely on it.

### 3.6 Retroactive change — explicit, audited

Sometimes the regulator *requires* retroactive change ("from 2027-01-01, the new rate applies to all existing instances"). The model handles this without violating pinning:

1. Vendor ships `pt.2027.1` with the new rate.
2. Bank explicitly invokes a **pack migration** for affected instances: `POST /v1/pack-migrations { from: pt.2026.x, to: pt.2027.1, instance_filter: ... }`.
3. Engine emits a `PackVersionMigrated` event per instance.
4. Instances now run under `pt.2027.1`. History prior to the migration remains pinned to `pt.2026.x`.

The migration is auditable (a regulator can ask exactly which instances were re-pinned, when, by whom), reversible-in-principle (replay an instance under the old pack to compute "what would have happened"), and explicit (no silent global rewrite).

### 3.7 Distribution and signing

- Packs ship as OCI artefacts: `oci://vendor/pt-pack:2026.1`, signed (cosign).
- Engine verifies signature at load time; rejects unsigned or wrong-signer packs unless the tenant explicitly allows local development overrides.
- Banks pin pack version in their cluster config; pack updates are an explicit operations action, not a background pull.

### 3.8 Pack ↔ engine compatibility matrix

| Engine version | Compatible pack versions |
|---|---|
| 1.4.x | pt.2026.1, pt.2026.2 |
| 1.5.x | pt.2026.1, pt.2026.2, pt.2027.1 |
| 2.0.x | pt.2027.1+ (breaking primitive rename) |

Published with every engine release. CI in the bank's monorepo can assert "every pinned pack is compatible with every running engine version" — caught at deploy time, never in production.

### 3.9 Pack test corpus

Every pack version ships with a sealed test corpus: canonical instances with expected event sequences over a multi-year horizon. The vendor runs the corpus against every engine release; banks run it in their own CI to detect drift from local engine forks.

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
  - { type: NetInterestCredited, at: 2027-01-15, net_cents: 21600 }
  - { type: DepositMatured, at: 2027-01-15 }
```

This is the canonical "the engine + pack do what the brief claims" evidence. Buyers will ask to see it.

### 3.10 Validator interplay (T2)

The validator (T2 in the configurability stack) consumes the pack to do its job. Two stages:

1. **Static.** Schema-check the config against the pack version it pins. Every `pt.*` reference must resolve to a primitive that exists. Every parameter must be in-range per pack-declared bounds (e.g. `tan_basis_points ≤ pack.max_consumer_rate`).
2. **Dynamic.** Run the simulator (T3) over a sample instance under the pinned pack version and assert outputs match the test-corpus expectations for that product shape.

Without the pack as a typed vocabulary, the validator could only catch syntax errors. With it, the validator catches regulatory misuse.

### 3.11 Open questions opened in this thread

- **Q-M. Pack governance / commercial model.** Vendor-published with regulatory-counsel sign-off (most common)? Industry consortium (better for credibility, slower for change)? Open-source with vendor-curated default plus community packs (interesting but likely too radical for retail banking)? A go-to-market decision shaping pricing and trust posture.
- **Q-N. Breaking-change opt-in mechanics.** When a pack ships with a non-empty `breaking_changes` block, what does adoption look like? Recommend: tenants must call `POST /v1/pack-adoptions` with explicit acknowledgement of each breaking item; engine logs an `OperatorAck` event. No silent pack upgrades.
- **Q-O. Pack forking.** Can a bank fork the pack to add a proprietary primitive (e.g. a private internal-credit-rating-based stamp-duty calculation the regulator allows it to self-determine)? Allowed but stigmatised: vendor maintains only the canonical pack; forks are the bank's responsibility; forks are flagged in reporting metadata so regulators can ask about them.
- **Q-P. Multi-pack composition.** A v5 cross-border product wants PT primitives for withholding (because the holder is PT-resident) but ES primitives for the booking entity. Is the resolution "pick one pack and inline what you need from the other," or "real composition with explicit precedence"? v5 question, but the v1 pack schema should reserve a `primitive_overlays` field (no-op in v1) to avoid painting into a corner.
- **Q-Q. Pack maintainer SLA.** When a DL ships with 30 days' notice, the vendor needs to publish the updated pack within (say) 14 days. A contractual obligation that needs to live in the vendor's commercial agreements. Currently unmodelled.

---

## 4. Consequences for the Brief

### 4.1 Sections that change

- **[§00-product-vision §2.4](./00-product-vision.md) ("the pack")** is currently a sketch. Replace with the three-layer model in §3.2 of this document and the pack manifest shape in §3.4.
- **[§01-product-architecture §2](./01-product-architecture.md) ("The Configuration Surface")** currently implies a single artefact family. Update to name three artefact families (configs, rate sheets, pack) with their distinct cadences and approvers (table in §1 of this document).
- **[§01-product-architecture §4](./01-product-architecture.md) ("The Regulatory Pack")** currently commits to "swappable from day one" without specifying *what* a pack is. Update to reference the pack manifest in §3.4 and the pinning invariant in §3.5.
- **[§02-v1-scope-term-deposits](./02-v1-scope-term-deposits.md)** event contract: `DepositConstituted` gains `rate_sheet_version_id` and `pack_version`; `InterestAccrued` and `WithholdingApplied` inherit both. Add `PackVersionMigrated` and `NetInterestCredited` to the standard event set.
- **[§02-v1-scope-term-deposits](./02-v1-scope-term-deposits.md)** subledger outputs: the BdP reporting hook for deposits is pack-defined (`reporting.bdp_estatisticas_taxas_juro`), not a separate engine module.

### 4.2 Open questions touched

The two sub-questions of [Q8 (Configurability Depth)](./04-open-questions.md) that this document resolves:

- **Sub-question: who owns the rate values.** Resolved: rate sheets, separate artefact, separate cadence, separate approver.
- **Sub-question: when are rates bound.** Resolved: at constitution time for fixed-rate products, at every revision for variable-rate products, with `rate_sheet_version_id` / `index_sheet_version_id` carried on every relevant event.

[Q8 itself](./04-open-questions.md) remains open — the template-vs-DSL boundary is still undecided. This document sharpens the configuration surface around it but does not pick the boundary.

### 4.3 New open questions to fold into [04-open-questions](./04-open-questions.md)

Rate sheets: Q-I (negative rates), Q-J (typo rollback), Q-K (tenant-scoped sheets), Q-L (index sheet sourcing).

Pack vocabulary: Q-M (pack governance), Q-N (breaking-change opt-in), Q-O (pack forking), Q-P (multi-pack composition), Q-Q (pack maintainer SLA).

---

## 5. Status

This document captures a design exploration, not an adopted spec. To move from exploration to spec:

1. Fold §4.1 changes into the numbered brief documents.
2. Fold §4.3 questions into [04-open-questions](./04-open-questions.md).
3. Decide Q-M (pack governance) — it gates the entire pack story commercially.
4. Prototype a single product config + rate sheet + pack triple against the v1 deposit scope and validate the schema against [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) cash-flow primitives.

The natural next design thread is the **validator / simulator CLI surface** (T2 + T3) — without it, the configuration-as-code story has no developer experience, and the synchronous-validation property in [§01-product-architecture §2](./01-product-architecture.md) cannot be honoured.
