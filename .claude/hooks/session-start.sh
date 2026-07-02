#!/bin/bash
#
# SessionStart hook — install the `bd` (beads) issue tracker CLI and hydrate its
# Dolt database, so the mandated bd workflow (CLAUDE.md §Beads Issue Tracker,
# §Session Completion) works inside ephemeral "Claude Code on the web" sessions.
#
# On a real dev machine bd + dolt come from Homebrew (Brewfile / INSTALL.md); the
# web container has neither, so we install bd here and clone the issue DB from the
# git remote. The container image is cached after this hook finishes, so the
# one-time build + DB download is paid once per environment, not once per session.
#
set -euo pipefail

# Only run in the remote (web) execution environment. Local machines manage bd,
# dolt and the Go toolchain via brew + mise — do not interfere with that setup.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

log() { echo "[session-start] $*" >&2; }

if ! command -v go >/dev/null 2>&1; then
  log "ERROR: go toolchain not found; cannot install bd (beads)."
  exit 1
fi

# `go install` drops binaries in $(go env GOPATH)/bin. Put it on PATH for this
# hook AND for the whole session (via $CLAUDE_ENV_FILE) so `bd` resolves later.
GOBIN_DIR="$(go env GOPATH)/bin"
export PATH="$PATH:$GOBIN_DIR"
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export PATH=\"\$PATH:$GOBIN_DIR\"" >> "$CLAUDE_ENV_FILE"
fi

# 1. Install the bd binary (idempotent — skip if already on PATH).
#    We build from source with `go install` rather than the upstream release
#    installer: the GitHub release API is rate-limited (HTTP 403) behind the
#    agent proxy, so the prebuilt-binary path fails. Go's automatic toolchain
#    switching fetches the required go >= 1.26.2, and bd's embedded Dolt needs
#    CGO (the remote image ships a C toolchain).
if ! command -v bd >/dev/null 2>&1; then
  log "installing bd (beads) via go install — first run builds from source..."
  # The bd binary lives in the cmd/bd subpackage and needs the gms_pure_go build
  # tag (matches the upstream installer). CGO gives the embedded-Dolt build that
  # `bd bootstrap` needs; fall back to a CGO-less build if the C path fails.
  if ! CGO_ENABLED=1 GOFLAGS="-tags=gms_pure_go" go install github.com/steveyegge/beads/cmd/bd@latest; then
    log "CGO build failed; retrying without CGO..."
    CGO_ENABLED=0 GOFLAGS="-tags=gms_pure_go" go install github.com/steveyegge/beads/cmd/bd@latest
  fi
  log "bd installed: $(command -v bd) ($(bd version 2>/dev/null | head -1))"
else
  log "bd already installed: $(command -v bd)"
fi

# Silence bd's 0700-permissions nag (dir perms are not git-tracked, so this
# leaves the working tree clean).
[ -d "$CLAUDE_PROJECT_DIR/.beads" ] && chmod 700 "$CLAUDE_PROJECT_DIR/.beads" 2>/dev/null || true

# 2. Hydrate the local Dolt database from the git remote (refs/dolt/data).
#    A fresh clone only carries the passive .beads/issues.jsonl mirror; the real
#    source of truth is the Dolt DB. `bd bootstrap` clones it — but with
#    sync.remote configured it is NOT idempotent: on a resumed/cached session the
#    DB already exists and a second bootstrap fails ("database exists"). So probe
#    first with `bd ready`, and only bootstrap when there is no working DB.
#    Best-effort: a transient network error must not block the session start.
if bd -C "$CLAUDE_PROJECT_DIR" ready >/dev/null 2>&1; then
  log "beads database already hydrated — 'bd ready' lists available work."
else
  log "hydrating beads database (bd bootstrap)..."
  if bd -C "$CLAUDE_PROJECT_DIR" bootstrap --yes >/dev/null 2>&1; then
    log "beads database ready — 'bd ready' will list available work."
  else
    log "WARNING: bd bootstrap did not complete; run 'bd bootstrap --yes' manually."
  fi
fi
