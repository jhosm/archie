# ADR-PC-007: Pack Manifest Format and Distribution — Signed YAML in an OCI Artefact, CUE-Validated

| Field | Value |
|---|---|
| Status | Accepted |
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

## Amendments

### A1 · The pack gains a family-manifest (`families.yaml`) — the pinned family set (2026-06-20, bd `babelstone-9w2k.3`)

**In plain English:** a pack already pins each family's *schema version* (`schema_pins`); this adds a sibling file that pins *which families* a deployment is allowed to run at all. The engine host cross-checks the code it discovered against this list at startup and refuses to boot on a mismatch — so a family whose code drifted ahead of the pinned pack can't quietly corrupt the audit trail.

The §P1 pack layout is **extended** with one more format-fixed data file, `families.yaml`, carrying a `families:` list of `{family_name, aggregate_type, schema_version, plugin_assembly}` entries. It is constrained by the new `#FamilyManifest` definition in `contracts/cue/pack/pack.cue` (closed struct; `schema_version` reuses `#SchemaRef`, the same `<family>@YYYY.N` shape `schema_pins` uses), validated by `cue vet` in the publish pipeline exactly like every other pack data file, and parsed into `VerifiedPack.Families` by the same fail-loud structural parser (`PackParser`). The `schema_version` of a family **must agree** with that family's `schema_pins` entry — one pin, named for two readers (the schema registry vs the host's module roster).

This is **additive**: it reverses no part of the Decision — the pack stays auditor-readable YAML data + bundled `.cue` schemas, distributed as a cosign-signed OCI artefact pulled by digest (§P1–P4). The fail-loud structural parse and the verified-signature-attests-CUE posture (§P2/§P4) extend to the new file unchanged. The load-time consumer of this manifest — the host's MANDATORY fail-closed family/schema-version cross-check — is owned by [ADR-PC-009 §A1](./ADR-PC-009-per-instance-version-pinning.md) (the pinned pack is the authoritative per-deployment family set); this ADR owns only the pack-format addition. Gated by `HOST_PACK_FAMILY_MANIFEST_CROSS_CHECK` in the [commitment catalogue](./commitment-catalogue.md) (recorded in Verifiable commitments below).

### A2 · The first-party container images are pinned by digest at the CD boundary — verify what you deploy (2026-07-09, bd `babelstone-2t16.30`)

**In plain English:** the same "cosign-signed, pulled by digest, never a movable tag" rule this ADR sets for pack OCI artefacts (§P2) now also governs the first-party *container images* the CD pipeline promotes. Until now the pipeline verified images by digest but then deployed manifests that still said `:latest` — so it could cosign-verify one set of bytes and let the kubelet pull another. The promote step now resolves each first-party image to its exact signed digest, verifies *that* digest, and pins the rendered manifest to it, so the bytes verified are the bytes deployed.

§P2's distribution posture — cosign-keyless-signed, identity-pinned, and consumed **by digest, never by a movable tag** — is **extended** from pack artefacts to the first-party container images built and signed by `image-build.yml` (the same cosign machinery, under the same OIDC signing identity §P2 already names). At promotion (`.github/workflows/cd.yml`), for every `ghcr.io/jhosm/babelstone-*` image the target overlay renders, the pipeline (1) resolves its in-manifest tag to the immutable manifest digest, (2) `cosign verify`s that `name@sha256:…` against the `image-build.yml` signing identity, and (3) pins the rendered manifest to that digest (`kustomize edit set image`) before `kubectl apply`. One digest flows resolve → verify → deploy, closing the time-of-check-to-time-of-use gap between the cosign verification and the kubelet pull. `scripts/cd-pin-images.sh` carries the logic in two modes — a hermetic `--contract` assertion on the push/PR gate lane (mirrors how the `verify-images` job asserts its cosign contract with no live digests) and the real `--pin` on a dispatched apply.

This is **additive**: it reverses no part of the Decision — packs are unchanged (still auditor-readable YAML data + bundled `.cue` schemas, cosign-signed OCI, pulled by digest, §P1–§P5), and this generalises §P2's "by digest, never by tag" trust rule to the container images the same signature already covers. Explicitly **out of scope** here and tracked separately (bd `babelstone-2t16.31`): third-party images (`svhd/logto`, `postgres`, `kong`, …) and commit-pinned (rather than current-`latest`-resolved) promotion. Recorded in Verifiable commitments below.

### A3 · Third-party container images are pinned by digest for reproducibility — a distinct, weaker guarantee than §A2 (2026-07-09, bd `babelstone-2t16.31.1`)

**In plain English:** §A2 froze *our own* images to cosign-verified digests at promotion. This does the same *freezing* for the **third-party** images the cluster deploys (postgres, kong, redpanda, logto, the k3s control-plane upgrader, …) — a redeploy now pulls the exact bytes we validated instead of whatever a movable tag resolves to that day. The crucial difference: we do **not** sign these images, so there is nothing of ours to verify them against. They are pinned for **reproducibility / supply-chain provenance, not identity verification** — a deliberately weaker guarantee than §A2's resolve → verify → pin.

§A2 generalised §P2's "by digest, never by a movable tag" rule to the first-party container images `image-build.yml` signs. This amendment extends the *digest-pinning* half of that rule — **and only that half** — to the third-party images the deployed overlays render, which §A2 explicitly left out of scope. The distinction is load-bearing and preserved here: third-party images are pinned to an immutable `sha256` digest so the bytes deployed are the bytes validated (reproducibility), but they are **NOT** routed through `cd-pin-images.sh`'s `cosign verify` path — we publish no signature under our identity for `svhd/logto`, `postgres`, `kong`, … so there is no identity to verify against. The first-party `ghcr.io/jhosm/babelstone-*` prefix filter in `cd-pin-images.sh` (§A2) deliberately continues to exclude them.

**Mechanism.** For kustomize-managed images the pin is an `images:` transformer entry (`name` + `newTag` + `digest`, so the render stays legible as `name:tag@sha256:…`) in the *owning* kustomization — base-originated images in `infra/k8s/base`, overlay-only images in the respective overlay. A kustomize transformer rewrites only the resources of the kustomization that declares it, so images the `apps/` layer or an overlay adds (postgres, openbao) are re-pinned at that level to the same digest; CI renders `base`, `overlays/ha` and `overlays/staging` independently, so each carries its own coverage. The bootstrap **k3s automated-upgrade Plan** is not part of any kustomize overlay (an `upgrade.cattle.io/v1` CRD applied once), so it is pinned by editing the manifest directly; because a digest pin is incompatible with system-upgrade-controller's floating `channel:` resolution (the controller keeps advancing `.status.latestVersion` while a pinned image installs a fixed version, re-running the upgrade forever), the Plan is converted from `channel:` to a **deliberate `version:`** plus a `tag@sha256` image — the correct posture for an in-place control-plane binary swap anyway.

**Maintenance.** A digest pin left unmaintained is a staleness hazard, so a scheduled `.github/workflows/cd-thirdparty-digest-audit.yml` job re-resolves each pinned tag with `crane` and opens a PR on drift — a deliberate, human-reviewed bump, never a silent float. Dependabot's `docker` ecosystem parses Dockerfiles only and cannot see a kustomize transformer digest or a Plan `image:`, which is why this is a hand-rolled auditor rather than a Dependabot ecosystem; the *maintenance mechanism* is recorded in [ADR-IC-014](../../integration_concepts/adrs/ADR-IC-014-static-analysis-and-supply-chain-scanning.md) (its supply-chain-tooling home), this ADR owning only the deploy-boundary pin.

This is **additive**: it reverses no part of the Decision or of §A2. Packs are unchanged; first-party images keep their §A2 resolve → verify → pin. Two third-party references discovered broken during this work — `pgbackrest/pgbackrest:2.54.2` (Docker Hub repo absent) and `bitnami/kubectl:1.31` (tag withdrawn in Bitnami's 2025 catalog sunset) — cannot be digest-pinned until they gain a maintained replacement image (tracked in bd `babelstone-2t16.31.4` / `babelstone-2t16.31.3`) and are left unpinned meanwhile: a visible, recorded gap, not a silent one. Recorded in Verifiable commitments below.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- The sealed test-corpus regression — `expected-events.yaml` is *generated* by running the engine against `canonical-instances.yaml` and reproduced at every pack-publish and engine-release (§P5) — is exercised through the separately-owned `PACK_SIM_DEPTH5_BUDGET` depth-5 simulation gate, governed by [ADR-PC-006 §P4](./ADR-PC-006-cue-schema-language.md). This ADR composes with that gate; it does not own it.
- **The pack family-manifest cross-check** (§A1 / [ADR-PC-009 §A1](./ADR-PC-009-per-instance-version-pinning.md)) — the host fails closed at load on a family/schema-version skew between the pinned pack's `families.yaml` and the discovered family modules — is gated by `HOST_PACK_FAMILY_MANIFEST_CROSS_CHECK` (catalogue row 12c). `Live` as `HostModuleLoaderTests` + `PackParserTests`.

Two falsifiable invariants this decision introduces are not yet wired to a Test ID (a deliberate, visible gap per [ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)), to be catalogued under the catalogue's growth provision when the engine load path is implemented:

- **Fail-loud pack load** (§P4): a pack pull or cosign-signature-verify failure at engine startup is fatal — the engine refuses to serve rather than silently degrading; new `pack_version` references trigger a hot pull + verify + cache with the same discipline. No Test ID is wired yet.
- **Generated-not-authored corpus** (§P5): the publish gate regenerates `expected-events.yaml` from the engine rather than accepting a hand-authored file, so a hand-edited corpus cannot land. No Test ID is wired yet.

The §A2 CD-boundary invariant — **verify what you deploy** — is gated by the CD workflow itself rather than an engine fitness function: the hermetic `cd-pin-images.sh --contract` assertion on the push/PR lane proves the resolve → verify → pin path stays wired, and the promote-time `cosign verify` of each resolved `ghcr.io/jhosm/babelstone-*@sha256:…` digest gates the real apply (fail-closed — an unverifiable digest aborts the promotion). No commitment-catalogue Test ID is wired: this is a delivery-pipeline invariant, not an engine replay/fold property.

The §A3 third-party pins carry a **weaker, explicitly-unverified** commitment: they are gated by neither an engine fitness function nor a `cosign verify` (we sign nothing to verify against), only by `kustomize build` rendering an immutable `name:tag@sha256:…` for every third-party image the overlays deploy — except the two recorded-broken references above — and by the scheduled `cd-thirdparty-digest-audit` job (`scripts/cd-thirdparty-digest-audit.py --check`), which also asserts the same image carries the same digest everywhere it recurs and proposes deliberate bumps on drift. Reproducibility, not identity — the distinction from §A2 is the whole point.

---

## Cross-references

- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — CUE is the schema language; pack `schemas/` ship `.cue` files validated by the same Go validator; cosign signing underwrites the validated-in-CI attestation.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — engine runtime is .NET 10; YamlDotNet parses pack YAML; the hand-rolled substrate runs the depth-5 corpus.
- [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) — `pack_version` is a contract column on the event envelope.
- [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — rate-sheet storage; this ADR carries rate-sheet refs only.
- [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) — per-instance pack/schema version pinning; this ADR carries the pinning column, PC-009 carries the migration-event semantics.
- [surface §3.4–§3.10](../feature-design-configuration-surface.md) — pack manifest shape, pinning, distribution/signing (§3.7), sealed test corpus (§3.9), validator interplay (§3.10).
- [ADR-IC-014](../../integration_concepts/adrs/ADR-IC-014-static-analysis-and-supply-chain-scanning.md) — supply-chain scanning; owns the *maintenance* of the §A3 third-party image digest pins (the hand-rolled `cd-thirdparty-digest-audit` workflow Dependabot's docker ecosystem cannot cover), this ADR owning the deploy-boundary pin itself.

---

*Decided 2026-05-23 by jhosm. Supersedes the prior JSON-Schema-context iteration of ADR-PC-007 (removed before acceptance): the pack format is unchanged (signed YAML in OCI) but the `schemas/` files are now `.cue` and validation is CUE, following the ADR-PC-006 schema-language change.*
