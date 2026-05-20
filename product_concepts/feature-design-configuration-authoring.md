# Feature Design — Configuration Authoring

> A design-notes companion to the brief, not a numbered member of the series. Deepens [§01-product-architecture §3](./01-product-architecture.md) ("The Configuration Surface") from a different angle than [feature-design-configuration-surface](./feature-design-configuration-surface.md): that document split the *artefact* surface into configs, rate sheets, and packs; this document splits the *authoring* surface into engine primitives, family schemas, and variants — and works through who authors each layer at what cadence with what review, plus the falsifiable agility-wedge claim that the workflow is designed to satisfy.
>
> The two design-notes documents are orthogonal: the artefact split says what kinds of files exist; the authoring split says who writes what kind of change to which file, on what timescale, under what review. Reading the existing surface document first helps but is not required.
>
> Reading order: §1 frames the wedge as an authoring claim. §2–§6 work through the authoring layers, the family/variant taxonomy, the review workflow, the validator's five depths, and the schema-version pinning invariant. §7 states the falsifiable agility wedge as two distinct claims. §8 names the roadmap consequence of the multi-country window. §9 collects consequences for the brief.

---

## 1. Frame: The Wedge as an Authoring Claim

The agility wedge in [§00-product-vision §2](./00-product-vision.md) and [§01-product-architecture §3](./01-product-architecture.md) commits to "new products are configuration changes, not new modules." The brief states the architectural property — declarative, synchronously validated, safe-by-default — without committing to *who turns what change into a deployable artefact, on what timescale, under what review*. Without that, "configuration change" is a category description, not a workflow.

The wedge is operable only when three layers exist with three different cadences and three different review postures:

| Layer | Artefact shape | Author | Reviewer | Cadence |
|---|---|---|---|---|
| **Primitives** | Engine code (Go / Rust / similar) | Platform engineering | Engineering + compliance lead (where the primitive encodes regulation) | Months — a new primitive is a real engineering change |
| **Family schemas** | Typed schemas (e.g. CUE / JSON Schema with a domain layer on top) | Platform engineering + product engineering | Engineering + compliance | Quarterly — a family schema absorbs new shapes as they emerge from variant work |
| **Variants** | YAML instances of a family schema | Product (PM, business analyst) | Engineer (technical correctness) + automated validator + risk (rate bands, exposure) | Weekly or faster — promotional campaigns and product tweaks live here |

Compliance review sits **upstream** at the primitive and family-schema layers, not at the variant layer. The variant layer carries only the scope that compliance has already pre-approved by signing off on the family schema and the pack ([feature-design-configuration-surface §3](./feature-design-configuration-surface.md)). The validator enforces the pre-approved scope at variant-commit time, so compliance does not need to re-read each variant.

This is the load-bearing claim: **the cheapest change moves through the cheapest approval**. A weekly variant change does not pay the cost of a quarterly family-schema review, and a family-schema change does not pay the cost of a months-cadence engine change. Embedding any of those costs at a wrong layer collapses the cadence of cheap changes onto the cadence of the most expensive one, and the wedge dies.

---

## 2. The Three Authoring Layers

### 2.1 Primitives — engine code

Primitives are the executable building blocks: `compute_interest_simple`, `compute_interest_compound_periodic`, `day_count_act_360`, `apply_withholding_percentage`, `schedule_amortisation_price`, and so on. They live in the engine's source tree, are versioned with the engine binary (semver), and ship as part of an engine release.

A new primitive is an engineering change. It carries the full weight of an engineering change: design, implementation, test corpus, code review, regression suite, release notes. A new primitive is rare by design; the v1 PT pack ([feature-design-configuration-surface §3.3](./feature-design-configuration-surface.md)) lists the dozen or so primitives v1 actually exercises, and most subsequent variants compose existing primitives rather than introducing new ones.

A primitive is referenced from a pack manifest (`formula_ref: engine.day_count.actual_360`), never from a variant directly. The variant layer never names a primitive; it names a family-schema field that the schema and pack together bind to a primitive.

### 2.2 Family schemas — the typed contract

A **family schema** is the typed contract that variants populate. It says, for one product family (term deposit, Price credit, mortgage, current account, credit card): which fields a variant must specify, which fields are optional, what the type and range of each field is, which fields are pack-bound (i.e. the schema declares "this field references a `pt.day_count.*` primitive — the pack supplies the actual list"), and which fields are free-form within a stated range (`tan_basis_points: integer, range [0, 5000]`).

A family schema is the *engine-shaped* understanding of what a product family can look like. v1 ships with one family schema per product family the engine supports (v1 = `term_deposit`, v2 = `personal_credit`, v3 = `mortgage`, v4 = `current_account` + `credit_card`). Engineering authors the schema; product engineering shapes it from the product side; compliance reviews it for regulatory completeness; the pack team reviews it for primitive-binding correctness.

Family schemas evolve. The cadence is **quarterly by design**, not weekly. When a new variant cannot be expressed in the current schema — a stepped-rate deposit when the schema only models flat rates, a deposit linked to an index that does not yet have a schema slot — the schema gets a new field or a new sub-shape, in a controlled release that goes through the full review chain. The variant work that surfaces the need is held until the schema lands.

If schema evolution becomes weekly, something is wrong: either the schema is under-modelled (most likely), or product is bypassing the variant layer and using the schema as the variant surface (less likely but worth detecting). Both are red flags for the wedge.

### 2.3 Variants — YAML instances

A **variant** is a single YAML file: an instance of a family schema, with concrete values for every required field. A 12-month *depósito a prazo* with interest at maturity and a flat TAN is a variant. A 12-month *depósito a prazo* with quarterly interest payments is another variant. A 24-month version of either is another. A new promotional product layered on top of an existing variant (with a different rate-sheet binding only) is, strictly, a new variant — or, equivalently, a rate-sheet change against an existing variant, depending on how the team factors it (see [feature-design-configuration-surface §2](./feature-design-configuration-surface.md) for the rate-sheet split).

Variants are authored by product — PMs, business analysts, the people who design the product the bank sells. The author writes the YAML; commits it to the configuration repository; opens a pull request. The author does not need to read engine code, does not need to read the pack manifest, and does not need to know which primitive backs `day_count: pt.act_360`. The author needs only the family schema and the pack vocabulary they bind to.

A variant's cadence is **weekly or faster**. The validator and the deployment train have to be sized for that cadence; if either takes longer, the wedge is bottlenecked outside the engine.

---

## 3. The Family vs Variant Taxonomy

### 3.1 Coarse-start, fine-drift

v1 ships one family schema per product family. The `term_deposit` schema covers every shape of term deposit the engine has to handle in v1: interest at maturity, periodic interest, advance interest, flat rate, stepped rate (if v1 stretches into it per [§02 §4](./02-v1-scope-term-deposits.md)). The schema is intentionally **coarse** at start — it carries optional fields and union shapes for variant patterns that are known to exist or expected soon.

This is the right starting point. Splitting `term_deposit` into `term_deposit_flat`, `term_deposit_stepped`, `term_deposit_index_linked`, etc., on day one is premature: each split is a real review gate (engineering + compliance) for a distinction that may not be load-bearing yet. Keeping them in one schema lets the variant layer enumerate the shapes that *actually* matter.

The fine-drift rule: when a family schema accumulates enough optional fields and union shapes that a variant author can no longer reason about which combinations are valid — typically when the schema's optional-field count crosses some threshold, or when the schema's worked-example coverage stops being complete — the schema is **split** into focused per-shape schemas. The split is a quarterly-cadence operation under the same review chain that owns the original schema. Existing variants pin to the old schema version until they are explicitly migrated (see §6 below).

Splitting is a strength signal, not a failure signal. It means the engine has accumulated enough domain understanding to draw real distinctions between product shapes. The opposite — a schema bloating indefinitely with weakly-typed `extra: { … }` fields — is the failure signal.

### 3.2 Worked example: flat-rate vs stepped-rate deposits

**Coarse, v1.** One `term_deposit` schema. The TAN field is a union: either a single `tan_basis_points` for a flat-rate deposit, or a `rate_steps: [{ from_day: int, tan_basis_points: int }, ...]` array for a stepped-rate deposit. The validator enforces that at most one of the two is set.

```yaml
# Variant A — flat-rate, schema_version: term_deposit@2026.1
variant_id: dpz_pt_12m_flat_juros_venc
schema: term_deposit@2026.1
pack: pt.2026.1
interest_variant: AT_MATURITY
term_days: 365
day_count: pt.act_360
rate:
  flat:
    rate_ref: { sheet: live, role_selector: deposit_origin }
```

```yaml
# Variant B — stepped-rate, same schema
variant_id: dpz_pt_24m_stepped_juros_trim
schema: term_deposit@2026.1
pack: pt.2026.1
interest_variant: PERIODIC
payment_period_months: 3
term_days: 730
day_count: pt.act_360
rate:
  stepped:
    rate_ref: { sheet: live, role_selector: deposit_origin }
    steps:
      - { from_day: 0,   pricing_band: tier_1 }
      - { from_day: 365, pricing_band: tier_2 }
```

Both variants populate the same `term_deposit@2026.1` schema. The validator checks the union (at most one of `flat` / `stepped` is set), the rate-sheet references the relevant roles, and the schema is the single source of truth for valid term-deposit shapes.

**Fine, hypothetical v1.5.** Two months in, the team has accumulated five stepped-rate variants with materially different conventions (PT pack vs ES pack, simple vs compound step boundaries, capital protection vs no protection). The schema bloats and the validator's error messages stop being useful — `term_deposit@2026.1` now requires five mutually-exclusive sub-blocks. The fine-drift response: split into `term_deposit_flat@2026.3` and `term_deposit_stepped@2026.3`. Existing variants stay on `term_deposit@2026.1`; new variants are authored against the focused schemas; engineering and compliance review the split itself.

The split is observable in the repository: two new schema files, two clean validation regimes, no force-migration of in-flight variants. The engine continues to support both schemas in parallel until the old one is empty.

---

## 4. The Variant Authoring and Review Workflow

The workflow is the wedge made operational. Each step has a named author or reviewer and a defined success/failure semantic.

**Step 1 — Authoring.** A product team member (PM, business analyst) writes the YAML variant against the current `<family>@<version>` schema, places it in the configuration repository under the relevant family directory, and opens a pull request. The PR template requires a short rationale (what does this variant let the bank sell that the existing variants do not?) and a rate-sheet pointer (does this variant draw from an existing rate-sheet pricing band, or does a new rate-sheet entry have to land alongside?).

**Step 2 — Synchronous validation.** The validator runs on every commit, against the pinned schema and pack, in the validator depths from §5 below. Validation is sized to run in under 60 seconds end-to-end; anything slower is a bug to fix, not a tolerance to accept. The validator's output is structured: every failure points at a specific schema field, a specific pack rule, or a specific simulation result, with enough context for the author to fix the variant without escalation.

**Step 3 — Engineer review.** An on-call engineer from the engine team reviews technical correctness: are the rate-sheet bindings coherent, are the lifecycle hooks reachable, is the variant doing anything the validator could not catch but a human can. Engineer review is a one-pass step; the engineer either approves or sends back with a specific blocker. Target SLA: 1–2 working days.

**Step 4 — Risk review.** Risk reviews the variant for exposure: rate bands fit within risk-approved corridors, principal limits are within product-policy bounds, the variant does not silently widen the bank's open positions. Risk approval is not about regulatory compliance (that lives in compliance review of the schema and pack, upstream) — it is about whether the bank's books can hold the variant under stress. Target SLA: 1–2 working days, in parallel with engineer review where possible.

**Step 5 — Deployment.** Once approved, the variant merges to the configuration repository's main branch. The deployment train picks it up on its next cycle. The deployment cadence is **weekly or faster**; anything less collapses the variant cadence onto the deployment cadence. A variant marked `effective_from: <future date>` waits for its activation; one marked effective immediately becomes available to constitute against on the first deployment after merge.

Compliance review is **not** in this list. Compliance reviews the family schema and the pack ([feature-design-configuration-surface §3](./feature-design-configuration-surface.md)), upstream of any variant. The variant layer can produce only what the schema and pack permit. Adding a compliance step at the variant layer would re-impose schema-cadence cost on variant work and break the wedge.

---

## 5. The Validator — Five Depths

The validator is a single CLI invocation but five logically-distinct checks. The depths are layered: each depth assumes the previous depths passed.

| # | Depth | What it checks | Runtime budget |
|---|---|---|---|
| 1 | **Syntactic** | The variant YAML parses and respects the schema's structural shape | < 1 s |
| 2 | **Type-check** | Every field's type and range match the schema declaration; pack-bound fields resolve to a known primitive in the pinned pack | < 5 s |
| 3 | **Pack compliance** | Variant respects the pack's bounds: e.g. `tan_basis_points ≤ pack.max_consumer_rate`; mandatory disclosures resolved; pack-required reporting hooks not bypassed | < 10 s |
| 4 | **Regulatory coherence** | Cross-field invariants required by regulation: e.g. interest-payment cadence consistent with deposit term; withholding mechanics align with the principal currency; PT pack rejects a variant that uses Act/365 for a deposit (Act/360 required) | < 10 s |
| 5 | **Simulation** | Run the engine's primitive computations on representative inputs and assert the resulting event sequence is well-formed (no negative balances, no double withholding, schedule matches the expected `J = Σ S(d) × r × Δt` shape) | < 30 s |

**Synchronous at commit time: depths 1–4.** All four run on every commit, with the budgets above. Total synchronous validation completes in under 30 seconds; anything slower is a bug. The product team learns at commit time, not on PR merge or on deploy, whether their variant is well-formed.

**Deferred to CI: depth 5.** The simulator runs the full sealed test corpus from [feature-design-configuration-surface §3.9](./feature-design-configuration-surface.md) against the variant. This is the depth that gives multi-year event-sequence confidence; it is also the depth that takes long enough that running it on every commit would tax the developer loop. CI runs depth 5 on every PR before merge; the engineer review (§4 Step 3) sees the result.

Depth 1 alone is a syntactic check; depth 5 alone is integration testing. The agility wedge depends on all five being part of the same validator, run from the same CLI, with consistent error messages and a clean separation between "you broke this at commit time, fix it now" (1–4) and "you broke this at PR time, fix it before merge" (5).

---

## 6. Schema-Version Pinning

[feature-design-configuration-surface §3.5](./feature-design-configuration-surface.md) commits every constituted instance to **the pack version active at constitution**. The same invariant applies to the family schema: every constituted instance pins to **both the pack version and the family-schema version** active at constitution.

Concretely, a depósito a prazo constituted on 2026-03-15 carries `pack_version: pt.2026.1` *and* `schema_version: term_deposit@2026.1`. When `term_deposit@2026.3` ships with the split into `term_deposit_flat` and `term_deposit_stepped`, the 2026-03-15 instance keeps running under `term_deposit@2026.1` until it matures. The engine supports both schemas in parallel for as long as in-flight instances reference them.

This is the regulatory and contractual stability guarantee at the schema layer, mirroring the same guarantee at the pack layer. Banks (and their internal auditors) need to be able to answer "what schema and pack governed this instance for its entire life?" The answer is on every instance's `DepositConstituted` event and never moves.

Schema migration, when the bank chooses to consolidate, uses the same explicit, audited shape as pack migration ([feature-design-configuration-surface §3.6](./feature-design-configuration-surface.md)): a `POST /v1/schema-migrations` action with explicit instance filter, an emitted `SchemaVersionMigrated` event per affected instance, and full reversibility-in-principle by replay. Silent global rewrites are not available.

Schema versioning is the same shape as pack versioning, deliberately. The two could in principle share a versioning mechanism; in practice they are kept separate because they have different owners (engineering owns schemas; the engine team plus internal regulatory counsel own packs) and different cadences (schemas quarterly; packs per regulatory change). Sharing the version space would couple two cadences that should remain independent.

---

## 7. The Falsifiable Agility Wedge — Two Claims

The agility wedge in [§00-product-vision §2](./00-product-vision.md) is currently a category statement ("new products are configuration changes, not new modules"). With the authoring workflow above defined, the wedge is restatable as **two falsifiable internal commitments**, each testable, each failure-diagnosable. These are *internal challenge targets*, not external promises — the point is to surface bottlenecks early, not to underwrite a contractual SLA.

### 7.1 Engine commitment: zero engine code per variant

> **Adding a new variant to an existing family touches zero lines of engine code. Adding a new family is contained work in a known artefact set — new primitives, family schema, pack bindings, and sometimes lifecycle or event types — on the months-cadence engine release track, never scattered across the engine.**

The variant claim is the falsifiable one. Trivially testable: a new variant PR that modifies any file under the engine's `src/` (or equivalent) directory has failed the test. The PR description should explicitly disclose whether the variant required new primitives; if it did, the work is structurally a family-schema or pack change, not a variant change, and should be split into separate PRs.

The family claim is a *containment* claim, not an absence claim. A new family typically does require new engine primitives — personal credit (v2) needs amortisation schedulers and a TAEG/IRR solver; mortgage (v3) needs variable-rate revision and composite-case primitives like *carência* and *prestações extraordinárias*; current accounts and cards (v4) need an entirely different operating mode (irregular accrual, continuous-state subledger, revolving-credit math). Each of those is real engine code on the months cadence. What the claim guarantees is that the new code is *contained*: a new family lands as a defined set of primitives + a family schema + pack bindings + (sometimes) new lifecycle states and event types — not as edits scattered across the existing engine. The wedge is not "families are free"; it is "family work is bounded, named, and on a slow cadence, so variant work on top of a finished family stays cheap."

The diagnostic value of separating the two: a variant that "needs" engine code reveals a primitive gap (which is a planned, slow-cadence family change) or a misuse of the variant layer (which is a workflow problem). A family that needs engine changes *outside* the contained artefact set (a primitive added under the wrong subsystem, a lifecycle hook bolted onto the runtime instead of the schema) reveals a leaky abstraction. Both surface at PR review, not silently absorbed into the wedge as "well, it's still a configuration change at heart."

The wedge ultimately rests on **cadence separation**, not on the absence of work. Primitives move on months; family schemas move on quarters; variants move on weeks. Variant cadence stays fast because the slower layers stay slower — not because nothing happens at the slower layers.

### 7.2 Organisational commitment: 5 working days end-to-end

> **From PM commit to first booked instance in production: ≤ 5 working days, including engineer review, automated validator, risk review, and the deployment train.**

The number 5 is the working target; the production cadence assumed below is what makes it operable. Failure (say, a variant takes 8 working days) is a diagnostic event: which step blew its SLA? The named steps in §4 above each have their own target SLA; the end-to-end target is the sum.

The cadence implications are concrete:

- **Deployment train: weekly or faster.** A bi-weekly train forces the end-to-end target past 5 days by definition. v1 ships with a weekly train; faster (daily activation windows) is desirable but not required for the commitment.
- **Engineer review SLA: 1–2 working days.** An on-call rotation in the engine team owns variant review; review queue depth and on-call load are monitored.
- **Risk review SLA: 1–2 working days, parallelisable with engineer review.** Risk and engineering review the same PR concurrently where possible.
- **Validator: synchronous depths complete in < 30 s; CI depth (simulation) completes in < 5 min.** The synchronous depths run on every commit so the author iterates locally; CI is the gate before merge.

When the 5-day target is missed, the diagnosis lives in the workflow data: PR open-to-merge time, validator runtime, deployment lag from merge to activation. Each step has named owners; debugging is observability work, not architecture work.

### 7.3 Why both claims, separately named

The engine claim and the organisational claim could be conflated into a single SLA. Separating them is deliberate:

- The engine claim is an **architectural invariant**. Failure means a real engineering bug — a leaky abstraction, a primitive gap incorrectly papered over, a schema design that smuggles engine concerns into the variant layer. The fix is architectural.
- The organisational claim is a **workflow SLA**. Failure means a process bottleneck — review queue depth, deployment cadence, validator runtime regressions. The fix is operational.

A failure of one does not imply a failure of the other. The team can have a perfectly clean zero-engine-code property and a stalled deployment train (organisational fail, engine fine); it can have a fast workflow and a leaky abstraction (organisational fine, engine bug). Naming them separately preserves diagnosability.

---

## 8. Multi-Country Within Two Years — Implication for Roadmap

The brainstorm context for this design notes set the multi-country constraint: **the ES pack must be a concrete artefact within roughly two years of v1**, not a theoretical placeholder that lands after v4. The current [03-roadmap](./03-roadmap.md) sequences v5+ (ES) after v4 (current accounts and cards on PT). Read literally, that puts ES pack work after the entire PT product family set is complete, which is too late.

The implication: **pack abstraction work for ES has to overlap v2–v3, not wait until v5**. Specifically:

- v2 (PT personal credit) is the first phase where the pack abstraction is genuinely exercised — TAEG, DL 133/2009 disclosures, charges. v2 is the right phase to start the ES pack alongside, because the abstractions being designed for the second pack are the ones that have to swap cleanly between PT and ES.
- v3 (PT mortgage) is the phase where the pack carries the most surface (DL 74-A/2017, mandatory insurance, variable rate). v3 is where the ES pack catches up, because the test of pack abstraction is whether the most complex surface swaps cleanly.
- v5+ becomes a *deployment* milestone (turn on ES in the operating bank's stack), not a *design* milestone (start building the ES pack now). The design is already two phases old by then.

The reshape does not change the order of PT product families — v2 and v3 still ship PT-first because the operating bank's volume and regulatory expertise are PT-side. What changes is that **the pack work is treated as a parallel track, not a sequential phase**. A follow-up issue should rewrite [03-roadmap](./03-roadmap.md) to surface this as an explicit parallel track in the phase table.

The architectural reading is consistent with the brief's "[§01 §5](./01-product-architecture.md) — the pack is swappable from day one." A pack that only ever holds PT can be a *de facto* fork; only a pack that holds two jurisdictions concurrently in active development proves the abstraction is real. The roadmap should reflect that the proof has to land by v3, not deferred to v5.

---

## 9. Consequences for the Brief

### 9.1 Sections that change

- **[§00-product-vision §2](./00-product-vision.md) ("The Wedge").** The two-bullet "Agility / Unification" pair is correct but stops at category description. Add a one-line internal-target form of the wedge, pointing to this document for the falsifiable shape (zero engine code per variant; ≤ 5 working days PM commit to production). Do not turn the wedge bullets into commercial promises; the target is internal challenge.
- **[§01-product-architecture §3](./01-product-architecture.md) ("The Configuration Surface").** Currently states three load-bearing properties (declarative, synchronous validation, safe-by-default) and defers depth to [Q4 in 04-open-questions](./04-open-questions.md). Add a cross-reference to this document for the three-authoring-layer split (primitives / family schemas / variants) and the named workflow. The depth question is still open; the *layering* question is resolved here.
- **[§01-product-architecture §5](./01-product-architecture.md) ("The Regulatory Pack").** Already cross-references [feature-design-configuration-surface](./feature-design-configuration-surface.md) for the pack manifest and pinning invariant. Add the schema-pinning parallel from §6 above so both pinning invariants are visible.
- **[§02-v1-scope-term-deposits §2.4](./02-v1-scope-term-deposits.md) (event contract).** `DepositConstituted` already gains `pack_version` from the surface doc; add `schema_version` alongside. Introduce `SchemaVersionMigrated` to the event set, parallel to `PackVersionMigrated`.
- **[§03-roadmap](./03-roadmap.md).** Major restructure per §8 above: surface the ES pack as a parallel track starting in v2, not a sequential phase after v4. This is a follow-up issue, not a same-PR change.

### 9.2 Open questions touched

This document does not resolve [Q4 (Configurability Depth)](./04-open-questions.md). It also does not resolve the template-vs-DSL boundary that [feature-design-configuration-surface §4.2](./feature-design-configuration-surface.md) flagged. What it does resolve:

- **Who authors variants.** Product (PM, business analyst). Settled in §2.3.
- **Who reviews variants.** Engineer (technical), automated validator (depths 1–4), risk (exposure). Settled in §4.
- **Where compliance lives in the loop.** Upstream of variants, at the family schema and pack layers. Settled in §1 and §4.
- **What the falsifiable wedge claim is, in two parts.** Zero engine code per variant; ≤ 5 working days end-to-end. Settled in §7.

A future resolution of [Q4](./04-open-questions.md) (templates vs DSL vs both) has to work with these answers. The authoring-layer split sits under the depth question, not inside it: whichever depth is chosen, variants are authored by product against typed family schemas, validated synchronously, reviewed by engineer + risk, with compliance upstream.

### 9.3 New open questions to fold into [04-open-questions](./04-open-questions.md)

Continuing the lettered sequence from [feature-design-configuration-surface](./feature-design-configuration-surface.md) (which opened Q-I through Q-Q):

- **Q-R. Family-schema split threshold.** What concrete signal triggers a fine-drift split (§3.1)? Candidates: number of mutually-exclusive sub-blocks crossing a threshold, validator error-message length crossing a threshold, an explicit decision in quarterly schema review. Worth naming because "split when bloated" is a judgement call that needs an observable proxy.
- **Q-S. Variant deployment cadence beyond weekly.** §7.2 assumes a weekly deployment train as the floor. Some variants (promotional rate campaigns) could plausibly want daily activation windows. Is that a separate same-day-activation flag, a separate cadence track, or a reason to shorten the deployment train to daily across the board? Sits with the deployment-pipeline design, not the engine.
- **Q-T. PM-authored YAML vs. PM-driven form UI.** §2.3 commits to YAML as the variant artefact. The author may not write YAML directly — a form UI that produces the YAML is a plausible long-term path. The question is whether the form UI is an engine-team-shipped artefact (then it has the cadence of an engine release) or owned by the product organisation independently of the engine (which can iterate on input shape without engine-release timing). The latter is structurally cleaner.
- **Q-U. Risk-review automation.** Risk review (§4 Step 4) is currently a human gate. Risk corridors (rate-band caps, principal-exposure ceilings) are largely encodable. Worth asking whether a portion of risk review can become a validator depth 6 (automated risk policy) with human review reserved for variants that flag exceptions. Sits at the boundary between risk function and engineering.
- **Q-V. Schema split-merge tooling.** §3.1's fine-drift relies on splitting a bloated schema. The mechanism — how the split is authored, how existing variants are mapped to one or the other side of the split, how the engine continues to support both during the transition — is not specified. Mostly a tooling question, but the schema-pinning invariant in §6 has to hold across splits.
- **Q-W. Multi-pack variant composition.** A variant authored in 2027 for a cross-border product (book in PT, hold by ES-resident customer) needs PT-pack disclosure rules and ES-pack withholding mechanics. The variant layer presumably names two packs; the family schema declares which fields are pack-A vs pack-B. Same shape as [Q-P in feature-design-configuration-surface](./feature-design-configuration-surface.md), reached from the authoring side. Defer to the same v5-era resolution.

---

## 10. Status

This document captures a design exploration, not an adopted spec. To move from exploration to spec:

1. Fold §9.1 changes into the numbered brief documents (one PR per affected document is cleanest; the changes are small).
2. Fold §9.3 questions into [04-open-questions](./04-open-questions.md).
3. Surface the parallel-ES-pack track in [03-roadmap](./03-roadmap.md) per §8.
4. Prototype the validator CLI to the depth split in §5 — the engine claim and the organisational claim both depend on the validator being a real, fast, error-message-clear tool, not a deployment-pipeline afterthought. The natural sequel to [feature-design-configuration-surface §5](./feature-design-configuration-surface.md)'s validator/simulator CLI thread.
5. Begin the ES-pack design in parallel with v2 architectural work, per §8.

The two design-notes documents (surface and authoring) together specify the configuration system from both the artefact and the human-process angles. Future design notes are expected to cover: the validator/simulator CLI (operational shape of the tool); the deployment-train shape (how a merged variant becomes an activated configuration); the migration tooling for schema and pack version changes.
