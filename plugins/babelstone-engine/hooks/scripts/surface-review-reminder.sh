#!/usr/bin/env bash
#
# PreToolUse (surfacing, not a gate) — about to open/push a PR: remind to run the
# `review` skill (the §P3 domain reviewers) over the diff first, if it hasn't been
# run. ADR-PC-020 §P1 (hooks surface, never gate) / §P3 (the review layer is
# dev-time judgement, advisory). Fires on `gh pr create` / `git push -u …`, and
# only when the diff vs origin/main touches a governed code/contract/adr path —
# stays silent otherwise (a surface hook says nothing when there's nothing to say).

input="$(cat)"
cmd="$(printf '%s' "$input" | jq -r '.tool_input.command // empty')"

# Only the "this PR is going out" moments. Bail fast on every other Bash call.
case "$cmd" in
  *"gh pr create"*|*"git push -u"*|*"git push --set-upstream"*) ;;
  *) exit 0 ;;
esac

root="$(git rev-parse --show-toplevel 2>/dev/null || echo "${CLAUDE_PROJECT_DIR:-.}")"

# What does this branch change vs the PR base? (merge-base so a stale branch
# doesn't surface upstream churn.) No diff / no base -> nothing to review.
changed="$(git -C "$root" diff --merge-base origin/main --name-only 2>/dev/null)" || exit 0
[ -n "$changed" ] || exit 0

# Governed paths — the union of every reviewer's stated scope (the `review` skill
# discovers the precise routing; this is just the "is a reviewer relevant at all?"
# trip-wire).
if ! printf '%s\n' "$changed" | grep -qE '^(engine|families|orchestrator|acl|notification|mcp-server|contracts|pack-validate)/|^docs/.*/adrs/'; then
  exit 0
fi

msg="About to open/push this PR. Its diff touches engine/contract/adr code — run the \`/babelstone-engine:review\` skill over the diff first (it fans out the §P3 domain reviewers and rolls up their verdicts) if you haven't already. Advisory, not a gate; the mechanical CI gates still run regardless."
jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PreToolUse",additionalContext:$c}}'
exit 0
