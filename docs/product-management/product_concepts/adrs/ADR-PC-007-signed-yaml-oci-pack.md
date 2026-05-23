# ADR-PC-007: Pack Manifest Format and Distribution — Signed YAML in an OCI Artefact, CUE-Validated

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) (family-schema language; CUE — pack schemas ship as `.cue`), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (engine language and framework), [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (per-instance pinning via event envelope `pack_version`) |
| Resolves | bd `archie-10r.8` (ADR-PC-007: Pack manifest format and distribution) |

---

## Context

A **pack** is the versioned, jurisdiction-scoped vocabulary the engine resolves against at constitution and pins per instance for life ([01 §5](../01-product-architecture.md), [surface §3](../feature-design-configuration-surface.md)). It carries primitives (pack-bound enumerations the schema references), parameters (pack-level constants), rate-sheet refs, and a **sealed test corpus** (canonical instances + expected event sequences — regulator-facing regression evidence per [surface §3.9](../feature-design-configuration-surface.md)). It is **declarative data, not executable code** ([surface §3.1](../feature-design-configuration-surface.md)), signed and version-pinned.

This ADR resolves three sub-problems ([bd archie-10r.8](../04-open-questions.md)): (1) **content format** — what the pack ships as and how it is laid out; (2) **distribution** — how the pack reaches the engine; (3) **signing and pinning** — how the engine verifies a pack at load and how an instance pins a version for life.

The schema language is now **CUE** ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)), reversing the premise of this ADR's prior iteration (which rejected CUE-as-pack-format *because* the schema language was JSON Schema). With a CUE evaluator already in the stack (the Go validator binary), CUE-as-pack-format becomes a genuine contender, and the content-format question is re-opened in that light. Distribution and signing are largely orthogonal to the format choice; [surface §3.7](../feature-design-configuration-surface.md) already commits packs to OCI artefacts signed with cosign, verified at engine load.

**Candidates evaluated** ([bd archie-10r.8](../04-open-questions.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **YAML data + `.cue` schemas, bundled in an OCI artefact, cosign-signed** | Pack values/primitives/parameters/test-corpus stay auditor-readable YAML; the `schemas/` directory ships `.cue` constraint files; `cue vet` validates the YAML data; distributed as an OCI artefact pulled by digest; cosign (Sigstore) signing. |
| B | **Fully CUE-native pack** (schema + values + constraints in one CUE tree) | Maximal coherence; the canonical regulatory artefact is CUE; `cue export` renders YAML/JSON views on demand. |
| C | **Signed YAML in a Git repository** | YAML in git; GPG commit signing; engine pulls by commit SHA. |
| D | **Binary format (Avro / Protobuf packs)** | Pack as binary; detached signature. |

The CUE adoption upstream collapses the live decision to **A vs B**: keep the canonical regulatory data as plain YAML and use CUE only for constraints (A), or make CUE the canonical pack form end-to-end (B). C and D are evaluated and rejected on distribution and auditability grounds respectively.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · YAML + `.cue` in OCI | YAML 1.2 (open); YamlDotNet (MIT); CUE (Apache 2.0); cosign/Sigstore (Apache 2.0); OCI image-spec (Apache 2.0); existing registry. | **Pass** |
| B · Fully CUE-native | CUE (Apache 2.0); cosign/OCI as A. | **Pass** |
| C · YAML in Git | Git (GPL 2.0); GPG (protocol). | **Pass** |
| D · Binary | Avro (Apache 2.0) / Protobuf (BSD-3); cosign/minisign. | **Pass** |

All pass F1.

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

No PII in packs (packs hold market/regulatory constants; PII lives in event payloads — [event-store §6.2](../feature-design-event-store-projections.md)). GDPR neutral across all four. The discriminating dimension is **auditor/regulator readability of the canonical artefact** (packs are regulatory evidence, read by humans in supervision and DORA-style due diligence — [surface §3.9](../feature-design-configuration-surface.md)) and **tamper-evidence**.

| Candidate | DORA / PSD2 | Verdict |
|---|---|---|
| A · YAML + `.cue` in OCI | OCI artefacts pulled-by-digest are immutable; cosign-signed = tamper-evident; pack version pinned per instance is the audit trail. Canonical values are plain-text YAML — `cat` + `diff` for any reviewer. | **Pass** |
| B · Fully CUE-native | Same distribution/signing. But the **canonical** audited artefact is CUE; regulator readability depends on a `cue export` rendering step that produces a *derived* view — the audited form is the niche language, the readable form is generated. | **Pass (conditional)** — a deterministic, version-stamped `cue export` rendering must be published alongside and treated as authoritative for review; otherwise the canonical/readable split is an audit hazard. |
| C · YAML in Git | Commit history immutable; signed commits tamper-evident; YAML readable. | **Pass** |
| D · Binary | Auditor readability requires tooling + retrievable schema; not plain-text. | **Pass (conditional)** — embedded schema + a published decode tool required for auditor access. |

---

### Soft criteria

#### A · YAML data + `.cue` schemas in an OCI artefact — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** The container registry is already operated (engine, Kong, Redpanda images). A `babelstone-packs/pt-deposit` repository adds zero new infrastructure; the engine pulls by digest with the same `oras pull` workflow already in use. Pack publication is a CI pipeline: `pack-validate` (CUE depths 1–4 over the YAML data, [ADR-PC-006 §P2](./ADR-PC-006-cue-schema-language.md)) + `cosign sign` + `oras push`. One registry, not a registry plus a Git host plus an artefact server.

**S2 · Ecosystem coherence.** Maximum, and now unified on one constraint language. YamlDotNet parses the YAML data in-engine; the `.cue` schemas in `schemas/` are validated by the same Go validator binary the engine and CI already run ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)) — depth-3 pack compliance is `cue vet parameters.yaml schemas/term-deposit.cue` with no translation. cosign composes with the existing registry; pack-pull operations emit OTel spans ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)).

**S3 · Exit cost.** Low. YAML data is portable, human-readable, diff-friendly; only the `.cue` constraint files are CUE-specific, and they are small and engine-team-owned. cosign signatures are detachable; the pack content stays valid YAML without them. Migrating off OCI (to object storage or Git) is a distribution change that leaves the format untouched.

**S4 · Community and longevity.** YAML 1.2 is a multi-decade open spec; OCI image-spec is CNCF-graduated and supported by every major registry; cosign is Linux-Foundation Sigstore with multi-vendor support. The only pre-1.0 dependency is CUE (the `.cue` schemas) — and that risk is already owned and mitigated in [ADR-PC-006 §S4](./ADR-PC-006-cue-schema-language.md), with the YAML-data separation keeping a JSON-Schema fallback open without touching pack data.

#### B · Fully CUE-native pack

**S1.** Comparable publication pipeline. **S2.** Highest *internal* coherence — schema, values, constraints, defaults in one CUE tree, one `cue vet` over the whole pack. **S3.** Highest exit cost — CUE syntax does not portably round-trip to YAML/JSON for the *values*, only lossy for constraints. **S4.** The entire canonical artefact now depends on pre-1.0 CUE, concentrating the [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) S4 risk on the regulatory evidence itself rather than confining it to constraint files.

**Decisive reason for not choosing B:** the pack is **regulator-facing evidence**, and the canonical stored artefact should be in the lingua franca (YAML/JSON), not a niche pre-1.0 language. B inverts the "plain text is the source of truth" property: the audited form becomes CUE and the human-readable form becomes a generated `cue export` view. For a banking compliance artefact that is exactly backwards. B's coherence gain is real but is captured *almost entirely* by A — A still validates the YAML data with CUE constraints; it simply keeps the *values* as the canonical plain-text form. A gets CUE's expressiveness at the constraint boundary without betting the regulatory evidence on CUE's longevity.

#### C · Signed YAML in Git

**S1.** Git is universally understood, but pack distribution via Git couples the engine's runtime pack-pull to Git-host availability — a *new* runtime dependency surface distinct from the existing image-pull path. **S2.** GPG key management vs cosign keyless OIDC — cosign is simpler at small-team scale. **S3.** Lowest exit cost. **S4.** Git-host longevity highest.

**Decisive reason for not choosing C:** S1 + S2 — OCI distribution composes with existing infrastructure; Git distribution adds a new runtime dependency surface for no compensating gain. (If the bank later prefers Git-based pack governance, the engine's pack-load source can be re-pointed without changing the YAML+`.cue` format.)

#### D · Binary format (Avro / Protobuf)

**S1/S2.** Avro is already at the bus boundary ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)), but using it for packs harms auditability. **Decisive reason for not choosing D:** auditor/PM readability at the pack boundary is load-bearing; YAML is plain-text and `diff`-friendly, binary is not, and the efficiency gain is irrelevant for a load-once-per-constitution lifecycle.

---

## Decision

**Chosen: a pack ships as auditor-readable YAML data plus `.cue` constraint schemas, bundled into an OCI artefact in the existing container registry, signed with cosign and pulled by digest; CUE (`cue vet`) validates the YAML data.**

The decisive forces: (1) **regulator readability of the canonical artefact** — the pack's values, primitives, parameters, and sealed test corpus stay plain-text YAML (`cat` + `diff`, no tooling), which is a hard requirement of the pack's evidence role ([surface §3.9](../feature-design-configuration-surface.md)); (2) **one constraint language, captured cheaply** — the `schemas/` directory carries `.cue` files validated by the same Go validator the engine and CI already run ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)), giving CUE's depth-3/4 expressiveness at the pack boundary without making CUE the canonical regulatory form; (3) **operational coherence** with the existing registry, cosign, and `oras` workflow.

**Rejected: fully CUE-native pack** — its coherence gain over A is marginal (A already validates with CUE), while it inverts the canonical/readable relationship for a regulatory-evidence artefact and concentrates the pre-1.0 CUE longevity risk onto that evidence. Retained as a future option if CUE reaches a v1 stability promise and regulator tooling normalises CUE.

**Rejected: signed YAML in Git** — adds a runtime dependency surface without gain over OCI. **Rejected: binary format** — auditor readability is the load-bearing constraint; YAML keeps it.

---

## Implementation Principles

### P1 — Pack manifest layout (YAML data + `.cue` schemas, single bundle)

A pack is one OCI artefact comprising a tar layer:

```
pack/
  pack.yaml                 # manifest: id, version, namespace, metadata, deps
  schemas/                  # CUE constraint schemas (one per family or shared)
    term-deposit.cue
    common.cue
  primitives/               # pack-bound primitives the schemas reference (YAML)
    day-count.yaml
    tax.yaml
    fgd.yaml
  parameters/
    constants.yaml          # pack-level constants (max_consumer_rate_bps, fgd_ceiling_eur, …)
  rate-sheet-refs/
    deposits-pt.yaml        # version-pinned refs to ADR-PC-008-stored rate sheets
  test-corpus/              # sealed regression evidence (YAML data)
    canonical-instances.yaml
    expected-events.yaml
  README.md                 # human-readable pack-version changelog
```

The **data** (`pack.yaml`, `primitives/`, `parameters/`, `rate-sheet-refs/`, `test-corpus/`) is YAML — the canonical, auditor-readable form. The **constraints** (`schemas/*.cue`) are CUE; `cue vet primitives/ parameters/ schemas/*.cue` is the depth-3 pack-compliance check. `pack.yaml` carries identity (`pack_id`, `pack_version` immutable once published, `namespace`, `publisher`, `publish_date`, `schema_version` of the manifest itself, `dependencies.engine_compatible_versions`, schema/rate-sheet pins) per [surface §3.4](../feature-design-configuration-surface.md).

### P2 — Distribution as OCI artefact, pulled by digest, cosign-signed

Packs publish to a dedicated registry repository (e.g. `babelstone-packs/pt-deposit`) with media type `application/vnd.babelstone.pack.v1+yaml`. The engine pulls by **digest** (sha256), never by tag. Signing is **cosign keyless** (Sigstore OIDC; engine-team identity in v1, bank-internal OIDC in production); the engine verifies at pull time via the Sigstore .NET client or a `cosign verify` step at image-build for sealed deployments. The cosign signature is also the attestation that CUE depths 1–4 passed in CI ([ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md)) — verified-signature ⇒ already-validated.

### P3 — Per-instance pinning via `pack_version` on every event

Every event carries `pack_version` in its envelope ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)). The engine resolves a `pack_version` string to its OCI digest via a `pack_versions` table (`(pack_id, pack_version) → OCI digest + signature digest`). An instance constituted under a `pack_version` keeps it for all subsequent lifecycle events until explicitly migrated by a `PackVersionMigrated` event ([surface §3.5–§3.6](../feature-design-configuration-surface.md)); the engine never silently re-resolves mid-lifetime.

### P4 — Engine load behaviour: validate-then-cache, fail-loud

At startup the engine pulls, signature-verifies, structurally re-parses, and caches every `pack_version` referenced by any live instance (queryable from `events.pack_version`). Because the signature attests prior CUE validation ([ADR-PC-006 §P3](./ADR-PC-006-cue-schema-language.md)), the load-time check is a structural parse + version check, not a full `cue vet` re-run. Pull/verify failure at startup is **fatal** — no silent degradation. New `pack_version` references trigger a hot pull + verify + cache with the same fail-loud discipline. Event handlers resolve primitives/parameters against the in-memory cache (no I/O).

### P5 — Sealed test corpus is the regression-evidence interface

`test-corpus/` ships canonical instances + expected event sequences (YAML) per pack version. CI runs the corpus against the engine's hand-rolled substrate ([ADR-PC-010 §P3](./ADR-PC-010-dotnet-hand-rolled-engine.md)) at every pack-publish and every engine-release; corpus failure is a release blocker. `expected-events.yaml` is **generated** by running the engine against `canonical-instances.yaml` and committed — never hand-authored — so the corpus cannot silently drift ([surface §3.9](../feature-design-configuration-surface.md)).

---

## Consequences

**What this choice makes easier:**

- Pack publication is a CI run with no new infrastructure (`pack-validate` + `cosign sign` + `oras push` against the existing registry).
- Auditor/regulator review of the canonical artefact is `cat pack.yaml` + `diff` — no tooling, no `cue export` indirection.
- One constraint language across schema ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)) and pack — the `.cue` schemas validate the YAML data directly at depth 3.
- Per-instance pinning via `pack_version` integrates with the [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) envelope unchanged.

**What this choice makes harder or impossible:**

- Pack-content evolution is YAML-diff-visible in the registry — a transparency feature, but it makes pack-version churn fully public to anyone with registry access.
- The OCI registry becomes a regulatory artefact store: pack repositories need keep-forever retention, distinct from image repositories' GC policy. The 1–2 person team operates this distinction.
- The pack-manifest CUE schema (describing `pack.yaml`'s own shape) is a forward-only contract following the same evolution discipline as event schemas ([event-store §5.4](../feature-design-event-store-projections.md)) — additive changes are fine; removal/re-typing requires a new pack-format major version with parallel publication.

**Residual risks:**

- **Pack-format CUE schema versioning.** A buggy publish reaching the registry. Mitigation: the publish CI runs `cue vet` of `pack.yaml` against the manifest schema before `oras push`; the engine re-checks structurally at load and refuses unparseable packs (defence in depth).
- **cosign keyless depends on Sigstore.** Mitigation: bank-internal OIDC for production; accept the Sigstore dependency for POC.
- **OCI distribution depends on the existing registry** — the same dependency as container images; shared HA topology. Acceptable.
- **Sealed-corpus drift.** Mitigation: the publish gate regenerates `expected-events.yaml` by running the engine against `canonical-instances.yaml`; the corpus is generated, not authored.
- **CUE longevity at the constraint layer** — owned and mitigated in [ADR-PC-006 §S4](./ADR-PC-006-cue-schema-language.md); the YAML-data separation confines the risk to the `.cue` files and keeps the JSON-Schema fallback open without touching pack data.

---

## Cross-references

- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — CUE is the schema language; pack `schemas/` ship `.cue` files validated by the same Go validator; cosign signing underwrites the validated-in-CI attestation.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — engine runtime is .NET 9; YamlDotNet parses pack YAML; the hand-rolled substrate runs the depth-5 corpus.
- [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) — `pack_version` is a contract column on the event envelope.
- [ADR-PC-008](../04-open-questions.md) — rate-sheet storage; this ADR carries rate-sheet refs only.
- [ADR-PC-009](../04-open-questions.md) — per-instance pack/schema version pinning; this ADR carries the pinning column, PC-009 carries the migration-event semantics.
- [surface §3.4–§3.10](../feature-design-configuration-surface.md) — pack manifest shape, pinning, distribution/signing (§3.7), sealed test corpus (§3.9), validator interplay (§3.10).

---

*Decided 2026-05-23 by jhosm. Supersedes the prior JSON-Schema-context iteration of ADR-PC-007 (removed before acceptance): the pack format is unchanged (signed YAML in OCI) but the `schemas/` files are now `.cue` and validation is CUE, following the ADR-PC-006 schema-language change.*
