"""HTTP client for the orchestrator's saga edge (ADR-IC-006 §P4 / Document 11 Pattern 2).

A thin async wrapper over the two saga-edge surfaces the MCP channel needs: START a constitution saga
(POST ``/api/v1/deposits/constitute``, the Pattern 2 PRODUCER) and poll a process's coarse status
(GET ``/api/v1/processes/{process_id}/status``, the Pattern 2 READ). It is the mcp→orchestrator boundary the
maintainer's vjoi decision (2026-06-17) introduced, mirroring the existing mcp→engine one (``engine_client``):
a separate boundary because the ORCHESTRATOR — not the engine — owns saga state, so it is the honest source
of the minted ``process_id`` and of in-flight / awaiting-approval / failed status (Document 11 Pattern 2).

Like ``engine_client`` it is fail-loud: a non-2xx response raises (``raise_for_status``) rather than
returning a partial result. The polling tool (``server.get_process_status``) translates the EXPECTED 404
(unknown process) / 403 (not the caller's process) into a clean ``McpError`` for the agent; any other
non-2xx propagates.

Every method takes an optional ``client_id`` — the gateway-attested caller (the OAuth ``sub`` Kong
overwrote into ``X-Client-Id``, ADR-IC-010 §P3 / Document 11). When given, the client FORWARDS it as the
``X-Client-Id`` header so the orchestrator can enforce per-process OWNERSHIP (the same header its edge authz
binds to): a guessed/stolen ``process_id`` from another caller yields 403, never another client's status.
The identity always originates from the gateway-attested token ``sub``, never a tool argument.

No sanitiser choke point here (unlike ``engine_client``): the status snapshot is entirely TYPED,
bank-controlled, structural values — a ``PROC-…`` reference, an enum saga state, an enum AgentStatus, an
integer version, a bool — with no customer-/external-writable free-text field, so there is no
prompt-injection surface to sanitise (sanitising a typed value would corrupt it). If the orchestrator ever
adds a free-text field to this snapshot, it gains a choke point here then — exactly as ``engine_client``
has one for the deposit position.
"""

from __future__ import annotations

from typing import Any

import httpx

# The gateway-attested caller header the MCP server forwards to the orchestrator (ADR-IC-010 §P3 /
# ADR-IC-006 §P4 — the orchestrator edge binds its per-process ownership check to this same header).
CLIENT_ID_HEADER = "X-Client-Id"


def _with_client_id(headers: dict[str, str] | None, client_id: str | None) -> dict[str, str] | None:
    """Add ``X-Client-Id`` to ``headers`` when ``client_id`` is given (attested caller, §P3)."""
    if not client_id:
        return headers
    merged = dict(headers or {})
    merged[CLIENT_ID_HEADER] = client_id
    return merged


class OrchestratorClient:
    """Calls the orchestrator's process-status read API. Inject an ``httpx.AsyncClient`` in tests."""

    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._client = client or httpx.AsyncClient(timeout=30.0)

    async def constitute(
        self, request: dict[str, Any], client_id: str | None = None
    ) -> dict[str, Any]:
        """POST /api/v1/deposits/constitute — STARTS a constitution saga (Document 11 Pattern 2 producer).

        Returns the edge's 202 body ``{deposit_id, process_id, status, stream_url}`` (snake_case) — the
        saga ``process_id`` is the public ``PROC-…`` reference the agent threads into ``get_process_status``
        to poll async completion. Unlike the engine command surface this is NOT a direct engine append: the
        orchestrator starts the saga, mints the ``process_id``, and owns its state (ADR-IC-006 §P4 / Document 05
        §Step 0). Raises ``httpx.HTTPStatusError`` on a non-2xx response (e.g. 400 on a structurally-malformed
        request, 403 on a missing gateway-attested caller).

        The ``request`` body carries ONLY PII-free structural references (ADR-PC-004 §P2): a ``product_code``,
        an integer-cents ``amount``, and OPAQUE ``source_account_ref`` / ``interest_account_ref`` tokens — never
        a raw IBAN. The owning client is NOT a body field: it is the gateway-attested ``X-Client-Id`` this client
        forwards (see below), the same header the edge binds per-process ownership to.
        """
        response = await self._client.post(
            f"{self._base_url}/api/v1/deposits/constitute",
            json=request,
            headers=_with_client_id(None, client_id),
        )
        response.raise_for_status()
        return response.json()

    async def process_status(
        self, process_id: str, client_id: str | None = None
    ) -> dict[str, Any]:
        """GET /api/v1/processes/{process_id}/status — returns the coarse saga status snapshot
        ``{process_id, state, status, version, terminal}`` (Document 11 Pattern 2).

        Raises ``httpx.HTTPStatusError`` on a non-2xx response — including the EXPECTED 404 (no such
        process) and 403 (the process is owned by another client), which the calling tool translates into a
        clean ``McpError``. ``process_id`` is the public ``PROC-…`` reference the saga edge minted.
        """
        response = await self._client.get(
            f"{self._base_url}/api/v1/processes/{process_id}/status",
            headers=_with_client_id(None, client_id),
        )
        response.raise_for_status()
        return response.json()

    async def aclose(self) -> None:
        await self._client.aclose()
