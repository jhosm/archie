"""Adapt the MCP tool surface to what the Anthropic loop needs.

In plain English: the MCP server publishes its tools (name, description, input schema) and runs them.
Claude needs those same tools described in its own format, and the loop needs one async call that runs
a tool and hands back the result as text. These two small helpers do exactly that, bridging the MCP
session and the agent loop.

Both helpers are duck-typed — they read ``.name`` / ``.description`` / ``.inputSchema`` off MCP tool
objects and ``.structuredContent`` / ``.content`` / ``.isError`` off a call result — so they carry no
hard dependency on the ``mcp`` types and are unit-testable with simple stand-ins. The live session is
opened in ``host`` (the composition root).

``_result_to_text`` prefers the MCP tool's structured output (the mandatory ADR-IC-010 §P6
``outputSchema`` result — e.g. a ``DepositPosition``) so Claude sees the named, typed fields.
"""

from __future__ import annotations

import json
from typing import Any, Awaitable, Callable


def to_anthropic_tools(mcp_tools: list[Any]) -> list[dict[str, Any]]:
    """Convert MCP tool descriptors to Anthropic tool schemas (``name``/``description``/``input_schema``).

    The MCP ``inputSchema`` is already a JSON Schema object, which is what Anthropic's ``input_schema``
    expects — a direct mapping. A tool with no description maps to an empty string (the API requires
    the key to be present).
    """
    converted: list[dict[str, Any]] = []
    for tool in mcp_tools:
        converted.append(
            {
                "name": tool.name,
                "description": getattr(tool, "description", None) or "",
                "input_schema": tool.inputSchema,
            }
        )
    return converted


def _result_to_text(result: Any) -> str:
    """Render an MCP ``call_tool`` result as text for a Claude ``tool_result``.

    Prefers the tool's ``structuredContent`` (the §P6 typed output — e.g. a ``DepositPosition``)
    serialised to JSON, so Claude sees the named fields (``deposit_id``, ``total_payout_cents``, …).
    Falls back to joining the text content blocks.
    """
    structured = getattr(result, "structuredContent", None)
    if structured:
        return json.dumps(structured)
    blocks = getattr(result, "content", None) or []
    texts = [getattr(b, "text", "") for b in blocks if getattr(b, "type", None) == "text"]
    return "".join(texts)


def make_dispatch(session: Any) -> Callable[[str, dict[str, Any]], Awaitable[str]]:
    """Build the loop's ``dispatch`` callable from an open MCP ``ClientSession``.

    The returned coroutine calls the tool over the session and returns its result as text. If the MCP
    call reports a tool error (``isError``), it raises — the loop catches that and feeds an
    ``is_error`` ``tool_result`` back to Claude so it can adapt.
    """

    async def dispatch(name: str, arguments: dict[str, Any]) -> str:
        result = await session.call_tool(name, arguments)
        text = _result_to_text(result)
        if getattr(result, "isError", False):
            raise RuntimeError(text or f"Tool '{name}' reported an error.")
        return text

    return dispatch
