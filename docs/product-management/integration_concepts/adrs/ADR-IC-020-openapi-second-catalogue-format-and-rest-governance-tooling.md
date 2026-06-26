# ADR-IC-020: OpenAPI as a Second Catalogue Specification Format — Spectral + oasdiff for REST Governance

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-26 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md), [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md), [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) |
| Resolves | bd `babelstone-ax0b.1` |

---

## In plain English

Until now our governed catalogue only describes **events** — the asynchronous facts the engine puts on the message bus, written as AsyncAPI files referencing Avro schemas. But the estate also has **synchronous REST APIs** (the public edge, the engine's read surface, the process-status endpoint) and those have no governed, machine-readable contract anywhere. This ADR adds **OpenAPI 3.1** as a second canonical specification format alongside AsyncAPI/Avro, so a REST API is described the same disciplined way an event is. It then picks the two free, self-contained tools that govern those OpenAPI files in CI — **Spectral** to lint them and enforce our governance fields, and **oasdiff** to catch breaking changes — the REST-side equivalents of the AsyncAPI CLI gate we already run for events. Both are Apache-2.0 and run with no paid tier and no network, which is exactly the licence posture that forced our last portal swap.

This is a **general integration-architecture decision**: any estate that fronts both an event bus and a synchronous REST surface needs a governed contract format for each, and the format choice should not be entangled with the event-promotion rules. The concrete API names and Kong routes below are this repository's running example — a Portuguese term-deposit estate — used only to make the decision legible; they are illustrations, not part of the decision.

## Context

[ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) made **AsyncAPI 3.0** the canonical, machine-readable business contract for every integration **event**, one file per event under `contracts/catalog/events/`, each referencing the governed Avro `.avsc` ([ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md)). That decision governs the **asynchronous** plane only. The estate also exposes a **synchronous** plane — REST APIs behind Kong ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)) — and that plane has **no governed specification format and no CI gate**. There is no equivalent of the AsyncAPI catalogue for REST: a REST endpoint can be added, changed, or removed with no machine-readable contract, no required governance fields, no breaking-change diff.

[ADR-IC-002 §S2](./ADR-IC-002-schema-format-and-registry.md) already anticipated this gap: it notes that a broader schema surface (Apicurio supports "Avro, Protobuf, JSON Schema, OpenAPI, AsyncAPI") "becomes relevant" when the estate grows beyond a pure Kafka context, and that JSON Schema is "appropriate for REST API contracts" but wrong for the durable event stream. OpenAPI is the standard, tool-rich realisation of that REST-contract surface; this ADR adopts it.

This decision has two coupled parts, the same way [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) bundled a format choice (AsyncAPI) with the tools that govern it:

1. **Format** — adopt **OpenAPI 3.1** as a *second* canonical specification format alongside AsyncAPI 3.0 / Avro. AsyncAPI keeps describing the event plane unchanged; OpenAPI describes the REST plane.
2. **Tooling** — select the REST-side governance tools (lint + governance-field enforcement, and breaking-change diff), evaluated through the [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) framework, with the **F1 licence-posture hard filter re-run deliberately** — because a licence drift on a documentation/governance tool is exactly what forced the EventCatalog → Backstage supersession ([ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)), and the lesson is to choose a permissive, hermetic, free tool from the start.

### What this decision is — and is *not*

> This is a **format-and-tooling decision** for the synchronous plane. It adopts a contract format and picks the CI tools that govern it; it does **not** decide *which* REST APIs exist, author any OpenAPI file, or build the gate's wiring (those are the sibling implementation issues bd `babelstone-ax0b.2` / `.3`). The [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) F1 (cost/licensing) hard filter is the decisive one and is re-run in full; F2 (regulatory fit) is light because these are build-time CLIs that touch no production data; the soft criteria S1–S4 settle the tie.

### What this decision must NOT entangle

[ADR-IC-017](./ADR-IC-017-integration-event-promotion-criterion.md) made *catalogued ⇔ on the bus* a hermetic biconditional for **events**: an event is published **iff** it has an AsyncAPI/`.avsc` catalogue entry (the catalog-gated relay, `INTEGRATION_EVENT_CATALOG_GATED`; the reverse orphan check, `NO_UNCATALOGUED_EVENT_ON_BUS`). That biconditional is **specific to the event-promotion plane** — it answers "should this event be on the durable bus?". A REST API is **not** promoted to a durable bus and has no relay; its OpenAPI contract carries **no promotion semantics whatsoever**. The OpenAPI catalogue surface is therefore **separate from, and must not be entangled with, the ADR-IC-017 event-promotion biconditional or its reverse-orphan gate** — the REST gate (bd `babelstone-ax0b.2`) reconciles OpenAPI paths against Kong routes ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)), it does **not** assert anything about the event bus. Keeping the two planes' gates disjoint is a load-bearing requirement of this decision.

## Evaluation

The format adoption (OpenAPI 3.1) is the standard, near-universal REST-contract IDL; the live question the [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) framework settles is the **tooling**. Two governance functions need a tool each:

- **Lint + governance-field enforcement** — the OpenAPI equivalent of the AsyncAPI CLI's `validate` + required-`info`-field checks: assert the file is a well-formed OpenAPI 3.1 document and carries the governance fields (owner, status, GDPR legal basis — mirroring [ADR-IC-015 Decision §1](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)).
- **Breaking-change diff** — the OpenAPI equivalent of `asyncapi diff --type breaking`: detect a backward-incompatible change to a REST contract (a removed path, a removed required response, a narrowed type) against `origin/main`.

### Candidates

| # | Candidate | Role | Licence |
|---|---|---|---|
| A | **Spectral** (`@stoplight/spectral-cli`) | lint + custom governance rules | Apache-2.0 |
| B | **oasdiff** | breaking-change diff | Apache-2.0 |
| C | **Redocly CLI** (`@redocly/cli`) | lint + (paid) breaking-change diff | MIT (CLI core) |
| — | Swagger / OpenAPI Generator validators | validity only (no governance rules / no breaking diff) | Apache-2.0 |

The lint and diff roles are filled by **two** tools because no single permissive, hermetic CLI does both well: Spectral is the de-facto OpenAPI linter with a custom-rule engine (the governance-field enforcement needs custom rules), but it does **not** do semantic breaking-change diff; oasdiff is the purpose-built OpenAPI breaking-change differ. This mirrors the event plane, where validity/governance and the breaking diff are also two concerns in one gate (`asyncapi validate` + `asyncapi diff` + the complementary `avro-compat-check.sh`).

### Hard filter results

#### F1 · Cost / licensing — re-run deliberately (the EventCatalog lesson)

| Candidate | Licence (checked 2026-06-26) | Verdict |
|---|---|---|
| **Spectral** (`@stoplight/spectral-cli`) | **Apache-2.0**; the CLI and the rule engine are fully open-source; no license key, no paywalled rules for our use (validity + custom governance rules) | **Pass** |
| **oasdiff** | **Apache-2.0**; a single self-contained Go binary; breaking-change detection is in the free, open-source core (no hosted tier required) | **Pass** |
| **Redocly CLI** | CLI core **MIT**, but the **breaking-change diff** (`openapi diff` / the change-management surface) is gated behind **Redocly's commercial/hosted tier** — the feature we need for the diff role is not in the free, permissive CLI | **Fail** — the required breaking-change feature sits in a paid tier; this is precisely the EventCatalog failure mode ([ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)): a required governance feature behind a commercial wall |

*Date of licence assessment: 2026-06-26.* The F1 re-run is the whole point: ADR-IC-015 superseded ADR-IC-008 because EventCatalog's required generator plugin left the permissive free tier (AGPL-3.0 / commercial, license-keyed). Choosing Spectral + oasdiff — both Apache-2.0 with the required features in the open-source core — avoids re-importing that risk on the REST plane from day one.

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | Assessment | Verdict |
|---|---|---|
| Spectral | A build-time linter; reads OpenAPI files from source control; touches no production data, stores no state, runs hermetically. No data-residency, erasure, or audit-trail surface. The `examples:` in OpenAPI files must be synthetic only — the same PII discipline AsyncAPI examples carry ([ADR-IC-015 Residual risks](./ADR-IC-015-event-catalog-governance-tooling-backstage.md), [ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) | **Pass** |
| oasdiff | A stateless CLI diffing two OpenAPI documents; no data plane; nothing to comply | **Pass** |

F2 is immaterial here — nothing in either tool processes regulated data; both are hermetic build-time CLIs. The PII obligation attaches to the *authored OpenAPI files* (synthetic examples only), not to the tools.

### Soft criteria

**Spectral — CHOSEN (lint + governance fields).** *S1 (operational complexity):* a single `npx @stoplight/spectral-cli lint <file>` invocation, no daemon, no service — the same `npx`-driven shape the AsyncAPI gate already uses (Node is already a runner dependency), so the operational floor does not rise. Governance fields are enforced with a `.spectral.yaml` ruleset (custom rules asserting `info.x-owner`, `info.x-status`, `info.x-gdpr-legal-basis`, etc.), which is declarative and version-controlled. *S2 (ecosystem coherence):* Spectral is *the* widely-used OpenAPI/AsyncAPI linter — it can even lint AsyncAPI, so the team learns one linter idiom across both planes; it composes with CI with no bespoke glue. *S3 (exit cost):* near-zero — the OpenAPI files are the portable asset (the same property AsyncAPI files have), and a `.spectral.yaml` ruleset is a small declarative file replaceable by any other linter; Spectral owns no proprietary format. *S4 (community/longevity):* Stoplight/SmartBear-backed, large user base, the de-facto standard linter, active releases. **Decisive reason:** it is the only permissive, hermetic, custom-rule-capable OpenAPI linter — custom rules are *required* for governance-field enforcement, which a validity-only validator (Swagger) cannot do, and it imports no commercial-tier dependency.

**oasdiff — CHOSEN (breaking-change diff).** *S1:* a single self-contained Go binary (or `docker run`), one `oasdiff breaking <base> <revision>` call — no service, no config required for the default ruleset; trivially pinnable to a release for reproducible CI. *S2:* purpose-built for OpenAPI breaking-change classification, exits non-zero on a breaking change (the gate idiom), emits JSON for machine consumption — composes with CI exactly like `asyncapi diff`. *S3:* near-zero — it diffs standard OpenAPI files and owns no format; replaceable. *S4:* actively maintained, the most widely-used open-source OpenAPI breaking-change differ, healthy release cadence. **Decisive reason:** it is the purpose-built, Apache-2.0, hermetic OpenAPI breaking-change differ — the REST-plane analogue of `asyncapi diff --type breaking` — with the breaking-diff feature in the free core, unlike Redocly.

**Redocly CLI — rejected.** Its CLI core is MIT and its linter is capable, but the **breaking-change diff feature we need is in Redocly's commercial/hosted tier**, not the free permissive CLI. **Decisive reason for rejection:** adopting it would re-import the exact failure mode that forced the EventCatalog → Backstage supersession — a required governance feature behind a paywall, failing F1. Even using only its (free) linter would split the linter choice from the differ for no benefit over Spectral, which is the stronger linter for our custom-rule need.

## Decision

**Adopt OpenAPI 3.1 as a second canonical specification format** alongside AsyncAPI 3.0 / Avro, and **select Spectral (`@stoplight/spectral-cli`, Apache-2.0) for lint + governance-field enforcement and oasdiff (Apache-2.0) for breaking-change diff** as the REST-plane governance tooling.

1. **OpenAPI 3.1 is the canonical REST-contract format.** A synchronous REST API in the estate has exactly one OpenAPI 3.1 specification file, governed and version-controlled, the way an integration event has exactly one AsyncAPI file. AsyncAPI 3.0 remains the canonical format for the **event** plane, unchanged; the two coexist as the estate's two specification formats (one per plane). OpenAPI 3.1 (over 3.0) because its JSON-Schema-2020-12 alignment is the cleanest fit for the estate's JSON-Schema-typed REST payloads ([ADR-IC-002 §S2](./ADR-IC-002-schema-format-and-registry.md) — JSON Schema is the appropriate REST-contract typing).

2. **Spectral lints the OpenAPI files and enforces the governance fields.** The REST-plane analogue of the AsyncAPI CLI's `validate` + required-`info`-field checks: a `.spectral.yaml` ruleset asserts OpenAPI 3.1 validity plus the governance fields (`info.x-owner`, `info.x-owner-contact`, `info.x-status`, `info.x-gdpr-legal-basis` — mirroring [ADR-IC-015 Decision §1](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)), hermetically (`npx`, no network).

3. **oasdiff detects breaking changes.** The REST-plane analogue of `asyncapi diff --type breaking`: every modified OpenAPI file is diffed against its `origin/main` version; a breaking change (removed path/operation, removed required response, narrowed type) fails the build unless explicitly approved — the same explicit-approval discipline [ADR-IC-015 Decision §6](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) applies to AsyncAPI.

4. **The REST gate is hermetic and plane-separate.** Like the AsyncAPI gate, the OpenAPI gate runs on free CLIs with no live service. **It governs only the synchronous plane** — it reconciles OpenAPI paths against Kong routes ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md), the sibling implementation issue bd `babelstone-ax0b.2`), and it is **disjoint from the [ADR-IC-017](./ADR-IC-017-integration-event-promotion-criterion.md) event-promotion biconditional and its reverse-orphan gate**. An OpenAPI entry is a REST-contract record, **not** a bus-promotion record; nothing about an OpenAPI file says anything about the event bus, and the REST gate asserts nothing about relay-capability.

5. **The same no-PII-in-examples discipline applies.** OpenAPI `examples:` use synthetic data only; a real `client_id` / NIF / IBAN / name in a version-controlled example is a GDPR incident ([ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md), [ADR-IC-015 Residual risks](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)).

### Rejected

- **Redocly CLI** — rejected on F1: the breaking-change diff feature we need sits behind Redocly's commercial/hosted tier, re-importing the EventCatalog paywall failure mode. The (free, MIT) linter alone offers no advantage over Spectral for the custom-rule governance need.
- **Swagger / OpenAPI Generator validators** — rejected: validity-only, no custom-rule governance-field enforcement, no semantic breaking-change diff. They cannot fill either governance role.

## Consequences

**Easier:**
- The synchronous REST plane gets the same governed-contract discipline the event plane already has — a machine-readable contract per API, required governance fields, an automatic breaking-change gate. No REST endpoint reaches the edge without a reviewed OpenAPI contract.
- The licence posture is sound from day one: both tools are Apache-2.0 with the required features in the open-source core, so the REST plane does not re-import the EventCatalog drift risk.
- The gate is hermetic and CLI-only — the same fast-PR-lane property the AsyncAPI gate has; no new service to operate.

**Harder / slower (by design):**
- A second specification format is a second thing to author and keep governed. The friction is intentional: it is the cost of a governed REST surface, matched against the alternative of an ungoverned one.
- Two tools (Spectral + oasdiff) rather than one — a small operational surface, mitigated by both being pinnable, hermetic CLIs that compose into one gate, exactly as the event plane already runs `asyncapi validate` + `asyncapi diff` + `avro-compat-check.sh`.

**Residual risks:**
- **Licence drift (the standing risk this ADR re-checked).** Any tool's required feature can later leave its free/permissive tier — the lesson of [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md). The mitigation that held there holds here: the OpenAPI files are the portable asset, and a `.spectral.yaml` ruleset is replaceable, so swapping either tool is a low-exit-cost change. The licence assessment is dated (2026-06-26) and should be re-checked at gate-implementation time (bd `babelstone-ax0b.2`).
- **Plane entanglement.** The one structural risk specific to this decision is letting the OpenAPI gate drift into asserting event-promotion semantics. Decision §4 names the disjointness as load-bearing; the gate-implementation issue (bd `babelstone-ax0b.2`) must keep the REST gate's reconciliation target as Kong routes ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)), never the relay or the catalogue's event set.
- **OpenAPI 3.1 tool maturity.** OpenAPI 3.1's JSON-Schema-2020-12 alignment is newer than 3.0; both Spectral and oasdiff support 3.1, but a future authoring tool may lag. Mitigated by 3.1 being the current standard with broad and growing support.

## Verifiable commitments

> No executable commitments are catalogued for this decision *here*. This ADR adopts a format and selects the REST governance tools; the load-bearing CI gate they realise — OpenAPI validity + governance-field enforcement (Spectral), breaking-change diff (oasdiff), and the OpenAPI-path ⇔ Kong-route reconciliation — is the deliverable of the sibling implementation issue bd `babelstone-ax0b.2`, which seeds the corresponding Test IDs into the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) as it lands the gate. The gate scripts will be the executable artefacts (the REST-plane analogue of `scripts/asyncapi-catalog-validate.sh`), kept **disjoint** from the [ADR-IC-017](./ADR-IC-017-integration-event-promotion-criterion.md) event-promotion gates (`INTEGRATION_EVENT_CATALOG_GATED`, `NO_UNCATALOGUED_EVENT_ON_BUS`) per Decision §4. Until then this is a deliberate, listed hole — visibility is the point.
