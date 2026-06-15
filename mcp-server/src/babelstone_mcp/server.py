"""The FastMCP server: ``constitute_deposit``, ``get_deposit``, ``mature_deposit``, ``pay_interest``.

All map 1:1 to the engine's HTTP API. Per ADR-IC-010's 2026-05-31 amendment, the tool/resource
axis is *control ownership* (model-invokable vs host-attached), not CQRS command/query — so a read
the agent fetches on demand is a tool, not a resource. ``constitute_deposit`` / ``mature_deposit`` /
``pay_interest`` are writes (engine commands); ``get_deposit`` is the read-only ``deposit_position``
projection. Each declares a structured return type, so the SDK publishes an ``outputSchema``
(ADR-IC-010 P6 — mandatory on every tool).

Auth (Epic J, babelstone-e50n): the secured edge fronts this server with Kong + OAuth. Every tool
reads the gateway-attested caller identity (``X-Client-Id``, derived from the OAuth token ``sub`` —
NEVER a tool argument; Document 11) from the request headers and enforces *scope-per-tool* (§P4) via
``check_tool_scope``: ``get_deposit`` needs ``deposits:read``, the writes need ``deposits:write``.
The authoritative ``aud`` re-check (§P3) and the public RFC 9728 metadata (§P2) live in ``app.py``.

§P8 human-in-the-loop elicitation (Epic J.4, bd babelstone-ar1y) is wired here via ``elicitation.py``:
``constitute_deposit`` uses FORM mode to confirm a PERIODIC interest selection (a non-irreversible
parameter clarification — live), and ``mature_deposit`` / ``pay_interest`` carry the URL-mode step-up
SCA machinery for irreversible settlement, dormant behind ``ELICITATION_URL_MODE_ENABLED`` (default
off) until the SCA-trigger + token-re-entry fork is resolved (see ``_maybe_stepup_sca``). The
elicitation messages ride the same Streamable-HTTP ``/mcp`` route — no kong.yml change is needed.
"""

from __future__ import annotations

import os
import uuid

from mcp.server.fastmcp import Context, FastMCP
from mcp.server.transport_security import TransportSecuritySettings
from pydantic import BaseModel, Field

from .auth import AuthContext, check_tool_scope
from .elicitation import (
    _PERIODIC_CONFIRM_MSG,
    _STEPUP_MSG,
    ElicitationAborted,
    PeriodicInterestConfirmation,
    aborted_error,
    elicit_form_clarification,
    elicit_url_stepup,
)
from .engine_client import EngineClient

# §P8 URL-mode step-up SCA is the v1 MACHINERY (built + tested), kept DORMANT by default behind this
# flag until the maintainer resolves the SCA-trigger + token-re-entry fork (see the money-mover tools
# below). With it off, mature_deposit / pay_interest behave exactly as before. Tests flip it on to
# exercise the affordance. Read as a bool from the environment ("true"/"1"/"yes" enable it).
ELICITATION_URL_MODE_ENABLED = os.environ.get(
    "ELICITATION_URL_MODE_ENABLED", "false"
).strip().lower() in ("true", "1", "yes")

# Base for the bank-controlled step-up SCA URL the agent navigates the human to (URL mode). The only
# dynamic parts of the constructed URL are a STABLE operation code and an elicitation_id UUID — never
# a deposit id, client id, IBAN, or amount (the no-PII invariant). Overridable per deployment.
_SCA_STEPUP_BASE_URL = os.environ.get(
    "BABELSTONE_SCA_STEPUP_BASE_URL", "http://localhost:9999/sca"
)


def _stepup_url(operation_code: str, elicitation_id: str) -> str:
    """Build the bank-controlled step-up SCA URL for ``operation_code`` (URL-mode elicitation).

    Carries ONLY the stable operation code (e.g. ``MATURE_DEPOSIT``) and the elicitation_id UUID —
    no business identifier (no deposit id) ever reaches the elicitation channel.
    """
    return f"{_SCA_STEPUP_BASE_URL}/stepup?operation={operation_code}&elicitation_id={elicitation_id}"


def _transport_security() -> TransportSecuritySettings:
    """Allow the Host/Origin Kong forwards to the upstream (ADR-IC-010 §P5).

    The MCP Streamable-HTTP transport applies DNS-rebinding protection: it 421s a request whose
    ``Host`` is not allow-listed. Behind Kong the upstream receives ``Host: mcp-server:8080`` (the
    docker-network upstream address Kong dials), which the default localhost-only allow-list
    rejects. Because this server's ONLY ingress is Kong over enforced mutual TLS — a bypassing
    actor is already rejected at the TLS handshake (``__main__.build_tls_kwargs``) — the Host
    header is not the trust boundary here, so we allow-list the upstream address Kong uses plus the
    local-dev addresses. ``BABELSTONE_ALLOWED_HOSTS`` / ``BABELSTONE_ALLOWED_ORIGINS`` override the
    defaults (comma-separated) for a different deployment hostname.
    """
    default_hosts = "mcp-server:8080,mcp-server,localhost,localhost:8000,127.0.0.1,127.0.0.1:8080"
    default_origins = "http://localhost:8000,https://mcp-server:8080"
    hosts = os.environ.get("BABELSTONE_ALLOWED_HOSTS", default_hosts)
    origins = os.environ.get("BABELSTONE_ALLOWED_ORIGINS", default_origins)
    return TransportSecuritySettings(
        enable_dns_rebinding_protection=True,
        allowed_hosts=[h.strip() for h in hosts.split(",") if h.strip()],
        allowed_origins=[o.strip() for o in origins.split(",") if o.strip()],
    )


mcp = FastMCP("babelstone-deposits", transport_security=_transport_security())

_engine: EngineClient | None = None


def _authorize(ctx: Context, tool: str) -> AuthContext:
    """Build the gateway-attested ``AuthContext`` for this request and enforce the tool's scope.

    Reads ``X-Client-Id`` / ``X-OAuth-Scope`` off the Starlette request the Streamable-HTTP transport
    threads onto the request context (ADR-IC-010 §P3/§P4). The gateway set those headers from the
    OAuth token; the identity is NEVER taken from a tool argument (Document 11). Raises ``McpError``
    on a missing identity or insufficient scope before the engine is touched.
    """
    request = ctx.request_context.request
    auth = AuthContext.from_headers(request.headers)
    check_tool_scope(auth, tool)
    return auth


def engine() -> EngineClient:
    """The engine client, lazily built from ``BABELSTONE_ENGINE_URL`` (overridable in tests)."""
    global _engine
    if _engine is None:
        _engine = EngineClient(os.environ.get("BABELSTONE_ENGINE_URL", "http://localhost:8080"))
    return _engine


def set_engine(client: EngineClient) -> None:
    """Inject an engine client (tests / a configured host)."""
    global _engine
    _engine = client


class ConstituteDepositResult(BaseModel):
    """Structured tool output (ADR-IC-010 P6) — the assigned id and lifecycle state."""

    deposit_id: str = Field(description="The engine-assigned deposit id (UUID).")
    status: str = Field(description="Lifecycle state — ACTIVE on a constituted deposit.")
    commit_sequence: int = Field(
        description="The per-stream version this constitution committed (ADR-IC-005 §P3). Pass it as "
        "get_deposit's min_sequence to read your own write before the projector catches up."
    )


@mcp.tool()
async def constitute_deposit(
    product_id: str,
    role: str,
    principal_cents: int,
    term_days: int,
    start_date: str,
    funding_account: str,
    ctx: Context,
    interest_variant: str = "AT_MATURITY",
    auto_renewal_policy: str = "NONE",
    payment_period_months: int = 0,
) -> ConstituteDepositResult:
    """Constitute a term deposit. ``principal_cents`` and all money are integer cents (never a float).

    ``product_id`` is the variant the rate sheet prices (e.g. ``dpz_pt_12m_juros_venc``); ``role`` is
    the pricing role (e.g. ``standard``); ``start_date`` is ISO-8601 (YYYY-MM-DD). The resolved TAN
    is stamped by the engine from the active rate sheet — never supplied here.

    ``interest_variant`` is one of AT_MATURITY (interest + principal at maturity), PERIODIC (coupons
    paid out to the current account, principal at maturity), or ADVANCE (full-term interest at t=0).
    ``payment_period_months`` is required for PERIODIC — 1 (monthly) or 3 (quarterly), the only
    cadences priced — and is 0/omitted for AT_MATURITY and ADVANCE.

    Requires ``deposits:write`` (ADR-IC-010 §P4). The actor is the gateway-attested ``X-Client-Id``
    (OAuth ``sub``), never a tool argument (Document 11).

    §P8 form-mode elicitation (Epic J.4): when ``interest_variant`` is PERIODIC, the server pauses and
    asks the human to confirm the periodic-coupon choice before constituting — the non-irreversible
    parameter clarification §P8 reserves form mode for. If the human declines/cancels, the call is
    aborted with an ``McpError`` and no deposit is constituted. The confirmation prompt carries only
    generic text (no PII). The AT_MATURITY / ADVANCE variants do not trigger it.
    """
    auth = _authorize(ctx, "constitute_deposit")

    # §P8 form mode — confirm the periodic-coupon selection (a non-irreversible clarification). The
    # prompt is the static, PII-free _PERIODIC_CONFIRM_MSG; the schema is a single bool. A decline or
    # cancel aborts before any engine command runs.
    if interest_variant == "PERIODIC":
        try:
            answer = await elicit_form_clarification(
                ctx, _PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation
            )
        except ElicitationAborted:
            answer = None  # decline / cancel — treated as non-confirmation below
        # Block on a decline/cancel (answer is None) AND on an explicit "no" (confirmed=False): only
        # an affirmative accept proceeds to the irreversible-in-intent constitute command.
        if answer is None or not answer.confirmed:
            raise aborted_error(
                "User did not confirm the periodic interest selection; the deposit was not "
                "constituted (ADR-IC-010 §P8 form-mode confirmation)."
            )

    result = await engine().constitute(
        {
            "principal_cents": principal_cents,
            "product_id": product_id,
            "role": role,
            "term_days": term_days,
            "start_date": start_date,
            "interest_variant": interest_variant,
            "auto_renewal_policy": auto_renewal_policy,
            "funding_account": funding_account,
            "payment_period_months": payment_period_months,
        },
        client_id=auth.client_id,
    )
    return ConstituteDepositResult(
        deposit_id=result["deposit_id"],
        status=result["status"],
        commit_sequence=result["commit_sequence"],
    )


class DepositPosition(BaseModel):
    """Structured tool output (ADR-IC-010 P6) — the ONE canonical deposit resource (ADR-IC-005).

    All money is integer cents (ADR-PC-010 §P1), never a float. The engine serves this from the fast
    denormalized read model by default and folds the event stream only for read-your-writes — the
    CQRS read/write split is the engine's internal business, not two shapes. ``last_sequence`` is the
    per-stream version this view reflects (thread it forward as ``min_sequence`` for monotonic reads);
    ``last_updated`` is the producing event's timestamp, for staleness display.
    """

    deposit_id: str = Field(description="The deposit id (UUID).")
    sor: str = Field(description="System of record — 'engine' for an engine-materialised deposit (ADR-PC-018 §6.2).")
    principal_cents: int = Field(description="Principal in integer cents.")
    tan_basis_points: int = Field(description="Resolved TAN in basis points, stamped by the engine.")
    rate_sheet_version_id: str = Field(description="Rate sheet version the TAN was resolved from (price/version key).")
    product_code: str = Field(description="Catalogue structural product code (which product); '' for pre-v794 deposits.")
    term_days: int = Field(description="Term length in days.")
    start_date: str = Field(description="ISO-8601 start date.")
    maturity_date: str = Field(description="ISO-8601 maturity date.")
    interest_variant: str = Field(description="Interest variant (AT_MATURITY, PERIODIC, or ADVANCE).")
    auto_renewal_policy: str = Field(description="Auto-renewal policy (e.g. NONE).")
    payment_period_months: int = Field(
        description="PERIODIC coupon cadence in months (1 monthly, 3 quarterly); 0 for AT_MATURITY/ADVANCE."
    )
    accrued_gross_interest_cents: int = Field(description="Gross interest accrued to date, cents.")
    withholding_to_date_cents: int = Field(description="Withholding tax accrued to date, cents.")
    net_interest_cents: int = Field(description="Net interest to date, cents.")
    total_payout_cents: int = Field(description="Total payout to date, cents.")
    coupons_paid: int = Field(description="PERIODIC coupons paid out so far (0 for AT_MATURITY/ADVANCE).")
    lifecycle: str = Field(description="Lifecycle state (e.g. Active, Matured).")
    last_sequence: int = Field(description="The per-stream version this view reflects (ADR-IC-005 §P3 read-your-writes barrier).")
    last_updated: str = Field(description="ISO-8601 timestamp of the producing event (for staleness display).")


@mcp.tool()
async def get_deposit(
    deposit_id: str, ctx: Context, min_sequence: int | None = None
) -> DepositPosition:
    """Read a term deposit's current state — the ONE canonical deposit resource (ADR-IC-005).

    ``deposit_id`` is the engine-assigned UUID returned by ``constitute_deposit``. Served from the
    fast denormalized read model by default. For read-your-writes (e.g. reading right after a
    constitute/mature), pass ``min_sequence`` = the ``commit_sequence`` that command returned: the
    engine then folds the event stream if the projection has not caught up, so you always see your own
    write. Money is integer cents; ``last_sequence`` on the result is the version served (thread it
    forward for monotonic reads).

    Requires ``deposits:read`` (ADR-IC-010 §P4) — the reserved read scope; a ``deposits:read`` token
    cannot reach the write tools.
    """
    auth = _authorize(ctx, "get_deposit")
    return DepositPosition(
        **await engine().deposit_position(deposit_id, min_sequence, client_id=auth.client_id)
    )


@mcp.tool()
async def mature_deposit(deposit_id: str, ctx: Context) -> DepositPosition:
    """Mature (settle) a term deposit — runs accrual to term end and returns the matured position.

    ``deposit_id`` is the engine-assigned UUID. Returns the same ``DepositPosition`` shape with the
    interest fields now folded in (``accrued_gross_interest_cents``, ``withholding_to_date_cents``,
    ``net_interest_cents``, ``total_payout_cents``) and ``lifecycle`` = ``Matured``. Money is integer
    cents.

    Requires ``deposits:write`` (ADR-IC-010 §P4). Settlement is irreversible, so under §P8 it gets
    URL-mode ``elicitation/create`` step-up SCA — the v1 machinery is here, dormant behind
    ``ELICITATION_URL_MODE_ENABLED`` (default off) until the SCA fork below is resolved.
    """
    auth = _authorize(ctx, "mature_deposit")
    await _maybe_stepup_sca(ctx, "MATURE_DEPOSIT")
    return DepositPosition(**await engine().mature(deposit_id, client_id=auth.client_id))


@mcp.tool()
async def pay_interest(deposit_id: str, ctx: Context) -> DepositPosition:
    """Pay one PERIODIC coupon on a term deposit — accrues the next coupon window, withholds tax on
    that one flow, pays the net to the current account, and returns the updated position.

    ``deposit_id`` is the engine-assigned UUID. Only an Active PERIODIC deposit pays coupons; the
    coupon window is derived by the engine from the deposit's schedule and the coupons already paid
    (not supplied here). Returns the same ``DepositPosition`` shape with the coupon's gross/withholding/
    net folded in and ``coupons_paid`` incremented; the final coupon is paid with the principal at
    maturity (use ``mature_deposit`` for that), so calling this once no intermediate coupon remains is
    rejected. Money is integer cents.

    Requires ``deposits:write`` (ADR-IC-010 §P4). Like ``mature_deposit``, the coupon settlement is
    irreversible; under §P8 it gets URL-mode step-up SCA — the v1 machinery is here, dormant behind
    ``ELICITATION_URL_MODE_ENABLED`` (default off) until the SCA fork is resolved.
    """
    auth = _authorize(ctx, "pay_interest")
    await _maybe_stepup_sca(ctx, "PAY_INTEREST")
    return DepositPosition(**await engine().pay_interest(deposit_id, client_id=auth.client_id))


async def _maybe_stepup_sca(ctx: Context, operation_code: str) -> None:
    """§P8 URL-mode step-up SCA affordance on an irreversible money-mover (Epic J.4, ar1y).

    When ``ELICITATION_URL_MODE_ENABLED`` is on, mint a fresh elicitation_id (a UUID — NOT the
    deposit id), build the bank-controlled step-up URL for ``operation_code``, and ask the human (via
    URL-mode ``elicitation/create``) to complete SCA out-of-band. On accept we proceed to the engine;
    on decline/cancel we abort with a static, PII-free ``McpError``. When the flag is off (the
    default), this is a no-op — the tool behaves exactly as before.

    ─────────────────────────────────────────────────────────────────────────────────────────────
    MAINTAINER FLAG — the step-up SCA fork (do NOT resolve speculatively; bd babelstone-ar1y ships
    the elicitation MACHINERY + this affordance, NOT a security gate):

    Q1 — SCA-TRIGGER DETECTION: how does this tool know fresh SCA is actually needed?
       (a) the engine returns a structured ``SCA_REQUIRED`` on a money-mover called without a
           fresh-enough SCA claim; this tool catches it, fires the step-up, then retries. Cleanest
           signal; needs a bounded engine-side addition. RECOMMENDED.
       (b) a Kong ``pre-function`` SCA gate on the ``/mcp`` route (mirroring the constitute REST
           route, ADR-IC-006 §P2) returns 403 before this server is even reached — which KILLS the
           tool call before elicitation can start. Structurally incompatible with firing elicitation
           on the tool call; the agent would have to handle the 403 itself. The ``/mcp`` route has NO
           such gate today — adding it is a maintainer decision, not this PR.
       (c) proactive: always fire the step-up at the top of every money-mover (this stub's current
           shape). Simplest; over-prompts users who already hold fresh SCA.

    Q2 — TOKEN RE-ENTRY: after the human completes SCA at the bank URL, how does the fresh proof
       flow back into the tool call?
       (a) agent re-call with a new Bearer (PKCE/refresh) — simplest, needs an agent that can refresh.
       (b) out-of-band: the bank's SCA completion signals the engine (a short-lived nonce tied to the
           elicitation_id); the engine accepts the next call within a TTL. No agent token refresh,
           but introduces session state in the engine.
       (c) ``session.send_elicit_complete(elicitation_id)`` from the bank's SCA callback + agent
           retry (still needs a fresh token if a Kong SCA gate exists).

    Both questions touch the saga orchestrator (ADR-IC-010 §P8: "realised by the saga orchestrator")
    and/or a new Kong gate — out of this lane's scope. v1 therefore: machinery present, flag default
    OFF, and we deliberately do NOT call ``send_elicit_complete`` (no out-of-band completion wired
    yet). Flip the default + wire Q1/Q2 once the maintainer decides.
    ─────────────────────────────────────────────────────────────────────────────────────────────
    """
    if not ELICITATION_URL_MODE_ENABLED:
        return
    elicitation_id = str(uuid.uuid4())  # a UUID, never the deposit id (no business identity leaks)
    try:
        await elicit_url_stepup(
            ctx, _STEPUP_MSG, _stepup_url(operation_code, elicitation_id), elicitation_id
        )
    except ElicitationAborted:
        raise aborted_error(
            "User did not complete step-up authentication; the operation was not performed "
            "(ADR-IC-010 §P8 URL-mode step-up SCA)."
        ) from None


# Side-effect import registers the prompt templates on ``mcp`` at import time (Epic J.2, bd
# babelstone-2ep0). It lives at the BOTTOM of the module — ``prompts`` does ``from .server import
# mcp``, so importing it here, after ``mcp`` is defined, avoids the circular import. ``app.py``
# already imports ``server`` to pick up the tool registrations; this rider means the same import now
# also brings the prompt surface (ADR-IC-010 §A1). The prompts ride the same Streamable-HTTP /mcp
# route — no kong.yml change is needed.
#
# There is deliberately NO deposit *resource*: ADR-IC-010 §A2 (2026-05-31 amendment) replaced the
# ``bank://deposits/{deposit_id}`` resource template with the ``get_deposit`` tool — the single,
# lag-sensitive deposit position is a model-controlled on-demand read (a tool with a mandatory §P6
# outputSchema), not host-attached context. Re-introducing that template would re-incur the exact
# discoverability + untyped-body trade-offs §A2 rejected (Document 11 §"single deposit → tool").
from . import prompts as _prompts  # noqa: E402,F401 — registers constitute_term_deposit + review_upcoming_maturities
