"""The agent's output event model — what ``run_agent`` yields as Claude thinks and acts.

In plain English: as Claude reasons and calls tools, the loop emits a small stream of typed events.
The streaming HTTP endpoint (bd babelstone-f0ic.6.2) serialises these into named SSE frames the
Mission Control console renders ("Claude said…", "called constitute_deposit", "→ result"). Keeping
the event model here — separate from both the loop and the transport — lets the loop stay pure and
lets the SSE layer own the wire format.

These events carry the tool inputs/outputs the model actually exchanged. That is fine for this
channel: it is the operator's own ephemeral, same-origin console, NOT the durable integration bus —
the no-PII-on-the-bus invariant (ADR-PC-004 §P2) governs Kafka/durable carriers, and deposit ids are
opaque references, not PII. The SSE layer (f0ic.6.2) owns any presentation-level redaction.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Union


@dataclass(frozen=True)
class Narration:
    """A chunk of Claude's user-facing prose (a ``text_delta`` while streaming)."""

    text: str


@dataclass(frozen=True)
class Thinking:
    """A chunk of Claude's summarised reasoning (a ``thinking_delta``).

    Surfaced only because the demo uses ``thinking={"type": "adaptive", "display": "summarized"}`` —
    the investor-facing "watch the AI reason" effect. The raw chain of thought is never returned by
    the API; this is the model's own summary.
    """

    text: str


@dataclass(frozen=True)
class ToolCall:
    """Claude decided to call ``tool`` with ``input`` (a real ``tool_use`` block)."""

    tool: str
    input: dict[str, Any]
    id: str


@dataclass(frozen=True)
class ToolResult:
    """The result of executing a ``ToolCall`` against the MCP server / engine.

    ``output`` is the tool's content as text (structured output serialised to JSON). ``is_error`` is
    True when the call failed (e.g. the engine 422'd a deposit that cannot mature) — the loop also
    feeds an ``is_error`` ``tool_result`` back to Claude so it can adapt rather than silently stall.
    """

    tool: str
    id: str
    output: str
    is_error: bool = False


@dataclass(frozen=True)
class Done:
    """The run completed naturally (Claude ended its turn). ``summary`` is its closing text."""

    summary: str
    turns: int


@dataclass(frozen=True)
class AgentError:
    """The run stopped without completing.

    ``kind`` is one of ``refusal`` (safety classifier / model decline), ``max_turns`` (hit the loop
    ceiling — the runaway-loop backstop), or ``exception`` (an unexpected failure in the host).
    """

    message: str
    kind: str
    details: dict[str, Any] = field(default_factory=dict)


AgentEvent = Union[Narration, Thinking, ToolCall, ToolResult, Done, AgentError]
"""The tagged union the agent loop yields; ``type(event).__name__.lower()`` is the SSE event name."""
