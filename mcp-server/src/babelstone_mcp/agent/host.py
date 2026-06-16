"""Composition root: wire Claude to the live MCP server and run the loop (bd babelstone-f0ic.6.1).

In plain English: this is where the pieces come together. It builds the Claude client (holding the
Anthropic API key, server-side only), opens a connection to the REAL secured MCP server presenting the
gateway-stand-in credential, discovers the deposit tools, and runs the agentic loop — re-yielding its
events for whatever drives it (the SSE endpoint in bd babelstone-f0ic.6.2). It is the ONE place the
``anthropic`` SDK is imported (lazily, so the rest of the package stays importable without it).

Connectivity notes for the demo (ADR-IC-010 §P3/§P5):
- The agent connects to the MCP server's Streamable-HTTP ``/mcp`` route (``BABELSTONE_AGENT_MCP_URL``,
  default ``http://localhost:8080/mcp`` — the demo-mcp bind port). It presents an audience-bound
  bearer whose ``aud`` is ``BABELSTONE_MCP_SERVER_URI`` plus the attested ``X-Client-Id`` /
  ``X-OAuth-Scope`` headers (see ``demo_token`` — the Kong stand-in). The two URLs must agree with the
  server's own env: the connect URL is where the server LISTENS, the audience is what the server
  CHECKS.
- The MCP transport applies DNS-rebinding protection (it 421s a non-allow-listed ``Host``). When
  connecting on ``localhost:8080``, ensure the server's ``BABELSTONE_ALLOWED_HOSTS`` includes
  ``localhost:8080`` (the demo bring-up sets this). This is a demo-wiring detail, exercised by the
  end-to-end smoke (bd babelstone-f0ic.6.4), not by the unit tests here.

The ``ANTHROPIC_API_KEY`` is read from the environment, server-side ONLY — never the browser, never
committed (ADR-IC-014 push-protection would block a committed key).
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from typing import Any, AsyncIterator

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

from .demo_token import DEFAULT_CLIENT_ID, DEFAULT_SCOPES, demo_headers
from .events import AgentError, AgentEvent
from .loop import DEFAULT_MAX_TOKENS, DEFAULT_MAX_TURNS, DEFAULT_MODEL, run_agent
from .mcp_tools import make_dispatch, to_anthropic_tools

# Opus-4-8-tuned operator persona. Concise, act-don't-ask on the obvious mechanics, lead with the
# outcome — matching the project's claude-api behavioral guidance for 4.8.
DEFAULT_SYSTEM = """\
You are a banking operations agent for the babelstone term-deposit engine. You operate the bank by \
calling the provided MCP tools — you never invent deposit data.

Tools and money:
- constitute_deposit opens a deposit; get_deposit reads its position; mature_deposit settles it; \
pay_interest pays one PERIODIC coupon. All money is integer cents (e.g. 10000 euros is 1000000), \
never a float.
- After a write (constitute/mature), if you read the deposit back, pass the commit_sequence the write \
returned as get_deposit's min_sequence so you see your own write.

How to work:
- For mechanics that follow plainly from the instruction (a product code, a sensible default), act \
rather than asking. Only stop to ask when the request is genuinely ambiguous about what to do.
- Lead with the outcome. Keep narration short — one line per action is enough; the operator is \
watching the tool calls.
- If a tool call fails, read the error, adjust, and try a sensible correction rather than repeating \
the same call.
"""


@dataclass(frozen=True)
class AgentConfig:
    """Where the agent connects and how it identifies itself, plus the loop guardrails."""

    mcp_url: str = "http://localhost:8080/mcp"
    audience: str = "http://localhost:8000/mcp"
    client_id: str = DEFAULT_CLIENT_ID
    scopes: tuple[str, ...] = DEFAULT_SCOPES
    model: str = DEFAULT_MODEL
    max_turns: int = DEFAULT_MAX_TURNS
    max_tokens: int = DEFAULT_MAX_TOKENS
    system: str = DEFAULT_SYSTEM

    @classmethod
    def from_env(cls, env: dict[str, str] | None = None) -> "AgentConfig":
        """Build from the environment. ``BABELSTONE_MCP_SERVER_URI`` is the audience the server checks
        (§P3) and must match the server's own value; ``BABELSTONE_AGENT_MCP_URL`` is where to connect.
        """
        e = env if env is not None else os.environ
        return cls(
            mcp_url=e.get("BABELSTONE_AGENT_MCP_URL", "http://localhost:8080/mcp"),
            audience=e.get("BABELSTONE_MCP_SERVER_URI", "http://localhost:8000/mcp"),
            client_id=e.get("BABELSTONE_AGENT_CLIENT_ID", DEFAULT_CLIENT_ID),
            model=e.get("BABELSTONE_AGENT_MODEL", DEFAULT_MODEL),
        )


def _build_anthropic_client() -> Any:
    """Build the async Anthropic client (the ONE place the SDK is imported).

    Reads ``ANTHROPIC_API_KEY`` from the environment — server-side only. Raises a clear error if the
    key is absent so the caller (the SSE endpoint, f0ic.6.2) can fall back to DEMO mode cleanly.
    """
    if not os.environ.get("ANTHROPIC_API_KEY"):
        raise RuntimeError(
            "ANTHROPIC_API_KEY is not set — the real-Claude agent needs it server-side. "
            "Set it in the agent host's environment (never the browser, never committed)."
        )
    try:  # pragma: no cover - the SDK import/instantiation needs the optional agent extra (f0ic.6.4 smoke)
        from anthropic import AsyncAnthropic
    except ImportError as exc:
        raise RuntimeError(
            "The 'anthropic' SDK is not installed. Install the agent extra: "
            "pip install 'babelstone-mcp[agent]'."
        ) from exc
    return AsyncAnthropic()


async def run(instruction: str, *, config: AgentConfig | None = None) -> AsyncIterator[AgentEvent]:
    """Run ``instruction`` end to end: open Claude + the MCP session, then drive the loop.

    Yields the same ``AgentEvent`` stream ``run_agent`` produces. On a setup failure (missing key,
    unreachable MCP server) yields a single ``AgentError`` rather than raising, so a streaming caller
    can surface it as a frame and degrade gracefully.
    """
    cfg = config or AgentConfig.from_env()
    try:
        client = _build_anthropic_client()
    except RuntimeError as exc:
        yield AgentError(str(exc), "exception")
        return

    # The live MCP connection + loop drive — exercised by the end-to-end smoke (bd babelstone-f0ic.6.4),
    # not the unit tests (which target the loop/token/conversion against fakes).
    headers = demo_headers(audience=cfg.audience, client_id=cfg.client_id, scopes=cfg.scopes)  # pragma: no cover
    async with streamablehttp_client(cfg.mcp_url, headers=headers) as (read, write, _get_session_id):  # pragma: no cover
        async with ClientSession(read, write) as session:
            await session.initialize()
            listed = await session.list_tools()
            tools = to_anthropic_tools(listed.tools)
            dispatch = make_dispatch(session)
            async for event in run_agent(
                instruction,
                client=client,
                tools=tools,
                dispatch=dispatch,
                system=cfg.system,
                model=cfg.model,
                max_turns=cfg.max_turns,
                max_tokens=cfg.max_tokens,
            ):
                yield event
