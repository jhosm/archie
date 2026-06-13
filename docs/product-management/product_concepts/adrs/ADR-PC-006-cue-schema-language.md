# ADR-PC-006: Family-Schema Language and Validator Runtime — CUE + Purpose-Built Go Validator

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (engine language and framework), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (pack manifest format — CUE schemas ship inside the pack), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers for depth-5 simulation) |
| Resolves | bd `archie-10r.7` (ADR-PC-006: Family-schema language and validator runtime) |

---

## Context

The engine's configuration surface ([feature-design-configuration-authoring](../feature-design-configuration-authoring.md), [feature-design-configuration-surface](../feature-design-configuration-surface.md)) requires a **family-schema language** that variant authors write against, and a **validator runtime** that enforces five validator depths ([authoring §5](../feature-design-configuration-authoring.md)):

| Depth | Budget | Check |
|---|---|---|
| 1 syntactic | < 1 s | Variant YAML parses to the schema's structural shape |
| 2 type-check | < 5 s | Every field's type and range matches the schema; pack-bound fields resolve to a known primitive in the pinned pack |
| 3 pack compliance | < 10 s | Variant respects the pack's bounds (e.g. `tan_basis_points <= pack.max_consumer_rate_bps`) |
| 4 regulatory coherence | < 10 s | Cross-field invariants required by regulation (e.g. PT pack rejects Act/365 for a deposit; payment cadence consistent with term) |
| 5 simulation | < 30 s (CI) | Engine primitive code over the sealed pack test corpus produces the expected event sequence |

Depths 1–4 run **synchronously at variant-commit time** in the PM author's editor (pre-commit hook) and on every PR; aggregate budget < 30 s. Depth 5 is deferred to CI ([authoring §5](../feature-design-configuration-authoring.md)).

The schema language is the boundary between two audiences: PM/product authors who write variant YAML, and engine code that interprets variants. [authoring §9.5](../feature-design-configuration-authoring.md) is explicit that there is **no DSL escape hatch** — the schema language must encode pack compliance and regulatory coherence as *declarative* constraints, not as runtime-evaluated procedure. The source design notes name the candidate space directly: [authoring §1](../feature-design-configuration-authoring.md) describes family schemas as "typed schemas (e.g. CUE / JSON Schema with a domain layer on top)", and [surface §3.2](../feature-design-configuration-surface.md) treats the schema as a typed contract over union types, optional fields, range-bounded scalars, and pack-bound primitives.

The engine runtime is C# .NET 10 with a **hand-rolled** event-sourcing core ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)). This ADR therefore must decide both the *language* and the *integration shape* of its validator against a .NET host — a constraint that materially shapes the trade-offs below, because the most expressive candidate is not a .NET-native one.

**Candidates evaluated** ([bd archie-10r.7](../04-open-questions.md): JSON Schema, CUE, Pkl, Avro-as-config, hand-rolled typed DSL):

| # | Candidate | Notes |
|---|---|---|
| A | **CUE, validated by a purpose-built Go validator binary** | CUE 0.x (CNCF Sandbox). No first-party .NET binding; the validator is a small Go program embedding `cuelang.org/go` invoked out-of-process. Native cross-field constraint expressiveness; validates YAML data directly (`cue vet data.yaml schema.cue`). |
| B | **JSON Schema (Draft 2020-12), validated by a .NET library** | Mature in-process .NET binding (NJsonSchema / Corvus.JsonSchema). Cross-field logic via verbose `if/then/else` nesting. Mainstream open spec. |
| C | **Pkl** | No .NET binding at Pkl 0.28.x; the [Pkl binding spec](https://github.com/apple/pkl) names Java, Kotlin, Swift, Go only. |
| D | **Avro-as-config / hand-rolled typed DSL** | Avro is a wire format, not a constraint language (no cross-field predicates). A hand-rolled schema DSL re-implements CUE/JSON Schema from scratch — large effort, no community, fails S4. |

Candidates C and D are disqualified early (C on F1 — no integration path on .NET; D on S4/effort — reinventing a constraint language is exactly the build the team should not absorb). The live decision is **A vs B**: maximal cross-field expressiveness paying an out-of-process seam (A), versus in-process same-ecosystem convenience at the cost of constraint expressiveness (B).

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| CUE + Go validator | CUE: Apache 2.0 (`cue-lang/cuelang.org`, CNCF Sandbox). Go toolchain: BSD-3. The Go validator is engine-team source under the repo licence. | **Pass (conditional)** — the validator binary is bundled into the engine container image alongside the .NET runtime. Mitigation: pin the CUE library version; declare an image-bundling policy; the binary is built reproducibly in CI from pinned `go.mod`. |
| JSON Schema + .NET lib | JSON Schema: IETF-track open spec. NJsonSchema / Corvus.JsonSchema: MIT. | **Pass** |
| Pkl | Pkl: Apache 2.0; no .NET binding exists. | **Fail** — no integration path on .NET at 2026-05-23. |
| Avro / hand-rolled DSL | Avro: Apache 2.0; hand-rolled DSL: engine-team source. | (not a viable constraint language — see S-criteria) |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

The validator is a pure-validation component: no persistent surface, no PII handling, no audit surface introduced. Schema diagnostics are application-layer. The regulatory obligations are identical across A and B.

| Candidate | GDPR | DORA | PSD2 | Verdict |
|---|---|---|---|---|
| CUE + Go validator | No PII; pure validation. | The subprocess boundary is a new failure mode (validator binary missing / version drift in a production image). Operationally surveyable. | No audit surface. | **Pass (conditional)** — boot-time validator liveness + version check; the engine refuses to start if the bundled validator's version digest does not match the pinned expectation. |
| JSON Schema + .NET lib | No PII; pure validation. | Pure in-process CPU work; no new resilience boundary. | No audit surface. | **Pass** |

Both A and B clear the hard filters at POC scale. A's conditional passes name the bundling and subprocess-liveness mitigations, both carried into Consequences.

---

### Soft criteria

#### A · CUE + purpose-built Go validator — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** The honest cost is the out-of-process seam: a Go binary in the engine image, invoked rather than linked. That cost is bounded by *when* validation fires — at variant-commit time (interactive, pre-commit hook / CI; latency-irrelevant) and at pack-load time (once per engine startup, cached thereafter per [ADR-PC-007 §P4](./ADR-PC-007-signed-yaml-oci-pack.md)). Validation never sits on the per-request hot path, so subprocess spawn latency (~ms–tens of ms) does not matter. Critically, the *same single static binary* serves three contexts — the PM author's pre-commit hook, the PR CI gate, and the engine's pack-load check — eliminating environmental drift between author laptop and CI and production. The binary is small (~10–20 MB, single static Go executable) and version-pinnable.

**S2 · Ecosystem coherence.** Two-sided. Within its own habitat, CUE has *maximum* coherence: CUE's only first-class API surface is its Go library (`cuelang.org/go/cue/cuecontext`, `cue/load`, `cue/errors`), so a Go validator gets native `Unify`, structured `cue/errors` carrying `file:line:col` positions, and `cue export` directly — far better than scraping the generic `cue` CLI's text output. The validator emits diagnostics already shaped like the engine's `{path, kind}` contract. Toward .NET, the integration is deliberately *thin*: the engine shells out to the binary and deserialises a documented JSON diagnostic contract; the .NET↔Go alien-ness is fully contained inside the binary's invocation, not spread through engine code. Depth-5 simulation reuses the engine's own hand-rolled substrate ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) and Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) — no CUE involved at depth 5.

**S3 · Exit cost.** Medium. CUE files are portable text; the pack's *data* stays auditor-readable YAML ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) option 2), so only the `.cue` constraint files are CUE-specific. `cue export --out openapi`/JSON-Schema exists but is lossy on the very cross-field predicates that motivate the choice. The documented fallback (S4 below) is JSON Schema: because the data is already YAML and unchanged, a fallback re-expresses the *constraints* (more verbosely) without touching variants or packs.

**S4 · Community and longevity.** This is CUE's weakest dimension and the chosen candidate's principal residual risk. CUE is CNCF Sandbox (admitted 2022) and still pre-1.0 (v0.x as of 2026-05-23); the community is materially smaller than JSON Schema's multi-decade, OpenJS-governed ecosystem. The [ADR-IC-000 S4 ≥25 trailing-12-month external-commit threshold](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) is met for the CUE core but the v0.x stability promise is not yet made. **Mitigation:** (1) the engine pins the CUE library version and upgrades deliberately; (2) the YAML-data / `.cue`-schema separation keeps a JSON-Schema fallback open at bounded cost (the data does not move); (3) the validator binary is engine-team-owned and small, so a CUE-API breaking change is absorbed by the team, not by every variant author. A manual `git log --since=` longevity audit of the CUE repo is an Open Action before production hardening.

#### B · JSON Schema + .NET library (NJsonSchema / Corvus)

**S1.** Lowest possible — an in-process .NET library, no subprocess, no second toolchain, one CLI surface (`dotnet tool`). This is B's decisive advantage and the reason the prior iteration of this ADR chose it.

**S2.** NJsonSchema covers draft 4 → 2020-12, returns structured `ValidationError` with `Path`/`Kind`, integrates with `System.Text.Json`. Fully in the .NET ecosystem with no impedance.

**S3.** Low. `.json` schema files are portable; multiple validators read them (Corvus, Newtonsoft, Ajv on Node). Lower than A.

**S4.** JSON Schema itself is multi-decade, OpenJS-governed — stronger than CUE. The *library* (NJsonSchema) has an unaudited cadence; Corvus.JsonSchema is the named in-ecosystem fallback. Net S4 is stronger than A.

**Decisive reason for not choosing B:** **cross-field constraint expressiveness at depths 3–4.** The pack's reason for existing is exactly the kind of constraint CUE expresses inline and JSON Schema expresses as deeply nested `if/then/else`: pack-bound bounds (`tan_basis_points <= pack.max_consumer_rate_bps`), and cross-field regulatory coherence (`interest_variant == "PERIODIC"` implies `payment_period_months in [1,3,6,12]`; PT pack forbids Act/365 for deposits). JSON Schema covers depths 1–4 *today* but the prior iteration of this ADR itself flagged "a future schema needing higher-order cross-field logic would feel cramped" as a residual risk — i.e. it conceded the expressiveness gap at the precise boundary the engine most needs. Choosing CUE moves that expressiveness from "residual risk" to "native capability," accepting a contained out-of-process seam and a weaker S4 in exchange. With the engine schema language and the pack format ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)) unified on one constraint language, there is no second dialect to maintain.

---

## Decision

**Chosen: CUE (0.x) as the family-schema constraint language, validated by a purpose-built Go validator binary embedding `cuelang.org/go`, invoked out-of-process by the .NET 9 engine and by the authoring/CI tooling.**

The decisive reason is **native cross-field constraint expressiveness at depths 3–4**, which is the load-bearing job of the schema-and-pack boundary, combined with **unifying the schema language and the pack-format constraint language on one tool** ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) ships `.cue` schema files validating YAML pack data). The out-of-process cost that disqualified CUE in the prior iteration is contained: validation fires only at commit-time and pack-load-time (never per-request); a single static Go binary serves author, CI, and engine identically; and the **CI-validates → cosign-signs → engine-trusts-signature** pattern (see §P3) shrinks the engine's runtime CUE surface to a structural re-check, not full re-evaluation.

**Rejected: JSON Schema + .NET library.** It wins S1 (in-process) and S4 (mature spec), but loses on the one criterion that matters most for this engine — cross-field expressiveness at the pack-compliance and regulatory-coherence depths — and would force a second constraint dialect to be maintained alongside the pack format. It is retained as the **named fallback** if CUE's S4 risk materialises (the YAML data is unchanged; only constraints are re-expressed).

**Rejected: Pkl** — no .NET binding at 2026-05-23 (F1 fail). **Rejected: Avro-as-config / hand-rolled DSL** — Avro carries no cross-field predicates; a hand-rolled schema DSL reinvents a constraint language (fails S4 and contradicts the "build only what we must" principle that motivates hand-rolling the *engine core*, not its schema tooling).

---

## Consequences

**What this choice makes easier:**

1. **Depths 3–4 are expressed natively, not emulated.** Pack-bound bounds and cross-field regulatory invariants are CUE constraints in the same files that declare the fields, evaluated by `cue vet`. No `if/then/else` nesting; no second dialect.
2. **One constraint language across schema and pack.** The `.cue` schema files in a pack ([ADR-PC-007 §P1](./ADR-PC-007-signed-yaml-oci-pack.md)) validate the pack's YAML data directly; depth-3 pack compliance is `cue vet parameters.yaml schemas/term-deposit.cue` with no translation layer.
3. **One validator binary, three contexts.** The PM author's pre-commit hook, the PR CI gate, and the engine's pack-load check run the *same* pinned binary — zero environmental drift between laptop, CI, and production.
4. **Depth-5 simulation reuses the engine substrate.** The hand-rolled event-store append/load and projection code ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) runs the sealed corpus against a `Testcontainers.PostgreSql` fixture ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)); no Marten, no CUE at depth 5.

**What this choice makes harder or impossible:**

1. **One accepted out-of-process seam.** The engine is single-runtime .NET except for this validator subprocess. This is the contained price of choosing a Go-native constraint language on a .NET engine; it is a tool at the edge, not a pervasive runtime cost. (Had the engine been Go — see [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) rejected alternatives — CUE would embed in-process; staying .NET means owning this seam.)
2. **CUE's pre-1.0 status is a live S4 risk.** A breaking CUE-API change must be absorbed by the engine team at a library bump; the variant-author surface is insulated but the binary is not.
3. **`cue export` to JSON Schema is lossy** on cross-field predicates, so the JSON-Schema fallback is a constraint *rewrite*, not a mechanical conversion. The data (YAML) is unaffected, bounding the cost.

**Residual risks:**

1. **CUE longevity (S4).** Mitigation: pin + deliberate upgrades; JSON-Schema fallback kept open by the YAML-data separation; manual `git log --since=` audit of `cue-lang/cuelang.org` before production hardening (Open Action #1).
2. **Validator/binary supply chain.** The bundled binary is built reproducibly in CI from a pinned `go.mod`; its version digest is checked at engine boot (F2 mitigation). A mismatched or missing binary is a fail-loud startup error, never a silent skip.
3. **Diagnostic-contract drift.** The JSON diagnostic contract between the Go validator and the .NET engine is itself a versioned interface; a CI contract test asserts the engine deserialises the validator's output shape.

---

## Implementation Principles

### P1 — Family schemas are `.cue`; variant and pack *data* stay YAML

A family schema (`term-deposit.cue`) declares fields, types, ranges, union shapes, optional fields, pack-bound references, and the depth-3/4 cross-field constraints in CUE. Variants remain YAML files ([authoring §2.3](../feature-design-configuration-authoring.md)); pack parameters/primitives/test-corpus remain YAML ([ADR-PC-007 §P1](./ADR-PC-007-signed-yaml-oci-pack.md)). Validation is `cue vet <variant>.yaml <family>.cue` (and the pack equivalent) — CUE ingests YAML as data and unifies it against the `.cue` constraints. Authors never write CUE; engineers and the pack team own the `.cue` files (the [authoring §1](../feature-design-configuration-authoring.md) cadence: family schemas quarterly, platform-owned).

### P2 — The validator is one Go binary, invoked out-of-process, with a JSON diagnostic contract

`pack-validate` is a single static Go executable embedding `cuelang.org/go`. It exposes depths 1–4 as subcommands and emits a documented JSON diagnostics array (`{depth, path, kind, message, pos}`) on stdout. It is invoked by: the PM author's pre-commit hook, the PR CI gate, and the .NET engine at pack-load. No `Process.Start` of the *generic* `cue` CLI — the purpose-built binary owns depth structure and diagnostic shape. The engine deserialises the contract into its own diagnostic objects; the contract is version-stamped and CI-tested.

### P3 — CI validates, cosign signs, the engine trusts the signature

Depths 1–4 run in CI on every variant/pack PR; a pack is cosign-signed ([ADR-PC-007 §P2](./ADR-PC-007-signed-yaml-oci-pack.md)) only after validation passes. The engine treats the verified signature as the attestation that CUE validation already succeeded, so its **runtime** CUE surface at pack-load shrinks to a structural re-parse and version check (fail-loud), not full depth-1–4 re-evaluation. This is what keeps the out-of-process seam off any hot or startup-critical path.

### P4 — Depth-5 simulation runs on the engine's own substrate, not on CUE

Depth 5 appends the sealed test-corpus events through the engine's hand-rolled `append` path into a `Testcontainers.PostgreSql` fixture, rebuilds the projection via the engine's own replay code, and asserts against `expected-events.yaml` ([surface §3.9](../feature-design-configuration-surface.md)). The fixture is session-scoped (one container per CI test session) to keep the < 30 s budget comfortable. CUE plays no part at depth 5 — it is a constraint language, not a simulator.

---

## Open Actions

1. **Manual `git log --since=` longevity audit** of `cue-lang/cuelang.org` before production hardening; record in this ADR's change log. If CUE stalls below the [ADR-IC-000 S4 threshold](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md), execute the JSON-Schema fallback.
2. **Reproducible validator build** — pin `go.mod` + CUE library version; CI publishes the binary with a version digest the engine checks at boot.
3. **JSON diagnostic-contract test** — a CI test asserting the .NET engine deserialises the Go validator's output shape; the contract is versioned.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `PACK_VALIDATE_DEPTH_BUDGETS` — depths 1–4 validate within budget (syntactic < 1 s, type < 5 s, pack-compliance < 10 s, regulatory-coherence < 10 s, aggregate < 30 s), synchronously at variant/pack-commit and on every PR (§P3 and the depth table in Context).
- `PACK_SIM_DEPTH5_BUDGET` — depth-5 simulation replays the sealed pack test-corpus through the engine substrate and reproduces the expected event sequence in < 30 s in CI (§P4).

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — engine is .NET 10 with a hand-rolled core; the validator is the one accepted out-of-process seam. This ADR depends on PC-010 for the runtime; PC-010 depends on this ADR for the schema-validation mechanism.
- [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) — pack ships `.cue` schemas validating YAML data; cosign signing underwrites the CI-validates-engine-trusts pattern (§P3).
- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — depth-5 simulation runs against the PostgreSQL event store's `events`-table contract via the hand-rolled append/replay path.
- [feature-design-configuration-authoring §5](../feature-design-configuration-authoring.md) — the five validator depths; §9.5 "no DSL escape hatch"; §1 names CUE / JSON Schema as the schema-language candidates.
- [feature-design-configuration-surface §3.2, §3.10](../feature-design-configuration-surface.md) — schema as typed contract; validator interplay (static pack-compliance + dynamic simulation).

---

## Amendment — 2026-06-13: depth-5's first increment gates the event SEQUENCE; the `expected-events.yaml` byte corpus follows (C.3 implementation)

Implementing C.3 ([bd babelstone-5qhp](../04-open-questions.md), with the CI wiring on [bd babelstone-fnqa](../04-open-questions.md)) landed depth-5 on the engine's own substrate exactly as §P4 decides — but revealed that the §P4 phrase "**asserts against `expected-events.yaml`**" cannot be the *byte-corpus* comparison in v1's first increment, because that artefact is itself blocked. This amendment is additive: it pins *what* depth-5 asserts in C.3 and time-bounds the byte-corpus comparison, leaving §P4's substrate decision (engine append/replay, session-scoped Testcontainers, no CUE, < 30 s) intact and implemented.

### A1 · C.3 gates the engine substrate + budget + per-shape event SEQUENCE

`PackSimulationDepth5Tests` (in the term-deposit Application test project, `Category=Integration`) loads the committed `pt.2026.1` pack, drives every canonical instance of `test-corpus/canonical-instances.yaml` through the engine's hand-rolled `append`/replay path into a session-scoped `Testcontainers.PostgreSql` fixture (constitute → intermediate coupons for PERIODIC → mature, all by **explicit command** — no clock-advance, A.8b stays out of scope), cold-replays each stream, and asserts the produced **`family.EventType` sequence** matches the documented per-interest-shape lifecycle. The whole corpus runs in well under the < 30 s `PACK_SIM_DEPTH5_BUDGET` ceiling (§P4 / commitment row 11, now `Live`). This is the depth-5 substrate and budget §P4 names, gating the regression-meaningful "engine + pack produce the right lifecycle shape from the sealed corpus".

### A2 · The byte-level `expected-events.yaml` comparison is deferred

`expected-events.yaml` is the ADR-PC-007 §P5 **generated** artefact (still the empty placeholder pack.sh treats as "generation pending"). Generating + comparing the full per-event payload corpus would serialise each event's fields, and `DepositConstituted` is a **bus-published** event whose Avro codec enforces strict C#↔`.avsc` parity and has no array-of-record support (the same constraint [ADR-PC-024 Amendment A2](./ADR-PC-024-constitution-precondition-contract.md) documents). Per [ADR-PC-028](./ADR-PC-028-event-store-payload-format.md) the audit book of record is the store JSON, so the byte-corpus generator/comparator is store-side work that does not need to widen the bus — but it is more than the C.3 increment, so it is **deferred**: depth-5 gates the event SEQUENCE now (A1) and the byte-level `expected-events.yaml` round-trip follows ([bd babelstone-vcxq](../04-open-questions.md)). Until then `expected-events.yaml` stays the logged-skip placeholder, never a silent pass.

### A3 · This amends the decision; it does not supersede this ADR

§P4 remains binding as written — depth-5 runs on the engine's hand-rolled append/replay substrate against a session-scoped `Testcontainers.PostgreSql` fixture, with no CUE and under the < 30 s budget. This amendment only **localises §P4's "asserts against `expected-events.yaml`" clause** to the structural event-sequence assertion in C.3 and time-bounds the byte-corpus comparison — it is appended to, not a revision of, §P4.

---

*Decided 2026-05-23 by jhosm. Supersedes the prior JSON-Schema + NJsonSchema iteration of ADR-PC-006 (removed before acceptance); JSON Schema retained as the named CUE-longevity fallback.*
