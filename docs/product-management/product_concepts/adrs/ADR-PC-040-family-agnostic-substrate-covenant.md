# ADR-PC-040: The Family-Agnostic Substrate Covenant — Default-Deny Family→Core Gating and Composition-Root Discovery

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-07-02 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category — a cross-cutting structural/engineering-practice covenant, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default; F1/F2 do not discriminate, the same class as [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md), [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md), [ADR-IC-019](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md), and [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the engine-estate instance of the covenant, whose §A2 "the host MAY reference families; the spine MAY NOT" exemption pattern and §A13–§A14 assembly-scan discovery this ADR generalises), [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) (the orchestrator-estate instance — `ISagaModule` over a family-agnostic substrate), [ADR-IC-019](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md) (the notification-estate instance — `IFamilyNotificationModule` over a family-agnostic core), [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) (the lifecycle-estate instance — `IFamilyLifecycleModule` over a family-agnostic driver core; also the home of `Babelstone.Cadence`, the shared generic library the discovery scanner lands in), [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (the fitness-function governance the two new gates register under), [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (the monorepo + extraction-ready-subtree posture that makes one repo-wide gate possible) |
| Resolves | bd `babelstone-64uw.1`; governs the gates of bd `babelstone-64uw.2` / `babelstone-64uw.3` (epic `babelstone-64uw`) |

---

## Context

**In plain English.** Four times now, this codebase has made the same architectural promise in four different places: the *generic* part of a service (the engine kernel, the saga substrate, the notification core, the lifecycle-driver core) must never know that a "term deposit" or a "personal loan" exists. Product knowledge lives in plug-in "family" modules, and one designated host per service — the *composition root* — discovers and wires those plug-ins in. The promise was enforced by four separate hand-written tests, each guarding a hardcoded list of projects. That worked for the projects on the lists — and failed exactly where lists fail: a *new* core project was protected only if someone remembered to add it. The lifecycle driver shipped with the dependency arrow pointing the wrong way (its core referenced both family assemblies) and no gate noticed until PR #404 fixed it by hand. This ADR flips the whole invariant to **default-deny**: every project in the repository is now presumed to be a family-agnostic core unless it *explicitly, visibly declares otherwise* in its own project file — so a new substrate piece is born gated, and opting out is one greppable line a reviewer cannot miss.

The covenant has been re-decided, estate by estate, four times:

| Estate | Governing ADR | Family-agnostic core set | Composition root (the standing exemption) | Estate gate |
|---|---|---|---|---|
| Engine | [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) §D2/§P2 + §A2/§A14 | the 8-project generic spine | `Babelstone.Engine.Api` (`HostModuleLoader` assembly-scan) | `ENGINE_FAMILY_AGNOSTIC` (row 12) + `ENGINE_API_HOST_FAMILY_AGNOSTIC` (row 12b) |
| Orchestrator | [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) §D2/§D4 | `Babelstone.Orchestrator.Substrate` | `Babelstone.Orchestrator` (explicit `ISagaModule` list at decision time) | `ORCHESTRATOR_FAMILY_AGNOSTIC` (ORCH-1) |
| Notification | [ADR-IC-019](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md) §D2/§D4 | `Babelstone.Notification` | `Babelstone.Notification.Host` (`NotificationModuleLoader` assembly-scan) | `NOTIFICATION_FAMILY_AGNOSTIC` (NOTIF-1) |
| Lifecycle driver | [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) §Decision 2 | `Babelstone.Lifecycle` | `Babelstone.Lifecycle.Host` (`LifecycleModuleLoader` assembly-scan) | `LIFECYCLE_FAMILY_AGNOSTIC` (LCD-3) |

Rule-of-three is over-satisfied: four estates prove the identical pattern — a family-agnostic core, a `family → core` one-way dependency arrow, one composition root that MAY reference `families/**` because it composes them, and composition **by discovery** (assembly-scan over the `Babelstone.Families.*` name prefix), never by naming a concrete family in generic code. Yet three structural gaps remained:

1. **Enumeration, not default-deny.** Every estate gate guards a hand-maintained allowlist (8 spine projects, 1 substrate project, 1 core, 1 core). Nothing gates a project that is on *no* list — which is every project the day it is created. The lifecycle-driver arrow reversal (fixed in PR #404) is the realised form of this risk.
2. **The covenant exists only as four precedents.** A fifth estate must synthesize the pattern from four ADRs; nothing states it once as the inheritable rule.
3. **Discovery was implemented three times and hand-wired once.** The engine, lifecycle, and notification module loaders are near-verbatim copies of one another (same ctor activation, duplicate-family throw, stable ordering, compile-graph + output-directory probe anchors); the orchestrator host still held an explicit module list and named `Babelstone.Families.TermDeposit.Orchestration` types in its composition file — the [ADR-IC-018 §D6](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) "assembly-scan later" that had not yet arrived.

**Candidates evaluated** (how to make the covenant default-deny and self-applying):

| # | Candidate | Notes |
|---|---|---|
| A | **One covenant ADR + a repo-wide default-deny fitness gate keyed on a single machine-readable MSBuild marker (`<BabelstoneRole>`), + one shared discovery scanner (`FamilyModuleScanner` in `Babelstone.Cadence`) + a universal composition-root source gate.** An unmarked project is presumed core and gated; a composition root opts out with one visible csproj line; every host discovers modules through one shared mechanism. | Chosen. The gate enumerates every `.csproj` off disk, so a new project is covered at birth with zero gate edit; the per-estate ADR-synced gates remain as the decision-linked layer. |
| B | **Keep replicating the per-estate pattern** — a fifth estate writes its own ADR section, its own allowlist gate, its own module-loader copy. | The status quo that produced the PR #404 arrow reversal. Each new estate re-pays the synthesis cost and re-introduces the unlisted-project gap. |
| C | **Enforce at MSBuild time** — a shared `Directory.Build.targets` that fails the *build* when a non-root project references `families/**`. | Strongest failure locality (the offending project itself fails), but it puts enforcement logic into build plumbing that four differently-configured subtrees (engine inherits `engine/Directory.Build.props`; orchestrator/notification/lifecycle/cadence deliberately carry their own settings for [ADR-PC-019 §P2](./ADR-PC-019-repository-strategy-monorepo.md) extraction-readiness) would each have to import — the exact cross-subtree coupling the extraction-ready posture avoids. Reserved as a hardening step; the marker this ADR fixes is deliberately MSBuild-native so C can be added later without re-deciding anything. |
| D | **A Roslyn analyzer** (a BENG-series diagnostic on family references outside a root). | Analyzers see *compilations*, not `.csproj` reference topology; the engine's analyzers are engine-scoped and the boundary subtrees do not reference them (extraction-readiness again). Wrong tool for a dependency-graph assertion. |

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category (an engineering-discipline/structural decision, declared tool-selection per the §D4 default): no tool is purchased, F1/F2 degenerate, and the decision rides on S1–S4 plus the default-deny reason.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

All four candidates are in-repo engineering work on already-licensed tooling (xunit, MSBuild, Roslyn). Uniform **Pass** — F1 does not discriminate.

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A dependency-direction covenant is not a regulated runtime artefact; no candidate touches PII, the durable bus, or an audit surface. Uniform **Pass** — F2 does not discriminate. (The covenant *supports* regulatory posture indirectly: a family-agnostic core is what keeps product-specific regulatory logic in pack-governed family modules, [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md).)

### Soft criteria

**A · Covenant ADR + default-deny marker gate + shared scanner — CHOSEN.**

- **S1 · Operational complexity for 1–2 people.** Lowest ongoing cost: a new core project requires *nothing* (it is gated by default); a new host requires exactly one `<BabelstoneRole>CompositionRoot</BabelstoneRole>` line; a new estate inherits the covenant + both universal gates with zero gate edits. The one-time cost is one test file, one scanner, and four one-line csproj markers.
- **S2 · Ecosystem coherence — decisive.** The marker is plain MSBuild (readable by the gate off disk, by a future `Directory.Build.targets` (candidate C), and by a human in review); the gate is the same xunit + parse-the-artefact-off-disk shape every existing fitness function uses ([ADR-PC-020 §P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md): fitness functions live where the work already is); the scanner extraction is the [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)-established `Babelstone.Cadence` move (extract the proven mechanism into the shared generic library) applied to discovery.
- **S3 · Exit cost.** Near zero: deleting the marker property and the two tests restores the per-estate world; the scanner unwinds into the three loaders it was extracted from.
- **S4 · Longevity.** The covenant outlives any estate; the default-deny posture is precisely what makes estate #5..N cheap.

**B · Keep replicating** — rejected on S1/S4: each estate re-pays synthesis, and the unlisted-project gap (the PR #404 failure mode) persists structurally. **C · MSBuild-targets enforcement** — deferred, not rejected: right mechanism, wrong moment; it needs cross-subtree build plumbing the extraction-ready posture resists, and it can be layered onto the same marker later. **D · Roslyn analyzer** — rejected on mechanism fit: reference topology is not a compilation concern.

---

## Decision

### Every product core/substrate is family-agnostic by default (default-deny); every composition root composes families by discovery; both are stated once, here, and enforced by universal gates.

- **D1 — The covenant, part 1: the `family → core` arrow is one-way, everywhere, by default.** Every .NET project in this repository that is not (a) a family contribution under `families/**`, (b) a test project, or (c) an explicitly declared composition root / test rig (§D2) **SHALL carry no `ProjectReference` into `families/**`**. This is the cross-cutting statement of [ADR-PC-021 §D2](./ADR-PC-021-application-layer-family-owned-deciders.md), [ADR-IC-018 §D2](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md), [ADR-IC-019 §D2](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md), and [ADR-PC-036 §Decision 2](./ADR-PC-036-lifecycle-command-driver.md) — those estate decisions remain binding and are *instances* of this covenant, not superseded by it.
- **D2 — One machine-readable classification signal: the `<BabelstoneRole>` MSBuild property; ABSENT means Core.** A project's covenant role is declared by a single MSBuild property in its own `.csproj`: **absent or `Core`** → a family-agnostic core, gated by D1 (the default — a new project is gated at birth, no gate edit); **`CompositionRoot`** → the one explicit, visible opt-out: the project MAY reference `families/**` because composing families is its job (the [ADR-PC-021 §A2](./ADR-PC-021-application-layer-family-owned-deciders.md) standing-exemption pattern, generalised) — and it is thereby *subject to D3*; **`TestRig`** → declared non-shipping test tooling (the [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) load harness) that, like a test project, may name families. Any **other** value fails the gate (fail-closed on vocabulary drift). Family contributions are recognised by their `families/**` path; test projects by `<IsTestProject>true</IsTestProject>` or a `tests/` path segment. The universal gate (`FAMILY_TO_CORE_DEFAULT_DENY`) enumerates **every** `.csproj` off disk and asserts D1 over the unmarked remainder.
- **D3 — The covenant, part 2: a composition root composes by discovery and names no family in its composition surface.** Every `CompositionRoot`-marked project discovers its family modules by **assembly-scan over the `Babelstone.Families.` name prefix** (the family-agnostic membership predicate) and its composition file (`Program.cs`) names **no** `Babelstone.Families.*` identifier in code — nor may a global import (`<Using>` item or `global using`) smuggle a bare family token into it. Adding a family to a host is the family's module + the host `ProjectReference` (the scan anchor) — never an edit to composition code. This generalises `ENGINE_API_HOST_FAMILY_AGNOSTIC` (row 12b) to every root as `COMPOSITION_ROOT_NAMES_NO_FAMILY`. The scope is deliberately the composition file + the global-import surface, exactly as row 12b: a host-local, family-specific *edge adapter file* (today, the orchestrator's term-deposit constitution edge under `Edge/`) may still name the family it fronts — that is an API-surface concern this covenant does not decide — but it must do so via a local import in its own file, never by leaking a bare token into `Program.cs`.
- **D4 — One shared discovery mechanism: `FamilyModuleScanner` in `Babelstone.Cadence`.** The proven module-loader mechanics (concrete-implementation scan, fail-loud activation diagnostics, duplicate-key throw before composing, stable assembly-then-type ordering, and the two-anchor assembly enumeration: compile-reference graph + `Babelstone.Families.*.dll` output-directory probe) are extracted from the three near-duplicate loaders into one generic `FamilyModuleScanner` in `Babelstone.Cadence` — the shared library [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) established as the home of generic, product-unaware mechanism. Each estate keeps its thin, estate-named loader (its module contract, its diagnostics vocabulary, any estate-specific cross-check such as the engine's pack-manifest fail-closed check, [ADR-PC-007 §A1](./ADR-PC-007-signed-yaml-oci-pack.md)) delegating to the scanner — so every host, present and future, gets discovery for free and none can drift from the mechanics.
- **D5 — A new estate inherits; it does not re-derive.** A future service with a generic core cites this ADR for the covenant, gains `FAMILY_TO_CORE_DEFAULT_DENY` coverage automatically (default-deny needs no registration), adds one marker line for its host to come under `COMPOSITION_ROOT_NAMES_NO_FAMILY`, and reuses `FamilyModuleScanner` for discovery. Its own ADR still decides the *estate-specific* questions (what its core owns, what its module contract carries) and MAY add a decision-linked estate gate in the ADR-synced style of rows 12/ORCH-1/NOTIF-1/LCD-3 — the two layers are complementary (§P4), not alternatives.

**Rejected: keep replicating per estate** — the unlisted-project gap is structural, not accidental. **Rejected: analyzer enforcement** — reference topology is not a compilation concern. **Deferred: MSBuild-targets (build-time) enforcement** — reserved as hardening on top of the same `<BabelstoneRole>` marker; adopting it later contradicts nothing here.

---

## Implementation Principles

### P1 — The universal dependency gate (`FAMILY_TO_CORE_DEFAULT_DENY`)

One repo-wide fitness test (`FamilyAgnosticDefaultDenyTests`, `Babelstone.Engine.Tests`, Docker-free default lane) walks every `*.csproj` under the repo root (excluding `bin/`/`obj/`), classifies each per §D2, and fails if any project classified Core carries a `ProjectReference` resolving under `families/**` (resolution against the csproj's own directory, path-normalised — the same technique the estate gates use). An unknown `<BabelstoneRole>` value is itself a failure. The gate asserts at least one `CompositionRoot` exists (so a marker sweep cannot make it vacuous). It runs in the engine CI job, whose path filter includes `**/*.csproj` — so any PR that adds or edits any project file re-runs it.

### P2 — The universal composition-root source gate (`COMPOSITION_ROOT_NAMES_NO_FAMILY`)

For every `CompositionRoot`-marked project, the sibling test scans (a) its `Program.cs` with comments and string literals stripped for any `Babelstone.Families.*` identifier, (b) its `.csproj` for a `<Using>` item importing a family namespace, and (c) every committed `.cs` in the project for a `global using` carrying the family prefix — the row-12b technique, applied uniformly. Pattern-scan on the namespace prefix, never a per-family token list: adding a family edits no gate.

### P3 — The shared scanner and the per-estate loaders

`FamilyModuleScanner` (in `Babelstone.Cadence`) exposes `LoadAll<TModule>(sources, familyKey, moduleKind, duplicateExplanation, activate?)` and `FamilyAssemblies(hostAssembly)`. Default activation requires a public parameterless constructor (the engine/lifecycle/notification module contracts); an estate whose modules take composition ingredients (the orchestrator's `ISagaModule(SagaModuleContext)`) supplies a custom `activate` with its own fail-loud diagnostic. The orchestrator host's explicit module list is replaced by discovery ([ADR-IC-018 §D6](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md)'s anticipated "assembly-scan later"): family `ISagaModule`s are discovered from `Babelstone.Families.*` assemblies; the substrate-owned settlement saga ([ADR-PC-032](./ADR-PC-032-money-movement-primitive.md), ADR-IC-018 Amendment A1/A2) stays host-constructed (it is substrate-named, not family-named) and receives its Movement-bearing subscribe topics as the union of the discovered family modules' declared `FamilyIntegrationTopics` (an additive, defaulted `ISagaModule` member each family module answers from its catalogue-generated constants) — so the host no longer names a family to supply them and ORCH-3's "the substrate names no family topic" is preserved with the host now equally clean.

### P4 — Two gate layers, deliberately

The per-estate, ADR-synced gates (rows 12/12a/12b/ORCH-1/ORCH-2/ORCH-3/NOTIF-1/LCD-3) remain the **decision-linked layer**: each is pinned to its estate ADR's enumeration and fails with that decision's vocabulary. The two universal gates are the **default-deny backstop**: coarser, but total. Removing an estate gate still leaves the backstop; adding an estate does not require touching the backstop. This redundancy is intentional and cheap (all are sub-second disk parses).

### P5 — CI reach

The universal gates live in the engine test suite, and the engine CI job's path filter names their full input surface: every `**/*.csproj`, every `**/Program.cs`, the marked composition-root source dirs, and `cadence/**` (which the engine solution builds). A genuinely new estate directory should be added to that filter when its host is marked — the one residual hand-step, noted in Residual risks.

---

## Consequences

**What this choice makes easier:**

1. **A new core/substrate project cannot silently violate the arrow.** It is gated the moment its `.csproj` exists — the PR #404 class of drift (a core born referencing families) now fails CI instead of shipping.
2. **Opting out is one visible, greppable line.** `<BabelstoneRole>CompositionRoot</BabelstoneRole>` in the project file is reviewable exactly where the exemption takes effect, replacing "absence from a test's allowlist" as the exemption mechanism.
3. **Estate #5 is cheap.** The covenant, both gates, and the discovery mechanism are inherited; only the estate-specific decisions remain to make.
4. **One discovery implementation to maintain.** The compile-graph-elision subtlety (the C# compiler drops an unused `ProjectReference` from IL metadata, so the output-directory probe is the load-bearing anchor) is now encoded once in `FamilyModuleScanner`, not four times.

**What this choice makes harder or impossible:**

1. **A legitimate new family-referencing library must declare itself.** There is no silent path: it is either a composition root, a test rig, a test, or a family — anything else fails. This is the point, but it adds one line of ceremony to rare legitimate cases.
2. **The `<BabelstoneRole>` vocabulary is now load-bearing.** Extending it (a new role value) requires amending this ADR (the gate fails unknown values by design).

**Residual risks:**

- **CI-trigger coverage vs. gate coverage.** The gates are repo-wide by construction, but they *run* in the engine CI lane; that lane's path filter names the current hosts' source dirs plus `**/*.csproj` / `**/Program.cs`. An edit that names a family in a *non-Program* host file of a new, not-yet-listed estate could merge without re-running the source gate until the next triggering change. Mitigation: the filter names its inputs self-documentingly (the `rate_sheets`-filter discipline), and any csproj/Program.cs change — the overwhelmingly common shape of such a drift — triggers repo-wide.
- **The `TestRig` role is trust-based.** A shipping service mis-marked `TestRig` would escape D1. Mitigation: the marker is a one-line, greppable review surface, and the load harness pair are the only intended members.
- **The orchestrator's family-specific edge adapter** (`Edge/`, the term-deposit constitution front door) remains a named family dependency inside a composition root, outside D3's scope. Making the edge family-contributed is real work this ADR deliberately does not decide; it is visible (local `using` in the adapter files) and tracked by the epic's follow-on backlog.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)), registered in the [commitment catalogue](./commitment-catalogue.md) as the single source of truth for claim, gate, and status:

- `FAMILY_TO_CORE_DEFAULT_DENY` — every `.csproj` outside `families/**` that is not a test project and carries no (or a `Core`) `<BabelstoneRole>` references **no** `families/**` project; an unknown role value fails; at least one `CompositionRoot` exists (§D1/§D2, §P1). Catalogue row XC-1, **Live**.
- `COMPOSITION_ROOT_NAMES_NO_FAMILY` — every `CompositionRoot`-marked project's `Program.cs` names no `Babelstone.Families.*` identifier in code, and no `<Using>`/`global using` imports a family namespace into it (§D3, §P2). Catalogue row XC-2, **Live**.

The four estate rows — 12 (`ENGINE_FAMILY_AGNOSTIC`), ORCH-1 (`ORCHESTRATOR_FAMILY_AGNOSTIC`), NOTIF-1 (`NOTIFICATION_FAMILY_AGNOSTIC`), LCD-3 (`LIFECYCLE_FAMILY_AGNOSTIC`) — and the host row 12b (`ENGINE_API_HOST_FAMILY_AGNOSTIC`) remain owned by their estate ADRs (their Verifiable-commitments sections keep referencing them); the catalogue now also cites this ADR on those rows as the shared governing source of the covenant they instantiate (§P4's two-layer posture). The shared `FamilyModuleScanner` (§D4) is exercised by the existing per-estate discovery fitness tests (`HostModuleLoaderTests`, `LifecycleModuleLoaderTests`, `NotificationModuleLoaderTests`, and the orchestrator's `SagaModuleLoaderTests`) plus its own unit tests in `Babelstone.Cadence.Tests` — mechanism reuse, not a new catalogued invariant.

---

## Cross-references

- [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) — the engine-estate instance: the family-as-plugin spine, the §A2 composition-root exemption pattern, and the §A13–§A14 assembly-scan discovery this covenant generalises.
- [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md) — the orchestrator-estate instance; its §D6 "assembly-scan later" posture is realised by §P3's discovery swap.
- [ADR-IC-019](../../integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md) — the notification-estate instance; its module loader now delegates to the shared scanner.
- [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) — the lifecycle-estate instance and the ADR that established `Babelstone.Cadence` as the shared generic-mechanism library the scanner lands in.
- [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) — the fitness-function/catalogue governance both new gates register under; the §D3 explicit-drift gate this ADR's estate-ADR revisions honour.
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the monorepo strategy that makes one repo-wide disk-walk gate possible, and the extraction-ready-subtree posture (§P2) that shaped the deferral of MSBuild-targets enforcement (candidate C).
- [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) — the substrate-owned settlement saga whose subscribe-topic sourcing §P3 re-plumbs onto discovered family declarations.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the load-test harness, the intended (and only) member of the `TestRig` role.

---

*Accepted 2026-07-02 by jhosm.*
