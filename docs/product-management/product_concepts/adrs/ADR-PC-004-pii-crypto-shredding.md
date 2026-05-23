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
