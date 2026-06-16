"""Unit tests for the MCP<->Anthropic tool adapters (bd babelstone-f0ic.6.1) — duck-typed, no mcp deps.

These prove the two bridge helpers:
- ``to_anthropic_tools`` maps MCP tool descriptors to Anthropic tool schemas (name/description/
  input_schema), defaulting a missing description to "";
- ``make_dispatch`` runs a tool over a session, prefers the structured (§P6) output serialised to
  JSON, falls back to text content, and raises on a tool error so the loop marks it is_error.
"""

from __future__ import annotations

import json
from types import SimpleNamespace
from typing import Any

import pytest

from babelstone_mcp.agent.mcp_tools import make_dispatch, to_anthropic_tools


def test_to_anthropic_tools_maps_fields() -> None:
    tools = [
        SimpleNamespace(name="get_deposit", description="read a deposit", inputSchema={"type": "object"}),
        SimpleNamespace(name="mature_deposit", description=None, inputSchema={"type": "object", "required": ["deposit_id"]}),
    ]
    converted = to_anthropic_tools(tools)
    assert converted[0] == {
        "name": "get_deposit",
        "description": "read a deposit",
        "input_schema": {"type": "object"},
    }
    # A missing description maps to "" (the API requires the key present).
    assert converted[1]["name"] == "mature_deposit"
    assert converted[1]["description"] == ""
    assert converted[1]["input_schema"]["required"] == ["deposit_id"]


class _FakeSession:
    def __init__(self, result: Any) -> None:
        self._result = result
        self.calls: list[tuple[str, dict[str, Any]]] = []

    async def call_tool(self, name: str, arguments: dict[str, Any]) -> Any:
        self.calls.append((name, arguments))
        return self._result


async def test_dispatch_prefers_structured_output_as_json() -> None:
    result = SimpleNamespace(
        structuredContent={"deposit_id": "d-1", "total_payout_cents": 1_021_900}, content=[], isError=False
    )
    session = _FakeSession(result)
    dispatch = make_dispatch(session)

    out = await dispatch("get_deposit", {"deposit_id": "d-1"})

    assert json.loads(out) == {"deposit_id": "d-1", "total_payout_cents": 1_021_900}
    assert session.calls == [("get_deposit", {"deposit_id": "d-1"})]


async def test_dispatch_falls_back_to_text_content() -> None:
    result = SimpleNamespace(
        structuredContent=None,
        content=[SimpleNamespace(type="text", text="d-1 is Active")],
        isError=False,
    )
    out = await make_dispatch(_FakeSession(result))("get_deposit", {"deposit_id": "d-1"})
    assert out == "d-1 is Active"


async def test_dispatch_raises_on_tool_error() -> None:
    result = SimpleNamespace(
        structuredContent=None,
        content=[SimpleNamespace(type="text", text="deposit cannot mature")],
        isError=True,
    )
    with pytest.raises(RuntimeError, match="cannot mature"):
        await make_dispatch(_FakeSession(result))("mature_deposit", {"deposit_id": "d-1"})
