# Feature Design — Strangler-Fig Coexistence

> Companion to the brief. Deepens [01 §6](./01-product-architecture.md) and supplies the architectural answer for [04 §5](./04-open-questions.md) — narrowed to operational-calibration scope once the architecture committed here. The brief treats coexistence as a half-page subsection; this note treats it as what it is: a multi-year period during which two peer systems run the same product family concurrently, with seven dimensions of dual operation.
>
> Unusually operations-heavy: names specific systems (legacy core, reporting application, ACL, unified read model) and is concrete about runbooks, alerts, and end-of-day plumbing. The brief's value depends on v1 actually working in production, and v1 is the period when coexistence is most fragile.
>
> Reading order: §1 period vs steady state · §2 seven dimensions · §3 SoR map · §4 settlement plumbing · §5 daily batch file · §6 unified read surface · §7 reconciliation · §8 regulatory reporting · §9 SoR-transition event chain · §10 cutover · §11 end state.

---

## 1. Frame: Coexistence Is a Period, Not a Steady State

The strangler-fig motion in [00 §5](./00-product-vision.md) and [01 §6](./01-product-architecture.md) commits to coexistence as a **multi-year period** with start, middle, and end phases — not a steady-state property the engine has. This document specifies the period: three phases with distinct risks, distinct operational shape, and distinct exit criteria; seven dimensions of dual operation; the system-of-record map; the legacy emission shape; the unified read surface; reconciliation; regulatory reporting; the SoR-transition event chain; cutover mechanics; and the end state.

Three phases, each load-bearing on different things:

| Phase | Duration | Defining characteristic | Dominant risk |
|---|---|---|---|
| **Cutover** | Days to weeks | First time both systems run the same product family in parallel; shadow run completes; routing flips | Operational: routing misconfiguration, ACL idempotency failures, the day-1 renewal load spike |
| **Middle** | 1–3 years (term-deposit-specific; longer for current accounts) | Both systems are peers; legacy book drains as deposits mature; engine book accumulates | Architectural: silent state divergence, reconciliation drift, regulatory reports built on incomplete inputs |
| **End** | Days to weeks | Last legacy instance matures or is force-renewed; legacy decommissioned | Operational: residual instances stranded on legacy; final reconciliation; archive migration |

Each phase has a different operational profile. Cutover is high-attention, short-duration, fully-staffed. The middle phase is the long tail where attention naturally drops but where most of the operational risk accrues. The end is short again but requires a deliberate trigger that someone has to pull.

[01 §6](./01-product-architecture.md) commits to three coexistence properties of the integration seam (per-product-line onboarding, API coexistence, event-contract reactivity). This document specifies how the period itself is operated underneath those properties.

---

## 2. The Seven Dimensions of Dual Operation

During the middle phase, seven things have to work in parallel. Each is its own engineering concern; each has a place where it can go wrong silently; each is named explicitly here so it does not get lost.

| # | Dimension | What it covers | Section |
|---|---|---|---|
| 1 | **System-of-Record map** | Which system owns each product instance, and the rule that decides | §3 |
| 2 | **Settlement plumbing** | Engine debits/credits current accounts on legacy via the ACL | §4 |
| 3 | **Legacy emission** | How legacy's state reaches the engine and downstream consumers | §5 |
| 4 | **Unified read surface** | CQRS read model that spans both systems with different staleness profiles | §6 |
| 5 | **Reconciliation** | End-of-day comparison: engine outbox vs legacy batch; alerts on mismatch | §7 |
| 6 | **Regulatory reporting** | BdP retail-deposit statistics, FGD coverage, IRS Modelo 39 — all cross-system | §8 |
| 7 | **SoR transitions** | When a legacy instance migrates to the engine (renewal = new engine instance) | §9 |

Two further concerns frame the period itself rather than the dual operation — **cutover mechanics** (§10) and **the end state** (§11) — and are treated separately because they are events, not steady-state dimensions.

The seven dimensions are not independent. Settlement plumbing and reconciliation share the ACL. Legacy emission and the unified read surface share the daily batch file. Regulatory reporting depends on the unified read surface and on the SoR map. The dependencies are called out in each section.

---

## 3. The System-of-Record Map

At any moment during the coexistence period, every product instance is owned by exactly one system. The engine owns its instances; the legacy core owns its instances; no instance has two SoRs. This is the property that prevents split-brain ledgers — the failure mode that [04 §5](./04-open-questions.md) was originally about under its earlier "Split-Brain Reconciliation" framing.

### 3.1 The rule for term deposits (v1)

For *depósito a prazo* in v1, the SoR map is **date-based** with one branch:

- **Constituted before cutover, still in-flight**: legacy is the SoR.
- **Constituted on or after cutover**: engine is the SoR.
- **Renewed on or after cutover** (originally constituted on legacy): engine is the SoR for the new instance; legacy is the SoR for the historical (now-matured) instance. See §9 for the transition mechanics.

The rule is a single function of the instance's `constituted_at` timestamp. There is no per-customer routing, no per-region split, no per-product-variant exception. Cutover is a wall-clock event: deposits constituted before the cutover timestamp stay on legacy until they mature; deposits constituted after are engine-native.

The simplicity is the point. A more nuanced rule (some new deposits on legacy because of a specific edge case; some legacy deposits migrated mid-life because of a customer request) is engineering complexity that compounds over the multi-year coexistence period. The discipline is to refuse those nuances at the architectural level and handle individual exceptions out-of-band rather than in the SoR map.

### 3.2 The rule for later product families (v2+)

For credit, mortgage, current accounts, and cards — the families added in the [roadmap](./03-roadmap.md) after v1 — the rule is **product-family-based**:

- A product family is *on the engine* once the engine ships its v-N support and the bank cuts over.
- Until that v ships, every instance of that family is owned by legacy.
- Once that v ships, every *new* instance of that family is owned by the engine; existing instances either age out on legacy (with-a-plan families — credit, mortgage) or are migrated in a separate operation (irregular families — current accounts, cards, where there is no maturity date to age out against).

The asymmetry between with-a-plan and irregular families is load-bearing for the end state (§11). With-a-plan families self-drain: deposits mature, credits amortise, mortgages amortise. Irregular families do not self-drain — a current account has no end date — so they require an explicit migration event or they live on legacy forever.

### 3.3 What the engine commits to per-instance

Every instance the engine constitutes carries, in addition to the envelope fields from [event-store §4.3](./feature-design-event-store-projections.md):

- `sor: engine` — set at constitution, never changes.
- `originating_legacy_id` (optional) — present when the engine instance was created by renewal of a legacy instance (see §9). Carries the legacy instance's identifier so the audit chain reaches across the SoR transition.
- `cutover_cohort` (optional) — names the cutover event that created the instance's SoR status, for cohort analysis during the middle phase.

Legacy instances carry no engine-side marker; the engine simply does not know about them except through the daily batch file (§5) and the unified read surface (§6).

---

## 4. Settlement Plumbing

The engine debits and credits customer current accounts on the legacy core throughout the coexistence period. This is already covered by [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md) and applied in [02 §3](./02-v1-scope-term-deposits.md); the coexistence framing does not change any of it. This section exists to name the dependency, not to redefine the mechanism.

The settlement events that flow through the ACL during coexistence:

| Event on the engine | Effect on legacy |
|---|---|
| `DepositConstituted` | Debit principal from depositor's current account on legacy |
| `InterestPaid` | Credit net interest to depositor's current account on legacy |
| `DepositMatured` | Credit principal + final net interest to depositor's current account on legacy |
| `DepositTerminatedEarly` | Credit (principal − penalty) + net accrued interest to depositor's current account on legacy |
| `DepositPartiallyWithdrawn` | Credit withdrawn principal + net accrued interest (on the withdrawn portion) to depositor's current account on legacy |

Each of these triggers an ACL call. The ACL handles the seven responsibilities from [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md) (semantic translation, protocol translation, idempotency, ID mapping, error translation, latency adaptation, periodic reconciliation), including the indeterminate-state problem that is the heart of the split-brain risk.

The settlement direction is asymmetric. The engine emits commands toward legacy via the ACL (engine → legacy). Legacy emits *facts* toward the engine via the daily batch file (legacy → engine, §5). There is no bidirectional command channel; legacy never commands the engine. This asymmetry is what makes the coexistence tractable — the engine is a strangler-fig consumer of legacy data and an active driver of legacy state changes, but not a participant in legacy's own command paths.

---

## 5. Legacy Emission: The Daily Batch File

The first decision committed in this document: **legacy emits a daily batch file**. Not events on Redpanda. Not a near-real-time CDC stream. A flat file, produced once per day at end-of-day, listing the day's facts (new constitutions, maturities, terminations, partial withdrawals, restructurings) for instances under legacy's SoR.

### 5.1 Why a batch file

Legacy is a multi-decade core that was never designed for event emission. The choices the bank has to surface legacy state to the engine are:

| Option | Engineering effort on legacy | Latency to engine | Failure modes |
|---|---|---|---|
| **Native event emission** (Redpanda producer in legacy) | Very high (touch legacy's core write path) | Near real-time | Couples legacy stability to broker availability |
| **CDC** (log-based change capture) | Moderate (database-level) but high operational complexity | Seconds | CDC pipeline failure modes; schema-drift surprises; transactional context lost |
| **Daily batch file** | Low (most legacy cores already produce end-of-day extracts for downstream consumers) | Up to 24 hours | File-level failure modes (truncation, malformed records); known and well-understood operationally |

The choice is the daily batch file. The reasoning is operational, not architectural: the bank's legacy core almost certainly already produces an end-of-day extract for some downstream consumer (the GL, the regulatory reporting application, the data warehouse). Adding the engine as another consumer of that extract — or producing a near-identical second extract scoped to product families on the engine — is days of work and zero risk to legacy's write path. CDC and native event emission are months of work, often touching code that nobody on the bank's current team wrote, with a non-trivial risk of destabilising legacy itself.

The cost is latency. The unified read surface (§6) and the reconciliation process (§7) both inherit a 24-hour staleness profile on legacy-sourced data. This is the architectural consequence the brief has to absorb: **the engine's coexistence guarantees are eventual, not real-time, on the legacy side.**

### 5.2 What the batch file carries

A daily batch file is a flat-format extract (CSV, fixed-width, or whatever the bank's legacy core natively produces) covering the day's facts for product families the engine cares about. For v1, that scope is term deposits on legacy. Per-record fields, at minimum:

- `legacy_instance_id` — legacy's identifier for the instance.
- `fact_kind` — one of: `constituted`, `interest_accrued`, `interest_paid`, `matured`, `terminated_early`, `partially_withdrawn`, `corrected`, `transferred_to_heirs`. Maps onto the family-specific events the engine declares in [event-store §4.2](./feature-design-event-store-projections.md) and [02 §2.4](./02-v1-scope-term-deposits.md).
- `fact_date` — when the fact occurred in legacy's books.
- `legacy_state_snapshot` — the legacy instance's state after the fact (principal, accrued interest, withholding to date, lifecycle state).
- Family-specific payload (principal cents, rate, term, maturity date, etc.) per fact kind.

Crucially: the batch file is **not** the engine's event store. The engine does not commit batch-file records to its event log as native events. Instead, the engine emits a single cross-cutting event per ingested record:

- `LegacyInstanceObserved` (cross-cutting, engine-declared, defined in [event-store §4.1](./feature-design-event-store-projections.md)). Payload: `legacy_instance_id`, `observed_at`, `legacy_state_snapshot`, `batch_file_id`, plus the `fact_kind` and family-specific data lifted from the batch record.

The semantic separation matters. `LegacyInstanceObserved` says "we observed that legacy reports this fact"; it is the engine's truthful record of *what legacy told us*. It is not "the fact is true in our domain"; legacy retains SoR for the instance, and the engine's view is a derived projection. When the fact's instance later migrates to the engine via renewal (§9), the engine emits its own native family-specific event (`DepositConstituted`) and the chain of `LegacyInstanceObserved` events stays as the audit trail of the legacy lifetime.

### 5.3 Batch file governance

The batch file is a public contract between legacy and the engine, even though both systems are inside the same bank. The governance shape:

- **Schema versioned.** Every batch file carries a schema version in its header. The engine's ACL rejects files with an unknown schema version and pages an operator rather than silently dropping records.
- **Idempotent ingestion.** A batch file can be re-ingested without producing duplicate `LegacyInstanceObserved` events. The engine deduplicates on `(legacy_instance_id, fact_kind, fact_date)` plus a fact-specific natural key (e.g. for periodic interest payments, the payment date).
- **Completeness contract.** Legacy's emission must be all-or-nothing per day: either every fact for the day is in the file or no file is shipped. Partial files are rejected.
- **Lateness contract.** The file must be available to the engine by a published cutoff (e.g. 04:00 local time on day D+1). Missed cutoffs page operations.
- **Schema-drift protocol.** When legacy changes its extract (a new field is added, a rate scaling changes), the change is coordinated through a written contract update; the engine's ACL is updated to parse the new shape before the new extract ships.

The detailed shape of this contract — exact format, exact cutoffs, exact dedupe keys — is left open as [Q-AH in 04-open-questions](./04-open-questions.md), because it depends on what the operating bank's legacy core already produces.

---

## 6. The Unified Read Surface

[01 §6](./01-product-architecture.md) names a unified read surface as one of the three coexistence properties. [integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md) covers the read-model pattern in general. This section specifies how the read model handles **two sources with different staleness profiles** — the case integration_concepts §03 does not address explicitly.

### 6.1 Two sources, two staleness profiles

The unified read surface ingests from two sources:

| Source | Ingestion shape | Staleness | Consistency model |
|---|---|---|---|
| **Engine** | Real-time event stream (Redpanda) via the CQRS projector | Seconds | Eventually consistent, near-real-time |
| **Legacy** | Daily batch file via ACL ingestion | Up to 24 hours | Eventually consistent, daily |

The read model's projector is the same regardless of source — it is the [integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md) projector consuming the engine's event stream — but the events arriving from legacy via `LegacyInstanceObserved` carry a 24-hour staleness profile that the events native to the engine do not.

### 6.2 Per-row staleness in the read model

Every row in the unified portfolio projection carries an `as_of` timestamp and a `source` label:

```sql
-- portfolio_term_deposits (sketch)
deposit_id              text primary key
customer_id             text
sor                     text  -- 'engine' or 'legacy'
principal_cents         bigint
accrued_interest_cents  bigint
maturity_date           date
state                   text
as_of                   timestamptz  -- when the engine last knew this row was true
source                  text  -- 'engine_event' or 'legacy_batch_file_<id>'
```

Channels reading the projection can ask the projection itself "how fresh is this row?" without consulting a separate metadata service. A row sourced from `engine_event` is fresh to within seconds; a row sourced from `legacy_batch_file_<id>` is fresh to within 24 hours of `as_of`.

The `as_of` is the **engine's transaction time** for the row — when the engine learned the fact. For engine-sourced rows it matches the originating event's transaction_time; for legacy-sourced rows it is the engine's ingestion timestamp of the batch file, which lags legacy's `fact_date` by up to 24 hours.

This pairs with the bitemporal projection capability from [event-store §6](./feature-design-event-store-projections.md). For legacy-sourced rows, the bitemporal pair is *(valid_time = fact_date, transaction_time = ingestion_time)*. The 24-hour gap between the two is observable, not hidden.

### 6.3 Channel implications

Different channels have different tolerances for staleness. The read-model architecture surfaces the staleness; the channel decides whether to use the row, refuse it, or annotate it for the user.

| Channel | Staleness tolerance | What it does with a 24h-stale row |
|---|---|---|
| Mobile app, online banking | Hours | Shows the row; may annotate "data as of yesterday" for legacy-sourced positions |
| Branch teller | Hours, but with operational caveats | Shows the row; refuses to authorise certain operations against legacy-sourced rows without an explicit refresh-from-legacy call |
| Regulatory report (BdP statistics) | Days, with monthly aggregation | Uses the row as-is; the daily granularity is sufficient for monthly returns |
| Risk reports (intraday liquidity) | Real-time | Cannot rely on legacy-sourced rows; must call legacy directly or accept the staleness explicitly |
| Internal analytics / BI | Days | Uses the row as-is; the analytics workload is already T+1 anyway |

The point is not to define every channel's contract here — that is the channel team's job. The point is that the read model **exposes the staleness as a first-class property** so the channel can make the right call. A read model that homogenises the two sources into "one consistent view" is a read model that lies, and the lie surfaces during incidents when a 24h-stale legacy row is acted on as if it were fresh.

This is the question that [Q-AF in 04-open-questions](./04-open-questions.md) leaves open: which channels can tolerate the staleness, and where do per-channel refresh paths back to legacy need to exist.

### 6.4 Per-instance routing for state-changing operations

When a customer wants to perform an operation against a specific instance — terminate early, withdraw partially, change the auto-renewal policy — the channel needs to know which system holds the SoR to route the command correctly. The unified read surface provides the routing data (`sor` column), but the routing logic itself lives somewhere in the request path between channel and core.

Three credible locations for the routing logic, none obviously correct:

| Location | Mechanism | Cost | Tradeoff |
|---|---|---|---|
| **Channel** | Each channel reads `sor` from the projection and dispatches accordingly | Per-channel duplication of routing logic | Channels diverge over time; the routing rule is enforced in N places |
| **Unified API gateway** | A single API in front of channels resolves `sor` and dispatches | A new system to build and operate | Centralises the routing rule but adds a hop and a new operational dependency |
| **Read model** | The projection exposes a "command endpoint" alongside the row | Couples read and command surfaces | Mixes CQRS sides; pragmatic but architecturally noisy |

The decision is deferred as [Q-AI in 04-open-questions](./04-open-questions.md). The operating bank's existing channel architecture probably already has an opinion about this — the engine should fit into it, not invent a new pattern.

---

## 7. Reconciliation

Reconciliation is the operational check that prevents split-brain ledgers. [04 §5](./04-open-questions.md) was originally about reconciliation; this section gives the architectural answer, leaving only the operational SLA calibration open.

### 7.1 Three reconciliation flows

Three distinct comparisons happen daily during coexistence. They catch different failure modes; all three are needed.

| # | What is compared | Catches |
|---|---|---|
| 1 | **Engine's settlement outbox** (commands sent to legacy via ACL) vs **legacy's incoming credit/debit journal** for the day | The engine sent a settlement that legacy didn't book (or vice versa); ACL idempotency failure |
| 2 | **Engine's view of legacy instances** (rows in the read model with `sor: legacy`) vs **legacy's current state** as reported in today's batch file | Engine missed a legacy event (ingestion failure); engine has a stale view of a legacy instance |
| 3 | **Engine's instances on the engine** (rows in the read model with `sor: engine`) vs **engine's own event store** rebuilt projection | Internal engine drift; covered fully by [event-store §7](./feature-design-event-store-projections.md) (this section just names it for completeness) |

Flow 1 is the split-brain check. Flow 2 is the staleness/completeness check. Flow 3 is the engine-internal check.

### 7.2 Reconciliation flow 1: settlements

The engine's outbox records every settlement command sent to legacy (debit at constitution, credit at interest payment, credit at maturity, credit at early termination, credit at partial withdrawal). The legacy core's incoming journal records every debit and credit it booked for the day. At end of day, a reconciliation job compares the two:

- **Match (expected):** Every engine outbox command for the day has a corresponding entry in legacy's journal with matching amount, matching customer current-account ID, matching correlation_id.
- **Engine-side orphan:** Outbox has a command; legacy journal does not. Either the ACL call failed silently (idempotency bug) or legacy missed the command (legacy-side bug). Alerts ops.
- **Legacy-side orphan:** Legacy journal has an entry the engine did not emit. Either the engine emitted the command but the outbox record was lost (engine-side bug, very serious) or legacy is showing an unrelated entry that has nothing to do with the engine (false positive — needs filtering). Alerts ops.
- **Amount mismatch:** Both sides have a record but the amounts disagree. Almost always a sign of a deeper bug; pages an on-call engineer.

The alert thresholds (how many engine-side orphans per day cross from "operational noise" to "fundamentally broken") are unknown in advance and require a calibration period. This is [Q-AG in 04-open-questions](./04-open-questions.md).

### 7.3 Reconciliation flow 2: legacy state

The engine's read model maintains a projected view of every legacy instance, built from the cumulative `LegacyInstanceObserved` event stream. Today's batch file should be consistent with that projected view plus today's facts. The reconciliation job compares the two:

- **Match:** Every legacy instance in the engine's projection matches its state in today's batch file (after applying today's facts).
- **Engine-side gap:** Engine's projection is missing an instance that legacy reports. Either the constitution batch file was never ingested (ingestion failure) or the engine's deduplication dropped a legitimate record (idempotency bug).
- **Legacy-side gap:** Engine's projection has an instance legacy doesn't report. Possible if legacy archived the instance; possible if there's a definitional drift. Investigated.
- **State mismatch:** Both have the instance but states disagree. Most concerning case — either ingestion lost a fact or there's an out-of-band change in legacy the batch file did not surface.

Flow 2's alert thresholds are also covered by [Q-AG in 04-open-questions](./04-open-questions.md).

### 7.4 Reconciliation cadence and ownership

All three flows run daily at the same end-of-day window. The runtime is a separate reconciliation job, not part of the engine's normal projection runtime. The job emits a daily reconciliation report — a structured record per day per flow, with counts of matches and mismatches and pointers to specific records on either side.

Ownership of the report sits with the operating bank's operations function, not the engine team. The engine team owns the *runtime* of the reconciliation job; the operations team owns the *interpretation* of the report and the decision tree for what to do when a mismatch is flagged. The boundary is the same as the boundary for any other periodic-reconciliation responsibility in [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md) §7.

---

## 8. Regulatory Reporting Under Coexistence

The second decision committed in this document: **regulatory reporting aggregates downstream**. The engine emits its own events; legacy emits its daily batch file; a third system — the reporting application — combines them and produces the unified returns required by Banco de Portugal, the deposit-guarantee fund, and the tax authority.

### 8.1 The reporting application as a named downstream system

The reporting application is already referenced in [02 §2.2](./02-v1-scope-term-deposits.md) ("The reports themselves are built by a downstream reporting application") but the brief is currently silent on its identity. This document commits to naming it: **the reporting application is a downstream system in the bank's estate, separate from the engine and separate from legacy, that consumes events from both and produces regulatory returns.** It is not part of the engine product. Its ownership and scope are tracked as an open question ([Q-AE](./04-open-questions.md)) because it is genuinely ambiguous whether the operating bank already has such a system or whether it has to be built.

The architectural commitment is **the engine never reads legacy data directly to produce regulatory output.** The engine emits its events; legacy emits its facts via the batch file (re-projected by the reporting application from the same source the engine consumes, or by another downstream consumer). The reporting application aggregates the two streams into the returns. The engine remains ignorant of legacy's data shape; legacy remains ignorant of the engine's data shape; the reporting application is where the cross-system aggregation lives.

This commitment is what keeps the engine's data model free of legacy concerns. An engine that has to read legacy data to compute a BdP return has effectively been forked into a hybrid system; the wedge ([00 §2](./00-product-vision.md)) is dead the moment that happens.

### 8.2 The three returns in v1 scope

Three returns matter for the v1 term-deposit period:

| Return | Cadence | Cross-system aggregation needed | Per-instance signal source |
|---|---|---|---|
| **BdP retail-deposit interest-rate statistics** (`bdp_estatisticas_taxas_juro` per [surface §3.3](./feature-design-configuration-surface.md)) | Monthly | Yes — totals span engine and legacy | Engine: `DepositConstituted` events; Legacy: `constituted` facts in batch file |
| **FGD (Fundo de Garantia de Depósitos) coverage report** | Annual + on-demand | Yes — per-customer aggregates span engine and legacy current accounts | Engine: `DepositConstituted` + `DepositMatured` + `DepositTerminatedEarly` running totals; Legacy: per-customer balance from batch file |
| **IRS Modelo 39 (withholding declaration)** | Annual | Yes — per-customer withholding aggregates span both | Engine: `WithholdingApplied` events; Legacy: `interest_paid` facts in batch file with withholding line |

Each return is the reporting application's responsibility. The engine guarantees its signals are present, correct, and timely; legacy guarantees its batch file is present, correct, and timely; the reporting application guarantees the aggregation is correct.

### 8.3 What this means for the engine's signal contract

The pack-defined reporting hooks from [surface §3.3](./feature-design-configuration-surface.md) (`bdp_estatisticas_taxas_juro`, `modelo_39`) describe the engine's side of the contract: the signals are present in the engine's event stream, computed under the pinned pack version, timely to the reporting application's ingestion cutoffs.

The reporting application's side of the contract — how it consumes events, how it aggregates with legacy data, how it formats the actual return for submission — is out of scope for the engine's product brief. It is the reporting application's product brief.

### 8.4 The coexistence-specific risk

Regulatory returns under coexistence are the highest-stakes place for cross-system inconsistencies to surface. A BdP return that under-reports total deposit volume because engine and legacy double-counted (or single-counted, depending on the bug) is a regulatory incident. The mitigation is:

- The reporting application reconciles its own inputs end-to-end before producing the return.
- The reconciliation flows in §7 catch most cross-system drift before it reaches the reporting application.
- The reporting application's monthly returns are reviewed by the bank's regulatory-reporting function before submission; the engine's signals are not submitted directly.

A demo can hand-wave; a production deployment cannot. The reporting application is a regulatory load-bearing dependency for the coexistence period.

---

## 9. The SoR-Transition Event Chain

The third decision committed in this document: **renewal of a legacy in-flight deposit creates a new instance on the engine**, not a renewed instance on legacy. The legacy instance matures; the engine constitutes a new instance with fresh terms. The two events are linked by correlation_id and form an explicit SoR transition.

### 9.1 The mechanics

When a legacy term deposit reaches its maturity date with an `auto_renewal_policy` other than `NONE`:

1. **Legacy matures the instance.** Legacy's batch file for the day includes a record: `legacy_instance_id`, `fact_kind: matured`, `fact_date`, `legacy_state_snapshot` showing final principal and final net interest.
2. **Engine ingests the batch file.** The engine's ACL parses the record and emits a `LegacyInstanceObserved` event with `event_id: <uuid_1>`.
3. **Engine evaluates the renewal policy.** The renewal policy (`SAME_TERM_CURRENT_RATE` or `SAME_TERM_SAME_RATE` per [02 §2.4](./02-v1-scope-term-deposits.md)) is encoded in the legacy state snapshot. The engine applies the current rate sheet ([surface §2](./feature-design-configuration-surface.md)) — the same rate sheet a customer would get for a new constitution — and computes the new deposit's terms.
4. **Engine constitutes the new instance.** The engine emits `DepositConstituted` (family-specific) with `event_id: <uuid_2>`, `instance_id: <new_engine_uuid>`, `causation_id: <uuid_1>` (the `LegacyInstanceObserved` event from step 2), `originating_legacy_id: <legacy_instance_id>`, `pack_version` and `schema_version` pinned to the current versions.
5. **Engine debits the customer's current account on legacy for the new principal** (which is identical to the legacy instance's matured principal, possibly plus the net interest the customer chose to roll over).

The causation chain is decidable from the event log alone: starting from the engine's `DepositConstituted`, following `causation_id` reaches the `LegacyInstanceObserved`; the `LegacyInstanceObserved`'s payload reaches `legacy_instance_id`; the legacy ID resolves to the legacy book's historical record. Audit can walk the chain across the SoR transition end-to-end.

### 9.2 The book-migration implication

The brainstorm-decided consequence: **the bulk of the legacy term-deposit book migrates to the engine within one auto-renewal cycle (3–24 months for typical PT term deposits), not over the full 5-year maturity tail.** Only deposits with `auto_renewal_policy: NONE` stay on legacy until full maturity. This compresses the middle phase materially — coexistence for term deposits is roughly the duration of the longest popular renewal cycle, not the duration of the longest popular term.

This is a planning-relevant fact. The roadmap ([03](./03-roadmap.md)) currently treats term-deposit coexistence as multi-year without naming a horizon; this document gives the horizon a concrete shape: most of the book is engine-native by the end of the first renewal cycle after cutover, with a long tail of `NONE`-policy deposits aging out over the next 1–5 years.

### 9.3 The cutover-day load risk

The renewal-creates-engine-instance decision interacts with cutover (§10) in a specific way: **on the first auto-renewal cycle after cutover, every legacy deposit that renews on a given day lands on the engine as a new constitution on that day.** If the legacy book has significant date clustering (a campaign in March that constituted 10,000 deposits on the same day, all renewing 12 months later), the engine sees a day-1 (well, day-365) load spike that does not reflect the engine's steady-state constitution rate.

The brief currently does not name this risk. The mitigation — a load-smoothing strategy, an explicit cutover scheduling that staggers renewals, or simply load-testing the engine for the worst-day case — is left as [Q-AD in 04-open-questions](./04-open-questions.md).

### 9.4 No new event types

The chain uses only events already declared in the brief:

- `LegacyInstanceObserved` — cross-cutting, declared by the engine in [event-store §4.1](./feature-design-event-store-projections.md).
- `DepositConstituted` — family-specific, declared by the term-deposit family schema, in the v1 catalogue in [02 §2.4](./02-v1-scope-term-deposits.md).

The SoR-transition link is carried by `causation_id` and `originating_legacy_id`; no new event type is introduced to represent the transition itself. The transition is the *relationship* between two existing events, not an event in its own right. This keeps the event taxonomy lean and consistent with the engine-vs-family separation from [event-store §3](./feature-design-event-store-projections.md).

---

## 10. Cutover Mechanics

Cutover is a discrete event with three distinct phases: pre-cutover shadow run, cutover day, post-cutover monitoring. Each phase has a different goal and a different exit criterion.

### 10.1 Pre-cutover shadow run

For some weeks before cutover, the engine runs in shadow mode against a subset of new legacy constitutions:

- New deposits constituted on legacy are also constituted on the engine, in parallel, with the same principal, customer, and terms.
- The engine processes the shadow instance through its full lifecycle (accrual, withholding, maturity simulation) without producing customer-visible effects and without settling to current accounts.
- The reconciliation flow 1 (settlements) is run in dry-run mode: the engine's outbox is compared against legacy's journal, but the engine does not send actual commands to legacy.
- Each day's shadow output is compared against legacy's actual output for the same instance set. Differences are investigated; the engine is fixed; the comparison runs again.

The exit criterion: a defined number of consecutive days (e.g. 10 business days) where the shadow output matches legacy's output for the entire test set within accepted tolerance. The tolerance is non-zero — legacy and engine compute interest on slightly different day-count assumptions in edge cases that have to be reconciled by name, not by automation.

The shadow run catches the bugs that integration testing missed: real customer data, real legacy state, real ACL response shapes, real reconciliation noise. Skipping it pushes those bugs into cutover day.

### 10.2 Cutover day

On the agreed date, the routing flips. The bank's channels (channel side of the system, owned by the channel teams, not by the engine) start sending new term-deposit constitutions to the engine instead of legacy. Three things must happen on cutover day:

- **Routing is switched atomically.** The decision "which system gets this new constitution" moves from "always legacy" to "engine for term deposits, legacy for everything else." There is no intermediate state where the routing is ambiguous.
- **Settlement plumbing is live.** The engine starts emitting real settlement commands to legacy via the ACL for the first engine-native constitutions. Reconciliation flow 1 runs end-of-day with real data for the first time.
- **The shadow run is decommissioned.** Shadow instances stop being created. The shadow comparison infrastructure stays available for incident investigation but is no longer producing new comparisons.

Cutover day is high-attention, fully-staffed, with explicit rollback criteria. The rollback shape: if cutover day surfaces a load-bearing bug, the routing flips back to legacy; engine-native constitutions of the day are either un-constituted (compensation via the engine's normal saga mechanics) or reconciled out-of-band. The rollback is operationally painful but architecturally available; designing the cutover so rollback is *impossible* would compound the operational risk.

### 10.3 Post-cutover monitoring

For some weeks after cutover, the engine is monitored with elevated attention:

- Reconciliation flow 1 is reviewed every day by a named operator, not just when alerts fire.
- The unified read surface's staleness profile is monitored to confirm the 24-hour bound holds.
- The first batch file ingestion after cutover is reviewed manually to confirm the contract from §5.3 is operational.
- Customer-impact incidents (a customer reaching out with a question about a new-engine deposit) are escalated to the engine team for direct investigation.

The exit criterion: a defined period (e.g. 30 days) of stable operations with no load-bearing incidents and reconciliation flow 1 producing zero engine-side orphans. After exit, the engine moves to normal operations cadence — monitoring continues, but elevated review is decommissioned.

The first auto-renewal cycle after cutover (§9.3) is a separate operational event, even though it is not "cutover day" architecturally. It is named in the operations calendar and treated with elevated attention.

---

## 11. The End State

Coexistence ends. The question is when, and what.

### 11.1 The end-state criterion for term deposits

Coexistence for term deposits ends when **the last legacy instance has matured or been force-renewed onto the engine**. For deposits with `auto_renewal_policy` other than `NONE`, this is the end of the first renewal cycle (§9.2). For deposits with `auto_renewal_policy: NONE`, this is whenever each instance reaches its original maturity — potentially years after cutover.

The end-state trigger is the architectural answer to "is coexistence over?" When the engine's reconciliation flow 2 (§7.3) reports zero legacy instances in scope, coexistence has structurally ended even if the legacy core continues running for other product families. At that point, the engine no longer ingests batch files for term deposits, the unified read surface no longer has legacy-sourced term-deposit rows, and the operational overhead of the multi-system reconciliation is decommissioned for that family.

### 11.2 The full-bank end state

For the full coexistence period (across all product families introduced in [03](./03-roadmap.md)), the end state is when the legacy core is fully decommissioned. That involves:

- All product families have migrated to the engine (v1 deposits, v2 credit, v3 mortgage, v4 current accounts and cards).
- The remaining legacy book has been force-migrated, archived, or otherwise resolved per the per-family end-state rules.
- Legacy stops producing batch files; the ACL is decommissioned; the reconciliation flows are retired.
- The legacy core itself is archived for read-only access (regulators may still demand historical reconstruction) and the legacy team is reassigned.

This is the strangler-fig's strangling completed. It is a multi-year horizon — possibly 5–10 years from initial cutover — and the brief does not commit to a timeline. The point of naming it here is to make explicit that **decommissioning is a deliverable**, not a side effect. A coexistence architecture that has no decommissioning plan is a coexistence architecture that becomes permanent.

The end-state trigger for the full bank — what specific signal indicates "we are done" and the legacy core can be archived — is left as [Q-AJ in 04-open-questions](./04-open-questions.md). For term deposits alone the answer is mechanical (last instance matured); for the full bank the answer involves cross-family judgement calls (when does a tail of low-value irregular instances justify the cost of running legacy for them?).

### 11.3 What does *not* end with coexistence

When term-deposit coexistence ends, several things continue:

- The unified read surface remains as the bank's read-model architecture; only the legacy ingestion path for term deposits stops.
- The ACL remains for as long as the engine settles to legacy current accounts (which is until v4 brings current accounts onto the engine).
- The reporting application remains as the regulatory-aggregation layer; only the legacy input for term deposits stops contributing.
- The cross-cutting `LegacyInstanceObserved` event remains a declared engine event even after no instances of it are emitted, because future product families will re-use the pattern.

The architecture survives the end of coexistence for any single family. Each family's coexistence subperiod has its own end state; the global end state is when every family's subperiod has ended.

---

## 12. §1 Resolution Inputs: Legacy Inventory Questionnaire

§§3–11 specify the coexistence architecture in terms abstract enough to work against any major legacy core. The v1 implementation needs concrete inputs about the operating bank's specific legacy estate — [§1 in 04-open-questions](./04-open-questions.md) names this as the inventory gap. The gap is not closable by architectural reasoning; only the bank knows what its legacy estate contains. This section specifies the conversation that closes it.

**Who attends.** Operating bank's legacy-core technical lead, integration architect, GL team lead, current-account operations lead, and the engine technical lead. Optional: vendor support representative if the legacy core is vendor-owned; the bank's enterprise architecture lead if the estate spans multiple cores.

### 12.1 What to inventory (per legacy system)

The bank's legacy estate is typically not one system. The questionnaire is filled in once per system the engine integrates with at v1 (per [02 §3](./02-v1-scope-term-deposits.md), at minimum the current-account module that handles deposit settlement).

| Dimension | What to capture | Why it matters |
|---|---|---|
| **System identity** | Name, version, vendor (if vendor product), age, last major upgrade, EOL date if known | Determines whether vendor-supplied connectors exist; age and EOL constrain adapter investment |
| **Transaction model** | ACID DB transactions, message queue, file-based, screen-scraping, custom RPC | Saga compensation in [integration_concepts §05](../integration_concepts/05-constitution-saga-walkthrough.md) depends on what the legacy system actually offers as an undo |
| **Idempotency guarantees** | Native idempotency keys; idempotent retry; non-idempotent (the engine must dedupe) | The ACL idempotency strategy from [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md) is shaped here |
| **Batch windows** | Real-time / near-real-time / daily batch / weekly batch; cutoff times in Lisbon time; lockout windows | Drives the daily batch file contract (§5) and the unified read model's staleness profile (§6.2) |
| **API surface** | REST / SOAP / proprietary RPC / file drop / DB read / message bus subscription | Constrains how the engine reads from and writes to the legacy system |
| **Settlement contract** | How a credit/debit to a legacy current account is initiated; ack semantics; reversal semantics; same-day vs T+1 | Drives the settlement plumbing in §4 |
| **Data export** | Schema, format, completeness guarantees, schema-drift coordination protocol | Drives the daily batch file in §5.2 ([Q-AH](./04-open-questions.md)) |
| **Outage profile** | Planned-maintenance frequency and duration; unplanned-incident MTTR; capacity headroom | Shapes the strangler-fig adoption schedule and the §10 cutover plan |
| **Customer-master role** | Whether this system owns customer master data or references it from another system | Interacts with [§6 in 04-open-questions](./04-open-questions.md) (customer-master ownership) |
| **GL coupling** | Whether this system posts to the GL directly, via an adapter, or by file extract | Interacts with [Q-AB](./04-open-questions.md) (GL adapter ownership) |

### 12.2 What to decide per inventoried system

| Classification | Engine team commitment | When to use |
|---|---|---|
| **First-class adapter** | Engine team builds and maintains a system-aware adapter that absorbs the legacy specifics. Shortens v1 onboarding measurably. | The system handles v1 settlement (current-account module) or v1 reconciliation (daily batch source) and is the dominant integration the engine cannot operate without |
| **Generic ACL-only** | The engine commits to the ACL pattern; the bank builds its own integration on top per [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md). | The integration is per-operator-bespoke even within the operating bank's estate (e.g. a brand-specific internal stack), or the system is touched only occasionally |
| **Out-of-scope at v1** | The engine does not integrate with this system at v1; the integration is deferred to a later phase. | The system is touched only at v2+ (e.g. a credit-bureau feed needed from v2) or a manual / batch process is operationally acceptable through coexistence |

The [§1 in 04-open-questions](./04-open-questions.md) commitment names "one or two named systems" as first-class. Naming more dilutes engineering focus; naming fewer leaves the v1 onboarding longer than the brief promises. The legacy current-account module is the load-bearing first-class candidate by virtue of [02 §3](./02-v1-scope-term-deposits.md).

### 12.3 Decision outputs needed from the meeting

- A named list of v1-relevant legacy systems with the §12.1 dimensions filled in for each. Systems that no one can fully describe are flagged for follow-up rather than treated as known.
- A §12.2 classification per system: first-class adapter / generic ACL-only / out-of-scope at v1.
- For each first-class adapter, an engineering owner on the engine side, a counterpart on the legacy side, and an effort estimate that fits the v1 calendar.
- A confirmation that the v1 onboarding plan (per [02 §3](./02-v1-scope-term-deposits.md)) is operable against the inventoried estate, or a named blocker — a system whose integration shape forecloses v1 as currently scoped.
- For each first-class candidate, a check against the dimensions that most often foreclose adapter feasibility: transaction model (does the legacy system offer compensation primitives, or does the saga have to invent them?), idempotency (will the engine see double-delivery in production?), and outage profile (does the legacy system have enough headroom to absorb engine-driven traffic?).

The meeting's output is folded into [§1 in 04-open-questions](./04-open-questions.md) — which moves from open to a Position with named systems — and into a new sub-section of [01 §6](./01-product-architecture.md) declaring the first-class adapters as in-scope for v1 engineering.

### 12.4 Pre-meeting preparation

The engine team prepares for the meeting by:

- Drafting a candidate inventory from public knowledge (what the bank has publicly disclosed about its core estate) and internal knowledge that pre-dates this conversation. The candidate inventory is *not* the answer; it is the starting list to confirm, expand, or correct in the meeting.
- Reading [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md) and §05 so the engine team can speak fluently about what each adapter classification implies engineering-wise.
- Bringing the §3.1 system-of-record map and the §5.2 batch file schema as concrete artefacts the inventoried systems must support.

Meeting failure modes to avoid: leaving without a per-system classification (the conversation becomes a "we'll figure it out" deferral), classifying everything as first-class (the engine team's v1 capacity does not absorb it), or classifying everything as generic ACL-only (no v1 onboarding speedup, which is the §1 commitment's purpose).

