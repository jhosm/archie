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

# 1. Install the bd binary from the gastownhall/beads fork (idempotent — skip if
#    already on PATH). We build from source: the GitHub release API is rate-limited
#    (HTTP 403) behind the agent proxy, so the prebuilt-binary path is unavailable.
#
#    gastownhall/beads keeps its Go module path as github.com/steveyegge/beads, so
#    a plain `go install github.com/gastownhall/beads/...` fails with a module-path
#    mismatch. We use a throwaway module with a `replace` directive that redirects
#    the steveyegge module path to the gastownhall fork, so `go build` compiles
#    gastownhall's actual source. The bd binary lives in the cmd/bd subpackage and
#    needs the gms_pure_go build tag; CGO gives the embedded-Dolt build that
#    `bd bootstrap` needs (Go's toolchain switching pulls the required go >= 1.26.2).
if ! command -v bd >/dev/null 2>&1; then
  log "installing bd (beads) from gastownhall/beads — first run builds from source..."
  work="$(mktemp -d)"
  (
    cd "$work"
    go mod init beads-build >/dev/null 2>&1
    # Redirect the steveyegge module path to the gastownhall fork; `go get` then
    # resolves @latest to a concrete version and adds the require line for us.
    go mod edit -replace=github.com/steveyegge/beads=github.com/gastownhall/beads@latest
    GOFLAGS=-mod=mod go get github.com/steveyegge/beads/cmd/bd@latest
    if ! CGO_ENABLED=1 GOFLAGS="-mod=mod -tags=gms_pure_go" go build -o "$GOBIN_DIR/bd" github.com/steveyegge/beads/cmd/bd; then
      log "CGO build failed; retrying without CGO..."
      CGO_ENABLED=0 GOFLAGS="-mod=mod -tags=gms_pure_go" go build -o "$GOBIN_DIR/bd" github.com/steveyegge/beads/cmd/bd
    fi
  )
  rm -rf "$work"
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
