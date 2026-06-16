"""The real-Claude demo agent (bd babelstone-f0ic.6.1).

In plain English: this package is the server-side brain for the investor demo's "AI operates the
bank" story. It takes a natural-language instruction, calls Claude (the Anthropic API) with the
babelstone deposit tools bound, lets Claude decide which tools to call, executes those calls against
the REAL secured MCP server, and yields a stream of events (Claude's narration, each tool call, each
result) that the Mission Control console renders. Today the demo's "Claude" console is hand-written
theatre; this makes it genuinely real.

Design (so it stays testable and conformant):
- ``loop.run_agent`` is the pure agentic loop — it takes an injected Anthropic-like client and a
  ``dispatch`` callable, so its control flow (turn cap, ``stop_reason`` branching, ``tool_result``
  threading, refusal handling) is unit-tested with fakes and never touches the network. It does NOT
  import ``anthropic``.
- ``demo_token`` mints the gateway-stand-in credential — an audience-bound bearer plus the
  gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` headers — so the agent traverses the REAL
  ADR-IC-010 §P3/§P4 edge (audience re-check + scope-per-tool) instead of a side door.
- ``mcp_tools`` converts the MCP tool surface to Anthropic tool schemas and adapts ``call_tool``.
- ``host`` is the composition root: it builds the ``anthropic`` client (the ONE place the SDK is
  imported, lazily) and opens the MCP Streamable-HTTP session, then runs the loop.

The ``anthropic`` SDK is an OPTIONAL dependency (the ``agent`` extra) — the core MCP server does not
need it, and the unit tests for the loop/token/conversion run without it installed.
"""

from __future__ import annotations

from .events import (
    AgentError,
    AgentEvent,
    Done,
    Narration,
    Thinking,
    ToolCall,
    ToolResult,
)
from .loop import DEFAULT_MAX_TOKENS, DEFAULT_MAX_TURNS, DEFAULT_MODEL, run_agent

__all__ = [
    "AgentError",
    "AgentEvent",
    "Done",
    "Narration",
    "Thinking",
    "ToolCall",
    "ToolResult",
    "run_agent",
    "DEFAULT_MODEL",
    "DEFAULT_MAX_TURNS",
    "DEFAULT_MAX_TOKENS",
]
