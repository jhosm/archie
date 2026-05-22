# ADR-PC-006: Family-Schema Language and Validator Runtime — JSON Schema + NJsonSchema

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-05-22 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store), [ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md) (engine language and framework), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro + Confluent Schema Registry at the bus boundary), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers for depth-5 simulation) |
| Resolves | bd `archie-10r.7` (ADR-PC-006: Family-schema language and validator runtime) |

---

## Context

The engine's configuration surface ([feature-design-configuration-authoring](../feature-design-configuration-authoring.md) and [feature-design-configuration-surface](../feature-design-configuration-surface.md)) requires a **family-schema language** that variant authors write against and a **validator runtime** that enforces five validator depths at variant-commit time and in CI ([authoring §5](../feature-design-configuration-authoring.md)):

| Depth | Budget | Check |
|---|---|---|
| 1 syntactic | < 1 s | YAML parses to the schema's structural shape |
| 2 type-check | < 5 s | Every field's type and range match the schema declaration; pack-bound fields resolve to a known primitive in the pinned pack |
| 3 pack compliance | < 10 s | Variant respects pack's bounds (e.g. `tan_basis_points <= pack.max_consumer_rate`) |
| 4 regulatory coherence | < 10 s | Cross-field invariants required by regulation |
| 5 simulation | < 30 s (CI) | Engine primitive code over the sealed pack test corpus produces the expected event sequence |

Depths 1–4 run **synchronously at commit time** in the PM author's editor (pre-commit hook) and on every PR commit; the aggregate budget is < 30 s. Depth 5 is deferred to CI under the < 5-minute CI budget.

The schema language is the boundary between two audiences: PM/product authors who write variant YAML, and engine code that interprets variants. The language must be expressive enough to encode pack compliance and regulatory coherence as declarative constraints ([authoring §9.5](../feature-design-configuration-authoring.md): "no DSL escape hatch"), and parseable enough that an authoring tool can surface field-pointing diagnostics in the < 30 s budget.

The chosen engine language is C# .NET 9 ([ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md)). The validator runtime must therefore be available as an in-process .NET library — no subprocess shell-out (defeats hot-reload and deterministic timing), no second-language toolchain in the variant author's repository.

**Candidates evaluated** (the three named viable schema languages for a .NET engine):

| # | Candidate | Notes |
|---|---|---|
| A | **JSON Schema (draft 2020-12), validated by NJsonSchema** | Mature .NET binding (`/ricosuter/njsonschema`, MIT); in-process validator; depth-3/4 via extension-validator hooks; mainstream schema language |
| B | **CUE, embedded via the CUE Go evaluator out-of-process** | No first-party .NET binding; integration is `Process.Start("cue", "vet", …)`. Distinctive constraint expressiveness; doubles the deployment surface |
| C | **Pkl on .NET** | No .NET binding documented at Pkl 0.28.2; the [Pkl language-binding spec](https://github.com/apple/pkl) names Java, Kotlin, Swift, and Go bindings only |

---

## Evaluation

### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| JSON Schema + NJsonSchema | NJsonSchema is MIT (`/ricosuter/njsonschema`, verified context7 2026-05-22). JSON Schema is an IETF-track open spec. | **Pass** |
| CUE on .NET (out-of-process) | CUE is Apache 2.0 (`/cue-lang/cuelang.org`, CNCF Sandbox). Shelling out to the CUE binary requires bundling it into the engine's container image — bundling permitted by Apache 2.0. | **Pass (conditional)** — image-bundling of a Go binary alongside the .NET runtime; container size increases; image-build pipeline carries a second toolchain. Mitigation: pin CUE version; declare image-bundling policy. |
| Pkl on .NET | Pkl itself is Apache 2.0. No .NET binding exists. | **Fail** — no integration path available at 2026-05-22. |

### F2 · Regulatory fit

| Candidate | GDPR | DORA | PSD2 | Verdict |
|---|---|---|---|---|
| JSON Schema + NJsonSchema | Pure validation library; no persistent surface; no PII handling. | Pure in-process CPU work; no resilience boundary introduced. | Schema diagnostics are application-layer; no audit surface introduced. | **Pass** |
| CUE on .NET (out-of-process) | Pure validation; no persistent surface. | Subprocess invocation adds a failure mode at the process boundary (CUE binary missing or version drift in production); operationally surveyable but a new surface. | Same as JSON Schema. | **Pass (conditional)** — operational surface of subprocess invocation; mitigation: liveness check at engine boot. |
| Pkl on .NET | — | — | — | (already failed F1) |

### Soft criteria

**JSON Schema + NJsonSchema.**

- **S1 operational complexity:** Lowest possible. The validator is an in-process .NET library that ships in the same `dotnet tool` (`bd-validate variant.yaml`) running locally and on every PR commit. No network round-trip, no subprocess, no schema-registry call. One library, one CLI surface, one configuration point. The PM author runs the same `dotnet tool` the CI runs — zero environmental drift.
- **S2 ecosystem coherence:** NJsonSchema covers draft 4 through 2020-12, returns structured `ValidationError` with `Path` and `Kind` for per-field diagnostics, and integrates with `System.Text.Json` (the canonical .NET JSON library). Depth-5 simulation reuses Testcontainers .NET ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) and Marten ([ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md)) — same ecosystem, no impedance.
- **S3 exit cost:** Low. JSON Schema is an open spec; schemas are `.json` text files; migration to a different validator (Corvus.JsonSchema, Newtonsoft.Json.Schema, or even a non-.NET runtime such as Ajv on Node) reads the same files. CUE export from JSON Schema is not lossless on advanced predicates, but the engine's depth-3/4 constraints fit standard JSON Schema vocabulary (`$ref`, `oneOf`, `if/then/else`, `const`, `pattern`, numeric range).
- **S4 community and longevity:** NJsonSchema's commit cadence is not audited against the [ADR-IC-000 S4 ≥25 trailing-12-month external commits threshold](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) from context7 metadata alone — `csharp-counter.md` §2.7 explicitly flags the audit gap (context7 Benchmark Score 14.5 reflects snippet coverage, not maintenance cadence). A manual `git log --since="2025-05-22"` against `/ricosuter/njsonschema` is required before production commitment. The fallback is `Corvus.JsonSchema` (build-time codegen, Microsoft-adjacent, full feature parity for depths 1–4). JSON Schema itself has CNCF-adjacent governance (the [JSON Schema project](https://json-schema.org/) under the OpenJS Foundation umbrella) and multi-decade industry adoption. Library risk: real but bounded by a named fallback.

**CUE on .NET (out-of-process).**

- **S1 operational complexity:** Higher. Two toolchains in the image (.NET runtime + Go-compiled CUE binary). The engine starts a subprocess per validation; failure modes include process-spawn errors, version drift between the dev laptop and production image, and Windows path-quoting issues if the PM author runs Windows. Mitigation requires careful image-build policy and a liveness check; real ongoing surface.
- **S2 ecosystem coherence:** Lower for .NET. CUE is a Go ecosystem citizen; the .NET integration is alien — no `cuelang.org/dotnet`. Subprocess output (JSON-formatted CUE error messages) must be deserialised by hand and re-shaped into the engine's diagnostic format.
- **S3 exit cost:** Low (CUE files are portable; CUE → JSON Schema export exists, though lossy on cross-field predicates).
- **S4 community and longevity:** CUE is CNCF Sandbox (admitted 2022); v0.x (not yet a v1 stability promise as of 2026-05-22). Smaller community; the CUE Go evaluator binary is the only viable embedding. Per the Go-stack R3 §2 perf row, CUE evaluator performance at the engine's workload scale (PT pack + 50–100 variants under fine-drift cadence) is **not benchmarked in CUE's upstream surface returned by context7 on 2026-05-22** — Go R3 retracted the R1 "verified empirically" claim. The < 30 s budget would be a v1 acceptance gate, not a write-time guarantee. For a .NET engine paying the cross-language subprocess cost on top, the rationale is thin.

**Pkl on .NET.**

Disqualified by F1. The [Pkl language-binding spec](https://github.com/apple/pkl/blob/main/docs/modules/bindings-specification/pages/index.adoc) names Java, Kotlin, Swift, and Go bindings only as of 2026-05-22. A third-party Pkl-on-.NET binding would have to be built from the spec — outside the F1/S1/S4 budget at 1–2 person scale.

---

## Decision

**Chosen: JSON Schema (draft 2020-12), authored as `.json` files alongside the family schema's source tree, validated by NJsonSchema (`/ricosuter/njsonschema`, MIT) running in-process inside the .NET 9 engine.**

The decisive reason is the **in-process / same-ecosystem affordance combined with sufficient expressiveness for [authoring §5](../feature-design-configuration-authoring.md) depths 1–4**. JSON Schema's vocabulary (`$ref`, `oneOf`, `if/then/else`, `const`, `pattern`, numeric range, `additionalProperties: false`) covers the [authoring §3.2](../feature-design-configuration-authoring.md) flat-vs-stepped union case, pack-bound numeric bounds, and cross-field regulatory invariants via `if/then/else`. NJsonSchema's `schema.Validate(jsonString)` returns the error collection synchronously with `Path` + `Kind` per field — exactly the diagnostic shape an authoring tool needs at variant-commit time.

The depth-5 simulation runs **inside an xUnit test against a `Testcontainers.PostgreSql` container** ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) with a Marten `DocumentStore` configured for `Projections.LiveStreamAggregation<DepositPosition>`. The simulation appends the canonical test-corpus events (per [feature-design-configuration-surface §3.9](../feature-design-configuration-surface.md)), reads the projection via `session.Events.AggregateStreamAsync<T>(streamId)`, and asserts against the expected event sequence. To keep the depth-5 budget comfortable, the Testcontainers PostgreSQL fixture is **session-scoped** (one container per xUnit test session, Marten schema initialised once per session) — per the mitigation committed in `csharp-counter.md` §2.8.

**Rejected: CUE on .NET (out-of-process).**

The decisive reason is the **subprocess shell-out cost and the absence of a first-party .NET binding**. The engine language is .NET 9; introducing a Go-compiled binary in the engine's image to call out to per validation is a real S1 cost paid every variant commit and every CI run. CUE's expressiveness advantage (cross-field predicates, pack-import constraints expressed in one language) is genuine but does not outweigh the operational surface for a 1–2 person team operating one .NET runtime per [ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md). CUE's perf at the engine's workload scale is also explicitly unverified (Go R3 §2 perf-row retraction).

**Rejected: Pkl on .NET.**

The decisive reason is the **absence of a .NET binding at Pkl 0.28.2**. No path exists at 2026-05-22.

---

## Consequences

1. **Depths 1–4 run in-process under the < 30 s combined budget with substantial headroom.** NJsonSchema is in-process pure CPU; for a typed family schema with ~50 fields, the four depths combined complete in single-digit milliseconds. The validator binary is a `dotnet tool` running locally in the PM author's editor and on every PR commit. No network round-trip, no schema-registry call, no external pack-loader — the pack is a file on disk, loaded once at process start, cached.
2. **Depth 5 reuses the engine's runtime substrate.** Testcontainers .NET + Marten + xUnit are already in scope under [ADR-PC-010](./ADR-PC-010-dotnet-marten-wolverine.md) and [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md). The depth-5 simulation does not introduce a new toolchain; it reuses the engine's event-append and projection-read code against canonical fixtures.
3. **PM authors and engine code share one schema language.** Variant YAML is validated against the same `.json` schema the engine code interprets at runtime. Forward-only schema discipline ([feature-design-event-store-projections §5.4](../feature-design-event-store-projections.md)) is enforced by review on the `.json` source tree — old schemas remain readable, new versions are added under new `schema_version` filenames.
4. **NJsonSchema audit gap is named.** Per `csharp-counter.md` §2.7, the [ADR-IC-000 S4 ≥25 trailing-12-month external commits threshold](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) cannot be verified from context7 alone for NJsonSchema. A manual `git log --since="2025-05-22"` against `/ricosuter/njsonschema` is a pre-production-commitment check. If the threshold is not met, the documented fallback is `Corvus.JsonSchema` (build-time codegen, full feature parity for depths 1–4) — a library-level swap that does not change the schema language.
5. **CUE's distinctive expressiveness is sacrificed.** Cross-field predicates that CUE expresses inline (`if interest_variant == "PERIODIC" then payment_period_months in [1, 3, 6, 12]`) require JSON Schema's `if/then/else` keyword with structurally equivalent but more verbose nesting. The engine's [authoring §5](../feature-design-configuration-authoring.md) depth-4 regulatory coherence checks are covered, but a future schema that needs higher-order constraint logic (e.g., disjunctive coverage proofs across hundreds of fields) would feel cramped in JSON Schema. This is named as a residual risk, not a current cost.
6. **The schema source tree is plain text.** `.json` files live alongside the engine's C# source tree under `schemas/`; PR reviewers, regulators, and migration tooling read them with no special tooling. CUE files would have required a `cue export --out json-schema` step for any non-Go reviewer.

---

## Residual Risks

1. **NJsonSchema maintenance cadence (S4).** If the manual `git log` audit shows NJsonSchema falls below the [ADR-IC-000 S4 threshold](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) before production commitment, switch to `Corvus.JsonSchema`. Both libraries implement the same IETF draft-2020-12 spec; the schemas themselves do not change. The migration cost is bounded — change the validator wiring in `Engine.Validation`, run the existing test suite. Mitigation: scheduled audit in the v1 acceptance pass (bd action) and re-audit at every major engine release.
2. **Future schema-evolution cadence vs JSON Schema vocabulary.** [feature-design-configuration-authoring §3.1](../feature-design-configuration-authoring.md) commits to "coarse-start, fine-drift" — horizontal sibling schemas (`term_deposit_flat@2026.3`, `term_deposit_stepped@2026.3`) under one family. JSON Schema handles this naturally via filename-as-version: each split is its own `.json` file referenced by the variant's pinned `schema_version`. No CUE-style vertical-versioning idiom to fight. The risk is the reverse: if a future schema family needs higher-order cross-field logic (beyond what `if/then/else` covers), the engine would face a schema-language migration. Mitigation: monitor JSON Schema vocabulary developments (draft 2020-12 → next IETF revision); the typed access layer in `Engine.Schemas` insulates engine code from the schema language for the cases that *do* fit JSON Schema today.
3. **Depth-5 simulation budget under Testcontainers cold start.** The depth-5 budget is < 30 s. Per `csharp-counter.md` §2.8, Testcontainers cold start of PostgreSQL is 3–6 s warm-cache plus ~1 s Marten schema init. The session-scoped fixture pattern amortises this across multiple simulation runs in one CI session, leaving comfortable headroom. Risk: a future CI executor with cold Docker layer caches widens the cold-start window. Mitigation: pin the Testcontainers PostgreSQL image tag; pre-pull in the CI runner setup step.

---

*Verification date: 2026-05-22. context7 sources cited inline by handle; NJsonSchema S4 audit gap acknowledged per `csharp-counter.md` §2.7 mitigation list. Library versions referenced: .NET 9 (GA); NJsonSchema 11.x; Marten 7.x; Testcontainers.PostgreSql 4.x.*
