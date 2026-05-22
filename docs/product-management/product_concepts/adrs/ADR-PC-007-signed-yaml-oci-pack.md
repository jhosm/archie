# ADR-PC-007: Pack Manifest Format and Distribution — Signed YAML in an OCI Artefact

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-05-22 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md) (family-schema language; JSON Schema), [ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md) (engine language and framework), [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (per-instance pinning via event envelope `pack_version`) |
| Resolves | bd `archie-10r.8` (ADR-PC-007: Pack manifest format and distribution) |

---

## Context

A **pack** is the regulatory + market-context container that the engine resolves against at constitution time and pins per instance for life ([01 §5](../01-product-architecture.md), [feature-design-configuration-surface §3.5–§3.6](../feature-design-configuration-surface.md)). A pack carries:

- **Primitives** — pack-bound enumerations the schema references (e.g. `pt.day_count.act_360`, `pt.tax.irs_residente`, `pt.fgd.deposit_eligible`) ([authoring §9.3](../feature-design-configuration-authoring.md))
- **Parameters** — pack-level constants the engine reads at runtime (e.g. `pack.fgd_ceiling_eur`, `pack.tax_residente_rate_bps`, `pack.max_consumer_rate_bps`) ([surface §3.4](../feature-design-configuration-surface.md))
- **Rate-sheet refs** — pointers to active rate sheets the pack vouches for (the rate-sheet *storage* is ADR-PC-008, not this ADR; this ADR only carries refs)
- **Sealed test corpus** — canonical instances with expected event sequences, per pack version, for regulator-facing regression evidence ([surface §3.9](../feature-design-configuration-surface.md))

Three problems this ADR resolves:

1. **Serialisation format** — what file format does a pack ship in, and what is the layout inside?
2. **Distribution mechanism** — how does the pack reach the engine (registry, file mount, embedded resource)?
3. **Signing and pinning** — how does the engine verify a pack at load time, and how does an instance pin to a specific pack version for life?

The engine runtime is .NET 9 ([ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md)). The schema language is JSON Schema ([ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md)). The container registry already exists for Redpanda/Kong/engine container images.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Signed YAML files bundled into an OCI artefact in the existing container registry** | YAML primary, JSON Schema sidecar; bundled as an OCI artefact with `application/vnd.babelstone.pack.v1+yaml` media type; signed with cosign (Sigstore); engine pulls by digest |
| B | **Signed YAML files in a Git repository** | YAML in git; signed with GPG commit signatures; engine pulls by commit SHA |
| C | **CUE-as-pack-format** (single CUE file embedding schema + parameters + primitives) | CUE doubles as schema language and pack format; out-of-process CUE evaluator required (per PC-006 rejected paths) |
| D | **Binary format (Avro / Protobuf packs)** | Pack as Avro / Protobuf binary; deserialised at load; signed via detached signature |

---

## Evaluation

### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| Signed YAML in OCI artefact | YAML 1.2 spec (open); YamlDotNet (MIT); cosign/Sigstore (Apache 2.0); OCI image-spec (Apache 2.0); existing container registry (Apache 2.0 Harbor / similar). All MIT/Apache 2.0. | **Pass** |
| Signed YAML in Git | Git (GPL 2.0); GPG commit signing (no licence concern for the protocol). | **Pass** |
| CUE-as-pack-format | CUE Apache 2.0 (CNCF Sandbox); out-of-process .NET binding required (carries CUE binary in container image). | **Pass (conditional)** — image-bundling of a Go binary alongside .NET. Mitigation: pin CUE version; declare image-bundling policy. |
| Binary format | Avro (Apache 2.0) / Protobuf (BSD-3); detached signature via cosign or minisign. | **Pass** |

All four pass F1. No disqualification on licence.

### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | GDPR | DORA | PSD2 | Verdict |
|---|---|---|---|---|
| Signed YAML in OCI artefact | No PII in packs (packs hold market/regulatory constants; PII lives in event payloads — `event-store §6.2`). GDPR neutral. | OCI registries support replication, immutability of pulled-by-digest artefacts. RTO/RPO inherits registry HA topology. cosign-signed artefacts are tamper-evident. | Pack version pinned per instance is the audit trail; the registry stores every pack version published, never modified. | **Pass** |
| Signed YAML in Git | GDPR neutral. | Git repo HA is operational discipline; commit history is immutable; signed commits are tamper-evident. | Pack version = commit SHA, immutable and signed. | **Pass** |
| CUE-as-pack-format | GDPR neutral. | Same as (A) when distributed via OCI; out-of-process CUE evaluator adds operational surface. | Same as (A). | **Pass (conditional)** — operational surface increase for the CUE evaluator at engine load. |
| Binary format | GDPR neutral. | Audit-trail readability requires the binary format's schema to be retrievable; if schema is embedded, fine; otherwise schema-registry lookup at pack-history time becomes a dependency. | Same as (A). | **Pass (conditional)** — auditor readability without tooling is harder than for YAML. |

All four pass F2 at POC scale; (C) and (D) carry operational caveats.

---

### Soft criteria — Candidate A (signed YAML in OCI artefact) — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** The container registry is already operated (engine images, Kong images, Redpanda images live there). Adding a `babelstone-packs/pt-deposit` repository for pack OCI artefacts costs zero new infrastructure. The engine pulls by digest (immutable reference); the operational toolchain is the same `docker pull` / `oras pull` workflow already in use for container images. Pack publication is a CI pipeline that runs `yaml-lint` + `cosign sign` + `oras push`. The 1–2 person team operates one registry, not a registry plus a separate Git repo plus a CI artefact server.

**S2 · Ecosystem coherence.** Maximum. YamlDotNet is the canonical .NET YAML library; in-process parsing in NJsonSchema-validated pipelines. cosign is the de-facto Sigstore signing tool with first-class .NET verification via the Sigstore .NET client (or a thin shell-out to `cosign verify` in the build-image-build step). OCI artefacts compose with the existing ADR-IC-007 (OpenTelemetry) observability; pack-pull operations emit OTel spans naturally. The pack manifest layout maps directly onto the JSON Schema validator inputs from ADR-PC-006 — pack primitives and parameters are referenced by schema validators at depth-3 (pack compliance) without translation.

**S3 · Exit cost.** Low. YAML is portable, human-readable, diff-friendly. The pack-manifest schema (a JSON Schema describing the YAML structure) is itself portable. cosign signatures are detachable; the pack content stays valid YAML without the signature. Migrating off OCI to plain object storage (or Git) is a registry change; the pack format itself is untouched.

**S4 · Community and longevity.** YAML 1.2 is a multi-decade open spec. OCI image-spec is CNCF graduated; OCI artefacts (the v1.1 generalisation beyond container images) are supported by every major container registry (Harbor, ECR, GCR, ACR, Docker Hub, GitLab Registry). cosign is part of Sigstore (Linux Foundation), with multi-vendor support and active development. YamlDotNet's commit cadence comfortably exceeds the ADR-IC-000 ≥25 trailing-12-month threshold (verified context7 2026-05-22 against `/aaubry/yamldotnet`).

---

### Soft criteria — Candidate B (signed YAML in Git)

**S1.** Git operations are universally understood; the team already operates Git for source control. But pack distribution via Git introduces a second consumer of the Git repo (the engine pulls pack tags at startup or registry-watch time) — this couples the engine's runtime availability to Git host availability in a way that the OCI registry does not. For 1–2 person ops, the OCI path is operationally cleaner because pack pulls and image pulls share infrastructure.

**S2.** GPG commit signing is fine but requires GPG key management discipline; cosign + Sigstore provides keyless signing via OIDC, which is materially simpler at small-team scale.

**S3.** Lowest exit cost of the four candidates (Git repo move = `git clone` + push).

**S4.** Git host longevity is highest; GPG signing is multi-decade stable.

**Decisive reason for not choosing (B):** S1 + S2 — OCI artefact distribution composes with existing infrastructure; Git distribution adds a new runtime dependency surface.

---

### Soft criteria — Candidate C (CUE-as-pack-format)

**S1.** Doubles the out-of-process CUE evaluator dependency from ADR-PC-006's rejected path. The engine load-time pack parsing would shell out to `cue export` to obtain JSON, then process. Operational surface increase for a 1–2 person team.

**S2.** CUE's expressiveness for pack constraints is genuinely powerful (typed constraint composition, bound primitives, schema + values in one file). But ADR-PC-006 already rejected CUE for the schema-language role on out-of-process grounds; doubling down here doubles the cost.

**S3.** Highest exit cost — CUE syntax does not portably translate to JSON Schema or YAML.

**S4.** CUE is CNCF Sandbox (still maturing); cadence is good but ecosystem maturity for .NET is thin.

**Decisive reason for not choosing (C):** Doubling the rejected-from-PC-006 out-of-process burden; (A) achieves the same pack-validation outcomes with NJsonSchema validating YAML.

---

### Soft criteria — Candidate D (binary format)

**S1.** Auditor readability is materially harder — packs are regulatory evidence, and regulators expect to read them. YAML is plain-text and `diff`-friendly; Avro/Protobuf require tooling.

**S2.** Avro is already chosen at the bus boundary (ADR-IC-002); using Avro for packs reuses serialisation but harms pack auditability.

**S3.** Medium exit cost (binary-to-YAML conversion is mechanical).

**S4.** Avro/Protobuf both stable.

**Decisive reason for not choosing (D):** Auditor / PM readability matters at the pack boundary — packs are read by humans in regulatory review, not only by the engine. YAML keeps this property.

---

## Decision

**Chosen: Signed YAML files bundled into an OCI artefact in the existing container registry, signed with cosign.**

The decisive forces are (1) operational coherence with the existing container-registry infrastructure (S1, S2), (2) human readability for regulatory review (which is a hard requirement of the pack role in `feature-design-configuration-surface §3.9`'s sealed-test-corpus discipline), and (3) lowest combined operational surface across the engine team's 1–2 person profile. YAML in OCI artefacts is the lowest-risk path that satisfies all five pack roles (primitives, parameters, rate-sheet refs, sealed test corpus, version pinning) without introducing a new operational surface.

**Rejected: Signed YAML in Git** — Operationally adds a second runtime dependency for the engine without compensating gain over OCI; Git tag immutability is real but the engine pull path via Git is a separate infrastructure surface from the existing image pull path. If the operating bank later prefers Git-based pack governance (PR workflow visible in the same tool as code review), the engine's pack-load code can be re-pointed to a Git source without changing the YAML format itself — i.e. the cost of switching to Git later is the registry-mechanism change, not a pack-format rewrite.

**Rejected: CUE-as-pack-format** — Doubles ADR-PC-006's rejected out-of-process burden; the validator at depth-3 (pack compliance) is already handled by NJsonSchema against pack-parameters embedded in the JSON Schema dialect's `$defs`; CUE would re-implement this less portably. CUE remains useful as an *evaluator* tool the engine team can run locally to cross-check pack assertions during pack authoring, but not as the on-disk pack format.

**Rejected: Binary format (Avro / Protobuf)** — Auditor readability is the load-bearing concern. Packs are regulatory evidence; YAML is plain-text; Avro/Protobuf are not. The marginal efficiency gain from binary packs is irrelevant at the pack-load-once-per-instance-constitution lifecycle.

---

## Implementation Principles

### P1 — Pack manifest layout (YAML, single bundle)

A pack is a single OCI artefact comprising these files inside a tar layer:

```
pack/
  pack.yaml                 # manifest: name, version, namespace, pack metadata, deps
  schemas/                  # JSON Schemas (one per family or shared $defs)
    term-deposit.schema.json
    common.schema.json
  primitives/               # pack-bound primitives the schemas reference
    day-count.yaml
    tax.yaml
    fgd.yaml
  parameters/
    constants.yaml          # pack-level constants (max_consumer_rate_bps, fgd_ceiling_eur, ...)
  rate-sheet-refs/
    deposits-pt.yaml        # refs to ADR-PC-008-stored rate sheets, version-pinned
  test-corpus/              # sealed regression evidence
    canonical-instances.yaml
    expected-events.yaml
  README.md                 # human-readable pack-version changelog
```

`pack.yaml` carries the pack identity:

```yaml
pack_id: "pt-deposit-pack"
pack_version: "2026.05.22-r3"        # semver-like; immutable once published
namespace: "pt-bank"
publisher: "babelstone-pack-team"
publish_date: "2026-05-22T08:30:00Z"
schema_version: "1.4"                # of pack.yaml itself, for forward-only evolution
dependencies:
  engine_compatible_versions: ">=1.0.0,<2.0.0"
  schemas:
    - term-deposit@2026.04.10-r1
  rate_sheets:
    - deposits-pt@2026.05.22
```

### P2 — Distribution as OCI artefact, pulled by digest

Packs publish to the existing container registry under a dedicated repository (e.g. `babelstone-packs/pt-deposit`). The artefact uses the `application/vnd.babelstone.pack.v1+yaml` media type. The engine pulls by **digest** (sha256), never by tag — tags are mutable labels for human convenience; digests are the immutable runtime reference. The pull path uses `oras pull` or the equivalent .NET OCI client.

The OCI artefact is signed with **cosign keyless signing** (Sigstore OIDC; CI-emitted signature against the engine team's GitHub identity in v1; bank-internal OIDC in production). The engine verifies the signature at pull time via the Sigstore .NET client (or shell-out to `cosign verify` at image-build time, with the pack baked into the engine image for sealed deployments).

### P3 — Per-instance pinning via `pack_version` on every event

Every event written by the engine carries `pack_version` in its envelope (per [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) contract column). The `pack_version` value matches the `pack.yaml` `pack_version` field; the engine resolves a `pack_version` string to the OCI digest via a `pack_versions` table (one row per `(pack_id, pack_version)` mapping to OCI digest + signature digest).

Per-instance pinning means: an instance constituted under `pack_version: 2026.05.22-r3` continues to use that pack version for all subsequent lifecycle events, until explicitly migrated via a `PackVersionMigrated` event (per [feature-design-configuration-surface §3.5–§3.6](../feature-design-configuration-surface.md)). The engine never silently re-resolves a pack version mid-instance-lifetime.

### P4 — Engine load behaviour: validate-then-cache

At engine startup, every pack version referenced by any live instance (queryable from the `events` table's `pack_version` column) is pulled, signature-verified, and parsed into an in-memory immutable cache. Pack pull failures at startup are fatal (the engine refuses to start with unresolvable packs) — silent degradation is forbidden. Subsequent instance constitutions that reference a new pack version trigger a hot pull + verify + cache, with the same fail-loud discipline.

Pack-load cost is amortised across instance lifetime: every event handler resolves pack primitives and parameters against the in-memory cache, no I/O. A pack-cache invalidation event (e.g. signature expiry, key rotation) is a deployment-time operator-initiated reload, never a runtime auto-refresh.

### P5 — Sealed test corpus is the regression-evidence interface

The `test-corpus/` directory ships canonical instances + expected event sequences per pack version. CI runs the corpus against the engine at every pack-publish AND at every engine-release; corpus failure is a release blocker. The corpus is the regulator-facing evidence that the engine + pack + schema combination produces the documented behaviour ([feature-design-configuration-surface §3.9](../feature-design-configuration-surface.md), [feature-design-event-store-projections §10.4](../feature-design-event-store-projections.md)).

---

## Consequences

**What this choice makes easier:**

- Pack publication is a CI pipeline run with no new infrastructure (`yaml-lint` + `cosign sign` + `oras push` against the existing registry).
- Pack auditor / regulator review is `cat pack.yaml` + `diff` — no tooling required.
- Per-instance pinning via `pack_version` integrates with ADR-PC-001 §P1 event envelope without schema changes.
- The pack-load fail-loud discipline aligns with the engine's deterministic-handler philosophy (no silent degradation, no clock-time pack resolution).

**What this choice makes harder or impossible:**

- Pack-content evolution is YAML-diff visible (not necessarily harder, but a discipline — every pack change is human-readable in the registry's diff view). This is a feature, not a bug, but it means pack-version churn is fully transparent to anyone with registry access.
- The OCI registry becomes a regulatory artefact store, not just a container image store. Retention policies for pack repositories must be different from image repositories (packs are kept forever; old container images may be GC'd). The 1–2 person team must operate this distinction.
- The pack-format JSON Schema (describing the structure of `pack.yaml`) is itself a forward-only contract that must follow the same `event_schema_version`-style evolution discipline as event schemas ([event-store §5.4](../feature-design-event-store-projections.md)). Adding fields is fine; removing or re-typing requires a new pack-format major version with parallel publication.

**Residual risks:**

- **Pack-format schema versioning.** A buggy pack-publication CI that publishes a malformed pack reaches the registry. Mitigation: the pack-publication CI runs `NJsonSchema` validation of `pack.yaml` against the pack-format JSON Schema before `oras push`; the engine re-runs the same validation at load and refuses to use unparseable packs (fail-loud). Defence in depth.
- **cosign keyless signing depends on Sigstore.** If Sigstore is unavailable at engine startup, the keyless verification path fails. Mitigation: for production, transition to a bank-internal OIDC identity provider for cosign signing; for POC, accept the Sigstore dependency.
- **OCI artefact distribution depends on the existing registry.** If the registry is offline, the engine cannot start. Mitigation: this is the same dependency as for container images; the registry HA topology is operationally shared. Acceptable.
- **Pack-version `2026.05.22-r3`-style strings are not lexicographically ordered against revision suffixes.** Mitigation: pack-version strings are opaque; the engine never compares them ordinally — it resolves them via the `pack_versions` mapping table. Ordering is a publication-policy concern, not a runtime concern.
- **Sealed test corpus drift.** A pack publication can ship without updating the test corpus, or with an inconsistent corpus. Mitigation: CI gate at pack-publish requires `expected-events.yaml` to be regenerated by running the engine against `canonical-instances.yaml` and committing the new output; the corpus is generated, not hand-authored.

---

## Cross-references

- [ADR-PC-006](./ADR-PC-006-json-schema-njsonschema.md) — JSON Schema is the schema language; pack `schemas/` ship JSON Schema files validated by the same NJsonSchema validator pipeline.
- [ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md) — engine runtime is .NET 9; YamlDotNet and Sigstore .NET client are the parsing + verification libraries.
- [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) — `pack_version` is a contract column on the event envelope.
- [ADR-PC-008](../04-open-questions.md) — rate-sheet storage; this ADR carries rate-sheet refs only, not the sheets themselves.
- [ADR-PC-009](../04-open-questions.md) — per-instance pack and schema version pinning; this ADR carries the pinning column, PC-009 will carry the migration-event semantics.
- [feature-design-configuration-surface §3.5–§3.6](../feature-design-configuration-surface.md) — per-instance pinning semantics.
- [feature-design-configuration-surface §3.9](../feature-design-configuration-surface.md) — sealed test corpus discipline.
- [feature-design-configuration-authoring §9](../feature-design-configuration-authoring.md) — coarse-start fine-drift; the pack format must accommodate evolving primitives across pack versions.

---

*Decided 2026-05-22 by jhosm.*
