"""The agent host HTTP app: POST an instruction, stream the run as SSE (bd babelstone-f0ic.6.2).

In plain English: this is the small web front door for the real-Claude agent. Mission Control POSTs a
natural-language instruction here; the server runs the agentic loop (which calls Claude and the real
MCP deposit tools) and streams every event back as Server-Sent Events — Claude's narration, each tool
call, each result, then a final done/error frame. The browser renders those live in the console.

It is a deliberately tiny Starlette app with ONE route:

  POST /agent/stream   body: {"instruction": "<natural language>"}   -> text/event-stream

The ``/stream`` suffix is intentional: Mission Control's ``serve.py`` proxy already drops the read
deadline and flushes frame-by-frame for any path ending in ``/stream`` (built for the saga SSE), so a
long agent run is not cut off and arrives incrementally — no proxy change beyond the route.

The Anthropic API key and the agent loop live HERE, server-side (ADR-IC-014: key from env, never the
browser). On a setup failure (missing key, unreachable MCP server) the loop yields a single ``error``
frame rather than failing the request outright, so the UI can fall back to DEMO mode cleanly.
"""

from __future__ import annotations

from typing import AsyncIterator

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, Response, StreamingResponse
from starlette.routing import Route

from .host import run
from .sse import event_to_sse

# Streaming response headers: disable caching and any intermediary buffering so frames arrive live.
# This mirrors the long-lived, no-deadline SSE relay ADR-IC-010 §P5 / Residual risks already use for
# the saga stream — serve.py reuses that same relay for the /stream-suffixed agent route.
_SSE_HEADERS = {"Cache-Control": "no-cache", "X-Accel-Buffering": "no"}


async def agent_stream(request: Request) -> Response:
    """Run the posted instruction and stream the agent's events as SSE.

    400s on a malformed body or a missing/blank ``instruction``. Otherwise returns a
    ``text/event-stream`` whose frames are the serialised agent events, in order.
    """
    try:
        body = await request.json()
    except Exception:  # noqa: BLE001 - any malformed body is a client error, not a server crash
        return JSONResponse({"error": "Request body must be JSON."}, status_code=400)

    instruction = body.get("instruction") if isinstance(body, dict) else None
    if not isinstance(instruction, str) or not instruction.strip():
        return JSONResponse(
            {"error": "Body must contain a non-empty 'instruction' string."}, status_code=400
        )

    async def frames() -> AsyncIterator[bytes]:
        async for event in run(instruction):
            yield event_to_sse(event).encode("utf-8")

    return StreamingResponse(frames(), media_type="text/event-stream", headers=_SSE_HEADERS)


def build_app() -> Starlette:
    """The agent host ASGI app — one route, ``POST /agent/stream`` (proxied as ``/agent/*``)."""
    return Starlette(routes=[Route("/agent/stream", agent_stream, methods=["POST"])])
