#!/usr/bin/env bash
#
# Stop hook — surface the mandatory session-close push protocol, but ONLY when work
# is pending (dirty tree or unpushed commits), so a clean stop stays silent.
# ADR-PC-020 §P1 + CLAUDE.md "Session Completion": not complete until pushed.

input="$(cat)"
cwd="$(printf '%s' "$input" | jq -r '.cwd // empty')"
[ -n "$cwd" ] && cd "$cwd" 2>/dev/null
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0

pending=""
[ -n "$(git status --porcelain 2>/dev/null)" ] && pending="uncommitted changes"
if git rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1; then
  ahead="$(git rev-list --count '@{u}'..HEAD 2>/dev/null || echo 0)"
  if [ "${ahead:-0}" != "0" ]; then
    [ -n "$pending" ] && pending="${pending} + ${ahead} unpushed commit(s)" || pending="${ahead} unpushed commit(s)"
  fi
fi
[ -n "$pending" ] || exit 0

msg="Pending work (${pending}). CLAUDE.md session-close is MANDATORY — not complete until pushed:  git pull --rebase → bd dolt push → git push → git status (must show 'up to date with origin'). Run 'bd dolt push' BEFORE committing .beads/issues.jsonl (the export lags)."
jq -n --arg m "$msg" '{systemMessage:$m}'
exit 0
