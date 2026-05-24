# ADR-PC-015: IFRS 9 Signal Contract — Raw Operational Facts (Arrears + Credit-Lifecycle Events), Staging Derived Counterparty-Side

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | The external IFRS 9 / expected-credit-loss (ECL) system and the risk-and-finance function that owns its staging model (out-of-scope as a product per [00 §4](../00-product-vision.md); in-scope as an integration shape) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (event store + outbox — the emission substrate), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (outbox — at-least-once relay), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro + schema registry — the wire contract), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) (event-catalogue governance — these signals are public API), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (consumer-driven contract tests gate change), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) + [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) (CQRS read model + bitemporal projections — the as-of days-past-due read surface) |
| Resolves | bd `archie-10r.16`; **§2** ([04 §2 IFRS 9 Signal Boundary](../04-open-questions.md)) — *shape* only; see Residual Risks and the partial-commitment note in §2 |
| Related | [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) (GL posting signal contract — the sibling outbound-signal contract that shares the raw-facts-out philosophy), [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md) (notification emit contract — same outbound shape + the PII-by-reference rule this ADR inherits), [ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md) (AML / KYC — resolved as an *upstream precondition* (shape (c)): the gate sits at the edge, so the engine never gates and never compensates; IFRS 9 is the unambiguous post-flag end of the same spectrum) |

> **Scope note — this is a v2 contract committed at v1.** IFRS 9 impairment and staging apply to financial *assets* (loans measured at amortised cost / FVOCI). The v1 product is the *depósito a prazo* — a bank **liability**, not an exposure to which ECL staging applies — so **nothing in this contract is emitted in v1**. Personal credit lands in v2 ([03 §v2](../03-roadmap.md)); the concrete event schemas register in the catalogue then. This ADR is filed now, against [04 §2](../04-open-questions.md), so the contract *shape* is a tracked decision rather than a v2-time scramble — and so the catalogue's public-API discipline ([integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md)) governs these names the moment they exist.

---

## Context

[00 §4](../00-product-vision.md) is explicit: "**IFRS 9 staging and ECL.** The engine emits the events an IFRS 9 system needs (days past due, restructuring, write-off triggers). The IFRS 9 logic runs elsewhere. The signal-boundary contract is in scope; the staging engine is not." [03 §v4 stance](../03-roadmap.md) reinforces it: IFRS 9 is "out of scope at every phase … someone else's product. The engine emits clean signals." The IFRS 9 system is out of scope as a product; the signal boundary to it is in scope.

[04 §2](../04-open-questions.md) frames the open work as a choice of contract shape over three operational-fact families the engine has (staging triggers, days-past-due, restructuring):

- **(a)** The engine emits one event per staging change (`Stage1To2`, `Stage2To3`) — i.e. the engine *computes the stage* and ships the verdict.
- **(b)** The engine emits two signal families — a **continuous days-past-due tracker** plus **discrete restructuring / forbearance (and write-off) events** — from which the IFRS 9 system **derives** the staging.

Shape (a) drags the IFRS 9 staging model into the engine: the significant-increase-in-credit-risk (SICR) assessment, the default definition (the 90-days-past-due rebuttable presumption of IFRS 9 B5.5.37 / CRR Art. 178, and the bank's own rebuttals), the forbearance classification, and ultimately the ECL — all of which are risk-and-finance-owned, model-validated, and operating-bank-specific, exactly the out-of-scope absorption [00 §4](../00-product-vision.md) forbids. It is also the same coupling [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) rejected for the GL boundary (the engine emits the business fact, not the posting; here it emits the credit fact, not the stage). Shape (b) keeps the engine a system of *operational record* and leaves regulatory *meaning* on the IFRS 9 side. **The brief already names shape (b)** in prose; this ADR fixes it as the contract and fills the six slots.

## Decision

The engine emits **raw operational credit facts** in two signal families; the **IFRS 9 system derives staging and computes ECL** from them. The engine **never emits a stage** (`Stage1To2`, `Stage2To3`, `Stage3`) and **holds no SICR model, no default definition, no forbearance classification, and no ECL**.

The two families honour [04 §2](../04-open-questions.md)'s "continuous + discrete" split; write-off (named in [00 §4](../00-product-vision.md)) is a sibling member of the discrete family:

1. **Payload shape.** Two engine-owned event families, wrapping the standard envelope ([02 §2.4.3](../02-v1-scope-term-deposits.md) — `event_id`, `partition_key = instance_id`, `valid_time`/`transaction_time`, `correlation_id`, `causation_id`, `pack_version`). `exposure_id` is the credit `instance_id`. **Structural fields only — no PII, cleartext or ciphertext, rides these events** (see PII rule below).

   *Continuous — arrears / days-past-due:*
   ```
   ExposureArrearsUpdated
     exposure_id,                  -- = instance_id (partition key)
     days_past_due,                -- integer, ABSOLUTE value (not a delta) as of as_of
     total_overdue_amount,         -- integer cents, EUR
     oldest_unpaid_due_date,       -- value-date of the oldest unsettled installment
     arrears_state ∈ {CURRENT, IN_ARREARS, CURED},  -- operational, not a stage
     as_of                         -- = valid_time
   ```
   Days-past-due is a deterministic **bitemporal projection** over the exposure's payment schedule and `LoanPaymentReceived` / `LoanPaymentDue` facts ([ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md)); `ExposureArrearsUpdated` is the *change-event* of that projection, emitted whenever the value moves — **including the daily tick that increments DPD while an arrear persists**, which is what makes the signal a genuine continuous tracker rather than a sparse one. The same projection is exposed as an **as-of read** (`GET /v1/exposures/{id}/arrears?as_of=<date>`, [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)) so the IFRS 9 system can pull DPD at any reporting date and backstop gaps (slot 3).

   *Discrete — credit-lifecycle events:*
   ```
   LoanRestructured
     exposure_id,
     change_set: { old/new rate, old/new maturity, payment_holiday_window, … },
     restructure_reason,           -- closed taxonomy code
     forbearance_indicated,        -- boolean: operator/originating-channel signal, NOT a regulatory determination
     as_of                         -- = valid_time

   LoanWrittenOff
     exposure_id,
     written_off_amount,           -- integer cents, EUR
     write_off_reason,             -- closed taxonomy code
     as_of
   ```
   No staging event type is introduced.

2. **Semantics.** Each signal asserts a *business fact* about the exposure — "as of date D, exposure X is N days past due with Y cents overdue," or "as of date D, exposure X's terms were modified thus, operator-tagged reason R, forbearance-indicated true/false," or "as of date D, Z cents of exposure X were written off." The IFRS 9 system maps these facts to a stage and an ECL **under a staging model it owns** — it applies the SICR test, the (possibly rebutted) 90-DPD default presumption and the 30-DPD SICR backstop, and the regulatory forbearance classification. **The engine commits to the meaning and arithmetic of the fact; the IFRS 9 system owns the threshold, the model, and the verdict** — the exact division [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) draws for postings. The engine has no opinion on the stage and never sees one.

   The load-bearing subtlety is `forbearance_indicated`: IFRS 9 forbearance hinges on whether a modification was granted *due to the obligor's financial difficulty* — a regulatory judgment the engine cannot make. The engine therefore carries the **operational signal** (the operator/channel marked this modification as difficulty-driven, with a reason code), not the **regulatory determination** (whether it is a forbearance measure with staging consequence). The IFRS 9 system interprets it. Emitting a regulatory forbearance verdict would be shape (a) by another name.

3. **Ordering and delivery guarantees.** **At-least-once**, via the outbox relay ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) to the backbone ([ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)). **Per-exposure order preserved** (`partition_key = instance_id`); **no cross-exposure global order** — the IFRS 9 system must not assume a global sequence across exposures. Gap detection is the IFRS 9 system's responsibility, but it is **self-healing for the continuous family**: `ExposureArrearsUpdated` carries the **absolute** days-past-due value, so a lost message is corrected by the next one, and the as-of read (slot 1) lets the IFRS 9 system reconcile or rebuild DPD-as-of-reporting-date directly from the engine without replaying the stream. Discrete events fall back to the same reconcile-against-outbox backstop as [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md).

4. **Idempotency.** The envelope **`event_id`** is the dedupe key; redelivery or replay yields no duplicate consumer effect. For the continuous family the composite **`(exposure_id, as_of)`** is additionally stable across event-store replay — replaying the payment log re-derives the same DPD-per-date, so a projection rebuild produces **no phantom arrears transitions**. For discrete events, `event_id` is keyed to the originating restructuring / write-off command. Dedupe runs on the **IFRS 9 (consumer) side**; the engine promises effectively-once outbox relay, not exactly-once delivery — same posture as [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) and [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md).

5. **Error model.** **Post-flag, never gated — unconditionally.** IFRS 9 staging and provisioning are a reporting/finance concern strictly downstream of the engine's commit; an IFRS 9-side rejection or outage **must never block or compensate the producing flow**. A payment is received, a loan is restructured, an amount is written off whether or not the IFRS 9 system ingested the resulting fact. Failures are IFRS 9-side operational alerts plus reconciliation mismatches, resolved by **replay from the outbox** or **re-pull of the as-of arrears read**. Unlike AML/KYC ([ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md)), where the financial-crime authority *can* stop a constitution — but does so as an **upstream edge precondition**, so the engine itself still never gates and never compensates — **IFRS 9 has no legitimate gating claim on the operational flow** at all; it accounts for facts after they occur. This is the decisive reason shape (a) is rejected: owning the stage inside the engine invites a synchronous staging dependency on the credit write path that IFRS 9's downstream nature never justifies.

6. **Ownership and versioning.** The **engine owns** the `ExposureArrearsUpdated` / `LoanRestructured` / `LoanWrittenOff` schemas, the `restructure_reason` / `write_off_reason` / `arrears_state` taxonomies, and the arrears-projection semantics — governed under [integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md) / [§09](../../integration_concepts/09-long-term-schema-evolution.md) and the [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) registry the moment they register (v2). The **IFRS 9 system owns** the staging model, the SICR thresholds, the default definition, the forbearance classification, the ECL, and the consuming adapter. Breaking changes follow [§09](../../integration_concepts/09-long-term-schema-evolution.md) (keep the name for compatible evolution; parallel `V2` event with a sunset window for incompatible change). A **consumer-driven contract test** (Pact, [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) lets the IFRS 9 team pin the fields its staging depends on, so a schema change breaks at CI, not at quarter-end provisioning.

### PII by reference — inherited from ADR-PC-014, not restated

These signals are **structural-only**; `exposure_id` and any obligor identifier are **references**, never PII. No name, NIF, address, or contact data — cleartext or ciphertext — rides them. If a downstream regulatory-reporting consumer ([Q-AX](../04-open-questions.md)) needs the obligor's identity on an IFRS 9-derived report, it resolves PII at point of use against the **engine-internal PII-resolve surface** ([ADR-PC-014 §"PII by reference"](./ADR-PC-014-customer-notification-emit-contract.md), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) — which returns null for a crypto-shredded subject and keeps the OpenBao boundary inside the engine. [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md) already named this ADR as a consumer of that cross-cutting rule; this contract is the second instance and adds nothing new — which is the point. IFRS 9 provisioning is largely exposure/portfolio-level and rarely needs obligor PII at all.

## Consequences

**Easier.** The engine stays a product engine, not a credit-risk engine — no SICR model, no default definition, no staging logic, no ECL, no forbearance classification crosses the boundary, honouring [00 §4](../00-product-vision.md). The IFRS 9 system can be swapped, re-modelled, or re-validated with **zero engine change**, because it consumes operational facts, not stages. The absolute-DPD-plus-as-of-read shape gives the IFRS 9 system a free, authoritative recovery and reporting-date snapshot path. The contract reuses the existing envelope, outbox, registry, and catalogue-governance machinery — same spine as [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) and [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md), no new transport.

**Harder / locked-in.** The IFRS 9 system must build and own the derivation (DPD → SICR → stage → ECL) and an idempotent, replay-tolerant adapter — real work this contract pushes onto the counterparty by design, and the price of keeping the model out of the engine. The engine commits the arrears projection **and** the credit-lifecycle event family as **public API** under [§09](../../integration_concepts/09-long-term-schema-evolution.md) once they exist. The continuous-tracker design means the engine emits an `ExposureArrearsUpdated` on **every DPD daily tick for every delinquent exposure** — a volume the v4 load profile ([Q-AK](../04-open-questions.md), [two-modes §8](../feature-design-two-modes-asymmetry.md)) must account for when credit scales, though it is bounded by the delinquent (not total) book.

**Impossible by construction.** The engine cannot emit a stage it does not compute (it computes none). An IFRS 9 outage cannot stall or fail a credit operation (post-flag, never gated). A lost arrears message cannot silently desynchronise staging (absolute values are self-healing; the as-of read is authoritative). The engine cannot become the system of record for "what stage is this exposure" — because it deliberately holds no stage.

## Residual risks

- **The contract shape is committed; the schema is not, and the SME conversation is the production gate.** [04 §2](../04-open-questions.md) names the unblocking input: an IFRS 9 SME conversation (a risk-quant or model-validation lead at the operating bank, or a consultant who has integrated several IFRS 9 vendors). This ADR fixes the *shape* (raw facts out, staging derived counterparty-side, two families, post-flag, absolute-DPD + as-of read, `event_id` idempotency); the **exact field set, the `restructure_reason` / `write_off_reason` taxonomies, and confirmation that a real IFRS 9 vendor can derive staging from this shape rather than expecting `Stage1To2`** land in the v2 credit scope after that conversation — the same accept-now-gate-at-production posture as [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) holds for [§1](../04-open-questions.md). If an SME shows a target vendor *requires* the engine to ship the stage, shape (a) is reconsidered — but the default position is that a vendor demanding the bank's engine compute its staging is the coupling to avoid, not accommodate.
- **A single-vendor bank pays a small tax for vendor-portability it may not use.** [04 §2](../04-open-questions.md) notes shape (a) is "simpler if the bank uses a single IFRS 9 system that already has a known contract." Shape (b) asks even a single-vendor bank to build the DPD→stage derivation rather than consume a ready-made staging event. The trade is deliberate: the derivation is the IFRS 9 system's competency anyway, and keeping the model out of the engine is worth more than saving one adapter the risk function would build regardless.
- **What this contract does not commit to.** The SICR model, the default definition and its rebuttals, the forbearance classification, the ECL methodology, and the IFRS 9 reconciliation thresholds are **IFRS 9-team / operating-bank deliverables**. The engine supplies facts; it does not supply (and will not be asked to supply) the regulatory interpretation of them.
- **Failure mode the contract permits.** An IFRS 9 adapter that under-subscribes (misses a credit-lifecycle event type as the v2+ catalogue grows — e.g. a future `LoanRescheduled` distinct from `LoanRestructured`) will mis-derive staging until reconciliation catches it; the Pact contract narrows but does not eliminate this, since a *newly added* event type is invisible to an existing consumer contract. Mitigation is the same catalogue-governance review ([integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md)) that [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) relies on, flagging new credit-relevant events to the IFRS 9 team at authoring time.

## Verifiable commitments

| # | Commitment | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| C1 | **Post-flag, never gated — unconditionally** (§Error model): IFRS 9 staging/provisioning is strictly downstream of the engine's commit — an IFRS 9-side rejection or outage never blocks or compensates the producing flow. | contract / saga | `POST_FLAG_NEVER_GATES` | Planned |

Seeded in the [commitment catalogue](../../../../conformance/README.md) ([`commitments.yaml`](../../../../conformance/commitments.yaml)) per [ADR-PC-020 §P5/§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (Open Action #4). The same invariant is verified once across [PC-012](./ADR-PC-012-gl-posting-signal-contract.md) and [PC-014](./ADR-PC-014-customer-notification-emit-contract.md).
