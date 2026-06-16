#!/usr/bin/env bash
#
# demo-agent.sh — one-command REAL-Claude demo (bd babelstone-f0ic.6.4).
#
# The strongest version of the AI-native story: a real model operating the bank. It brings up the
# engine + MCP server (by reusing scripts/demo-mcp.sh — the walking skeleton), installs the agent
# extra, then starts:
#   • the real-Claude AGENT HOST (POST /agent/stream → SSE) — it calls Claude with the babelstone
#     deposit tools bound and drives them through the REAL secured MCP server (ADR-IC-010 §P3/§P4)
#   • the Mission Control proxy (serve.py), so the browser reaches /agent same-origin
# then point a browser at http://localhost:9000, flip the Operator toggle to CLAUDE, type an
# instruction, and watch the model constitute → read → mature a deposit live.
#
# This is the MINIMAL real-agent path: it inherits demo-mcp's Postgres-only engine, so the LIVE·saga
# Mode is NOT available here. For the WHOLE UI (LIVE·engine AND LIVE·saga AND Operator=CLAUDE in one
# bring-up), use scripts/demo-all.sh / `make demo` instead.
#
# ANTHROPIC_API_KEY is required and lives SERVER-SIDE ONLY (in the agent host) — never the browser,
# never committed (ADR-IC-014). If it is absent the agent host is SKIPPED and CLAUDE mode degrades
# to an illustrative narration (the UI's graceful fallback). The engine + MCP + UI still come up.
#
# Usage:
#   ANTHROPIC_API_KEY=sk-ant-… scripts/demo-agent.sh [up]   # run, leave it up    (make demo-agent)
#   scripts/demo-agent.sh down                              # stop agent + UI     (make demo-agent-down)
#
# Overridable env: MCP_PORT ENGINE_PORT AGENT_BIND_PORT MC_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# shellcheck source=scripts/demo-lib.sh
. "$ROOT/scripts/demo-lib.sh"

MCP_PORT="${MCP_PORT:-8000}"          # the MCP server demo-mcp.sh starts (its aud + listen port)
ENGINE_PORT="${ENGINE_PORT:-8080}"
AGENT_PORT="${AGENT_BIND_PORT:-8091}"
MC_PORT="${MC_PORT:-9000}"
RUNDIR="$ROOT/.demo-agent"            # logs + pidfiles (gitignored)
VENV_PY="$ROOT/mcp-server/.venv/bin/python"

# ---------------------------------------------------------------------------
# down — stop the agent host + UI this demo started, then the engine + MCP
# ---------------------------------------------------------------------------
if [ "${1:-up}" = "down" ]; then
  say "Stopping the agent host + Mission Control"
  stop_pidfile "$RUNDIR/serve.pid" "Mission Control proxy"
  stop_pidfile "$RUNDIR/agent.pid" "agent host"
  pkill -f 'babelstone_mcp.agent' 2>/dev/null && ok "swept stray agent host process(es)" || true
  pkill -f 'mission-control/serve.py' 2>/dev/null && ok "swept stray Mission Control process(es)" || true
  scripts/demo-mcp.sh down
  exit 0
fi

[ "${1:-up}" = "up" ] || die "usage: $0 [up|down]"
mkdir -p "$RUNDIR"

# ---------------------------------------------------------------------------
# 1. engine + MCP server (reuse the walking-skeleton bring-up)
# ---------------------------------------------------------------------------
say "1/4 Bringing up the engine + MCP server (scripts/demo-mcp.sh)"
scripts/demo-mcp.sh up

# ---------------------------------------------------------------------------
# 2. install the agent extra (anthropic) into the MCP venv demo-mcp created
# ---------------------------------------------------------------------------
say "2/4 Installing the agent extra (anthropic) into mcp-server/.venv"
[ -x "$VENV_PY" ] || die "mcp-server/.venv not found — did scripts/demo-mcp.sh complete?"
setup_mcp_venv agent
ok "anthropic installed"

# ---------------------------------------------------------------------------
# 3. start the real-Claude agent host (only when the key is present)
# ---------------------------------------------------------------------------
if [ -n "${ANTHROPIC_API_KEY:-}" ]; then
  say "3/4 Starting the real-Claude agent host on http://localhost:${AGENT_PORT}"
  start_agent_host "http://localhost:${MCP_PORT}/mcp" "${AGENT_PORT}" "$RUNDIR/agent.pid" "$RUNDIR/agent.log"
  AGENT_NOTE="real model — Operator=CLAUDE runs Claude through the MCP tools"
else
  say "3/4 ANTHROPIC_API_KEY not set — SKIPPING the agent host"
  warn "Operator=CLAUDE will degrade to an illustrative narration (set ANTHROPIC_API_KEY to run a real model)"
  AGENT_NOTE="illustrative — set ANTHROPIC_API_KEY and re-run for a real model"
fi

# ---------------------------------------------------------------------------
# 4. start the Mission Control proxy (serves the UI + /agent same-origin)
# ---------------------------------------------------------------------------
say "4/4 Starting Mission Control on http://localhost:${MC_PORT}"
AGENT_URL="http://localhost:${AGENT_PORT}" ENGINE_URL="http://localhost:${ENGINE_PORT}" MC_PORT="${MC_PORT}" \
  nohup python3 docs/demo/mission-control/serve.py > "$RUNDIR/serve.log" 2>&1 &
echo $! > "$RUNDIR/serve.pid"
wait_up "http://localhost:${MC_PORT}/" 20 "Mission Control" "$RUNDIR/serve.log"

cat <<DONE

$(printf '\033[1;32m✓ Real-Claude demo is up.\033[0m')

  UI       http://localhost:${MC_PORT}        (logs: .demo-agent/serve.log)
  agent    http://localhost:${AGENT_PORT}        (logs: .demo-agent/agent.log)
  engine   http://localhost:${ENGINE_PORT}        MCP  http://localhost:${MCP_PORT}/mcp
  ${AGENT_NOTE}

Drive it:
  • open http://localhost:${MC_PORT}
  • flip the Operator toggle (top-right) to CLAUDE
  • type, e.g.: "open a €10,000 12-month deposit at maturity and mature it" → Run
  • watch the model's narration + REAL MCP tool calls stream into the console, and the
    living ledger + position fold out of the results.

Stop everything (engine + MCP + agent + UI):
  make demo-agent-down
DONE
