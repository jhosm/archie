"""Serialise agent events to Server-Sent Events frames (bd babelstone-f0ic.6.2).

In plain English: the agent loop yields typed events as Claude thinks and acts; this turns each one
into a named SSE frame the browser can route. Mission Control's console listens per event name —
"narration" (what Claude says), "tool_call" (a tool it invoked), "tool_result" (what came back),
plus "thinking", "done", and "error". Keeping the wire format here — separate from the loop and the
web app — means the loop stays pure and the contract lives in one place.

The frame shape is the standard SSE one: ``event: <name>\\n`` then ``data: <json>\\n`` then a blank
line. The data is the event's fields as compact JSON, so any newlines in Claude's prose are escaped
into a single ``data:`` line (a raw newline would break SSE framing).

PII note: these frames carry the tool inputs/outputs the model exchanged, on the operator's own
ephemeral, same-origin console — NOT the durable integration bus the no-PII invariant (ADR-PC-004
§P2) governs. Deposit ids are opaque references. Any presentation-level redaction is the UI's call.
"""

from __future__ import annotations

import json
from dataclasses import asdict

from .events import AgentError, AgentEvent, Done, Narration, Thinking, ToolCall, ToolResult

# Each event dataclass -> its SSE event name. The data payload is the dataclass's fields as JSON.
_EVENT_NAMES: dict[type, str] = {
    Narration: "narration",
    Thinking: "thinking",
    ToolCall: "tool_call",
    ToolResult: "tool_result",
    Done: "done",
    AgentError: "error",
}


def sse_name(event: AgentEvent) -> str:
    """The SSE event name for ``event`` (e.g. ``ToolCall`` -> ``"tool_call"``)."""
    name = _EVENT_NAMES.get(type(event))
    if name is None:
        raise TypeError(f"No SSE mapping for event type {type(event).__name__!r}")
    return name


def event_to_sse(event: AgentEvent) -> str:
    """Render one agent event as a complete SSE frame (``event:`` + ``data:`` + blank line)."""
    data = json.dumps(asdict(event), separators=(",", ":"))
    return f"event: {sse_name(event)}\ndata: {data}\n\n"
