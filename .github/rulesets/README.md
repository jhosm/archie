# Branch rulesets — source of record

GitHub branch rulesets are **remote repository state**, not config GitHub reads from
the repo. This directory is the version-controlled **source of record** for them, so the
protection on `main` is auditable in git and reproducible if it is ever lost or changed
out-of-band. The live ruleset is the authority; this JSON must be kept in sync with it.

## `main-protect.json`

Protects the default branch (`~DEFAULT_BRANCH`). Wired by **Q.7** (bd `archie-j72w`,
"wire fitness/coverage/conformance as required checks"); honours [ADR-PC-019 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)
(path-scoped CI) and [ADR-PC-020](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
(fitness functions / explicit-drift gate).

- **`deletion` + `non_fast_forward`** — `main` cannot be deleted or force-pushed.
- **`pull_request` (0 approvals)** — every change to `main` merges via a PR (enforces the
  CLAUDE.md "never commit/push directly to main" policy at the platform level — the local
  `.githooks/pre-push` guard is bypassable). 0 required approvals keeps it
  solo-maintainer-friendly; raise it when the team grows.
- **`required_status_checks`** — the checks that must be green to merge:
  - **`CI gate`** — the always-run aggregator in [`ci.yml`](../workflows/ci.yml). It covers
    every path-scoped per-subtree job without the skipped-required-check footgun (a skipped
    job never satisfies a required check, so the per-subtree jobs are *not* required
    directly). `DETERMINISM_GATE` (A.7, `archie-k03q`) is already covered here — the
    HandlerPurity analyser + fixture-replay determinism test run inside the `engine` job's
    non-Integration tier, which the gate already `needs:`. The still-pending fitness gates —
    acceptance gates (L.3), no-PII/emit-contract (G.6) — join the gate's `needs:` as they
    land, growing coverage without editing this ruleset.
  - **`CodeQL gate`** — the always-run aggregator in [`codeql.yml`](../workflows/codeql.yml),
    the SAST twin of `CI gate` (ADR-IC-014, Q.7). CodeQL is path-scoped at its workflow
    trigger, so — exactly like `CI gate` — the ruleset requires this one always-present gate
    rather than the per-language `Analyze (…)` jobs (which skip on docs-only PRs). The gate
    requires that CodeQL analysis actually **ran and succeeded** on every scannable PR.
    Findings-blocking is staged: [`codeql-failon.sh`](../scripts/codeql-failon.sh) flags
    results at `error` level or `security-severity >= 7.0` (GitHub's default check-failure
    bar, in a version-controlled, locally-tested script rather than a hidden repo UI setting),
    but ships **report-only** (`CODEQL_FAILON_ENFORCE` unset) because a pre-existing
    report-only alert backlog must be triaged before blocking (ADR-IC-014 residual risk
    "Baseline noise"). Flip `CODEQL_FAILON_ENFORCE=1` in `codeql.yml` once triaged.
  - **`dependency-review`** — the [`dependency-review.yml`](../workflows/dependency-review.yml)
    GitHub-native SCA gate (ADR-IC-014, Q.7c `archie-2t16.11`). Diffs the dependency graph
    between the PR base and head and **blocks** when a PR introduces a dependency carrying a
    HIGH/CRITICAL advisory — the blocking companion to Dependabot, whose alerts cannot block a
    merge. It runs on **every** PR (no paths filter), so it always reports and is required
    directly — no always-run aggregator is needed (unlike `CI gate` / `CodeQL gate`, whose
    analysis jobs are path-scoped and skip on unrelated PRs). The grype SBOM scan
    ([`sbom.yml`](../workflows/sbom.yml)) stays **report-only**: it CPE-matches the published
    .NET assembly tree and false-positives on framework version strings, so it is SBOM / DORA
    evidence + defense-in-depth, not a gate.
  - **`pr-body-adrs`** + **`adr-immutability`** — the [`adr-governance.yml`](../workflows/adr-governance.yml)
    explicit-drift gate (ADR-PC-020 §D3).
  - **`spec-coverage`** — the [`spec-coverage.yml`](../workflows/spec-coverage.yml) ADR↔catalogue↔test
    coverage checker (ADR-PC-020 §P6).

## Applying / re-applying

List rulesets and find the id:

```bash
gh api repos/jhosm/babelstone/rulesets --jq '.[] | {id, name}'
```

Update the live ruleset from this file (replace `<ID>`):

```bash
gh api -X PUT repos/jhosm/babelstone/rulesets/<ID> --input .github/rulesets/main-protect.json
```

To create it fresh (if it does not exist):

```bash
gh api -X POST repos/jhosm/babelstone/rulesets --input .github/rulesets/main-protect.json
```
