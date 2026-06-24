# ADR-PC-004: PII Encryption Envelope and Key Management — Crypto-Shredding with OpenBao

| Field | Value |
|---|---|
| Status | Accepted (gated by DPO — production gate, not required for the POC; see §Gate) |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (event payload envelope holds the ciphertext), [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) (projections host the field-level envelope), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (.NET engine; the encrypt/decrypt boundary), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro payload carrying ciphertext bytes), [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) (the backup-retention horizon that bounds true erasure) |
| Resolves | bd `archie-10r.5` (ADR-PC-004: PII encryption envelope and key management) |

---

## Context

GDPR Article 17 requires a data subject to compel erasure of their personal data; an immutable event log cannot delete in place without breaking the replay invariant audit and as-of queries depend on. [event-store §6.2](../feature-design-event-store-projections.md) and [04 §7](../04-open-questions.md) resolve this with **crypto-shredding**: PII fields are encrypted per data subject under a per-subject key; erasure = key destruction; ciphertext stays in the log, plaintext becomes unrecoverable. Structural fields (principal, rate, dates, lifecycle) are not PII and remain cleartext, so handlers and projections operate over erased records exactly as over live ones — PII fields return null. Tombstoning is rejected (some EU supervisors reject it because ciphertext remains recoverable); the fallback if crypto-shredding is vetoed is PII off-store. The mechanism is committed; **this ADR decides the key store and the operational contract**.

Four decisions ([bd archie-10r.5](../04-open-questions.md)): (1) KMS / key-store choice (self-hostable per [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)); (2) the per-event PII-field annotation contract; (3) key rotation and post-erasure projection behaviour; (4) audit-trail semantics after erasure.

**Candidates evaluated** (key store; must be self-hostable — the engine deploys on-prem / private-cloud K8s per [01 §6](../01-product-architecture.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **OpenBao** (self-hosted, Vault-compatible secrets manager) | Transit secrets engine = encryption-as-a-service; per-subject named keys; crypto-shred = delete the named key; engine never holds key material. |
| B | **Keys hand-managed in PostgreSQL** | Per-subject DEK rows (AES-256-GCM via .NET `AesGcm`) wrapped by a master KEK; crypto-shred = delete the DEK row; KEK in an external root-of-trust. |
| C | **pgcrypto (in-DB)** | Encryption primitives inside PostgreSQL. |
| D | **Cloud KMS (AWS/GCP)** | Managed key service. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · OpenBao | **MPL-2.0** (OSI-approved); Linux Foundation project; the OSS fork of HashiCorp Vault created after Vault relicensed to **BSL 1.1** (2023). Self-hostable. | **Pass** — note: Vault *itself* is BSL, which [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) flags ("BSL — flag even if currently free"); OpenBao is the OSI-permissive path and is the candidate, not Vault. *Licence assessment 2026-05-23; re-check before production.* |
| B · Hand-managed in PG | PostgreSQL + .NET `System.Security.Cryptography.AesGcm` (platform). | **Pass** |
| C · pgcrypto | PostgreSQL contrib (PostgreSQL licence). | **Pass** |
| D · Cloud KMS | Managed; paid beyond free tier; not self-hosted. | **Fail (for this deployment)** — the on-prem / private-cloud target ([01 §6](../01-product-architecture.md)) and the self-hostable F1 preference rule out a managed cloud KMS as the v1 mechanism. |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | GDPR (Art 17 via crypto-shred) | DORA | PSD2 | Verdict |
|---|---|---|---|---|
| A · OpenBao | Per-subject transit key; destroy key ⇒ plaintext unrecoverable; aligns with the Art 4(5) pseudonymisation / "additional information held separately" test. | **OpenBao availability gates PII decrypt** — it becomes a resilience-critical dependency requiring HA + its own DR ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)). | Erasure recorded as an event; audit trail intact. | **Pass (conditional)** — OpenBao HA + backup is mandatory; key-store DR coordinated with [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md). |
| B · Hand-managed in PG | Same crypto-shred semantics; bank owns key-lifecycle correctness. | KEK root-of-trust still external; fewer moving services. | Same. | **Pass (conditional)** — hand-built key-lifecycle correctness is the bank's liability. |
| C · pgcrypto | Crypto in the DB; per-subject key rotation/destruction lifecycle is weak. | Couples crypto to the DB engine. | Same. | **Pass (conditional)** — weak key-lifecycle story. |

---

### Soft criteria

#### A · OpenBao — **CHOSEN** (per deciders' direction, 2026-05-23)

**S1 · Operational complexity.** One additional self-hosted service for a 1–2 person team — a real cost, the honest downside. Mitigated: OpenBao is purpose-built with strong defaults; it runs in the same K8s topology; and key custody is exactly the domain where a dedicated tool earns its operational keep.

**S2 · Ecosystem coherence.** The conventional secure pattern — encryption-as-a-service via the transit engine; the engine never holds key material. Vault-compatible API means a broad client ecosystem (including .NET clients) and a documented operational model.

**S3 · Exit cost.** Moderate. Ciphertext is standard AEAD; migrating key stores means re-wrapping/rewrapping under a new provider. Because OpenBao speaks the Vault transit API, swapping to another Vault-compatible provider is bounded.

**S4 · Community and longevity.** The named risk. OpenBao is young (LF-hosted fork, 2023) and smaller than the Vault lineage it descends from. Mitigation: Vault-API compatibility (broad, mature client and operational ecosystem) and the maturity of the lineage; the engine couples to the *transit API*, not to OpenBao internals, bounding provider swap. Re-audit OpenBao's cadence before production hardening.

#### B · Keys hand-managed in PostgreSQL

Maximum control and the fewest moving services — and it aligns with the engine's hand-rolled core philosophy. But **cryptographic key custody is the one area where "fully control / hand-roll" is a liability rather than an asset**: key generation, rotation, destruction, access control, and audit are precisely what a dedicated tool exists to get right, and a subtle bug here is a data-protection incident, not a recoverable defect. The master KEK still needs a hardened external root-of-trust regardless. **Decisive reason for not choosing:** the deciders' direction (2026-05-23) is to delegate key custody to a vetted purpose-built tool rather than hand-roll it — the deliberate exception to the hand-rolled-core posture, made because key custody is security-critical.

#### C · pgcrypto

Single substrate, simplest to stand up, but the weakest per-subject key rotation and destruction lifecycle, and it couples crypto to the DB engine. **Decisive reason for not choosing:** crypto-shredding lives or dies on clean per-subject key destruction; pgcrypto's key-lifecycle story is the weakest of the field.

#### D · Cloud KMS

Failed F1 for this deployment (managed, not self-hosted).

---

## Decision

**Chosen: OpenBao (self-hosted, Vault-compatible) as the per-subject key store, used as encryption-as-a-service.** PII fields are encrypted/decrypted through OpenBao's transit engine under a per-subject named key; the engine stores only ciphertext (in the Avro payload and in projections); crypto-shredding is the destruction of the subject's named key. The engine never holds key material.

The decisive reason is that **key custody is security-critical and is the deliberate exception to the engine's hand-rolled-core posture** ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) — delegating it to a vetted, purpose-built tool is the responsible default. OpenBao (MPL-2.0) is the OSI-permissive, self-hostable path; Vault itself is excluded by its BSL relicense under [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md).

**Rejected: hand-managed keys in PostgreSQL** — control is not the right priority for key custody; vetted tooling is. **Rejected: pgcrypto** — weakest key-lifecycle/destruction story. **Rejected: cloud KMS** — not self-hostable for the on-prem target (F1).

### Gate

This ADR is **Accepted**. DPO confirmation that crypto-shredding satisfies the operating bank's interpretation of Article 17 under Lei 58/2019, in conjunction with PT banking-record retention (10-year accounting, 7-year AML), remains a **production gate, not a POC prerequisite** ([event-store §6.4](../feature-design-event-store-projections.md), [04 §7](../04-open-questions.md)). The same meeting that resolves Q-Y resolves this. Named outputs needed: crypto-shredding accepted as erasure (vs PII off-store fallback); retention windows for structural events vs PII fields; **the cipher-text-after-window question** (must ciphertext be deleted at the storage level after the 10-year window, or is key destruction sufficient?); and whether the engine must support a regulator-query mode that bypasses subject-erasure for supervisory inspection. If the DPO vetoes crypto-shredding, the v1 fallback is PII off-store; tombstoning stays rejected.

*Amendment 2026-06-22 — DPO gate cleared (additive, does not change the Decision — bd `babelstone-nktv` / Epic `babelstone-pqwc`): the operating bank's DPO and compliance functions **confirmed crypto-shredding satisfies their interpretation of Article 17** under Lei 58/2019 with PT banking-record retention (10-year accounting, 7-year AML). Key destruction is accepted as erasure; the PII-off-store fallback is **not** invoked; ciphertext may remain past the retention window (the §P5 backup-retention horizon is the true erasure-completion time, carried jointly with [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)). Recorded at [04 §7](../04-open-questions.md); the same meeting cleared the [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) Q-Y gate.*

---

## Implementation Principles

### P1 — Per-event PII-field annotation contract; CI-enforced

Every event-type payload schema declares each string field as **PII** or **non-PII** ([event-store §6.2](../feature-design-event-store-projections.md)); the engine's CI rejects any schema introducing an unannotated string field. A PII field declares the `subject_id` it belongs to (the data subject whose key encrypts it). Family schemas declare; the engine enforces. v1 PII surface is bounded — customer name, NIF, address, contact, free-text on a small set of lifecycle events ([event-store §6.2](../feature-design-event-store-projections.md)).

### P2 — Encrypt at the boundary; ciphertext in payload and projections; structural fields cleartext

At event emission, PII fields are encrypted via OpenBao transit under the subject's named key; the ciphertext bytes live inside the Avro `payload` ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) and, when projected, in the projection's PII columns ([ADR-PC-002 §P1](./ADR-PC-002-application-level-bitemporality.md)). Structural fields are never encrypted, so they stay queryable on erased records. PII decryption happens only when PII is actually surfaced (display, regulatory report) — never on the structural hot path. If per-read transit latency bites, the envelope-datakey optimisation (OpenBao issues a wrapped data key; the engine does local `AesGcm` with a short-lived in-memory key) is available, **provided** the plaintext data key is never persisted and is evicted on erasure so key destruction remains effective.

### P3 — Crypto-shred = destroy the subject's key + evict caches; post-erasure behaviour

GDPR erasure for a subject destroys that subject's OpenBao key and evicts any in-memory data-key cache. Afterwards: replay and projection rebuild produce **null** in PII fields and intact structural state; the audit trail shows "an event occurred at this `transaction_time`; payload PII is unrecoverable due to subject erasure" — the GDPR-compliant audit state, not a gap ([event-store §6.2](../feature-design-event-store-projections.md)). A `SubjectErased` (engine-cross-cutting) event records the erasure with its `transaction_time` and actor.

### P4 — Key rotation

OpenBao transit supports versioned keys: rotation adds a new key version while old ciphertext remains decryptable under prior versions; optional rewrap migrates ciphertext forward. Rotation policy (cadence, rewrap-on-rotation) is an operational parameter; rotation must never compromise the ability to crypto-shred (destroying all versions of a subject's key shreds regardless of rotation history).

### P5 — Audit and erasure semantics; the backup-retention tension

Erasure is auditable (which subject, when, by whom) and the structural audit trail is untouched. **The sharp interaction with DR ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)):** crypto-shredding claims plaintext is unrecoverable once the key is destroyed, but (a) DR backups of the `events` table still hold the ciphertext, and (b) backups of the OpenBao key store may still hold the key — so an erasure is only *complete* once every backup containing the subject's key has rolled past its retention horizon. v1 must either (i) ensure key destruction propagates to key-store backups within a bounded window, or (ii) treat the backup-retention horizon as the true erasure-completion time and document it. This is one of the named outputs of the DPO meeting (the cipher-text-after-window question) and is carried jointly with [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md).

---

## Residual Risks

1. **OpenBao availability is on the PII-decrypt critical path (DORA).** Mitigation: HA OpenBao + its own DR, coordinated with [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md). Structural reads do not depend on OpenBao, so an OpenBao outage degrades PII display but not core engine operation.
2. **OpenBao S4 youth.** Mitigated by Vault-API compatibility and lineage maturity; re-audit before production.
3. **Crypto-shred completeness across backups, snapshots, and caches.** The central GDPR-vs-DR tension (§P5); resolved by the DPO meeting and coordinated with [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md) (discard PII snapshots) and [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) (backup retention).
4. **Per-read decrypt latency.** Mitigated: PII is off the structural hot path; the datakey-envelope optimisation (§P2) is available with the no-persist/evict constraint.
5. **Key loss = mass de-facto erasure.** Losing the OpenBao key store irreversibly destroys all PII — a catastrophic-availability event that is also, ironically, the erasure mechanism. Key-store DR ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)) is therefore as critical as event-store DR.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

No catalogued Test ID governs crypto-shredding yet — this is a deliberate, visible hole, to be closed under the catalogue's growth provision when the crypto-shred mechanism is built. The two load-bearing, falsifiable invariants this ADR earns are:

- **PII-field annotation is CI-enforced** — the engine's CI rejects any event-type payload schema that introduces an unannotated string field, so every string field is decided PII or non-PII at the schema boundary (§P1). An analyser gate; no Test ID is wired yet.
- **Crypto-shred yields null PII + intact structural state, and records the erasure** — after a subject's OpenBao key is destroyed and caches evicted, replay and projection rebuild produce `null` in that subject's PII fields while structural fields stay queryable, and a `SubjectErased` event is recorded with its `transaction_time` and actor (§P3). No Test ID is wired yet.

These are buildable regardless of the §Gate DPO production gate. Key-store and backup-completion concerns compose with the separately-owned DR gates of [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) (§P4–§P5) rather than being claimed here.

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — the Avro `payload` carries PII ciphertext; structural columns cleartext.
- [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) — projections host the field-level envelope (the reason the bitemporal path is field-granular).
- [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md) — PII materialised in snapshots is discarded/rebuilt after erasure.
- [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) — key-store DR; the backup-retention horizon that bounds true erasure (§P5).
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the engine's encrypt/decrypt boundary; OpenBao is the deliberate exception to the hand-rolled posture.
- [event-store §6.2, §6.4](../feature-design-event-store-projections.md) — crypto-shredding commitment; DPO conversation agenda.
- [04 §7, Q-Y](../04-open-questions.md) — GDPR-vs-immutability position; DPO gate (shared meeting with Q-Y).

---

*Decided 2026-05-23 by jhosm. Accepted; DPO confirmation is a production gate, not required for the POC. Key store (OpenBao) selected per deciders' direction as the deliberate exception to the hand-rolled-core posture, key custody being security-critical.*

---

*Revised 2026-05-31: Amendment A1 — Application-credential secrets via OpenBao KV v2.*

The Decision above scopes OpenBao to **per-subject PII transit keys**, where "the engine
never holds key material" (§P2/§Decision). Resolving **application / integration
credentials** — the database connection string (ADR-PC-001 §P1) today, Redpanda SASL
credentials later — is a materially different mode: the engine **does** hold the resolved
credential in process memory in order to open connections. This amendment is **additive**;
it does not reverse, narrow, or edit the Decision above.

- **Mechanism.** Application/integration credentials are resolved from OpenBao **KV v2** via
  **AppRole** through a new `ISecretProvider` boundary in `engine/src/Babelstone.Pii`
  (`OpenBaoKvSecretProvider`). AppRole login (`POST v1/auth/approle/login` →
  `auth.client_token`) yields a client token attached as `X-Vault-Token`; the secret is a
  versioned KV v2 read (`GET v1/{mount}/data/{name}` → `data.data[name]`). No SDK is used —
  the in-house client mirrors `OpenBaoTransitClient` (single send chokepoint, small
  `JsonPropertyName` records, `EnsureSuccess`, prove-don't-infer error handling, never
  echoing secret material), honouring ADR-PC-010's hand-rolled-core posture. `Babelstone.Pii`
  stays self-contained (no engine project references; its only added dependency is
  `Microsoft.Extensions.Configuration.Abstractions` for the dev/test/CI
  `ConfigurationSecretProvider` fallback).

- **Two distinct abstractions.** `IPiiKeyStore` (transit; key material stays at the boundary,
  the engine never holds a key) is **separate** from `ISecretProvider` (KV; the engine holds
  the resolved credential). They are deliberately not unified — the trust models differ.

- **Rotation vs crypto-shred.** For application credentials, *rotation* is a KV v2 **version
  bump** in the store followed by `ISecretProvider.RefreshAsync`, which re-resolves the latest
  version and invalidates the cached value without breaking a live reconnect. This is the
  **inverse** of the transit erasure path, where *key destruction* crypto-shreds all
  ciphertext under a subject key (§P3).

- **Boundary note (unchanged guarantees).** A resolved credential is **never** carried by the
  saga (ADR-IC-003 §P7 — saga messages carry the identity trio only) nor placed on the durable
  integration bus (§P2 / the PII-bus rule). It lives only at the composition root.

- **Deferred.** Production secret-store HA / unseal / DR hardening for this KV usage is
  **out of scope** here and deferred to M.4 / [ADR-PC-005 §P4](./ADR-PC-005-dr-rto-rpo.md),
  exactly as for the transit usage (Residual Risk 1). The dev stack uses OpenBao in `-dev`
  mode (`infra/compose.yaml`).

---

*Revised 2026-06-17: Amendment A2 — the realised erasure event is the family-scoped `PersonalDataErasureRequested`.*

In plain English: when the GDPR right-to-be-forgotten was actually built (bd `babelstone-nzw6`),
the erasure fact landed as a **per-deposit** event the term-deposit family owns, rather than the
engine-wide `SubjectErased` the original §P3 text named. The deposit gets a new terminal `Erased`
lifecycle state, and the event carries a **salted one-way pseudonym** of the subject instead of the
raw id — so even the "which subject" reference on the bus is non-reversible. The crypto-shred itself
(destroy the subject's key) is unchanged; only the *shape of the recorded fact* changed. This
amendment is **additive** — the key-store decision and the crypto-shred mechanism (§Decision, §P2,
§P3's "key destruction = erasure") all stand verbatim.

- **A2 · The recorded erasure fact (§P3 refinement).** §P3 named a "`SubjectErased`
  (engine-cross-cutting)" event recording the erasure with its `transaction_time` and actor. The
  realised event is **`PersonalDataErasureRequested`** — a **family-scoped, per-deposit** integration
  event (the engine is aggregate-per-stream, so an erasure fact reads most naturally as a per-deposit
  lifecycle event). It is folded by a **pure** handler into a new terminal
  `DepositLifecycle.Erased` state and carries `{ deposit_id, subject_pseudonym, erased_on,
  erasure_reason }`. The `transaction_time`/actor §P3 names ride on the `EventEnvelope` as for every
  event (not on the payload). A subject holding several deposits gets one such fact per deposit; the
  single key destruction (`IPiiKeyStore.DestroyKeyAsync`) shreds all of that subject's PII at once,
  independent of how many deposit facts record it.

- **A2 · The subject reference is a salted one-way pseudonym (a strengthening of §P2).** §P3's text
  implied recording the subject directly; the realised event carries **`subject_pseudonym`** — a
  salted HMAC one-way hash of the subject id ([ADR-IC-016 §8](../../integration_concepts/adrs/ADR-IC-016-service-identity-and-mtls.md)),
  never the raw id. This is **stricter** than §P2 requires: even the "which subject was erased"
  correlation reference is non-reversible on the durable bus, resolvable only inside the Customer Data
  Store that holds the same salt. The raw subject id stays engine-internal at the OpenBao boundary.

- **A2 · The null-PII-field behaviour (§P3) is unchanged and activates with real PII annotation.**
  §P3's "replay and projection rebuild produce **null** in PII fields and intact structural state"
  remains the contract. Under the current `NullPiiProtector` posture (no PII field is annotated/
  encrypted yet — Epic C, `archie-e6fr.5`) there is no PII field to null, so today's realised flow
  records the structural erasure fact and crypto-shreds the (placeholder) subject key; the
  null-on-replay behaviour engages unchanged once real PII annotation lands and the real
  `OpenBaoTransitClient` key store is wired (`OpenBao:Enabled`).

- **A2 · This amends §P3; it does not supersede this ADR.** §Decision (OpenBao as the per-subject key
  store; crypto-shred = key destruction; the engine never holds key material), §P2 (no PII/secrets on
  the bus), and §P3's erasure semantics (key destruction yields unrecoverable plaintext; the audit
  trail is preserved) all remain binding **as written**. This amendment refines only *the shape and
  scope of the event that records the erasure*, and strengthens the subject reference to a salted
  pseudonym. The Verifiable-commitments invariant (§ "Crypto-shred yields null PII + intact structural
  state, and records the erasure") still holds — read "records the erasure" as the
  `PersonalDataErasureRequested` fact; no Test ID is wired yet (unchanged).

---

*Revised 2026-06-20: Amendment A3 — key-cardinality residual risk at v4 scale, and why key count cannot be collapsed (bd `c14p.2` / M.6).*

In plain English: this ADR gives every customer their own OpenBao key, so that "forget me" destroys exactly one person's key. A retail bank has millions of customers, so that is millions of named keys living in OpenBao — and the ADR never said whether OpenBao can carry that many. With Integrated Storage (Raft) the whole keyspace is memory-resident, so RAM footprint, Raft snapshot size, unseal time, and replication/join time all scale with key count. This amendment records that cardinality gap as a named residual risk, registers the cheap "use fewer keys" answer as one that **breaks per-subject erasure**, and points at the load-harness probe that now measures the per-key-op slope. It is **additive** — the per-subject-named-key decision (§Decision, §P2, §P3) stands verbatim; nothing here reverses or narrows it.

- **A3 · The named gap (a strengthening of §Decision's scope).** §Decision and §P5 address per-read decrypt latency and the DR backup-retention horizon, but are **silent on key cardinality** — how many named transit keys OpenBao holds at steady state. At v4 scale (`N_acct` ~3M, `N_card` ~1.5M per [ADR-PC-011 §8.1](./ADR-PC-011-in-house-load-test-harness.md)) the per-subject-named-key model implies **millions of resident keys**. OpenBao's transit docs neither bless nor bound one-key-per-end-user at high cardinality; with Raft the keyspace is memory-resident, so steady-state RAM, snapshot size, unseal/join time, and replication lag scale with the key count. This is now an explicit Residual Risk (added below), not an unaddressed assumption.

- **A3 · Why key count cannot simply be collapsed (the crux).** The standard high-cardinality answer is envelope encryption — a few KEKs plus a per-record data key (DEK). That scales, but it **breaks per-subject crypto-shredding**, because destroying a shared KEK shreds *everyone*. Per-subject erasure (§P3) needs the destroyable unit to be per-subject, which lands back at a destroyable per-subject key. Note the §P2 **datakey-envelope optimisation is framed as a per-read LATENCY mitigation** and still keeps a per-subject NAMED key as the shred root — it does **not** reduce key count. So the cardinality cannot be collapsed without redesigning how erasure targets one person.

- **A3 · The measurement seam (what now exists).** The load harness (ADR-PC-011) gains a `KeyCardinalityProbe` (`engine/load/Babelstone.LoadHarness/KeyCardinalityProbe.cs`) that seeds a growing population of per-subject keys through the engine's OWN `IPiiKeyStore`/`OpenBaoTransitClient` and samples encrypt/decrypt/destroy latency as the resident population climbs — the falsifiable signal being whether per-key op latency stays **flat** (cardinality-independent) or **degrades**. The slope verdict is judged on the **median** (a robust central statistic a single slow round-trip cannot move); the p99 tail is recorded as context but is not the CI gate, so dev-mode container/HTTP jitter cannot flip the verdict (hardened in bd `babelstone-tihv`). A `[Trait("Category","Integration")]` lane runs it at a bounded N against a dev-mode OpenBao (and passes flat); the production sizing pass dials N to v4 cardinality on a **Raft-backed** cluster, where the snapshot/unseal/join dimensions a single-node `-dev` container cannot exhibit become measurable. The dev-mode probe proves the per-key-op slope; it does **not** prove the Raft HA/DR dimensions — those are the residual sizing budget owned jointly with [ADR-PC-005 §P4](./ADR-PC-005-dr-rto-rpo.md).

- **A3 · The chosen posture, and the mitigation triggers.** v1 keeps **per-subject named keys** — the crypto-shred property is load-bearing and the cheap collapses break it — with the cardinality budget treated as a **sized, load-tested HA/DR concern** (the snapshot/RAM/unseal numbers feed [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)). If the v4-cardinality sizing pass shows a rising per-key-op slope or an untenable snapshot/unseal/RAM budget, two mitigations are pre-registered, to be filed as an amend/supersede per [ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) at that point: (i) **shard** the transit keyspace across multiple mounts/clusters keyed by subject hash (preserves per-subject destroyability, spreads the resident keyspace); or (ii) per-subject **DEKs stored as EDEK** where the DEK is the individually-destroyable unit (a destroyable per-subject key store, just not OpenBao-transit-named). Either keeps per-subject erasure; neither is adopted unless the measurement forces it.

- **A3 · This amends §Decision's scope; it does not supersede this ADR.** The per-subject-named-key model, the no-PII-on-the-bus rule (§P2), and the crypto-shred erasure semantics (§P3) all remain binding **as written**. This amendment only *names the cardinality dimension §Decision left implicit*, records the measurement seam, and pre-registers mitigation triggers — no decision is reversed here.

**Residual Risk 6 (added by A3): Key cardinality at v4 scale is unbudgeted until the Raft-cluster sizing pass.** Millions of resident per-subject transit keys put RAM, Raft snapshot size, unseal/join time, and replication lag on a curve that scales with key count, and OpenBao does not bound one-key-per-end-user at that cardinality. *Mitigation:* the `KeyCardinalityProbe` (ADR-PC-011) measures the per-key-op slope (flat at the bounded CI N); the full v4-cardinality sizing pass on a Raft-backed cluster derives the HA/DR budget (jointly with [ADR-PC-005 §P4](./ADR-PC-005-dr-rto-rpo.md)); two key-preserving mitigations (sharding, destroyable per-subject DEK/EDEK) are pre-registered should the sizing be untenable. Key count cannot be collapsed via shared-KEK envelope encryption without breaking per-subject erasure (§P3).

---

*Revised 2026-06-21: Amendment A4 — the erasure event is the engine-declared CROSS-CUTTING `operations.PersonalDataErasureRequested`, not a family-scoped event (reverses A2's scope choice; bd `babelstone-g6ar`).*

In plain English: when the second product family (a personal loan) landed, it copied the term-deposit's erasure event verbatim — same shape, same name, only the id field renamed. That collided: the engine's Avro codec keys schemas by the event's simple type name, so two families both owning a `PersonalDataErasureRequested` is unresolvable (the catalog rejects the duplicate). The deeper point the collision exposed is that GDPR erasure was never family-specific: "this subject's key was crypto-shredded" is the same structural fact for a deposit, a loan, or any future product, and a subject's single key destruction erases their PII everywhere at once. So erasure moves to where the engine already keeps cross-cutting facts (the `CrossCuttingEvents` spine, alongside `PackVersionMigrated`): ONE engine-owned event, declared under the synthetic `operations` aggregate_type, folded into each family's own terminal `Erased` state through a small `IErasable` seam. This is **additive** — the key-store decision (§Decision), the no-PII-on-the-bus rule (§P2), and the crypto-shred mechanism (§P3) all stand verbatim; only the *owner, scope, and namespace of the recorded fact* change. It is also a partial **return to the original §P3 intent**, which named an "engine-cross-cutting `SubjectErased`" before A2 walked it back to family-scoped.

- **A4 · The recorded erasure fact is cross-cutting (reverses A2's "family-scoped, per-deposit").** A2 made the realised event a **family-scoped** `term_deposit.PersonalDataErasureRequested` carrying `{ deposit_id, … }`, reasoning that "aggregate-per-stream reads most naturally as a per-deposit lifecycle event." That reasoning conflated two things: *per-stream recording* (still true) and *family ownership* (not load-bearing). The realised event is now **`operations.PersonalDataErasureRequested`** — engine-declared in `Babelstone.Engine.CrossCuttingEvents` (names no family, ADR-PC-021 §P2), carrying `{ instance_id, subject_pseudonym, erased_on, erasure_reason }`, registry subject `operations.PersonalDataErasureRequested-value`. The per-stream property A2 valued is **preserved**: it is still appended once per affected instance and folded into that instance's stream; a subject holding several instances (across one or more families) still gets one fact per instance, and the single `IPiiKeyStore.DestroyKeyAsync` still shreds all their PII at once. What changes is that the event TYPE is declared once and shared, not duplicated per family.

- **A4 · Why family-duplication was unworkable, not merely inelegant.** The engine's family-agnostic Avro codec resolves a schema by the CLR event-type's *simple name* (`AvroSchemaCatalog.ForRecordName` / `AvroEventSerializer`), and the catalog asserts simple-name uniqueness across all embedded schemas. Two families each owning a `PersonalDataErasureRequested` therefore could not coexist (the second family's schema makes the catalog throw). A unique-prefix rename (`LoanPersonalDataErasureRequested`) would have satisfied the codec but entrenched the wrong model — a cross-cutting concern re-derived per family. The cross-cutting event removes the duplication at the root.

- **A4 · The fold seam (`IErasable`).** Unlike `PackVersionMigrated` (a no-op fold), erasure transitions the aggregate to a terminal `Erased` state that each family represents on its own lifecycle enum. The engine owns the event record and a generic `PersonalDataErasureRequestedHandler<TState>`; each family's projection state implements `IErasable<TState>.WithErased()` (e.g. `DepositPosition` → `DepositLifecycle.Erased`, `LoanPosition` → `LoanLifecycle.Erased`) and splices the binding in via `CrossCuttingEventRegistrations.For<TState>()`. The engine knows "mark it erased"; the family knows what "erased" means. The fold stays pure (BENG001/002/003).

- **A4 · Promotion to the bus (a refinement of the ADR-IC-017 §P4 classification).** Erasure keeps NAMED downstream consumers (acl cascades deletion, notification suppresses messaging), so unlike the store-only `PackVersionMigrated` it is a **promoted** cross-cutting event: it carries a governed `contracts/avro/operations/PersonalDataErasureRequested.avsc` and AsyncAPI catalogue entry (subject `operations.PersonalDataErasureRequested-value`). It is the first bus-promoted member of the cross-cutting set; the reverse-orphan gates (ADR-IC-017 §P3, the `asyncapi-catalog-validate.sh` scan and `CatalogGatedRelayReverseOrphanTests`) now resolve a catalogued schema to a DomainEvent in the engine spine as well as in `families/**`. Because it is the first cross-cutting event to carry a governed schema and registry subject, it also drives an [ADR-IC-002 amendment (2026-06-21, §A3)](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) reserving the single-segment synthetic namespace `operations` (subject `operations.PersonalDataErasureRequested-value`) for cross-cutting events, rather than IC-002 §P1's two-segment `{domain}.{aggregate_type}` family form.

- **A4 · This amends A2's scope; it does not supersede this ADR.** §Decision (OpenBao per-subject key store; crypto-shred = key destruction; the engine never holds key material), §P2 (no PII/secrets on the bus; salted one-way `subject_pseudonym`), and §P3's erasure semantics (key destruction yields unrecoverable plaintext; the audit trail is preserved) all remain binding **as written**, as do A3's cardinality findings. This amendment refines only *the ownership, scope, and namespace of the event that records the erasure*. The Verifiable-commitments invariant (§ "Crypto-shred yields null PII + intact structural state, and records the erasure") still holds — read "records the erasure" as the cross-cutting `operations.PersonalDataErasureRequested` fact.
