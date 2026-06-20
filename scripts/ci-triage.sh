#!/usr/bin/env bash
# ci-triage — turn a red CI run into a local to-do list.
#
# The ~14 path-scoped ci.yml jobs collapse into one always-run `CI gate` aggregator that fails
# with just `result=failure` (ci.yml, ADR-PC-019 §P1) — it never says WHICH job failed or how to
# reproduce it, so a red gate means hand-driving `gh run view`. This helper lists a run's failed
# jobs and maps each to the ONE local command that reproduces it (most of which `make preflight`
# already bundles). Deliberately narrow: it does NOT classify flaky-vs-real, chase coverage
# floors, or map failures to ADRs — that judgement is the adr-conformance agent's job. This is
# pure navigation: failed job -> the command to run next.
#
# Usage:
#   scripts/ci-triage.sh                 # latest run on the current branch (make ci-triage)
#   scripts/ci-triage.sh <run-id>        # a specific run    (make ci-triage RUN=<run-id>)
#   scripts/ci-triage.sh --pr <number>   # latest run on a PR's head branch
# Needs: gh (authenticated). bash 3.2-safe (macOS).
set -euo pipefail

command -v gh >/dev/null 2>&1 || {
  echo "ci-triage: needs the GitHub CLI (gh) — https://cli.github.com/ , then 'gh auth login'." >&2
  exit 2
}

run=""
case "${1:-}" in
  -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
  --pr)
    pr="${2:-}"; [ -n "$pr" ] || { echo "ci-triage: --pr needs a PR number" >&2; exit 2; }
    branch="$(gh pr view "$pr" --json headRefName -q .headRefName)"
    run="$(gh run list --branch "$branch" --limit 1 --json databaseId -q '.[0].databaseId')"
    ;;
  "")
    branch="$(git rev-parse --abbrev-ref HEAD)"
    run="$(gh run list --branch "$branch" --limit 1 --json databaseId -q '.[0].databaseId')"
    ;;
  *) run="$1" ;;
esac

[ -n "$run" ] || {
  echo "ci-triage: no CI run found — push the branch first, or pass a run id / --pr <n>." >&2
  exit 1
}

# The local reproduction command for a failed job, keyed by its name. Order matters: the more
# specific pattern must precede the broader one (pack-validate before pack, docs-verify before docs).
reproduce() {
  case "$1" in
    *engine*)             echo 'make preflight   # or: mise exec -- dotnet test engine/Babelstone.slnx --configuration Release --filter "Category!=Integration"' ;;
    *orchestrator*)       echo 'mise exec -- dotnet test orchestrator/tests/Babelstone.Orchestrator.Tests/Babelstone.Orchestrator.Tests.csproj --configuration Release --filter "Category!=Integration"' ;;
    *pack-validate*)      echo 'make pack-validate-test' ;;
    *contracts*)          echo 'make contracts-check && make asyncapi-catalog-validate' ;;
    *mcp*)                echo 'cd mcp-server && mise exec -- python -m venv .venv && .venv/bin/python -m pip install -e ".[dev]" && .venv/bin/python -m pytest -q' ;;
    *product-config*)     echo 'make pack-validate-test   # then re-run product-config validation (ci.yml: product-configs)' ;;
    *rate-sheet*)         echo 'make contracts-check   # rate-sheet refs cue-vet against #RateSheetRefs (ci.yml: rate-sheets)' ;;
    *pack*)               echo 'make pack-validate PACK=<pt.YYYY.N>' ;;
    *infra*)              echo './scripts/kong-config-check.sh' ;;
    *docs-verify*)        echo 'make docs-verify' ;;
    *docs*)               echo 'bash .github/scripts/diagram-render-check.sh   # (+ the lychee link check; see ci.yml: docs)' ;;
    *[Cc]ode[Qq][Ll]*)    echo 'bash .github/scripts/codeql-failon.test.sh   # gate-logic test; review the run SARIF for the finding' ;;
    *adr*|*ADR*)          echo 'bash .github/scripts/adr-immutability-check.test.sh   # + check the PR-body "ADRs touched/honoured" section' ;;
    *acl*|*notification*) echo '(stub job — build lands later; inspect the job log)' ;;
    *gate*)               echo '(aggregator — the real failure is one of the jobs above)' ;;
    *)                    echo "(no local mapping — open the job log:  gh run view $run --log-failed)" ;;
  esac
}

echo "CI run $run"
gh run view "$run" --json workflowName,headBranch,status,conclusion \
  -q '"  workflow: \(.workflowName)\n  branch:   \(.headBranch)\n  status:   \(.status) / \(.conclusion // "running")"'

failed="$(gh run view "$run" --json jobs -q '.jobs[] | select(.conclusion=="failure") | .name')"
if [ -z "$failed" ]; then
  echo
  echo "  No failed jobs on this run — nothing to triage."
  exit 0
fi

echo
echo "Failed jobs → reproduce locally:"
printf '%s\n' "$failed" | while IFS= read -r job; do
  [ -n "$job" ] || continue
  printf '\n  ✗ %s\n' "$job"
  printf '      %s\n' "$(reproduce "$job")"
done

echo
echo "Tip: 'make preflight' runs the fast hermetic tiers in one shot."
echo "     Full logs:  gh run view $run --log-failed"
