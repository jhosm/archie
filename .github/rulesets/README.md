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
    directly). The pending fitness gates — `DETERMINISM_GATE` (A.7, `archie-k03q`),
    acceptance gates (L.3), no-PII/emit-contract (G.6) — join the gate's `needs:` as they
    land, growing coverage without editing this ruleset.
  - **`pr-body-adrs`** + **`adr-immutability`** — the [`adr-governance.yml`](../workflows/adr-governance.yml)
    explicit-drift gate (ADR-PC-020 §D3).
  - **`spec-coverage`** — the [`spec-coverage.yml`](../workflows/spec-coverage.yml) ADR↔catalogue↔test
    coverage checker (ADR-PC-020 §P6).

Not yet required (tracked under Q.7): **CodeQL** code-scanning ([ADR-IC-014](../../docs/product-management/integration_concepts/adrs/ADR-IC-014-static-analysis-and-supply-chain-scanning.md)
ties its required-gating + `fail-on` to Q.7). CodeQL is path-scoped at the workflow
trigger, so making it requireable needs an always-run gate of its own — a remaining Q.7
sub-task, landing with the other pending fitness gates.

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
