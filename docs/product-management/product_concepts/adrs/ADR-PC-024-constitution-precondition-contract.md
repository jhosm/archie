# ADR-PC-024: Constitution Precondition Contract — Engine Declares, Upstream Evaluates, Decider Refuses

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-03 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | The **constitution saga** ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md), [integration_concepts §05](../../integration_concepts/05-constitution-saga-walkthrough.md)) that resolves each precondition verdict from its owning upstream system (CRM for account age / relationship; Core Banking for fund provenance and salary domiciliation; the credit system for a linked mortgage), and the **product-config author** who declares which preconditions a product requires |
| Depends on | [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (product config + deploy API — where the required-precondition list is authored), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the family decider that performs the refusal stays pure), [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) (the saga that gathers verdicts) |
| Resolves | §B commercial-eligibility gap (term-deposit scope review, 2026-06-03) |
| Related | [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) / [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) / [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (the signal-contract family — the [ADR-PC-000 signal-contract principle](./ADR-PC-000-namespace-and-contract-shape-framework.md) *"the engine records/consumes a fact, never owns the verdict"* applied here to an inbound, product-specific precondition) |

---

## Context

The v1 scope ([02 §2.1](../02-v1-scope-term-deposits.md)) narrows the depositor to a resident individual and does not enumerate **commercial product eligibility** — the rules that decide *who may constitute which product*: new-client-only promotions, new-money requirements (the principal must not have come from another account at the same bank within a window), salary-domiciliation requirements, mortgage-linked preferential products. Real PT deposit product lines run these; a v1 that cannot express them cannot run those lines.

These checks have an awkward property: they are **product-specific** (the rule belongs to the product — like a product limit), yet their evidence lives **upstream** (the engine cannot see the bank's transaction history to decide "new money", or the CRM to decide "new client"). So they fit neither of the engine's existing patterns cleanly:

- They are **not** like `ValidateProductLimits` ("does the client already hold N of this product; is the amount in range") — that the engine evaluates entirely from its own state.
- They are **not** financial-crime adjudication. AML/KYC is upstream and **out of scope** for the product engine ([00 §4](../00-product-vision.md)); it is not modelled here at all.

The decision splits the concern across the seam it actually lives on: the **engine declares and refuses; upstream evaluates**.

## Decision

Constitution preconditions are a **generic, product-config-declared contract**: the **engine declares** which preconditions a product requires and **refuses** without them; **upstream evaluates** them.

1. **Payload shape.** The product config ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) artefact) carries a `required_preconditions` list of closed verdict keys:

   ```
   product config
     ...
     required_preconditions: [ is_new_client, is_new_money, salary_domiciled,
                               mortgage_linked ]   -- pack-constrained closed set
   ```

   The constitution command arrives carrying the **resolved verdicts** the saga gathered:

   ```
   constitute command (into the engine)
     ...standard fields (customer_id, product_code, principal_cents, …)
     preconditions: { is_new_money: { satisfied: true,  evidence_ref, evaluated_at },
                      salary_domiciled: { satisfied: false, evidence_ref, evaluated_at } }
   ```

   Each resolved verdict is an opaque `{ satisfied, evidence_ref, evaluated_at }` triple. The engine records the verdicts on the `DepositConstituted` (or `DepositConstitutionFailed`) envelope **for audit lineage only**. **No PII rides a verdict** (`evidence_ref` is a reference, not identity data; [ADR-PC-014 PII-by-reference](./retired/ADR-PC-014-customer-notification-emit-contract.md), inherited).

2. **Semantics.** A verdict asserts a *fact* — "an upstream authority evaluated this product-specific predicate for this customer at `evaluated_at` and it is `satisfied` / not." The **meaning** of each predicate (what counts as "new money", the look-back window, what a "salary domiciliation" is) is **entirely upstream/pack-owned**. The engine holds **no transaction history, no CRM data, no provenance model**; it treats each verdict as opaque and never re-evaluates it. What the engine owns is *which* verdicts a product requires (`required_preconditions`) and *that they must be `satisfied: true`* to constitute.

3. **Ordering and delivery guarantees.** Verdicts are resolved **before the decider runs** — the saga gathers them in its validation fan-out ([integration_concepts §05](../../integration_concepts/05-constitution-saga-walkthrough.md)) alongside the `ReserveAccountBalance` and `ValidateProductLimits` steps, and passes them in the command. The decider is a pure function of `(state, command)`; **no precondition is fetched inside the fold** ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md), [event-store §5.1](../feature-design-event-store-projections.md)). There is no in-engine precondition adapter call and no compensation flow.

4. **Idempotency.** The refusal is a **pure function of the command's verdicts**; replay re-presents the recorded verdicts and re-derives the identical accept/reject without any upstream re-call, because none ever happened inside the engine. A re-submitted constitution with the same verdicts yields the same outcome.

5. **Error model.** A **required precondition that is absent or `satisfied: false` fails the constitution** — the decider emits `DepositConstitutionFailed` ([02 §2.4.1](../02-v1-scope-term-deposits.md), reason `COMPLIANCE_REJECTED`, or a new `ELIGIBILITY_NOT_MET` reason). This is **not** a compensation: the deposit is never constituted, so there is nothing to unwind. The engine refuses *before* the irreversible Core debit, behind the saga's reversibility ordering ([integration_concepts §05](../../integration_concepts/05-constitution-saga-walkthrough.md), Primitive 6).

6. **Ownership and versioning.** The **engine owns** the `required_preconditions` schema, the closed verdict-key taxonomy, and the refusal semantics. The **product config owns** which preconditions a given product requires (authored + deployed per [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md), versioned + pinned per instance like every product rule). The **pack owns** which predicates are legally permissible in a jurisdiction (a PT pack may forbid a discriminatory predicate). The **upstream systems own** evaluation. A new predicate is a pack/config addition with **zero generic-engine diff** ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)); the engine changes only if the verdict *envelope* shape changes, governed under [integration_concepts §09](../../integration_concepts/09-long-term-schema-evolution.md). A consumer-driven contract test ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) pins the verdict keys the saga must supply, so an upstream change that drops a required verdict breaks at CI, not at a production constitution.

### How this relates to `ValidateProductLimits`

This contract extends the side of constitution validation the engine **keeps** — product rules the engine enforces, like `ValidateProductLimits` — to product rules whose *evidence* is an upstream-supplied verdict rather than engine state. `ValidateProductLimits` reads engine state and rejects; a precondition reads a verdict the saga placed in the command and refuses. Both are pure decider logic; neither calls out from inside the fold, holds a screening model, or runs a compensation. The only difference is where the evidence comes from.

## Consequences

**Easier.** The engine can run eligibility-gated product lines without learning the bank's transaction history, CRM, or credit estate — it stays a product engine. Adding a predicate (a new promotion's new-money window) is pack/config work, zero engine diff. The saga's existing validation fan-out is the natural home; the contract reuses the command path, the `DepositConstitutionFailed` event, and the audit-lineage envelope pattern shared with the rest of the signal-contract family.

**Harder / locked-in.** The saga must reliably resolve every verdict a product's config requires *before* dispatching the constitution command, and the operating bank must own each upstream evaluator (new-money provenance is a real Core-Banking query). The engine's correctness now depends on an upstream contract it does not own — mitigated by the CDC test on the required-verdict set. The required-precondition list is product config and therefore **public, versioned, pinned-per-instance** API.

**Impossible by construction.** The engine cannot constitute a deposit whose product requires a precondition that is absent or unsatisfied (the decider refuses). The engine cannot *be* the system that decides "is this new money" — it holds no provenance data and records only an opaque verdict. A replay cannot diverge on an eligibility decision (the verdicts are on the command; the fold is pure).

## Residual risks

- **Verdict freshness is an upstream/operating-bank concern.** `evaluated_at` records *when* a verdict was taken, but how stale a verdict may be before the saga must re-resolve it (a salary domiciliation cancelled between evaluation and constitution) is a saga-design / operating-bank policy, not an engine guarantee. The engine records the timestamp; it does not enforce a freshness window.
- **Which predicates are *legal* is pack work, deferred.** This contract fixes the *mechanism*; the closed verdict-key taxonomy and the per-jurisdiction legality of each predicate are PT-pack content authored when a launch product first requires them — **v1.x**, not v1 (v1 launch products are not eligibility-gated per [02 §4](../02-v1-scope-term-deposits.md)).
- **Coarse edge admission vs fine engine precondition.** [integration_concepts §00](../../integration_concepts/00-introduction-and-decisions.md) names "product eligibility?" among *edge* pre-validations. That edge check is the **coarse admission** gate (is this customer in the eligible population at all — resolvable upstream, product-agnostic); the **fine, product-config-specific** preconditions are decider-enforced here. The two coexist without conflict.
- **What this contract does not commit to.** The evaluators (provenance queries, CRM look-ups, credit checks), the data they read, and the look-back windows are **upstream / operating-bank deliverables**. The contract supplies the declaration surface and the refusal; it does not supply the evidence.

## Amendment — 2026-06-12: where the verdict audit-lineage is recorded (F.9 implementation)

Implementing F.9 ([bd babelstone-k6r8.2](../04-open-questions.md)) revealed that §1's "the engine **records the verdicts** on the `DepositConstituted` (or `DepositConstitutionFailed`) envelope for audit lineage only" is satisfiable on the **refusal** path now, but not on the **accepted** path without a disproportionate generic-engine change — because the two events sit on different contract surfaces. This amendment is additive: it pins *which* envelope carries the lineage in v1 and time-bounds the accepted-path gap, leaving §1's recording intent intact.

### A1 · The refusal-path lineage rides `DepositConstitutionFailed` (store-only JSON)

For an `ELIGIBILITY_NOT_MET` refusal, the resolved verdicts (the opaque `{ key, satisfied, evidence_ref, evaluated_at }` triples) are recorded on `DepositConstitutionFailed`. That event is **not** bus-published (it has no `.avsc`), so it flows only through the event-store JSON codec — the audit book of record per [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md). This is the lineage the load-bearing commitment (`CONSTITUTION_PRECONDITION_REFUSAL`, §Verifiable-commitments) depends on, and it lands in v1 with F.9. The verdicts stay structural / non-PII (`evidence_ref` is a reference, §1) so the store record carries no identity data.

### A2 · The accepted-path on-envelope lineage on `DepositConstituted` is deferred to v1.x

`DepositConstituted` is a **bus-published** event (governed by the Avro schema + registry of [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md); it has an `.avsc` and an AsyncAPI EventCatalog entry). The Avro bus codec enforces strict C#↔schema field parity and currently has no array-of-record support, so recording a verdict **list** on `DepositConstituted` would (a) force store-only audit lineage onto the durable bus — widening the bus contract for data with no named consumer — and (b) require a generic-codec change. Per [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md) the audit book of record is the store JSON, not the Avro projection; so v1 records the *accepted-path* verdict lineage **not at all on the envelope** and defers an on-`DepositConstituted` (or store-side) accepted-path lineage to **v1.x**, when a launch product is first eligibility-gated (the same v1.x boundary §Residual-risks already sets for predicate legality). The refusal-path lineage (A1) is unaffected.

### A3 · This amends the decision; it does not supersede this ADR

§D1–§D6 remain binding as written — payload shape, opaque-verdict semantics, the pre-decider ordering, the pure-replay idempotency, the `ELIGIBILITY_NOT_MET` refusal before the irreversible debit, and the engine-owned-taxonomy / config-owned-list ownership split are all implemented exactly as decided. This amendment only **localises §1's "records the verdicts on the envelope" clause** to the refusal-path event in v1 and time-bounds the accepted-path recording — it is appended to, not a revision of, §1.

## Verifiable commitments

This contract's load-bearing commitment is a fitness function in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for its exact claim, gate, and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `CONSTITUTION_PRECONDITION_REFUSAL` — a required precondition that is absent or `satisfied: false` yields `DepositConstitutionFailed`, computed as a pure function of the command's verdicts; no in-engine evaluation, no compensation (slot 5 · Error model).
