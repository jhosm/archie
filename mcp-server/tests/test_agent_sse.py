"""Unit tests for the agent->SSE serialisation contract (bd babelstone-f0ic.6.2).

Prove each event type maps to its named SSE frame with a JSON-decodable data payload, and that prose
with newlines stays on a single ``data:`` line (a raw newline would break SSE framing).
"""

from __future__ import annotations

import json

from babelstone_mcp.agent.events import (
    AgentError,
    Done,
    Narration,
    Thinking,
    ToolCall,
    ToolResult,
)
from babelstone_mcp.agent.sse import event_to_sse, sse_name


def _parse(frame: str) -> tuple[str, dict]:
    assert frame.endswith("\n\n")  # terminated by a blank line
    event_line, data_line = frame.rstrip("\n").split("\n")
    return event_line.removeprefix("event: "), json.loads(data_line.removeprefix("data: "))


def test_event_names() -> None:
    assert sse_name(Narration("x")) == "narration"
    assert sse_name(Thinking("x")) == "thinking"
    assert sse_name(ToolCall(tool="t", input={}, id="i")) == "tool_call"
    assert sse_name(ToolResult(tool="t", id="i", output="o")) == "tool_result"
    assert sse_name(Done(summary="s", turns=1)) == "done"
    assert sse_name(AgentError("m", "refusal")) == "error"


def test_narration_and_thinking_frames() -> None:
    assert _parse(event_to_sse(Narration("Opening the deposit."))) == ("narration", {"text": "Opening the deposit."})
    assert _parse(event_to_sse(Thinking("Plan: open then mature."))) == ("thinking", {"text": "Plan: open then mature."})


def test_tool_call_frame_carries_tool_input_id() -> None:
    name, data = _parse(event_to_sse(ToolCall(tool="constitute_deposit", input={"principal_cents": 1_000_000}, id="t1")))
    assert name == "tool_call"
    assert data == {"tool": "constitute_deposit", "input": {"principal_cents": 1_000_000}, "id": "t1"}


def test_tool_result_frame_carries_output_and_error_flag() -> None:
    ok = _parse(event_to_sse(ToolResult(tool="get_deposit", id="t2", output='{"deposit_id":"d-1"}', is_error=False)))
    assert ok == ("tool_result", {"tool": "get_deposit", "id": "t2", "output": '{"deposit_id":"d-1"}', "is_error": False})
    err = _parse(event_to_sse(ToolResult(tool="mature_deposit", id="t3", output="422: not at term", is_error=True)))
    assert err[1]["is_error"] is True


def test_done_and_error_frames() -> None:
    assert _parse(event_to_sse(Done(summary="All set.", turns=4))) == ("done", {"summary": "All set.", "turns": 4})
    name, data = _parse(event_to_sse(AgentError("hit the limit", "max_turns", {"max_turns": 6})))
    assert name == "error"
    assert data == {"message": "hit the limit", "kind": "max_turns", "details": {"max_turns": 6}}


def test_multiline_prose_stays_one_data_line() -> None:
    frame = event_to_sse(Narration("line one\nline two"))
    # Exactly one data line — the newline is JSON-escaped, not emitted raw. (_parse's two-part
    # unpack below would raise if the payload had spilled onto a second line.)
    assert frame.count("data: ") == 1
    _, data = _parse(frame)
    assert data["text"] == "line one\nline two"
