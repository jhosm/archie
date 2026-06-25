"""Authorization context and per-tool scope enforcement for the secured MCP edge (Epic J).

In plain English: when an agent calls one of our tools, the gateway has already proven *who* the
caller is (an OAuth token) and *what they're allowed to do* (the token's scopes). This module is
the small, in-process check that the tool the agent is calling matches the scope it was granted —
a read tool needs ``deposits:read``, the money-moving tools need ``deposits:write``. If they don't
match, the call is rejected before it touches the engine.

Formally: this is the application-layer half of ADR-IC-010 §P3/§P4. The gateway (Kong) attests the
caller identity as ``X-Client-Id`` (derived from the OAuth token's ``sub``, never a tool argument —
Document 11 §"hallucinated parameters") and the granted scopes as ``X-OAuth-Scope``. The MCP server
trusts those gateway-attested headers and enforces *scope-per-tool* here: exactly one scope per tool
family, no "god scope" (§P4). The ``aud`` check (§P3) lives in the ASGI middleware (``app.py``); this
module owns the scope half and the ``AuthContext`` both layers share.
"""

from __future__ import annotations

from dataclasses import dataclass

from mcp.shared.exceptions import McpError
from mcp.types import INVALID_PARAMS, ErrorData

# Gateway-attested request headers (ADR-IC-010 §P3/§P4). Kong sets these on the upstream request
# from the OAuth token after it validates the signature; the MCP app trusts them and never reads the
# token to derive identity. X-Client-Id comes from the token `sub` (Document 11) and is OVERWRITTEN
# by the gateway, so a client-supplied value can never reach a tool.
CLIENT_ID_HEADER = "X-Client-Id"
OAUTH_SCOPE_HEADER = "X-OAuth-Scope"

# The gateway-attested RFC 8705 mTLS-bound sender-constraint thumbprint (ADR-IC-010 §A8, bd
# babelstone-26rb). Kong validates the step-up token's `cnf.x5t#S256` against the presented client
# cert and, on a match, OVERWRITES this header with the confirmed thumbprint (and on a mismatch 401s
# before the request ever reaches here — a token replayed from a different sender never arrives). The
# value is the confirmed binding for THIS request; an empty/absent header means the token was a plain
# (POC-legacy) Bearer, not sender-constrained. Read here so the attestation chain carries the binding
# the gateway confirmed; like every other identity header it is NEVER taken from a tool argument.
SCA_CNF_X5T_HEADER = "X-SCA-Cnf-X5t"


# Scope-per-tool (ADR-IC-010 §P4): one tool family maps to exactly one scope; reads carry the
# reserved read scope, writes the write scope. No tool maps to a "god scope". The read/write tiering
# keys on SCOPE, not on MCP method (§A3).
DEPOSITS_READ = "deposits:read"
DEPOSITS_WRITE = "deposits:write"

TOOL_SCOPES: dict[str, str] = {
    "constitute_deposit": DEPOSITS_WRITE,
    # The orchestrator-routed constitution producer (Document 11 Pattern 2; bd babelstone-ziu3.6) STARTS
    # a saga and returns a process_id — a write, so it carries the same deposits:write scope as the
    # engine-direct constitute tool (§P4 — one tool maps to exactly one scope).
    "constitute_deposit_saga": DEPOSITS_WRITE,
    "mature_deposit": DEPOSITS_WRITE,
    "pay_interest": DEPOSITS_WRITE,
    "get_deposit": DEPOSITS_READ,
    # The async-completion polling tool (Document 11 Pattern 2; bd babelstone-vjoi) is a READ — it only
    # observes saga process status — so it carries the reserved read scope; a deposits:read token can poll
    # status but cannot reach the write tools (§P4 — one tool maps to exactly one scope).
    "get_process_status": DEPOSITS_READ,
}


@dataclass(frozen=True)
class AuthContext:
    """The gateway-attested caller, as the MCP app sees it for a single request.

    ``client_id`` is the OAuth ``sub`` Kong attested via ``X-Client-Id`` (ADR-IC-010 §P3); ``scopes``
    is the set of OAuth scopes from ``X-OAuth-Scope``. ``sender_bound`` is the RFC 8705 mTLS-bound
    sender-constraint confirmation: the ``cnf.x5t#S256`` thumbprint Kong validated against the
    presented client cert and attested via ``X-SCA-Cnf-X5t`` (ADR-IC-010 §A8) — non-empty when the
    request arrived on a sender-constrained step-up token, empty for a plain Bearer. All are read from
    request headers the gateway set — never from a tool argument.
    """

    client_id: str
    scopes: frozenset[str]
    sender_bound: str = ""

    @property
    def is_sender_constrained(self) -> bool:
        """True iff the gateway confirmed an RFC 8705 mTLS-binding for this request (§A8)."""
        return bool(self.sender_bound)

    @classmethod
    def from_headers(cls, headers: object) -> "AuthContext":
        """Build an ``AuthContext`` from a mapping-like (Starlette ``Headers``) of request headers.

        Fail-closed: a missing/empty ``X-Client-Id`` raises — a request with no gateway-attested
        identity has no business reaching a tool. ``X-OAuth-Scope`` is a space-delimited list per the
        OAuth convention; an absent header is an empty scope set (which fails every tool's check).
        ``X-SCA-Cnf-X5t`` is the gateway-confirmed mTLS-binding thumbprint (§A8); absent => an
        unbound (plain-Bearer) request. The gateway has already 401'd a token replayed from a
        different sender (its ``cnf`` did not match the presented cert), so a non-empty value here is
        a binding the gateway confirmed, not one to re-verify at the app layer.
        """
        get = headers.get  # Starlette Headers / dict both expose .get
        client_id = (get(CLIENT_ID_HEADER) or "").strip()
        if not client_id:
            raise McpError(
                ErrorData(
                    code=INVALID_PARAMS,
                    message=(
                        "Missing gateway-attested caller identity. The MCP edge requires the "
                        "X-Client-Id header set by the gateway from the OAuth token sub; it is never "
                        "accepted as a tool argument (ADR-IC-010 §P3, Document 11)."
                    ),
                )
            )
        raw_scope = get(OAUTH_SCOPE_HEADER) or ""
        scopes = frozenset(s for s in raw_scope.split() if s)
        sender_bound = (get(SCA_CNF_X5T_HEADER) or "").strip()
        return cls(client_id=client_id, scopes=scopes, sender_bound=sender_bound)


# Prompts carry NO scope guard: they are pure templates (no engine call, no PII), so they are
# deliberately absent from any scope registry — a read-only token can still enumerate and render
# them. There is likewise no resource-scope registry: ADR-IC-010 §A2 (2026-05-31 amendment) replaced
# the only candidate deposit resource (`bank://deposits/{deposit_id}`) with the `get_deposit` tool,
# so the scoped MCP surface is tools-only. The read/write tiering keys on SCOPE, not on MCP method
# (§A3) — `get_deposit` carries the reserved `deposits:read`.


def check_tool_scope(auth: AuthContext, tool: str) -> None:
    """Enforce scope-per-tool (ADR-IC-010 §P4). Raise ``McpError`` if ``auth`` lacks the tool's scope.

    A read token (``deposits:read``) calling a write tool (``constitute_deposit``) is rejected here,
    before the engine is touched. An unknown tool is rejected too (no implicit grant).
    """
    required = TOOL_SCOPES.get(tool)
    if required is None:
        raise McpError(
            ErrorData(
                code=INVALID_PARAMS,
                message=f"Unknown tool '{tool}' has no scope mapping (ADR-IC-010 §P4).",
            )
        )
    if required not in auth.scopes:
        raise McpError(
            ErrorData(
                code=INVALID_PARAMS,
                message=(
                    f"Insufficient scope for tool '{tool}': requires '{required}'. The OAuth token "
                    "presented does not carry it (ADR-IC-010 §P4 — one tool maps to exactly one scope)."
                ),
            )
        )
