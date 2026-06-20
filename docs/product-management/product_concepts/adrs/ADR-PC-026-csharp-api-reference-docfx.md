# ADR-PC-026: C# API-Reference Surface — DocFX to GitHub Pages

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-07 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (.NET 10 / mise toolchain pin), [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) (documentation architecture this sits beside), [ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md) (the GitHub-Pages static-site precedent) |
| Resolves | bd `babelstone-sfnt.18` (Epic R.18) |

## Context

The engine's C# source carries substantial XML doc comments (~400 `<summary>` blocks
across the `engine/src` and `families/*/src` public surface), but nothing renders them:
`GenerateDocumentationFile` was never enabled, so the comments were reachable only by
reading source. The documentation estate already has two governed surfaces —

- the **concern-axis corpus** (`docs/product-management/`, hand-authored, the normative
  spine), and
- the **generated reference** (`docs/product-docs/reference/` — moved there 2026-06-19, bd `babelstone-sfnt.26`; `docs/product-management/reference/` at this ADR's authoring,
  [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) §P2: rendered from
  *machine-readable contracts* — Avro, CUE, the MCP tool surface, ADR front-matter — as
  in-tree Markdown, byte-drift-gated by `make docs-verify`).

Neither covers the **C# API surface**: a different source (XML doc comments in code), a
different output (a browsable HTML site), and different drift semantics (the compiler
itself validates doc comments against the code they annotate, so a separate byte-drift
gate adds nothing). This ADR selects the renderer/publisher for that third surface and
fixes the disciplines around it. [ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md)
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
[ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md) §S1
accepted for EventCatalog).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | Assessment | Verdict |
|---|---|---|
| All four | The site renders only content already public in this repo (source XML doc comments). No PII enters the pipeline — the no-PII-on-the-bus rule ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) governs runtime data, which never reaches docs. Publishing adds no new regulatory surface. | Pass |

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
3. **§P3 — The site is the C# API reference only; the corpus is not stitched.** DocFX
   publishes *only* the generated API metadata plus a one-page landing and a minimal
   navbar (`docfx/index.md`, `docfx/toc.yml`). The hand-authored corpus (the three
   series, both ADR namespaces, `reference/`, `product-docs/`) is **not** rendered here
   — it reads on GitHub, where every relative cross-link resolves against the repo tree
   *at the revision being viewed*. That property is load-bearing: the corpus links to
   repo files DocFX does not publish (`packs/`, `contracts/`, `.github/`, `engine/`
   source, `Makefile`, …); stitching the corpus into the site turns each of those into
   a 404 on the published site, whose only repair is an absolute `…/blob/<ref>/…` URL —
   and a ref fixed at authoring time is wrong for every *other* version once babelstone
   is multi-version. Keeping the corpus on GitHub's at-ref relative rendering avoids the
   problem at its root rather than patching it. The API reference, generated from code,
   carries no cross-root links and is self-contained. The navbar
   ([ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) dropped the
   navigation-overlay genre, and this site must not re-create one) is a single
   `API Reference` entry plus one outbound link to the repo docs on GitHub.

   *Revised 2026-06-11: the original §P3 stitched the corpus alongside the API
   reference in one site. That coupling forced corpus links escaping the DocFX content
   root to render as 404s on the published site, "fixable" only by `main`-pinned
   absolute URLs that are wrong under multi-version docs. Narrowed to
   API-reference-only; the corpus stays on GitHub's version-correct relative rendering.
   The `docs/**`, `README.md`, and `INSTALL.md` trigger inputs were dropped from
   `docs-site.yml` to match, and `docfx.json` no longer stitches `../docs` or the root
   READMEs.*

   *Revised 2026-06-19 (bd `babelstone-sfnt.26`): the **landing page is now the
   config-author product-docs README** (`docs/product-docs/README.md`), making
   product-docs the site's front door rather than a bespoke one-paragraph page;
   `docfx/index.md` is a one-line DocFX `[!include]` of that README, so it stays the
   single source (no copy). The navbar's outbound link now points at
   `…/tree/main/docs/product-docs` (was `…/docs`), since product-docs — which now
   also hosts the moved generated `reference/` (ADR-PC-022 §P1, same-day amendment) —
   is the docs root a reader should land in. The §P3 invariant is **deliberately
   narrowed, not abandoned**: still exactly **one** corpus page is rendered here (the
   README, as the home page), and the rest of the corpus is **not** stitched — it
   reads on GitHub. The README's own deeper links (into `tutorials/`, `reference/`,
   `../product-management/`) are not published in the site; they resolve on GitHub,
   reached via the "Docs on GitHub" navbar entry — so the multi-version-404 problem
   §P3 designs out is **contained to that single landing page**, whose job is to route
   readers onward to the version-correct GitHub tree. This is a scoped exception (one
   landing doc), not a return to the rejected corpus-wide stitching.*

   *Revised 2026-06-20 (bd `babelstone-517i`): the landing page's own deeper links
   no longer 404 on the site. `docfx/index.md` is **generated** from the
   product-docs README by `scripts/docs-gen/generate.py` (ADR-PC-022's generator,
   reused) — the README's corpus-relative links are rewritten to absolute
   `…/blob|tree/main/…` GitHub URLs, and the generated file is byte-drift-gated by
   `make docs-verify` and the pre-commit hook, exactly like the `reference/` tree.
   The README on GitHub keeps its version-correct relative links untouched. This
   does **not** reopen the rejected main-pinned-absolute-URL approach for the
   *corpus*: the objection there was that one ref is wrong for every other version
   once docs are multi-version, but this single page is itself built **only** from
   `main` (the Pages artifact is main-only — same reason the navbar's "Docs on
   GitHub" entry already pins main), so a main-pin is correct here, not a
   compromise. The home page now routes readers onward with working links rather
   than relying on the "Docs on GitHub" navbar entry alone. Two standalone demo
   pages (the pitch deck, the design principles) were also added to the navbar,
   pointing at the pages staged into the Pages artifact under `/demo/` (bd
   `babelstone-q096`).*
4. **§P4 — Publishing is the artifact-based GitHub Pages flow.**
   `.github/workflows/docs-site.yml` builds on PRs touching its inputs (build-only
   validation — *not* a required check, so the
   [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) §P1 always-run-aggregator
   pattern is not needed) and deploys on push to `main` via
   `upload-pages-artifact`/`deploy-pages` with job-scoped `pages: write` +
   `id-token: write`. No `gh-pages` branch — no second rendered-content history to
   drift. The Pages site itself was a one-time admin-credential creation
   (2026-06-07, `build_type=workflow`): `GITHUB_TOKEN` cannot create a Pages site
   (that needs `administration: write`, not grantable to workflow tokens), so the
   workflow only deploys to the existing slot.
5. **§P5 — Single-Pages-slot composition.** A repo has one GitHub Pages site. This
   workflow owns the slot; when the EventCatalog site
   ([ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md),
   Epic G.4) lands, it composes into the *same* artifact under a subpath (e.g.
   `/events/`) inside this workflow — never a second `deploy-pages` workflow racing for
   the slot.

   *Revised 2026-06-20: G.4 (PR #110) superseded
   [ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md)
   with [ADR-IC-015](../../integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md)
   (EventCatalog → Backstage). The catalogue portal is now a separately-deployed runtime
   service (host deploy deferred — bd `babelstone-s4ol.1`), not a static site stitched
   into Pages, so the specific EventCatalog-on-Pages composition task (bd
   `babelstone-ra1u`) is withdrawn. The invariant generalises and still binds: this
   workflow owns the one Pages slot, and any future catalogue portal published to Pages
   composes into this same artifact under a subpath — never a second `deploy-pages`
   workflow.*

Rejected: MkDocs (hand-rolling the XML-doc ingester), Docusaurus (no XML-doc path, MDX
friction), Doxygen (second-class C#) — details above.

## Consequences

**Easier:** the ~400 existing doc comments become a browsable, searchable API reference
at zero marginal authoring cost; comment↔code integrity is now compiler-enforced on
every build (it never was before — enabling emission immediately surfaced and fixed 18
latent doc-rot errors); the corpus stays on GitHub's version-correct relative
rendering (it is not stitched here, §P3); future families inherit the pipeline by
existing (`families/*/src/*/*.csproj` glob).

**Harder/impossible:** every later C# project is in the docs build by default (exclusion
is an explicit `docfx.json` edit); malformed doc comments now break the build —
deliberate, but a new failure mode for contributors; the site rebuilds wholesale on
`main` pushes touching `engine/**` (acceptable: minutes of CI, no runtime cost).

**Residual risks:** GitHub Pages usage limits (published-site size and soft bandwidth
caps) — a docs site is far inside them today, but they are GitHub's to move; the §P5 composition
invariant is review-enforced, not mechanically gated (listed as a Gap below). The
broken-site-link failure mode a corpus-stitching site would have carried is **designed
out** by §P3, not deferred: the corpus is not published here, so its cross-root links
cannot 404 on the site, and the API reference is generated from code and is
self-contained — so no DocFX link-gate is needed.

## Verifiable commitments

These commitments are documentation-scoped (not engine load-bearing), so they live as an
inline table here rather than in the engine-focused
[commitment catalogue](./commitment-catalogue.md) (the
[ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) precedent).

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The DocFX site builds green from current sources — API metadata over `Babelstone.slnx`; the corpus is not stitched (§P1/§P3). | CI (`docs-site.yml` build lane, path-scoped on its inputs) | `DOCS_SITE_BUILDS` | Live |
| 2 | XML doc comments cannot rot against the code they annotate: malformed/unresolvable doc references fail the build (§P2). | compiler (`TreatWarningsAsErrors` + `GenerateDocumentationFile`, every `dotnet build` incl. the ci.yml engine lane) | `DOCS_XMLDOC_NO_ROT` | Live (lands with this ADR's PR) |
| 3 | One Pages deploy workflow: any future catalogue portal published to Pages composes into this artifact under a subpath, never a second `deploy-pages` (§P5). | review discipline (PR review + this ADR) | `DOCS_PAGES_SINGLE_SLOT` | Gap (no mechanical gate; EventCatalog-on-Pages task withdrawn under ADR-IC-015 — Backstage is a separate runtime portal, not a Pages static site; see §P5 revision 2026-06-20) |
