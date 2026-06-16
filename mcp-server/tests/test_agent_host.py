"""Unit tests for the agent composition root's config + graceful no-key path (bd babelstone-f0ic.6.1).

The live MCP connection is covered by the end-to-end smoke (f0ic.6.4); here we prove the parts that
need no network: env-driven config, and that a missing ANTHROPIC_API_KEY degrades to a single
``AgentError`` frame rather than raising (so the SSE endpoint can fall back to DEMO cleanly).
"""

from __future__ import annotations

import pytest

from babelstone_mcp.agent.events import AgentError
from babelstone_mcp.agent.host import AgentConfig, run


def test_config_from_env_reads_connect_url_audience_and_identity() -> None:
    cfg = AgentConfig.from_env(
        {
            "BABELSTONE_AGENT_MCP_URL": "http://mcp-server:8080/mcp",
            "BABELSTONE_MCP_SERVER_URI": "https://mcp.example/mcp",
            "BABELSTONE_AGENT_CLIENT_ID": "CLI-7",
            "BABELSTONE_AGENT_MODEL": "claude-opus-4-8",
        }
    )
    assert cfg.mcp_url == "http://mcp-server:8080/mcp"
    assert cfg.audience == "https://mcp.example/mcp"  # the §P3 aud the server checks
    assert cfg.client_id == "CLI-7"
    assert cfg.model == "claude-opus-4-8"


def test_config_from_env_defaults() -> None:
    cfg = AgentConfig.from_env({})
    assert cfg.mcp_url == "http://localhost:8080/mcp"
    assert cfg.audience == "http://localhost:8000/mcp"
    assert cfg.client_id == "CLI-DEMO-0001"


async def test_run_without_api_key_yields_a_single_error_frame(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    events = [event async for event in run("open a deposit", config=AgentConfig())]
    assert len(events) == 1
    assert isinstance(events[0], AgentError)
    assert events[0].kind == "exception"
    assert "ANTHROPIC_API_KEY" in events[0].message
