"""Unit tests for the agentic tool-use loop (bd babelstone-f0ic.6.1) — fakes, no network.

These prove the loop's control flow against an injected fake Anthropic client and a fake dispatch:
- a constitute -> get -> mature flow drives real tool calls and threads each tool_result back with the
  matching tool_use_id (the API's hard requirement);
- a tool error is fed back to Claude as an is_error tool_result rather than crashing the run;
- a refusal stops cleanly; the turn ceiling is a hard backstop;
- streamed text and summarised thinking surface as events;
- the request carries the model, adaptive thinking, tools, and system prompt.

No ``anthropic`` import is needed — the loop is duck-typed, so the agent extra need not be installed.
"""

from __future__ import annotations

from types import SimpleNamespace
from typing import Any, AsyncIterator

from babelstone_mcp.agent.loop import _THINKING, run_agent
from babelstone_mcp.agent.events import (
    AgentError,
    Done,
    Narration,
    Thinking,
    ToolCall,
    ToolResult,
)


# --- fakes -----------------------------------------------------------------------------------------


def _text_delta(text: str) -> SimpleNamespace:
    return SimpleNamespace(type="content_block_delta", delta=SimpleNamespace(type="text_delta", text=text))


def _thinking_delta(text: str) -> SimpleNamespace:
    return SimpleNamespace(
        type="content_block_delta", delta=SimpleNamespace(type="thinking_delta", thinking=text)
    )


def _text_block(text: str) -> SimpleNamespace:
    return SimpleNamespace(type="text", text=text)


def _tool_block(name: str, inp: dict[str, Any], block_id: str) -> SimpleNamespace:
    return SimpleNamespace(type="tool_use", name=name, input=inp, id=block_id)


def _final(content: list[Any], stop_reason: str) -> SimpleNamespace:
    return SimpleNamespace(content=content, stop_reason=stop_reason)


class _FakeStream:
    def __init__(self, deltas: list[Any], message: Any) -> None:
        self._deltas = list(deltas)
        self._i = 0
        self._message = message

    async def __aenter__(self) -> "_FakeStream":
        return self

    async def __aexit__(self, *exc: Any) -> bool:
        return False

    def __aiter__(self) -> "_FakeStream":
        return self

    async def __anext__(self) -> Any:
        if self._i >= len(self._deltas):
            raise StopAsyncIteration
        item = self._deltas[self._i]
        self._i += 1
        return item

    async def get_final_message(self) -> Any:
        return self._message


class _FakeMessages:
    def __init__(self, turns: list[tuple[list[Any], Any]]) -> None:
        self._turns = list(turns)
        self.calls: list[dict[str, Any]] = []

    def stream(self, **kwargs: Any) -> _FakeStream:
        # Snapshot the messages list at call time — the loop mutates the same list in place across
        # turns (the SDK reads it when stream() is called), so a by-reference capture would show only
        # the final state. The inner message dicts are never mutated after being appended, so a
        # shallow copy of the list faithfully records what each turn was sent.
        snapshot = dict(kwargs)
        snapshot["messages"] = list(kwargs.get("messages", []))
        self.calls.append(snapshot)
        deltas, message = self._turns.pop(0)
        return _FakeStream(deltas, message)


class _FakeClient:
    def __init__(self, turns: list[tuple[list[Any], Any]]) -> None:
        self.messages = _FakeMessages(turns)


async def _collect(agen: AsyncIterator[Any]) -> list[Any]:
    return [event async for event in agen]


# --- tests -----------------------------------------------------------------------------------------


async def test_constitute_get_mature_flow_threads_tool_results() -> None:
    turns = [
        (
            [_text_delta("Opening the deposit.")],
            _final(
                [_text_block("Opening the deposit."), _tool_block("constitute_deposit", {"principal_cents": 1_000_000}, "t1")],
                "tool_use",
            ),
        ),
        ([], _final([_tool_block("get_deposit", {"deposit_id": "d-1", "min_sequence": 0}, "t2")], "tool_use")),
        ([], _final([_tool_block("mature_deposit", {"deposit_id": "d-1"}, "t3")], "tool_use")),
        ([_text_delta("Matured to 10,219.00 EUR.")], _final([_text_block("Matured to 10,219.00 EUR.")], "end_turn")),
    ]
    client = _FakeClient(turns)

    dispatched: list[tuple[str, dict[str, Any]]] = []

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        dispatched.append((name, arguments))
        return {
            "constitute_deposit": '{"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0}',
            "get_deposit": '{"deposit_id": "d-1", "lifecycle": "Active"}',
            "mature_deposit": '{"deposit_id": "d-1", "lifecycle": "Matured", "total_payout_cents": 1021900}',
        }[name]

    events = await _collect(
        run_agent("Open a 10k deposit and mature it.", client=client, tools=[{"name": "x"}], dispatch=dispatch)
    )

    # Real tool calls in order, each with a non-error result, ending in Done.
    tool_calls = [e for e in events if isinstance(e, ToolCall)]
    assert [tc.tool for tc in tool_calls] == ["constitute_deposit", "get_deposit", "mature_deposit"]
    tool_results = [e for e in events if isinstance(e, ToolResult)]
    assert all(not tr.is_error for tr in tool_results)
    assert dispatched == [
        ("constitute_deposit", {"principal_cents": 1_000_000}),
        ("get_deposit", {"deposit_id": "d-1", "min_sequence": 0}),
        ("mature_deposit", {"deposit_id": "d-1"}),
    ]
    done = events[-1]
    assert isinstance(done, Done)
    assert done.turns == 4
    assert "Matured" in done.summary

    # The 2nd request must carry the 1st tool's result, keyed by the matching tool_use_id.
    second_messages = client.messages.calls[1]["messages"]
    tool_result_msg = second_messages[-1]
    assert tool_result_msg["role"] == "user"
    assert tool_result_msg["content"][0]["tool_use_id"] == "t1"
    assert "d-1" in tool_result_msg["content"][0]["content"]


async def test_tool_error_is_fed_back_as_is_error() -> None:
    turns = [
        ([], _final([_tool_block("mature_deposit", {"deposit_id": "d-1"}, "t1")], "tool_use")),
        ([_text_delta("That deposit cannot mature yet.")], _final([_text_block("That deposit cannot mature yet.")], "end_turn")),
    ]
    client = _FakeClient(turns)

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        raise RuntimeError("422: deposit is not yet at term")

    events = await _collect(run_agent("Mature it.", client=client, tools=[], dispatch=dispatch))

    errors = [e for e in events if isinstance(e, ToolResult) and e.is_error]
    assert len(errors) == 1
    assert "422" in errors[0].output
    # The error was fed back to Claude as an is_error tool_result so it could adapt.
    second_messages = client.messages.calls[1]["messages"]
    fed_back = second_messages[-1]["content"][0]
    assert fed_back["tool_use_id"] == "t1"
    assert fed_back["is_error"] is True
    assert isinstance(events[-1], Done)


async def test_refusal_stops_without_dispatching() -> None:
    turns = [([], _final([], "refusal"))]
    client = _FakeClient(turns)
    called = False

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        nonlocal called
        called = True
        return "{}"

    events = await _collect(run_agent("do something", client=client, tools=[], dispatch=dispatch))

    assert not called
    assert len(events) == 1
    assert isinstance(events[0], AgentError)
    assert events[0].kind == "refusal"


async def test_max_turns_backstop() -> None:
    # The model keeps asking for tools forever; the ceiling must stop the run.
    turns = [([], _final([_tool_block("get_deposit", {"deposit_id": "d-1"}, f"t{i}")], "tool_use")) for i in range(5)]
    client = _FakeClient(turns)

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        return "{}"

    events = await _collect(run_agent("loop", client=client, tools=[], dispatch=dispatch, max_turns=2))

    assert len(client.messages.calls) == 2  # stopped at the ceiling, not the 5 scripted turns
    assert isinstance(events[-1], AgentError)
    assert events[-1].kind == "max_turns"
    assert events[-1].details == {"max_turns": 2}


async def test_streamed_text_and_thinking_surface_as_events() -> None:
    turns = [
        (
            [_thinking_delta("Plan: open then mature."), _text_delta("On it.")],
            _final([_text_block("On it.")], "end_turn"),
        )
    ]
    client = _FakeClient(turns)

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        return "{}"

    events = await _collect(run_agent("go", client=client, tools=[], dispatch=dispatch))

    assert any(isinstance(e, Thinking) and "Plan" in e.text for e in events)
    assert any(isinstance(e, Narration) and "On it." in e.text for e in events)


async def test_request_carries_model_thinking_tools_and_system() -> None:
    turns = [([], _final([_text_block("done")], "end_turn"))]
    client = _FakeClient(turns)
    tools = [{"name": "get_deposit", "description": "read", "input_schema": {"type": "object"}}]

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        return "{}"

    await _collect(
        run_agent("go", client=client, tools=tools, dispatch=dispatch, system="be a bank", model="claude-opus-4-8")
    )

    kwargs = client.messages.calls[0]
    assert kwargs["model"] == "claude-opus-4-8"
    assert kwargs["thinking"] == _THINKING
    assert kwargs["tools"] == tools
    assert kwargs["system"] == "be a bank"
    assert kwargs["max_tokens"] == 4096


async def test_system_omitted_when_not_given() -> None:
    turns = [([], _final([_text_block("done")], "end_turn"))]
    client = _FakeClient(turns)

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        return "{}"

    await _collect(run_agent("go", client=client, tools=[], dispatch=dispatch))
    assert "system" not in client.messages.calls[0]
