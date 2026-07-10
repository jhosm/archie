"""HTTP client for the engine command/query boundary (Babelstone.Engine.Api, ADR-PC-021 §D5).

A thin async wrapper over the engine command/query surfaces the MCP server maps: the deposit commands
(constitute / mature / pay-interest, POST), the deposit position read (GET), and the personal-loan
installment money-mover (POST /v1/loans/{id}/installment). Money crosses the wire as integer cents
(ADR-PC-010 §P1), snake_case.
The client is fail-loud: a non-2xx engine response raises (``raise_for_status``) rather than
returning a partial/empty result — the MCP layer surfaces that to the agent.

Every method takes an optional ``client_id`` — the gateway-attested caller (the OAuth ``sub`` Kong
overwrote into ``X-Client-Id``, ADR-IC-010 §P3 / Document 11). When given, the client FORWARDS it to
the engine as an ``X-Client-Id`` header so the engine sees who acted, for audit and ownership — the
identity always originates from the gateway-attested token ``sub``, never a tool argument.
"""

from __future__ import annotations

import os
import uuid
from typing import Any

import httpx

from .internal_mtls import build_client_ssl_context
from .sanitize import sanitize_free_text

# The gateway-attested caller header the MCP server forwards to the engine (ADR-IC-010 §P3).
CLIENT_ID_HEADER = "X-Client-Id"

# The stable code the engine returns on a money-mover called without FRESH step-up SCA (the §P8 gate,
# Q-BE Q1 / bd babelstone-ziu3.5). Kept in lock-step with the engine's ScaPrecondition.RequiredCode. The
# money-mover tools key their step-up-then-retry on this code (Q2).
SCA_REQUIRED_CODE = "SCA_REQUIRED"


class ScaRequiredError(Exception):
    """Raised when the engine refuses a money-mover with ``422 SCA_REQUIRED`` (ADR-IC-010 §P8).

    The engine settles an irreversible operation only on FRESH gateway-attested step-up SCA — the
    AS-signed ``acr``/``auth_time`` Kong attests. When that proof is absent, weak, or stale the engine
    returns ``422`` with a stable ``code`` of :data:`SCA_REQUIRED_CODE`; the client raises THIS typed
    error so the money-mover tool can distinguish it from any other 4xx and run the step-up-then-retry
    flow (Q2), rather than surfacing a raw transport error to the agent. Carries no PII — the engine's
    refusal body is a stable code + generic message only (ADR-PC-004 §P2).
    """

# Free-text fields in an engine deposit response that a CUSTOMER or an EXTERNAL party can write, and
# which therefore carry a prompt-injection surface (Document 11 §Trust Model / ADR-IC-010 §P9). Each
# is run through ``sanitize_free_text`` before it reaches a tool — the bank's second-line defence
# against "the bank's own data attacking the bank's agent". The map value is the field's
# business-justified max length (the "smallest length consistent with its business use" §P9 requires);
# ``None`` uses the conservative sanitiser default.
#
# Every OTHER field the engine returns is a bank-controlled TYPED value — a UUID, an ISO date, an enum
# lifecycle state, integer cents, a basis-point rate, a structural product code — with no injection
# surface, so it is deliberately NOT sanitised (sanitising a typed value would corrupt it). The deposit
# position the engine serves today is entirely typed and has no such field; this map is the forward-
# safe choke point so the instant a customer-writable free-text field IS added to the read model, it
# cannot reach the agent un-sanitised. Adding a customer-writable string to the engine response without
# listing it here is the drift this central point exists to prevent.
CUSTOMER_FREE_TEXT_FIELDS: dict[str, int | None] = {
    # e.g. "customer_reference": 140, "beneficiary_name": 70 — none exist on the position yet.
}


def sanitize_engine_response(payload: dict[str, Any]) -> dict[str, Any]:
    """Sanitise any customer-/external-writable free-text field in an engine response (§P9).

    Returns a shallow copy with each :data:`CUSTOMER_FREE_TEXT_FIELDS` field run through
    ``sanitize_free_text`` (control-character + instruction-shape stripping, length cap, and the
    data-not-instruction fence). Typed bank-controlled fields are passed through untouched. With no
    free-text field on the deposit position today this is an identity transform, but it is the single
    boundary every engine read/write result flows through, so a future free-text field is sanitised
    by construction rather than by remembering to.
    """
    if not CUSTOMER_FREE_TEXT_FIELDS:
        return payload
    sanitised = dict(payload)
    for field, max_len in CUSTOMER_FREE_TEXT_FIELDS.items():
        if field in sanitised and isinstance(sanitised[field], str):
            kwargs = {"max_len": max_len} if max_len is not None else {}
            sanitised[field] = sanitize_free_text(sanitised[field], **kwargs)
    return sanitised


def _with_client_id(headers: dict[str, str] | None, client_id: str | None) -> dict[str, str] | None:
    """Add ``X-Client-Id`` to ``headers`` when ``client_id`` is given (attested caller, §P3)."""
    if not client_id:
        return headers
    merged = dict(headers or {})
    merged[CLIENT_ID_HEADER] = client_id
    return merged


def _raise_for_sca_required(response: httpx.Response) -> None:
    """Raise :class:`ScaRequiredError` when ``response`` is the engine's ``422 SCA_REQUIRED`` (§P8).

    A no-op for any other response. Called on a money-mover's response BEFORE ``raise_for_status`` so the
    step-up gate surfaces as a typed signal the tool acts on (Q2), not a generic ``HTTPStatusError``.
    Robust to a non-JSON body — a 422 that does not decode to the ``SCA_REQUIRED`` code falls through to
    the normal ``raise_for_status`` path (it is a different domain rejection).
    """
    if response.status_code != 422:
        return
    try:
        body = response.json()
    except ValueError:
        return
    if isinstance(body, dict) and body.get("code") == SCA_REQUIRED_CODE:
        raise ScaRequiredError()


class EngineClient:
    """Calls the engine's deposits HTTP API. Inject an ``httpx.AsyncClient`` in tests."""

    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None) -> None:
        self._base_url = base_url.rstrip("/")
        # Caller-side internal mTLS on the outbound engine hop (bd babelstone-zla1.12.10; ADR-IC-006 §P5
        # Boundary 2 / ADR-IC-016 plane (i)). Only the DEFAULT-construction path pins the internal CA +
        # presents the client cert, gated on BABELSTONE_INTERNAL_CA_CERTS (staging mounts /certs/ca.crt
        # and sets it); with it unset the client dials plain HTTP exactly as before. An INJECTED client
        # (tests, a configured host) is respected verbatim — the context is never forced onto it.
        if client is not None:
            self._client = client
        else:
            ssl_context = build_client_ssl_context(os.environ)
            self._client = httpx.AsyncClient(
                timeout=30.0, verify=ssl_context if ssl_context is not None else True
            )

    async def constitute(
        self, request: dict[str, Any], client_id: str | None = None
    ) -> dict[str, Any]:
        """POST /v1/deposits — returns {deposit_id, status, commit_sequence}. Raises on a non-2xx
        engine response. ``commit_sequence`` is the read-your-writes token (ADR-IC-005 §P3): pass it
        back as ``min_sequence`` on the follow-up read to see the just-written deposit.

        The engine MANDATES a UUID ``Idempotency-Key`` header (ADR-PC-029 slot 1) and 400s without it.
        On the saga channel that key is the ``saga_outbox`` row id; on this agent channel there is no
        such row (the agent is not the saga), so the client mints a fresh per-call UUID. Each tool
        invocation is its own command, so a per-call key is the correct contract here — the MCP server
        is a co-consumer of the engine command surface (ADR-IC-010 / ADR-PC-029 slot 6).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits",
            json=request,
            headers=_with_client_id({"Idempotency-Key": str(uuid.uuid4())}, client_id),
        )
        response.raise_for_status()
        # §P9: routed through the same choke point as the reads so EVERY engine read/write result is
        # sanitised by construction. The constitute result is {deposit_id, status, commit_sequence} —
        # all typed/bank-controlled with no free-text surface — so this is an identity transform today;
        # wrapping it keeps the "every result flows through" invariant literally true.
        return sanitize_engine_response(response.json())

    async def deposit_position(
        self, deposit_id: str, min_sequence: int | None = None, client_id: str | None = None
    ) -> dict[str, Any]:
        """GET /v1/deposits/{id} — the ONE canonical deposit resource (ADR-IC-005). Served from the
        denormalized read model by default; when ``min_sequence`` is given (a commit_sequence token),
        sends ``If-Min-Sequence`` so the engine folds the stream for read-your-writes if the projector
        is still behind. Raises on 404/other non-2xx.
        """
        headers = {"If-Min-Sequence": str(min_sequence)} if min_sequence is not None else None
        response = await self._client.get(
            f"{self._base_url}/v1/deposits/{deposit_id}",
            headers=_with_client_id(headers, client_id),
        )
        response.raise_for_status()
        # §P9: sanitise any customer-writable free-text before the position reaches the agent.
        return sanitize_engine_response(response.json())

    async def mature(self, deposit_id: str, client_id: str | None = None) -> dict[str, Any]:
        """POST /v1/deposits/{id}/maturity — settles the deposit, returns the matured position.

        Same position shape as ``deposit_position`` with ``lifecycle`` = ``Matured``. Raises
        :class:`ScaRequiredError` on the engine's ``422 SCA_REQUIRED`` step-up gate (§P8, Q-BE Q1) so the
        money-mover tool can step up + retry (Q2); raises ``HTTPStatusError`` on any other non-2xx (e.g. a
        422 lifecycle rejection if the deposit cannot mature).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits/{deposit_id}/maturity",
            json={},
            headers=_with_client_id(None, client_id),
        )
        _raise_for_sca_required(response)
        response.raise_for_status()
        # §P9: sanitise any customer-writable free-text before the matured position reaches the agent.
        return sanitize_engine_response(response.json())

    async def pay_interest(self, deposit_id: str, client_id: str | None = None) -> dict[str, Any]:
        """POST /v1/deposits/{id}/interest — pays one PERIODIC coupon, returns the updated position.

        Same position shape as ``deposit_position`` with the coupon's gross/withholding/net folded
        in and ``coupons_paid`` incremented. The coupon window is derived by the engine from the
        deposit's schedule — not supplied here. Raises :class:`ScaRequiredError` on the engine's
        ``422 SCA_REQUIRED`` step-up gate (§P8, Q-BE Q1) so the tool can step up + retry (Q2); raises
        ``HTTPStatusError`` on any other non-2xx (e.g. a 422 if the deposit is not Active, not PERIODIC,
        or has no intermediate coupon left).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits/{deposit_id}/interest",
            json={},
            headers=_with_client_id(None, client_id),
        )
        _raise_for_sca_required(response)
        response.raise_for_status()
        # §P9: sanitise any customer-writable free-text before the coupon position reaches the agent.
        return sanitize_engine_response(response.json())

    async def pay_installment(
        self, loan_id: str, collection_account_ref: str, client_id: str | None = None
    ) -> dict[str, Any]:
        """POST /v1/loans/{id}/installment — pays the next scheduled installment, returns the loan
        command outcome ({loan_id, status, commit_sequence}).

        Carries NO ``Idempotency-Key`` header — UNLIKE ``constitute`` (and the deposit money-movers'
        saga channel), the installment key is SERVER-DERIVED and number-pinned on the stable installment
        NUMBER (ADR-PC-036 §Decision 1+3 / LCD-1; ADR-PC-029 slot 4, AMENDED): the engine derives the key
        from ``(loan, "pay_installment", installment-number)`` itself, so a manual operator, this MCP
        agent, and the automated driver paying the SAME occurrence all converge on ONE key and dedupe to
        ONE money leg. The client therefore supplies no key of its own (bd babelstone-6cpq.1) — a re-dated
        at-least-once retry of the same occurrence cannot double-collect. ``collection_account_ref`` is an
        OPAQUE account token the installment is collected from (a reference, NEVER an IBAN — ADR-PC-004
        §P2). Raises :class:`ScaRequiredError` on the engine's ``422 SCA_REQUIRED`` step-up gate (§P8, Q-BE
        Q1) so the money-mover tool can step up + retry (Q2); raises ``HTTPStatusError`` on any other
        non-2xx (e.g. a 422 lifecycle rejection on a settled loan).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/loans/{loan_id}/installment",
            json={"collection_account_ref": collection_account_ref},
            headers=_with_client_id(None, client_id),
        )
        _raise_for_sca_required(response)
        response.raise_for_status()
        # §P9: sanitise any customer-writable free-text before the loan outcome reaches the agent.
        return sanitize_engine_response(response.json())

    async def aclose(self) -> None:
        await self._client.aclose()
