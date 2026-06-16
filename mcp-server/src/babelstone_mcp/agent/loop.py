"""The pure agentic tool-use loop (bd babelstone-f0ic.6.1).

In plain English: this drives one conversation with Claude. It sends the instruction, streams back
the model's words and reasoning, and whenever Claude asks to use a tool it runs that tool, feeds the
result back, and lets Claude continue — until Claude is done or a safety limit trips. It is the
"brain loop" the demo's real-AI console shows live.

Why a hand-rolled loop (not the Anthropic ``mcp_servers`` connector and not ``tool_runner``):
- The ``mcp_servers`` connector requires Anthropic's servers to reach the MCP server over the public
  internet. Ours is localhost behind Kong + mutual TLS + an audience-bound OAuth edge (ADR-IC-010
  §P3/§P5) — unreachable that way. So Claude's tool calls are dispatched HERE, by us, to the local
  MCP session.
- A manual loop lets us stream each token of narration and each tool call into the console as it
  happens, and gives us the seam to gate irreversible money-movers if we ever want to. That control
  is the point of the demo.

This module is deliberately framework-free: it takes an injected ``client`` (anything exposing
``client.messages.stream(...)`` the way the Anthropic SDK does) and an injected ``dispatch`` callable
(``async (name, input) -> str``). That makes the loop's logic unit-testable with fakes and keeps
``import anthropic`` out of this file entirely (it lives only in ``host``). The model id and the
guardrail defaults below follow the project's claude-api reference: Claude Opus 4.8, adaptive
thinking, a turn ceiling, and a bounded ``max_tokens``.
"""

from __future__ import annotations

from typing import Any, AsyncIterator, Awaitable, Callable, Protocol

from .events import AgentError, AgentEvent, Done, Narration, Thinking, ToolCall, ToolResult

# Per the project's claude-api reference: always Claude Opus 4.8 unless the user names another model.
DEFAULT_MODEL = "claude-opus-4-8"
# A hard ceiling on agentic turns — a manual loop has no built-in stop, and constitute/mature are
# real (irreversible-in-intent) engine commands. A constitute -> get -> mature flow is ~3 tool turns;
# 6 leaves headroom for a retry without risking a runaway.
DEFAULT_MAX_TURNS = 6
# Bounds cost/latency for a demo; ample for the deposit tool flow's short narration.
DEFAULT_MAX_TOKENS = 4096

# Adaptive thinking with a summarised display — opus-4-8 supports adaptive only, and "summarized"
# surfaces a readable reasoning trace for the investor-facing "watch the AI think" effect (the raw
# chain of thought is never returned by the API).
_THINKING = {"type": "adaptive", "display": "summarized"}

# Dispatch executes one tool call and returns its result content as text. It MAY raise: the loop
# catches any exception and feeds an is_error tool_result back to Claude so it can adapt (e.g. the
# engine 422'd a deposit that cannot mature) rather than the run silently stalling.
Dispatch = Callable[[str, dict[str, Any]], Awaitable[str]]


class _Stream(Protocol):
    """The async streaming context the SDK's ``messages.stream(...)`` returns (duck-typed)."""

    def __aiter__(self) -> "AsyncIterator[Any]": ...
    async def get_final_message(self) -> Any: ...
    async def __aenter__(self) -> "_Stream": ...
    async def __aexit__(self, *exc: Any) -> Any: ...


class MessagesClient(Protocol):
    """Just the slice of the Anthropic client the loop needs (duck-typed for testability)."""

    @property
    def messages(self) -> Any: ...


def _text_of(message: Any) -> str:
    """Join the ``text`` blocks of a final message into a single string (its user-facing prose)."""
    parts = [getattr(b, "text", "") for b in message.content if getattr(b, "type", None) == "text"]
    return "".join(parts).strip()


async def run_agent(
    instruction: str,
    *,
    client: MessagesClient,
    tools: list[dict[str, Any]],
    dispatch: Dispatch,
    system: str | None = None,
    model: str = DEFAULT_MODEL,
    max_turns: int = DEFAULT_MAX_TURNS,
    max_tokens: int = DEFAULT_MAX_TOKENS,
) -> AsyncIterator[AgentEvent]:
    """Run the instruction through Claude with ``tools`` bound, yielding events as it acts.

    Loops: stream a turn, echo the assistant message back into the history, and — while
    ``stop_reason == "tool_use"`` — dispatch each tool call, feed the results back, and continue.
    Stops on ``end_turn`` (yields ``Done``), on ``refusal``, or on hitting ``max_turns`` (both yield
    ``AgentError``). Each ``tool_result`` carries the matching ``tool_use_id`` the API requires.
    """
    messages: list[dict[str, Any]] = [{"role": "user", "content": instruction}]

    create_kwargs: dict[str, Any] = {
        "model": model,
        "max_tokens": max_tokens,
        "tools": tools,
        "thinking": _THINKING,
    }
    if system is not None:
        create_kwargs["system"] = system

    for turn in range(1, max_turns + 1):
        async with client.messages.stream(messages=messages, **create_kwargs) as stream:
            async for event in stream:
                if getattr(event, "type", None) != "content_block_delta":
                    continue
                delta = event.delta
                kind = getattr(delta, "type", None)
                if kind == "text_delta":
                    yield Narration(delta.text)
                elif kind == "thinking_delta":
                    yield Thinking(delta.thinking)
            message = await stream.get_final_message()

        # Echo the assistant turn back verbatim — including thinking and tool_use blocks — so the
        # next request carries the full, valid history (the API rejects modified thinking blocks).
        messages.append({"role": "assistant", "content": message.content})

        stop_reason = getattr(message, "stop_reason", None)
        if stop_reason == "refusal":
            yield AgentError("Claude declined this request.", "refusal")
            return
        if stop_reason != "tool_use":
            yield Done(summary=_text_of(message), turns=turn)
            return

        tool_uses = [b for b in message.content if getattr(b, "type", None) == "tool_use"]
        tool_results: list[dict[str, Any]] = []
        for tu in tool_uses:
            tool_input = dict(tu.input or {})
            yield ToolCall(tool=tu.name, input=tool_input, id=tu.id)
            try:
                output = await dispatch(tu.name, tool_input)
                yield ToolResult(tool=tu.name, id=tu.id, output=output, is_error=False)
                tool_results.append(
                    {"type": "tool_result", "tool_use_id": tu.id, "content": output}
                )
            except Exception as exc:  # noqa: BLE001 — surface ANY tool failure to the model + console
                message_text = str(exc) or exc.__class__.__name__
                yield ToolResult(tool=tu.name, id=tu.id, output=message_text, is_error=True)
                tool_results.append(
                    {
                        "type": "tool_result",
                        "tool_use_id": tu.id,
                        "content": message_text,
                        "is_error": True,
                    }
                )

        messages.append({"role": "user", "content": tool_results})

    # Fell out of the loop: hit the turn ceiling without an end_turn. The backstop, surfaced honestly.
    yield AgentError(
        f"Reached the {max_turns}-turn limit without completing the task.",
        "max_turns",
        {"max_turns": max_turns},
    )
