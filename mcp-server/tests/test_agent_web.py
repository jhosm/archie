"""Unit tests for the agent host HTTP app (bd babelstone-f0ic.6.2) — ASGI round-trip, no model.

Prove POST /agent/stream streams the agent's events as ordered SSE frames, and that a malformed body
or a missing instruction is a 400. ``run`` is monkeypatched to a fake async generator, so no Claude
call and no MCP connection happen — the test exercises the endpoint + serialisation wiring only.
"""

from __future__ import annotations

from typing import AsyncIterator

from httpx import ASGITransport, AsyncClient

from babelstone_mcp.agent import web
from babelstone_mcp.agent.events import AgentEvent, Done, Narration, ToolCall, ToolResult


def _client() -> AsyncClient:
    return AsyncClient(transport=ASGITransport(app=web.build_app()), base_url="http://agent")


async def test_stream_serialises_events_in_order(monkeypatch) -> None:
    async def fake_run(instruction: str) -> AsyncIterator[AgentEvent]:
        assert instruction == "open a 10k deposit and mature it"
        yield Narration("Opening the deposit.")
        yield ToolCall(tool="constitute_deposit", input={"principal_cents": 1_000_000}, id="t1")
        yield ToolResult(tool="constitute_deposit", id="t1", output='{"deposit_id":"d-1"}')
        yield Done(summary="Matured.", turns=3)

    monkeypatch.setattr(web, "run", fake_run)

    async with _client() as client:
        resp = await client.post("/agent/stream", json={"instruction": "open a 10k deposit and mature it"})

    assert resp.status_code == 200
    assert "text/event-stream" in resp.headers["content-type"]
    body = resp.text
    # Each event surfaced as its named frame...
    assert "event: narration" in body
    assert "event: tool_call" in body
    assert "event: tool_result" in body
    assert "event: done" in body
    # ...in the order the loop yielded them.
    assert body.index("narration") < body.index("tool_call") < body.index("tool_result") < body.index("done")


async def test_setup_failure_streams_a_single_error_frame(monkeypatch) -> None:
    # run() yields an error event (e.g. missing API key) rather than raising — the endpoint still
    # returns 200 + an SSE error frame so the UI can fall back to DEMO cleanly.
    from babelstone_mcp.agent.events import AgentError

    async def fake_run(instruction: str) -> AsyncIterator[AgentEvent]:
        yield AgentError("ANTHROPIC_API_KEY is not set", "exception")

    monkeypatch.setattr(web, "run", fake_run)

    async with _client() as client:
        resp = await client.post("/agent/stream", json={"instruction": "go"})

    assert resp.status_code == 200
    assert "event: error" in resp.text
    assert "ANTHROPIC_API_KEY" in resp.text


async def test_missing_instruction_is_400() -> None:
    async with _client() as client:
        resp = await client.post("/agent/stream", json={})
    assert resp.status_code == 400


async def test_blank_instruction_is_400() -> None:
    async with _client() as client:
        resp = await client.post("/agent/stream", json={"instruction": "   "})
    assert resp.status_code == 400


async def test_malformed_body_is_400() -> None:
    async with _client() as client:
        resp = await client.post(
            "/agent/stream", content="not json", headers={"content-type": "application/json"}
        )
    assert resp.status_code == 400
