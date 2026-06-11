# ADR-IC-014: Static Analysis and Supply-Chain Scanning — GitHub-Native Trio (CodeQL + Dependabot + Secret Scanning)

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-30 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-009](./ADR-IC-009-testing-infrastructure.md), [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md), [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) |
| Resolves | bd `archie-lrzc` (Q.8 — secret scanning + dependency audit), bd `archie-2t16.7` (Q.9 — SAST / CodeQL) |

---

## Context

[ADR-IC-009](./ADR-IC-009-testing-infrastructure.md) settles *test* infrastructure across four areas — integration-container management, behavioural contract testing, external-service simulation, and resilience injection. It does not decide the adjacent concern that lives in the same orbit: **static analysis of the source itself, and the provenance of the dependencies it pulls in.** That gap is what Epic Q closes — Q.8 (secret scanning + dependency audit, framed as DORA evidence) and Q.9 (SAST / CodeQL across the estate). This ADR records the tool pick for both, in one place, because the three mechanisms below are one coherent supply-chain posture, not three independent purchases.

**The estate to be scanned** (per [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P1, [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md)) is multi-language and lives in one monorepo:

- **C#** — the `engine/` solution (event store, financial-math kernel, PII boundary, analysers) and the four in-house estate services (orchestrator, ACL, notification — [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md)/[004](./ADR-IC-004-outbox-pattern-mechanism.md)/[012](./ADR-IC-012-anti-corruption-layer-implementation.md)/[011](./ADR-IC-011-async-saga-completion-notification.md)).
- **Go** — the `pack-validate/` validator binary ([ADR-PC-006](../../product_concepts/adrs/ADR-PC-006-cue-schema-language.md)).
- **Python** — the MCP server ([ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md)), a stub today (no `.py` source yet) — wired so SAST turns on automatically when code lands.

**Two concrete SAST motivations** name the highest-value findings for this estate (Q.9):

- **Deserialization safety** around YamlDotNet — product configs, packs, and rate-sheets are YAML, and an unsafe deserialiser is the classic RCE foothold in a config-driven engine.
- **Crypto-misuse** around the OpenBao / crypto-shredding boundary ([ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) — the per-subject-key erasure scheme is only as strong as its key handling; a hard-coded IV, a reused nonce, or an ECB-mode call there silently breaks the GDPR erasure guarantee.

### The posture that makes the cost question concrete — repository visibility

The deciding cost fact is **repository visibility**, because several GitHub-native security features are free on **public** repositories but require paid **GitHub Advanced Security (GHAS)** on **private** ones:

| Feature | Public repo | Private repo without GHAS |
|---|---|---|
| CodeQL *code-scanning upload* (Security tab + PR annotations) | **Free** | Paid (GHAS) |
| GitHub secret scanning + push protection | **Free** | Paid (GHAS Secret Protection) |
| Dependabot (version PRs + alerts) | **Free** | **Free** |

This repository is **public**, so all three native legs are free, and [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md)'s **F1 (zero cost)** hard filter passes the GitHub-native trio outright. (Posture note: F1 is *visibility-dependent* here — on a private repo without GHAS it would `Fail` the two GHAS-gated legs and force a free fallback of CodeQL-analysis-to-SARIF-artifact + gitleaks for secrets. That fallback is recorded under "Residual risks" as the contingency if the repo ever goes private without GHAS.)

**Three sub-concerns, each with candidates:**

| Sub-concern | Question | Candidates |
|---|---|---|
| **SAST** | How is the source statically analysed for vulnerabilities across C#/Go/Python? | CodeQL · Semgrep OSS · SonarQube CE |
| **SCA / dependency audit** | How are vulnerable and stale dependencies (NuGet, Go modules, GitHub Actions) surfaced and updated? | Dependabot · Renovate · Trivy / OSV-Scanner |
| **Secret scanning** | How are committed (and about-to-be-committed) secrets caught? | GitHub secret scanning + push protection · gitleaks · TruffleHog |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost on a **public repo** | Verdict |
|---|---|---|
| CodeQL (→ Security tab) | Code-scanning is free on public repos; CodeQL runs on GitHub-hosted runners at no cost. | **Pass** |
| Semgrep OSS | LGPL CLI + open rules; free. | **Pass** |
| SonarQube CE | LGPL; free, but self-hosted (a server + database to operate). | **Pass** |
| Dependabot | GitHub-native; version updates **and** alerts free. | **Pass** |
| Renovate | OSS (self-hosted) or free Mend-hosted app. | **Pass** |
| Trivy / OSV-Scanner | Apache-2.0 / open; free. | **Pass** |
| GitHub secret scanning + push protection | Free on public repos. | **Pass** |
| gitleaks / TruffleHog | MIT / AGPL+OSS; free. | **Pass** |

*Date of licence assessment: 2026-05-30.* On a public repo F1 does not discriminate; the decision rides on S1–S2 plus the two SAST correctness motivations. (Were the repo private without GHAS, F1 would discriminate — see the posture note above.)

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

All candidates analyse source code and dependency metadata at CI time. None persists PII, processes payment data, or runs in a regulated runtime path; all are GitHub-hosted within the existing CI trust boundary or self-hosted. GDPR/DORA/PSD2 impose no discriminating constraint — every candidate returns **Pass**. (The DORA *posture* point — demonstrable supply-chain risk management — is a correctness/coverage property handled in the soft criteria.) Note: a public repository exposes source publicly by design; this is an accepted project decision and the no-PII / no-secret invariants this ADR enforces are exactly what keep that exposure safe.

### Soft criteria

#### GitHub-native trio (CodeQL + Dependabot + secret scanning + push protection) — CHOSEN

**S1 · Operational complexity (1–2 people).** The decisive axis. All three are first-party to the platform the project already runs CI on, with **zero operated infrastructure**:

- **CodeQL** runs as a GitHub Action; results land in the Security tab and as PR annotations. For C# it supports `build-mode: none` (buildless extraction) — no .NET SDK pin, no `.slnx` build in the security lane; for Go it autobuilds the single module.
- **Dependabot** is a repo-level config file (`.github/dependabot.yml`); it opens dependency-update PRs and raises alerts with no external service, no token wiring, no cron of our own.
- **Secret scanning + push protection** is a repo *setting* — push protection blocks a secret *before* it enters history, which is strictly stronger than detecting one after the fact, and needs no pipeline step at all.

SonarQube CE fails this axis hard: a server + Postgres to run, patch, and back up — disproportionate at 1–2-person scale, the same reason [ADR-IC-009](./ADR-IC-009-testing-infrastructure.md) rejected operationally heavy options and [ADR-PC-020](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) rejected wholesale TLA⁺. Self-hosted Renovate, gitleaks-in-CI, and TruffleHog all add a moving part the native equivalent makes unnecessary now that the natives are free.

**S2 · Ecosystem coherence.** The trio shares one home (the GitHub Security tab), one surfacing mechanism (PR annotations + alerts), and one trust boundary (GitHub-hosted CI, already the substrate per [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md), [`ci.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/ci.yml)). A single [`codeql.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/codeql.yml) matrix covers every compiled language; one [`dependabot.yml`](https://github.com/jhosm/babelstone/blob/main/.github/dependabot.yml) covers every package ecosystem (NuGet, Go modules, **and the GitHub Actions the other workflows pin** — closing the action-pinning supply-chain gap); secret scanning needs no file at all. The two named SAST motivations are squarely in CodeQL's wheelhouse: its C# query packs cover insecure-deserialization sinks (YamlDotNet) and the crypto-misuse rules (weak cipher mode, static IV/nonce, hard-coded key) map directly onto the [ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) boundary.

**S3 · Exit cost.** Low and bounded. SARIF (CodeQL's output) is an open, portable format; Dependabot config is declarative and small; the secret-scanning setting carries no lock-in. Swapping CodeQL for Semgrep later is a workflow change, not a data migration — findings are not a source of truth, the code is.

**S4 · Community + longevity.** First-party, maintained by GitHub/Microsoft, with continuously updated query and advisory databases (the GitHub Advisory Database backs both CodeQL and Dependabot). No third-party-maintainer abandonment risk.

**Decisive reason:** on a public repo the native trio delivers SAST + SCA + secret scanning with **zero operated infrastructure** and one review surface, covers exactly the two correctness motivations Q.9 names, and — uniquely among the candidates — gives **push-time secret blocking** (push protection) that no in-CI scanner can match. No competing combination matches that ops/coherence profile without adding a server or a redundant pipeline step.

#### Semgrep OSS — rejected (SAST)

Strong, fast, and rule-transparent — a credible CodeQL alternative and a reasonable *future* supplement for custom project-specific rules. Rejected as the primary on S2: it would be a second SAST surface alongside the native Security tab, and CodeQL's dataflow depth on the insecure-deserialization and crypto-misuse sinks (the two named motivations) is the better out-of-the-box fit. Reserved as a possible later addition if a bespoke rule need arises.

#### SonarQube CE — rejected (SAST)

Rejected on S1: a self-hosted server + database is disproportionate operational weight for a 1–2-person team, with no offsetting coverage advantage over CodeQL for this estate.

#### Renovate — rejected (SCA)

More configurable than Dependabot and excellent at scale. Rejected on S1/S2: self-hosting adds a moving part, and the hosted app adds a second external surface, when Dependabot already covers NuGet + Go modules + GitHub Actions natively in the Security tab. Re-evaluate only if Dependabot's grouping/scheduling proves insufficient.

#### Trivy / OSV-Scanner — rejected (SCA, here)

Excellent CLI scanners — and **Trivy is the right tool for the *container* image + SBOM scan in Q.4** ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) per-service Dockerfiles), a distinct concern from source-dependency audit. Rejected for *this* ADR's dependency-audit leg only: running them as an extra CI step duplicates what Dependabot surfaces natively. A scope boundary, not a quality rejection — the two coexist.

#### gitleaks / TruffleHog — rejected (secret scanning)

Solid free in-CI secret scanners — and gitleaks was the chosen secret-scanning leg while this repo was briefly private without GHAS (native push protection was paywalled then). Now that the repo is public, **native push protection blocks a secret before it lands**, which an in-CI scanner cannot — it only catches a secret already committed. The native control is strictly stronger, so gitleaks is retired here; a pre-commit gitleaks hook remains a fine *optional local* supplement.

---

## Decision

**Chosen: the GitHub-native trio.**

1. **SAST — CodeQL.** A [`.github/workflows/codeql.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/codeql.yml) matrix over `csharp` (build-mode `none`) and `go` (build-mode `autobuild`), triggered on PRs and pushes to `main` that touch the relevant source paths, plus a weekly scheduled full scan. Results upload to the Security tab (free on a public repo). Python is listed in the matrix design and turned on when `mcp-server/` gains `.py` source. **Report-only at first** (no `fail-on`, not a required check yet) — see Consequences.
2. **SCA / dependency audit — Dependabot.** A [`.github/dependabot.yml`](https://github.com/jhosm/babelstone/blob/main/.github/dependabot.yml) covering `nuget` (engine + estate services), `gomod` (`pack-validate`), and `github-actions` (the workflows' own pinned actions), weekly with PR grouping. Dependabot **alerts** are enabled at the repo level.
3. **Secret scanning — GitHub secret scanning + push protection.** Enabled as a **repository setting** (Settings → Code security); free on a public repo. Push protection blocks a committed secret before it reaches history.

**Container-image / SBOM scanning (Trivy) is out of scope here** and belongs to Q.4; **required-check gating is deferred to Q.7** (bd `archie-j72w`) — it flips CodeQL to `fail-on` and adds the security jobs to the required-check set. This ADR establishes the baseline in report-only mode so it can settle before becoming blocking.

---

## Consequences

**Easier:**

- One review surface (the Security tab + PR annotations) for SAST, SCA, and secrets — no separate dashboard, no server to operate, and **push-time secret blocking** that an in-CI scanner cannot provide.
- The two named correctness risks — YamlDotNet deserialization and [ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) crypto-misuse — are continuously analysed by query packs built for exactly those sink classes.
- Dependency *and* GitHub-Actions-pinning drift is surfaced and auto-PR'd, closing a supply-chain gap (an unpinned/compromised action) that nothing else in the pipeline watched.
- DORA supply-chain evidence is produced as a by-product of normal CI — the Security-tab history and alert ledger *are* the demonstrable-management artefact, per the [ADR-PC-020](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)/[ADR-IC-009](./ADR-IC-009-testing-infrastructure.md) framing.

**Harder / impossible:**

- **Report-only is not yet a gate.** Until Q.7 wires required checks (and flips CodeQL to `fail-on`), a finding annotates a PR but does **not** block merge. A deliberate, time-boxed baseline-settling choice, surfaced here so it cannot drift into a silent permanent state.
- **Push protection depends on a repo setting** that lives outside version control. If the setting is off, the control is absent with no file to reveal it — so the setting state is part of the repo's security posture, asserted in the PR that introduced this ADR.
- Custom/bespoke SAST rules are not covered by CodeQL's default packs; a Semgrep supplement would be a follow-up if a project-specific rule need arises.

**Residual risks:**

- **Visibility coupling.** The zero-cost premise for the two GHAS-gated legs (CodeQL upload, secret scanning + push protection) holds *because the repo is public*. If it ever goes **private without GHAS**, those two legs revert to paid and the free fallback is: CodeQL analysis with `upload: never` publishing SARIF as a workflow artifact, and **gitleaks** in CI for secrets (Dependabot stays free either way). This contingency is recorded so the reversal is a known, documented switch rather than a surprise.
- **CodeQL C# `build-mode: none` coverage.** Buildless extraction trades a little dataflow precision for zero build-toolchain coupling. Accepted for the report-only baseline; if a motivation-relevant query needs a built database, that one language leg can switch to a `manual` build (mise-pinned .NET, as [`ci.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/ci.yml) does) without changing the rest.
- **Baseline noise.** The first scans may surface pre-existing findings; triage is expected before Q.7 makes them blocking. The report-only window exists precisely to absorb this.
- **Python gap until `mcp-server/` lands.** SAST coverage of the MCP server is `Planned`, not live, until its source exists — a visible, intended hole, not a silent one.

---

## Verifiable commitments

> No executable engine-runtime commitments — this decision is realised as CI/CD configuration (CodeQL, Dependabot) and a repository setting (secret scanning + push protection), not as engine code with a fitness function in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md). Its *gates are the workflows and settings themselves*: the presence and green status of the [`codeql.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/codeql.yml) analysis, the [`dependabot.yml`](https://github.com/jhosm/babelstone/blob/main/.github/dependabot.yml) ecosystems, and push protection. Making these **required checks** is the bd `archie-j72w` (Q.7) deliverable; when that lands, the security jobs join the [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P1 path-scoped required-check set.

---

## Amendment — 2026-06-06: the SCA leg's PR-blocking is a layer of per-ecosystem gates; the grype SBOM scan stays report-only

The original Decision named **Dependabot** as the SCA / dependency-audit leg and rejected extra in-CI SCA scanners (Trivy / OSV-Scanner) as duplicating what Dependabot surfaces natively (§"Trivy / OSV-Scanner — rejected"). It deferred making the security legs **blocking** required checks to Q.7 (§Decision; §Consequences "Report-only is not yet a gate"; §Verifiable commitments). Wiring that gating (Q.7c, bd `archie-2t16.11`, discovered from the Q.7b triage `archie-2t16.10`) surfaced that **Dependabot alerts cannot block a merge**: Dependabot opens update PRs and raises Security-tab alerts, but nothing in it fails a PR check when that PR *introduces* a vulnerable dependency. The blocking realization is therefore a **layer of per-ecosystem gates** — language-native where one exists, plus one GitHub-native cross-ecosystem catch-all. This amendment records that layer and the report-only role of the grype SBOM scan.

### A1 · The blocking-SCA layer, by ecosystem

| Ecosystem | Blocking gate | How it blocks |
|---|---|---|
| **NuGet** (engine + families) | **NuGetAudit** (NU1901–1904) + `TreatWarningsAsErrors` in the engine build ([`engine/Directory.Build.props`](https://github.com/jhosm/babelstone/blob/main/engine/Directory.Build.props) → `CI gate`) | restore-time, CONCRETE resolved versions (direct + transitive), no false positives. *Already present before Q.7c; recorded here because the original Decision named only Dependabot for the NuGet leg, yet NuGetAudit — not Dependabot or any Security-tab tool — is what actually fails the build on a vulnerable NuGet package.* |
| **Go** (pack-validate) | **govulncheck** (reachability-based) in the pack-validate CI job ([`ci.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/ci.yml) → `CI gate`) | fails only on advisories reachable from the module's own call graph (low-noise). Baseline triaged to clean first — Q.7c bumped go 1.26.3→1.26.4 (two reachable stdlib advisories: GO-2026-5037 crypto/x509, GO-2026-5039 net/textproto) and `golang.org/x/net` 0.52.0→0.55.0 (GO-2026-5026) — the same triage-before-blocking discipline as Q.7b. |
| **GitHub Actions; Python when `mcp-server/` lands; npm; …** | **`actions/dependency-review-action`** ([`dependency-review.yml`](https://github.com/jhosm/babelstone/blob/main/.github/workflows/dependency-review.yml) → required check `dependency-review`) | GitHub-native; on every PR it diffs the dependency **graph** (base→head) and fails on an introduced advisory at/above HIGH. The uniform cross-ecosystem catch-all and the **primary** gate for ecosystems with no language-native one. For NuGet it is redundant *and* inert (this repo uses Central Package Management without committed `packages.lock.json`, so the graph records NuGet packages version-less as `>= 0` — NuGetAudit is the NuGet gate); for Go it is belt-and-suspenders behind govulncheck. |

`dependency-review` is GitHub-native, reads the same GitHub Advisory Database as Dependabot, and is free on a public repo, so it sits inside the §S1/§S2 GitHub-native posture (one home, one trust boundary, one advisory DB) rather than being the third-party scanner the Decision rejected. Dependabot remains the **non-blocking** companion across all ecosystems — it keeps the dependency graph fresh and opens update PRs + alerts. The new checks join the [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P1 path-scoped required-check set (`dependency-review` directly; NuGetAudit and govulncheck via `CI gate`).

### A2 · The grype SBOM scan stays REPORT-ONLY

Q.4 (bd `archie-2t16.9`) added a CycloneDX SBOM (syft) + grype scan over the *published .NET assembly tree* — a tool the original Decision did not name (it named **Trivy** for the *container-image* SBOM, which remains out of scope here). grype CPE-matches framework assembly version strings, pinned at `X.0.0.0` across every patch, so it false-positives against advisory *package* ranges: the Q.7b triage dismissed all 35 such findings as false positives, while the authoritative `dotnet list package --vulnerable --include-transitive` reported the solution clean. Making grype blocking would demand perpetual CPE-suppression upkeep for no signal the gates above do not already provide more precisely. It is retained for its genuine, unique value — the **CycloneDX SBOM as DORA supply-chain evidence**, plus a Security-tab defense-in-depth cross-check over the *published / native* closure (e.g. bundled native libraries the package graph never sees) — and is explicitly **not** a gate. This closes the previously-unrecorded grype-vs-Trivy/Dependabot tool gap (the Q.4 acceptance criterion: "format/tool is consistent with ADR-IC-014, or the gap is recorded via amendment").

### A3 · This amends the Decision; it does not supersede this ADR

The chosen tools are unchanged: CodeQL (SAST), Dependabot (SCA / dependency audit), GitHub secret scanning + push protection. This amendment (a) records the per-ecosystem blocking-SCA layer that realizes the deferred Q.7 "make the SCA leg blocking" step — NuGetAudit (NuGet), govulncheck (Go), dependency-review-action (cross-ecosystem) — with Dependabot remaining the non-blocking graph/alert companion, and (b) records that the Q.4 grype SBOM scan is SBOM/DORA evidence + defense-in-depth, report-only by decision, not a blocking gate. The §F1 public-repo cost premise and the report-only-then-blocking staging are unchanged.
