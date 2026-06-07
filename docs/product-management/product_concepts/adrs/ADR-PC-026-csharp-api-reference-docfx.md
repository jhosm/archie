# ADR-PC-026: C# API-Reference Surface — DocFX to GitHub Pages

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-07 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (.NET 10 / mise toolchain pin), [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) (documentation architecture this sits beside), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) (the GitHub-Pages static-site precedent) |
| Resolves | bd `babelstone-sfnt.18` (Epic R.18) |

## Context

The engine's C# source carries substantial XML doc comments (~400 `<summary>` blocks
across the `engine/src` and `families/*/src` public surface), but nothing renders them:
`GenerateDocumentationFile` was never enabled, so the comments were reachable only by
reading source. The documentation estate already has two governed surfaces —

- the **concern-axis corpus** (`docs/product-management/`, hand-authored, the normative
  spine), and
- the **generated reference** (`docs/product-management/reference/`,
  [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) §P2: rendered from
  *machine-readable contracts* — Avro, CUE, the MCP tool surface, ADR front-matter — as
  in-tree Markdown, byte-drift-gated by `make docs-verify`).

Neither covers the **C# API surface**: a different source (XML doc comments in code), a
different output (a browsable HTML site), and different drift semantics (the compiler
itself validates doc comments against the code they annotate, so a separate byte-drift
gate adds nothing). This ADR selects the renderer/publisher for that third surface and
fixes the disciplines around it. [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)
already established the in-house pattern: a generated static site, built in CI, deployed
to GitHub Pages, zero runtime cost.

Candidates evaluated:

| Candidate | What it is |
|---|---|
| **DocFX** | .NET Foundation static-site generator; ingests C# projects/XML doc comments natively (Roslyn), stitches Markdown content alongside the API metadata |
| MkDocs (+ Material) | Python SSG; excellent Markdown sites, **no C# XML-doc ingester** (mkdocstrings has no C# handler) |
| Docusaurus | Node/React SSG; no .NET XML-doc ingestion; MDX parsing is strict about raw HTML/JSX in existing Markdown |
| Doxygen | C/C++-first reference generator; C# support is second-class, web output dated, Markdown-corpus stitching weak |

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| DocFX | MIT (.NET Foundation), free | Pass |
| MkDocs | BSD-2, free | Pass |
| Docusaurus | MIT, free | Pass |
| Doxygen | GPL-2 (tool licence does not encumber generated output), free | Pass |

GitHub Pages hosting is free on this public repo (the same footing
[ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) §S1
accepted for EventCatalog).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | Assessment | Verdict |
|---|---|---|
| All four | The site renders only content already public in this repo (source XML doc comments + the docs corpus). No PII enters the pipeline — the no-PII-on-the-bus rule ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) governs runtime data, which never reaches docs. Publishing adds no new regulatory surface. | Pass |

### Soft criteria

#### DocFX — CHOSEN

- **S1 (operational complexity):** one `dotnet` local tool pinned in
  `.config/dotnet-tools.json` and restored by the SDK already pinned in `mise.toml`
  ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) — no new language toolchain,
  no new version-pinning mechanism. `metadata` runs Roslyn over the same
  `Babelstone.slnx` CI already restores.
- **S2 (ecosystem coherence):** the .NET-native answer — XML doc comments are its
  first-class source; it is what learn.microsoft.com's API reference is built on. The
  engine is a C# kernel; its API reference tool speaking C# natively is the coherent
  choice.
- **S3 (exit cost):** lowest possible — the sources of truth remain XML doc comments in
  code and the Markdown corpus. DocFX owns only `docfx/` (config, a landing page, a
  two-entry TOC) and one workflow; swapping renderers later rewrites ~4 small files,
  zero content.
- **S4 (community/longevity):** .NET Foundation project (`dotnet/docfx`), actively
  released (2.78.x line current).
- **Decisive:** the only candidate where the C# API surface needs *zero* hand-rolled
  conversion machinery.

#### MkDocs — rejected

No C# handler for mkdocstrings: the XML-doc→Markdown step would have to be hand-rolled —
re-implementing DocFX's core competence as bespoke code (the
[ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) generator is exactly
this pattern *for contracts*, justified there because the sources are bespoke; for C#
XML docs an off-the-shelf ingester exists). **Decisive: hand-rolling the hard part.**

#### Docusaurus — rejected

Same gap (no .NET XML-doc ingestion) plus a Node/MDX toolchain whose strict MDX parsing
would fight the existing corpus's raw-HTML Markdown. Epic G.4's EventCatalog does bring
Node for *its* site, but that does not buy Docusaurus an XML-doc ingester. **Decisive:
no XML-doc path, MDX friction.**

#### Doxygen — rejected

C# is supported but second-class (XML-doc tag coverage is partial); output styling and
Markdown-corpus stitching are weak for a modern docs site. **Decisive: second-class C#.**

## Decision

1. **§P1 — DocFX renders the C# API-reference surface.** Pinned as a `dotnet` local
   tool in `.config/dotnet-tools.json` (2.78.5 at adoption); config lives in `docfx/`
   (`docfx.json`, landing page, TOC). `make docs-site` builds; `make docs-site-serve`
   previews. API metadata comes from `engine/src/*/*.csproj` and
   `families/*/src/*/*.csproj`, excluding the two Roslyn-analyzer projects (internal
   tooling, `netstandard2.0`) and all test projects.
2. **§P2 — XML doc emission is on repo-wide, with coverage/rot split.**
   `engine/Directory.Build.props` (inherited by `families/`) sets
   `GenerateDocumentationFile=true`. Doc **coverage** is a docs concern, not a build
   gate: `CS1591` (missing comment) and `CS1573` (partially documented params — records
   deliberately document only non-obvious positional params) are in `NoWarn`. Doc
   **rot** is a build break: malformed XML and unresolvable/ambiguous references
   (`CS1570`, `CS1574`, `CS1734`, `CS0419`, …) stay errors under
   `TreatWarningsAsErrors`, so the compiler guards comment↔code integrity on every
   build — the API-reference analogue of ADR-PC-022's drift gate, enforced at a
   stronger level (compiler, not byte-diff).
3. **§P3 — One stitched site, minimal door.** The site carries the API reference
   *alongside* the existing corpus (the README document map, the three series, both ADR
   namespaces, `reference/`, `product-docs/`) — rendered as-is from their existing
   locations. The TOC is deliberately a two-entry door (Document Map + API Reference):
   [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) dropped the
   navigation-overlay genre, and this site must not re-create it as `toc.yml`. The
   corpus navigates through its own cross-links.
4. **§P4 — Publishing is the artifact-based GitHub Pages flow.**
   `.github/workflows/docs-site.yml` builds on PRs touching its inputs (build-only
   validation — *not* a required check, so the
   [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) §P1 always-run-aggregator
   pattern is not needed) and deploys on push to `main` via
   `upload-pages-artifact`/`deploy-pages` with job-scoped `pages: write` +
   `id-token: write`. No `gh-pages` branch — no second rendered-content history to
   drift. First deploy enables Pages (`configure-pages` with `enablement: true`).
5. **§P5 — Single-Pages-slot composition.** A repo has one GitHub Pages site. This
   workflow owns the slot; when the EventCatalog site
   ([ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md),
   Epic G.4) lands, it composes into the *same* artifact under a subpath (e.g.
   `/events/`) inside this workflow — never a second `deploy-pages` workflow racing for
   the slot.

Rejected: MkDocs (hand-rolling the XML-doc ingester), Docusaurus (no XML-doc path, MDX
friction), Doxygen (second-class C#) — details above.

## Consequences

**Easier:** the ~400 existing doc comments become a browsable, searchable API reference
at zero marginal authoring cost; comment↔code integrity is now compiler-enforced on
every build (it never was before — enabling emission immediately surfaced and fixed 18
latent doc-rot errors); the corpus and the API reference cross-resolve in one site;
future families inherit the pipeline by existing (`families/*/src/*/*.csproj` glob).

**Harder/impossible:** every later C# project is in the docs build by default (exclusion
is an explicit `docfx.json` edit); malformed doc comments now break the build —
deliberate, but a new failure mode for contributors; the site rebuilds wholesale on
`main` pushes touching `engine/**` (acceptable: minutes of CI, no runtime cost).

**Residual risks:** GitHub Pages usage limits (published-site size and soft bandwidth
caps) — a docs site is far inside them today, but they are GitHub's to move; the §P5 composition
invariant is review-enforced, not mechanically gated (listed as a Gap below); DocFX
warnings (broken site links) do not fail the build yet — tightening to
`--warningsAsErrors` is a candidate once the corpus's external-file links are
triaged.

## Verifiable commitments

These commitments are documentation-scoped (not engine load-bearing), so they live as an
inline table here rather than in the engine-focused
[commitment catalogue](./commitment-catalogue.md) (the
[ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) precedent).

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The DocFX site builds green from current sources — API metadata over `Babelstone.slnx` + the stitched corpus (§P1/§P3). | CI (`docs-site.yml` build lane, path-scoped on its inputs) | `DOCS_SITE_BUILDS` | Live (lands with this ADR's PR) |
| 2 | XML doc comments cannot rot against the code they annotate: malformed/unresolvable doc references fail the build (§P2). | compiler (`TreatWarningsAsErrors` + `GenerateDocumentationFile`, every `dotnet build` incl. the ci.yml engine lane) | `DOCS_XMLDOC_NO_ROT` | Live (lands with this ADR's PR) |
| 3 | One Pages deploy workflow: EventCatalog (G.4) composes into this artifact under a subpath, never a second `deploy-pages` (§P5). | review discipline (PR review + this ADR) | `DOCS_PAGES_SINGLE_SLOT` | Gap (no mechanical gate; revisit when G.4 lands) |
