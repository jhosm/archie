# ADR-IC-015: Event Catalog Governance Tooling — Backstage

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-07 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md), [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md) |
| Supersedes | [ADR-IC-008](./retired/ADR-IC-008-event-catalog-governance-tooling.md) |
| Resolves | bd `babelstone-ymav` |

---

## Context

This ADR replaces the **portal** half of [ADR-IC-008](./retired/ADR-IC-008-event-catalog-governance-tooling.md). The **specification format** half is unchanged and carried forward verbatim: AsyncAPI 3.0 (a CNCF project, Apache 2.0) remains the canonical, machine-readable business contract for every integration event, one file per event under version control, referencing the registered Avro schema ([ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md)). What changes is which tool renders that catalogue as a navigable portal for humans.

### The audit trail — what was decided, what changed, why the reversal

ADR-IC-008 chose **EventCatalog** as the portal, on the decisive principle that *governance tooling nobody uses is not governance* — a static-site portal that nobody has to operate, rendering the same AsyncAPI files the CI gate validates. Backstage was its named, explicit upgrade path ("when the team grows … Backstage with the AsyncAPI plugin is the natural next step"); Git-native AsyncAPI (no portal) was its named, explicit downgrade path.

EventCatalog passed F1 only **conditionally**: *"required features must be verified to fall within the open-source tier at implementation time; free-tier boundaries can shift."* ADR-IC-008's Residual Risks section named the realisation of that risk and pre-committed both exit paths:

> **License drift:** EventCatalog's commercial tier boundaries can shift. … If they migrate to a paid tier, the exit path is Backstage (no specification change — Backstage imports the same AsyncAPI files) or Git-native AsyncAPI (portal removed, CI validation retained via the AsyncAPI CLI). Re-assess the Core feature surface at implementation time against the features required.

**The implementation re-check (2026-06-07, G.4 / bd `babelstone-ymav`) found the conditional partially realised.** The EventCatalog **portal engine** (`@eventcatalog/core`, `@eventcatalog/cli`, `@eventcatalog/linter`) remains MIT; but the **AsyncAPI generator plugin** (`@eventcatalog/generator-asyncapi`, the component that ingests the AsyncAPI files into the portal) moved to a **dual-licensed (AGPL-3.0 / commercial), license-keyed** model — no longer in the free tier. The catalogue can still be *validated* by the Apache-2.0 AsyncAPI CLI (the governance gate never regressed), but rendering it in EventCatalog now requires either an AGPL-3.0 acceptance or a commercial key.

For a Portuguese-banking estate, AGPL-3.0 on a build-time documentation generator is an avoidable licence-compliance surface (network-copyleft reach is contested for a static-site build step, and the commercial alternative reintroduces the paid dependency F1 forbids). Rather than carry that ambiguity, **this ADR takes ADR-IC-008's own prescribed Backstage exit path**, formally — so the estate keeps a *rendered, navigable* portal (the discoverability requirement document 08 set, which Git-native alone does not meet) with **no AGPL/commercial exposure** (Backstage is Apache-2.0, CNCF). Git-native AsyncAPI remains the degenerate fallback if the Backstage host is never deployed: the gate and the AsyncAPI files alone already satisfy the *existence* requirement.

This is a portal change, not a specification change — exactly the low-cost migration ADR-IC-008's S3 analysis predicted. No AsyncAPI file changes shape because the portal changed.

### Candidates re-scored

ADR-IC-008 evaluated four candidates (Git-native AsyncAPI, EventCatalog, Backstage, Confluent Stream Governance) against the full [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) framework. That table is not restated here. This is a **delta-evaluation**: only the rows that changed since 2026-05-17 are re-scored.

---

## Evaluation

### Delta — only what changed since ADR-IC-008

The full hard-filter and soft-criteria tables live in [ADR-IC-008 §Evaluation](./retired/ADR-IC-008-event-catalog-governance-tooling.md#evaluation). The F2 (regulatory fit) and S1–S4 prose for every candidate stand unchanged. The **only** movement is one F1 cell, dated:

#### F1 · Cost / licensing — re-scored 2026-06-07

| Candidate | Licence (re-checked 2026-06-07) | Verdict | Change vs 2026-05-17 |
|---|---|---|---|
| Git-native AsyncAPI | AsyncAPI spec + CLI: Apache 2.0 | **Pass** | unchanged |
| **EventCatalog** | Portal engine MIT, but the AsyncAPI **generator plugin** (`@eventcatalog/generator-asyncapi`) is now dual-licensed **AGPL-3.0 / commercial, license-keyed** — the feature required to render *these* files is no longer in the free, permissive tier | **Fail** — the conditional named in ADR-IC-008 F1 is realised: the required portal-ingest feature left the open-source permissive tier | **Pass (conditional) → Fail** (drift realised) |
| **Backstage** | Apache 2.0; CNCF graduated | **Pass** | unchanged |
| Confluent Stream Governance | Proprietary; Confluent Platform paywall | **Fail** | unchanged |

*Date of licence re-assessment: 2026-06-07.* EventCatalog's F1 conditional pass is now a **Fail** — the mitigation it required ("verify the features remain in the open-source tier at implementation time") could not be met. Per [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md), a realised conditional is restated in Consequences / Residual risks (see below).

F2 is unchanged: Backstage persists catalog metadata (service ownership, team membership) in PostgreSQL — standard personnel data that must be entered in the GDPR data inventory; the AsyncAPI files themselves carry no PII. This is the **Pass (conditional)** ADR-IC-008 recorded for Backstage; it stands.

### Soft criteria — what the EventCatalog → Backstage swap costs and buys

Backstage's S1–S4 are exactly as ADR-IC-008 analysed them. The decisive re-weighting is **S1 vs S3**:

- **S1 (operational complexity) — worse, but bounded.** ADR-IC-008 rejected Backstage on S1: it is a Node.js service + PostgreSQL backend, "a freight elevator to go up one floor" for a 1–2 person team rendering ~15 events. That objection is real and is the reason **the Backstage host deployment is deferred** (see Decision): the descriptors ship now, the running portal is platform work. The gate does not depend on it, so the operational floor for *governance* stays at zero — only the optional rendered portal carries the operational cost, and only when someone chooses to stand it up.
- **S3 (exit cost) — the decisive criterion, now binding.** ADR-IC-008's whole portal argument rested on AsyncAPI files being the portable asset no portal can hold hostage. That property is exactly what makes this supersession cheap: Backstage imports the same files via `catalog-info.yaml` API-entity descriptors. The EventCatalog `eventcatalog.config.js` is deleted with nothing lost; the AsyncAPI files are untouched.
- **S4 (community / longevity) — strictly better.** Backstage is CNCF *graduated* (originally Spotify); EventCatalog is single-vendor with a commercial tier whose incentive pressure is precisely what realised the drift. Apache-2.0 + foundation governance removes the single-vendor monetisation risk that bit here.

Git-native AsyncAPI (no portal) remains a valid Pass on every criterion and is retained as the **fallback posture**: until the Backstage host exists, the estate *is* operating Git-native (files + gate + GitHub's file renderer), and that already meets the governance *existence* bar. Backstage is what restores the *discoverability* bar document 08 also sets.

---

## Decision

**Chosen: Backstage** as the catalogue portal, with **AsyncAPI 3.0 retained as the governance format**. The four AsyncAPI files (the term-deposit events the engine emits today) are the source of truth; Backstage renders them.

The decisive reason is the realised licence drift: EventCatalog's required portal-ingest feature left the permissive open-source tier (AGPL-3.0 / commercial), failing F1. Of ADR-IC-008's two pre-committed exit paths, **Backstage** is taken over Git-native because it preserves the *discoverability* requirement (a rendered, navigable, searchable portal) that document 08 makes load-bearing, at no AGPL/commercial cost — Backstage is Apache-2.0 and CNCF-graduated. Git-native AsyncAPI is retained as the degenerate fallback that already holds while the Backstage host is undeployed.

### The governance contract (present tense, single read)

These principles are the live contract. They fold the implementation reality discovered during G.4 into a single statement — there is no amendment archaeology to replay.

1. **AsyncAPI is the governance source of truth.** Every integration event has exactly one AsyncAPI 3.0 file at `contracts/catalog/events/<EventName>.asyncapi.yaml`. The file is authoritative for the *who / why / when* of the event; the schema registry ([ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md)) is its structural-validation layer; the CI gate is its enforcement mechanism; Backstage is *only* the rendering layer. No governance record lives outside these files and the registry. Required `info` governance fields: `x-owner`, `x-owner-contact`, `x-status` (closed enum `active | deprecated | sunset`), `x-gdpr-legal-basis`, plus `x-authorized-consumers`.

2. **Payloads reference the governed Avro `.avsc`, never restate it.** Each message's `payload.schema.$ref` points at the governed `contracts/avro/**.avsc` **on disk by relative path** — so the catalogue can never drift from `contracts/avro/`, and the fast PR gate resolves it *hermetically* (no live registry). The registry subject is recorded as **`x-schema-registry-subject`** and must reconstruct exactly as `{namespace}.{name}-value` from the referenced `.avsc` ([ADR-IC-002 §P1](./ADR-IC-002-schema-format-and-registry.md) subject rule), agreeing with the G.3 Avro-compatibility gate. (An embedded registry-URL `$ref` is deliberately *not* used: it would force the PR lane to reach a live registry, the CI-fragility risk this decision avoids.)

3. **Required CloudEvents headers document the wire that exists.** Each message's `headers` block lists, as `required`, the eight CloudEvents 1.0 Binary-Content-Mode attributes the outbox relay emits today (`ce_specversion`, `ce_id`, `ce_source`, `ce_type`, `ce_time`, `ce_datacontenttype`, `ce_subject`, `ce_aggregatetype` — `OutboxDrainer.BuildHeaders`). The lineage attributes `ce_correlationid` / `ce_causationid` ([doc 01 Primitive 4](../01-the-six-primitives.md)) and the W3C `traceparent` / `tracestate` headers are documented as **optional** until the engine emits them (a later observability epic), at which point they move to `required` in the same change that ships the emission — the catalogue documents the real wire, not an imagined one.

4. **The PR gate is hermetic (§P2-equivalent).** `scripts/asyncapi-catalog-validate.sh` is the fast (<30s) PR lane: Apache-2.0 AsyncAPI CLI only, **no live Schema Registry, no running portal**. It asserts AsyncAPI validity + required governance fields (1), the on-disk subject well-formedness (2), the **orphan check** (every governed `.avsc` under `contracts/avro/` is `$ref`'d by some catalogue file — no integration-event schema without an entry), the GDPR tombstone field (5), the breaking-change diff (6), and the deprecation notice period (7).

5. **GDPR tombstone field is mandatory on compacted topics.** Any channel with `x-compacted: true` must carry `x-tombstone-contract` (consumers must tolerate null-payload tombstone records on compacted topics — [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md) / [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md) erasure). A compacted channel without it fails the gate.

6. **Breaking changes are detected automatically, approved explicitly.** Every modified AsyncAPI file is diffed (`asyncapi diff --type breaking`) against its `origin/main` version; an AsyncAPI-structural breaking change (channel / operation / message removal or rename) fails the build unless the file carries `info.x-breaking-change-approved: true` (with the `x-breaking-change-rfc` / `x-breaking-change-consumers-notified` record). Field-level wire-schema breaks are the **complementary** `scripts/avro-compat-check.sh` (G.3) gate in the same job; the two compose — neither alone is the full breaking-change guard.

7. **Deprecation lifecycle is machine-readable.** `x-status: deprecated` requires `x-deprecated-date` and an `x-sunset-date` at least 180 days later; the gate rejects a shorter notice.

8. **Merge-time schema-registry reconciliation.** On push to main, `scripts/asyncapi-catalog-reconcile.sh` registers the working-tree `.avsc` set into a throwaway Redpanda built-in Schema Registry and asserts every `x-schema-registry-subject` resolves there — so the catalogue can never document an event whose registry subject is missing. This live check never runs on the PR lane (it needs a reachable registry); the PR lane checks subject well-formedness only.

9. **Backstage is the portal — descriptors ship now, the host is deferred.** Backstage imports the same AsyncAPI files via a `catalog-info.yaml` API-entity descriptor (`kind: API`, `spec.type: asyncapi`, `spec.definition.$text` pointing at each `events/*.asyncapi.yaml`). **The descriptor ships in this change**; the **Backstage host deployment is explicitly deferred to platform work** (out of scope here — bd `babelstone-s4ol.1`). Until the host exists, the estate operates Git-native (files + gate + GitHub's renderer), the documented fallback posture.

### Rejected

- **EventCatalog (the prior choice)** — rejected on F1: its AsyncAPI-generator plugin moved to AGPL-3.0 / commercial license-keyed (re-checked 2026-06-07), failing the zero-cost / permissive-licence filter. This is the realisation of the conditional pass ADR-IC-008 recorded.
- **Git-native AsyncAPI (no portal)** — not rejected, *retained as the fallback*. It meets the governance *existence* bar but not the *discoverability* bar document 08 sets; Backstage adds the latter at no licence cost. It is the active posture until the Backstage host is deployed.
- **Confluent Stream Governance** — rejected on F1 (Confluent Platform paywall), unchanged from ADR-IC-008.

---

## Consequences

**What this choice makes easier:**

- The governance gate is unchanged and unaffected by the licence drift: it always ran on the Apache-2.0 AsyncAPI CLI, never on EventCatalog. Renaming the scripts (`eventcatalog-* → asyncapi-catalog-*`) makes that independence legible.
- Backstage's `catalog-info.yaml` descriptors are plain Backstage entities — when the platform team stands up a Backstage instance for the broader estate, these events register with zero catalogue-specific glue. The portal is one host deployment away, with no AGPL/commercial key in the path.
- The migration touched no AsyncAPI file shape — the S3 low-exit-cost property ADR-IC-008 banked on paid off exactly as predicted.

**What this choice makes harder or impossible:**

- A *rendered, searchable* portal is not live today — only the descriptors are. Until the Backstage host is deployed (bd `babelstone-s4ol.1`), discovery is Git-native (file browser + `git grep`), which is the *existence*-bar posture, not the *discoverability*-bar posture. This is a deliberate, tracked deferral, not a silent gap.
- Operating Backstage later is a real cost (Node.js service + its own PostgreSQL) — the S1 objection ADR-IC-008 raised stands; it is paid only when the host is deployed, and is then platform-team work, not a governance prerequisite.

**Residual risks:**

- **Realised licence drift (restated per [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md)).** EventCatalog's conditional F1 pass became a Fail when `@eventcatalog/generator-asyncapi` went AGPL-3.0 / commercial. The lesson generalises: any tool whose required feature sits in a vendor's open-source tier carries this risk; the mitigation that held here is the format-portability (AsyncAPI files are the asset, not the portal) that kept the exit cheap. Backstage's Apache-2.0 + CNCF-graduated posture removes the single-vendor monetisation pressure for the portal going forward.
- **Backstage GDPR surface (Pass-conditional carried from ADR-IC-008 F2).** When the Backstage host is deployed, its user / team identity data (for ownership and access control) is personnel data that **must** be entered in the GDPR data inventory. The descriptors shipped here hold no such data; the obligation attaches at host-deployment time (bd `babelstone-s4ol.1`).
- **Example-payload PII.** AsyncAPI `examples:` blocks must use synthetic data only; a real `client_id` / IBAN / name in a version-controlled example is a GDPR incident ([ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
- **CI-gate fragility.** The "no event without catalogue entry" gate must validate the AsyncAPI file set fast and hermetically, never require a portal build or live registry on the PR lane — preserved by the two-lane split (PR lane hermetic; reconcile on main only).

---

## Verifiable commitments

> No executable commitments are catalogued for this decision. The §P1–§P8-equivalent governance principles above are realised by the CI gate scripts (`scripts/asyncapi-catalog-validate.sh`, `scripts/asyncapi-catalog-reconcile.sh`) and the `contracts` CI job, not by a row in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) — the superseded [ADR-IC-008](./retired/ADR-IC-008-event-catalog-governance-tooling.md) carried no catalogue rows, and this supersession adds none. The gate is the executable artefact; when a load-bearing catalogue commitment is added for the event-catalogue surface it will be seeded here as a Test-ID reference.

---

## Amendment — 2026-06-16: register `ce_autorenewalpolicy`, the first CloudEvents extension attribute, and the generic event-declared promotion mechanism

**In plain English:** until now the relay published a fixed set of eight CloudEvents headers. We added a generic way for a domain event to declare extra routing labels that the relay turns into `ce_<key>` headers, and the first one is `ce_autorenewalpolicy` on `DepositMatured` (so a renewal saga can filter on the header without reading the payload). This amendment records that header in the catalogue, exactly as governance clause 3 requires the catalogue to document the wire that actually exists.

Governance clause 3 enumerates the eight Binary-Content-Mode attributes the relay emits today and requires the catalogue to "document the real wire, not an imagined one." The generic, family-agnostic CloudEvents-extension promotion seam [ADR-IC-018 §P5](./ADR-IC-018-family-owned-saga-modules.md) anticipates is now realised: a domain event declares CloudEvents extension attributes (`DomainEvent.IntegrationHeaders`), the engine carries them on the outbox row's `integration_headers` column ([ADR-IC-004 amendment 2026-06-16](./ADR-IC-004-outbox-pattern-mechanism.md)), and the relay (`OutboxDrainer.BuildHeaders`) promotes each entry to a `ce_<key>` header alongside the standard eight. The relay names no key and knows no family — it copies whatever the event declared.

- **`ce_autorenewalpolicy` is registered** as the first CloudEvents **extension attribute**: the deposit's renewal policy (`NONE | SAME_TERM_CURRENT_RATE | SAME_TERM_SAME_RATE`), promoted from `DepositMatured`'s `auto_renewal_policy` payload field. It is documented as an **optional** header on `DepositMatured.asyncapi.yaml` (present only when the deposit declared a policy; absent for pre-seam streams) — consistent with clause 3's discipline that an attribute the wire only sometimes carries is optional. A structural enum token, never PII (ADR-PC-004 §P2).
- **The mechanism is generic.** Any future event may declare further extension attributes the same way; each is registered in that event's catalogue file as the change that ships its emission lands — the catalogue keeps documenting the real wire. Clause 3's enumerated set of eight is unchanged; this adds an optional ninth attribute on the one event that emits it.

This is additive — the eight required headers and the Decision (AsyncAPI-as-source-of-truth governed by a hermetic CI gate) are unchanged — so it does not supersede the ADR (ADR-PC-020 §D3/§D5). The `asyncapi-catalog-validate` gate confirms the change is non-breaking (the new header is optional, not in `required`).

---

## Amendment — 2026-06-26: widen the catalogued surface to OpenAPI (REST) API entities, governed alongside the AsyncAPI event entities

**In plain English:** until now this ADR's catalogue described only **events** (AsyncAPI files referencing Avro schemas). We are adding a second kind of catalogue entry — **synchronous REST APIs**, described in OpenAPI — so the same Backstage catalogue holds both planes. This amendment records that widening; the event-governance Decision above is untouched. The new format and the tools that govern it are decided in [ADR-IC-020](./ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md).

[ADR-IC-020](./ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) adopts **OpenAPI 3.1** as a *second* canonical specification format alongside AsyncAPI/Avro, for the estate's synchronous REST plane (the APIs behind Kong, [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)). Backstage already models a REST API natively: a `catalog-info.yaml` API-entity descriptor with **`spec.type: openapi`** (the REST analogue of the `spec.type: asyncapi` descriptor Decision §9 ships for each event). This amendment **widens this ADR's catalogued surface** so the Backstage descriptor set may carry `spec.type: openapi` API entities (pointing at each governed OpenAPI file) **alongside** the `spec.type: asyncapi` event entities it already carries.

- **The event-governance Decision is unchanged.** Every clause above (AsyncAPI as the event source of truth; payload references the governed Avro `.avsc`; the eight required CloudEvents headers; the hermetic AsyncAPI CI gate; the §P3 reverse-orphan biconditional) continues to hold **in full** for the event plane. This amendment adds a second, parallel entity *kind* to the catalogue; it does **not** alter, relax, or extend any event clause.
- **The two planes' gates stay disjoint.** The OpenAPI files are validated by their own gate (Spectral + oasdiff, [ADR-IC-020](./ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md)), **not** by `asyncapi-catalog-validate.sh`, which keeps governing only `contracts/catalog/events/**.asyncapi.yaml`. Critically, the OpenAPI surface is a **REST-contract** record and carries **no event-promotion semantics**: it is disjoint from [ADR-IC-017](./ADR-IC-017-integration-event-promotion-criterion.md)'s *catalogued ⇔ on the bus* biconditional and its reverse-orphan gate — an OpenAPI entry never implies anything about the durable event bus.
- **Why amend, not supersede (ADR-PC-020 §D3/§D5).** Adding a REST plane **widens** the catalogued surface; it does not contradict or reverse any event-governance decision, so an additive amendment is the correct §D3 instrument and the §D5 immutability of the Decision above is preserved (no in-place edit).
