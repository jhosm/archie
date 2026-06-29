"""The FastMCP server: ``constitute_deposit``, ``get_deposit``, ``mature_deposit``, ``pay_interest``,
``pay_installment``.

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

§P9 agent trust-model hardening (Epic J.5, bd babelstone-u01t): the agent is the UNTRUSTED caller
(Document 11 §"Trust Model — The Agent Is Untrusted"). Three structural defences compose here. (1)
*Typed-not-free-text*: every tool returns a structured ``outputSchema`` (§P6), never a free-text
confirmation, so there is no untyped body for an injection to ride in. (2) *Sanitisation*: any
customer-/external-writable free-text the engine returns is run through ``sanitize.sanitize_free_text``
at the ``engine_client`` boundary (control-character + instruction-shape stripping, length cap, and a
data-not-instruction fence) before it reaches a tool — the bank's second-line defence against prompt
injection via bank-returned content (the agent vendor is the first line; the bank cannot control it).
(3) *Hallucinated-parameter resistance*: the actor identity is the gateway-attested ``X-Client-Id``
(OAuth ``sub``), NEVER a tool argument, and ``inputSchema`` is strict with no implicit defaults for
security-relevant parameters. The deposit position has no customer free-text field today, so (2) is an
identity transform now; it is the forward-safe choke point so a future free-text field is sanitised by
construction. None of this *eliminates* the threat — it *reduces the attack surface*, the posture
Document 11 §Trust Model commits the bank to.

§P8 human-in-the-loop elicitation (Epic J.4, bd babelstone-ar1y) is wired here via ``elicitation.py``:
``constitute_deposit`` uses FORM mode to confirm a PERIODIC interest selection (a non-irreversible
parameter clarification — live and §P8-conformant), and ``mature_deposit`` / ``pay_interest`` carry the
URL-mode step-up-SCA gate for irreversible settlement (Q-BE resolved, bd babelstone-ziu3.5). That gate is
now REAL: the load-bearing rule that "the irreversible action transitions on the BANK's own out-of-band
signal, not anything the agent reports back" is satisfied by the ENGINE — its ``ScaPrecondition`` (Q1)
refuses to settle without FRESH gateway-attested SCA (the AS-signed ``acr``/``auth_time`` Kong attests) and
returns ``422 SCA_REQUIRED``. The money-mover tool catches that, fires the URL-mode step-up elicitation, and
RETRIES with the refreshed token (Q2, ``_settle_with_stepup_sca``). The trust anchor is the AS signature the
engine sees, never the agent's navigate-consent — so an agent that fabricates an accept is still 422'd on the
retry. ``ELICITATION_URL_MODE_ENABLED`` (default ON) governs only whether the tool runs the elicitation
prompt on a 422; with it off the engine STILL gates (the tool surfaces the McpError directly). The elicitation
messages ride the same Streamable-HTTP ``/mcp`` route — no kong.yml change is needed for the prompt itself;
the SCA-claim attestation Kong forwards to the engine is the kong.yml addition this lane makes.
"""

from __future__ import annotations

import os
import uuid
from collections.abc import Awaitable, Callable

import httpx
from mcp.server.fastmcp import Context, FastMCP
from mcp.server.transport_security import TransportSecuritySettings
from mcp.shared.exceptions import McpError
from mcp.types import INVALID_PARAMS, ErrorData
from pydantic import BaseModel, Field

from .auth import AuthContext, check_tool_scope
from .elicitation import (
    PERIODIC_CONFIRM_MSG,
    STEPUP_MSG,
    ElicitationAborted,
    PeriodicInterestConfirmation,
    aborted_error,
    elicit_form_clarification,
    elicit_url_stepup,
)
from .engine_client import EngineClient, ScaRequiredError
from .orchestrator_client import OrchestratorClient

# §P8 URL-mode step-up SCA is now a REAL enforced gate (Q-BE resolved, bd babelstone-ziu3.5), default ON.
# The enforcement does NOT rest on this elicitation: the load-bearing gate is the ENGINE's ScaPrecondition
# (Q1), which 422s a money-mover called without FRESH gateway-attested SCA (the AS-signed acr/auth_time
# Kong attests). This flag governs whether the money-mover tools wrap settlement in the step-up-then-retry
# flow (Q2): on a 422 SCA_REQUIRED, fire the URL-mode step-up elicitation so the human re-authenticates at
# the bank, then RETRY with the refreshed token. The trust anchor is the AS signature the engine sees, never
# the agent's navigate-consent — exactly what §P8 requires. Flipping it OFF reverts the tools to a single
# unguarded engine call (which the engine STILL 422s — the gate cannot be bypassed from the client side);
# OFF is only for environments that front the engine with a different step-up transport. Read as a bool
# from the environment ("true"/"1"/"yes" enable it).
ELICITATION_URL_MODE_ENABLED = os.environ.get(
    "ELICITATION_URL_MODE_ENABLED", "true"
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


_orchestrator: OrchestratorClient | None = None


def orchestrator() -> OrchestratorClient:
    """The orchestrator client, lazily built from ``BABELSTONE_ORCHESTRATOR_URL`` (overridable in tests).

    A SEPARATE boundary from the engine client (maintainer's vjoi decision, 2026-06-17): the orchestrator
    owns saga state, so process status is read from it, not the engine. Defaults to the saga edge's
    dev address (``http://localhost:8090``, the orchestrator host the demo starts; see serve.py).
    """
    global _orchestrator
    if _orchestrator is None:
        _orchestrator = OrchestratorClient(
            os.environ.get("BABELSTONE_ORCHESTRATOR_URL", "http://localhost:8090")
        )
    return _orchestrator


def set_orchestrator(client: OrchestratorClient) -> None:
    """Inject an orchestrator client (tests / a configured host)."""
    global _orchestrator
    _orchestrator = client


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
    # prompt is the static, PII-free PERIODIC_CONFIRM_MSG; the schema is a single bool. A decline or
    # cancel aborts before any engine command runs.
    if interest_variant == "PERIODIC":
        try:
            answer = await elicit_form_clarification(
                ctx, PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation
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


class ProcessStatus(BaseModel):
    """Structured tool output (ADR-IC-010 §P6) — the coarse async-completion status of a saga process
    (Document 11 Pattern 2; bd babelstone-vjoi). Every field is structural and PII-free (ADR-PC-004 §P2):
    a process reference, a state label, a coarse status, a version, a bool — no NIF/IBAN/name/amount."""

    process_id: str = Field(description="The public PROC-… process reference the saga edge minted.")
    state: str = Field(
        description="The verbatim saga state — the operator-grade label (e.g. PARALLEL_VALIDATION, "
        "AWAIT_WORKFLOW_APPROVAL, COMPLETED). The family's own vocabulary."
    )
    status: str = Field(
        description="The COARSE agent-facing status: PROCESSING (still working — poll again), "
        "AWAITING_APPROVAL (paused on an approval), ACTION_REQUIRED (a bank operator must reconcile), "
        "COMPLETED, FAILED, or CANCELLED."
    )
    version: int = Field(description="The saga's optimistic-concurrency version this snapshot reflects.")
    terminal: bool = Field(description="Whether the saga reached a terminal state — stop polling when true.")


@mcp.tool()
async def get_process_status(process_id: str, ctx: Context) -> ProcessStatus:
    """Poll the status of an in-flight saga process — the async-completion read (Document 11 Pattern 2).

    ``process_id`` is the public ``PROC-…`` reference minted when a deposit constitution is STARTED through
    the saga edge. Use it to poll a long-running request (parallel validations, an approval wait, core
    clearance, automatic compensation) until ``terminal`` is true. Returns the coarse ``status`` an agent
    acts on (PROCESSING / AWAITING_APPROVAL / ACTION_REQUIRED / COMPLETED / FAILED / CANCELLED) alongside
    the verbatim ``state`` and the ``terminal`` flag.

    Requires ``deposits:read`` (ADR-IC-010 §P4) — the reserved read scope; a ``deposits:read`` token can
    poll status but cannot reach the write tools. The actor is the gateway-attested ``X-Client-Id`` (OAuth
    ``sub``), never a tool argument (Document 11); the orchestrator enforces that you OWN the process —
    polling another client's ``process_id`` returns a not-authorized error, never their status.

    NOTE (bd babelstone-vjoi / ziu3.6): this is the READ half of the loop; its PRODUCER is
    ``constitute_deposit_saga`` (bd babelstone-ziu3.6), the orchestrator-routed constitution tool that STARTS
    a saga and returns the ``process_id`` to poll here — closing the producer gap so the Pattern 2 loop can be
    exercised end-to-end purely over MCP. (The engine-direct ``constitute_deposit`` tool returns a
    ``deposit_id``, not a saga ``process_id``, so it is NOT this loop's producer.)
    """
    auth = _authorize(ctx, "get_process_status")
    try:
        result = await orchestrator().process_status(process_id, client_id=auth.client_id)
    except httpx.HTTPStatusError as exc:
        # 404 (no such process) and 403 (owned by another client) are EXPECTED business outcomes, not engine
        # failures — surface a clean, PII-free McpError to the agent rather than a raw transport error. Any
        # other non-2xx propagates (a genuine fault).
        status_code = exc.response.status_code
        if status_code == 404:
            raise McpError(
                ErrorData(
                    code=INVALID_PARAMS,
                    message=f"No process found for process_id '{process_id}'.",
                )
            ) from None
        if status_code == 403:
            raise McpError(
                ErrorData(
                    code=INVALID_PARAMS,
                    message=(
                        "That process is owned by a different client; you can only poll your own processes "
                        "(ADR-IC-006 §P4 — process_id is not a capability token)."
                    ),
                )
            ) from None
        raise
    return ProcessStatus(**result)


class ConstituteDepositSagaFollowUp(BaseModel):
    """The agent-directed next step (Document 11 §Tool result ``follow_up``): poll ``get_process_status``.

    A structural, typed hint — NOT free text (ADR-IC-010 §P6) — telling the agent exactly which tool to
    call next, and with which argument, to follow the async-completion loop (Document 11 Pattern 2) to a
    terminal state. The agent reasons over this rather than guessing the next call.
    """

    kind: str = Field(description="The follow-up kind — 'poll_tool' for the Pattern 2 polling loop.")
    tool: str = Field(description="The tool to call next — 'get_process_status'.")
    arguments: dict[str, str] = Field(
        description="The arguments to pass — {'process_id': <the minted PROC-… reference>}."
    )


class ConstituteDepositSagaResult(BaseModel):
    """Structured tool output (ADR-IC-010 §P6) — the saga PRODUCER result (Document 11 Pattern 2).

    Every field is a structural, PII-free reference (ADR-PC-004 §P2): the client-facing deposit and process
    references, the coarse acceptance status, and a typed ``follow_up`` hint. The ``process_id`` is the public
    ``PROC-…`` reference the agent threads into ``get_process_status`` to poll the saga to completion — the
    producer the engine-direct ``constitute_deposit`` tool cannot supply (it returns a ``deposit_id``, not a
    saga ``process_id``)."""

    deposit_id: str = Field(description="The client-facing DEP-… deposit reference the saga pinned.")
    process_id: str = Field(
        description="The public PROC-… saga process reference — pass it to get_process_status to poll "
        "async completion (Document 11 Pattern 2)."
    )
    status: str = Field(
        description="The coarse synchronous acceptance status — PROCESSING while the saga runs."
    )
    follow_up: ConstituteDepositSagaFollowUp = Field(
        description="The typed next-step hint pointing the agent at get_process_status(process_id)."
    )


@mcp.tool()
async def constitute_deposit_saga(
    product_code: str,
    amount_cents: int,
    source_account_ref: str,
    ctx: Context,
    interest_account_ref: str | None = None,
) -> ConstituteDepositSagaResult:
    """Constitute a term deposit through the saga edge — the async, orchestrator-routed path that returns a
    saga ``process_id`` (Document 11 Pattern 2 PRODUCER). Use this when the agent must follow the request to
    completion (parallel validations, an approval wait, core clearance); it is the producer that lets a later
    ``get_process_status`` call exist.

    Unlike ``constitute_deposit`` — which calls the engine DIRECTLY and returns a ``deposit_id`` (the engine
    walking-skeleton path) — this POSTs to the orchestrator edge (``POST /api/v1/deposits/constitute``), which
    STARTS the constitution saga and mints a public ``PROC-…`` ``process_id`` (ADR-IC-006 §P4 / Document 05
    §Step 0). The result carries that ``process_id`` plus a typed ``follow_up`` hint directing the agent to
    ``get_process_status(process_id)`` to poll the saga to a terminal state. The two tools sit ALONGSIDE each
    other: engine-direct for the DIRECT skeleton, saga-routed for the production async path.

    ``product_code`` is the catalogue reference (e.g. ``TD-TRAD-12M``); the deposit's SHAPE (term, interest
    variant, renewal policy, coupon cadence, pricing role) and the rate are resolved by the engine from the
    product code at constitution — never supplied here (the engine is the single home of product config,
    ADR-PC-009 / ADR-PC-008 §S2). ``amount_cents`` is the principal in integer cents (never a float).
    ``source_account_ref`` / ``interest_account_ref`` are OPAQUE account REFERENCES — tokens the PII boundary
    already issued — NOT raw IBANs (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).

    Requires ``deposits:write`` (ADR-IC-010 §P4). The owning client is the gateway-attested ``X-Client-Id``
    (OAuth ``sub``), forwarded to the orchestrator so it binds saga ownership to that identity — NEVER a tool
    argument (Document 11); a later ``get_process_status`` poll of this ``process_id`` enforces that same
    ownership (another client's poll is 403).
    """
    auth = _authorize(ctx, "constitute_deposit_saga")
    request: dict[str, object] = {
        "product_code": product_code,
        "amount": amount_cents,
        "source_account_ref": source_account_ref,
    }
    if interest_account_ref is not None:
        request["interest_account_ref"] = interest_account_ref
    result = await orchestrator().constitute(request, client_id=auth.client_id)
    process_id = result["process_id"]
    return ConstituteDepositSagaResult(
        deposit_id=result["deposit_id"],
        process_id=process_id,
        status=result["status"],
        follow_up=ConstituteDepositSagaFollowUp(
            kind="poll_tool",
            tool="get_process_status",
            arguments={"process_id": process_id},
        ),
    )


@mcp.tool()
async def mature_deposit(deposit_id: str, ctx: Context) -> DepositPosition:
    """Mature (settle) a term deposit — runs accrual to term end and returns the matured position.

    ``deposit_id`` is the engine-assigned UUID. Returns the same ``DepositPosition`` shape with the
    interest fields now folded in (``accrued_gross_interest_cents``, ``withholding_to_date_cents``,
    ``net_interest_cents``, ``total_payout_cents``) and ``lifecycle`` = ``Matured``. Money is integer
    cents.

    Requires ``deposits:write`` (ADR-IC-010 §P4). Settlement is irreversible, so under §P8 it is gated by
    real step-up SCA (Q-BE resolved, bd babelstone-ziu3.5): the ENGINE refuses to settle without FRESH
    gateway-attested SCA (the AS-signed ``acr``/``auth_time`` Kong attests) and returns ``422
    SCA_REQUIRED``. This tool then fires the URL-mode step-up elicitation so the human re-authenticates in
    the bank-controlled context, and RETRIES with the refreshed token. The settlement transitions on the
    bank's own signal (the AS signature the engine sees), never the agent's report — the §P8 invariant.
    If the human declines/cancels the step-up, the call is aborted with an ``McpError`` and nothing settles.
    """
    auth = _authorize(ctx, "mature_deposit")
    position = await _settle_with_stepup_sca(
        ctx,
        "MATURE_DEPOSIT",
        lambda: engine().mature(deposit_id, client_id=auth.client_id),
    )
    return DepositPosition(**position)


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
    irreversible, so under §P8 it is gated by real step-up SCA (Q-BE resolved, bd babelstone-ziu3.5): the
    ENGINE 422s the coupon without FRESH gateway-attested SCA (the AS-signed ``acr``/``auth_time`` Kong
    attests), this tool fires the URL-mode step-up elicitation, and RETRIES with the refreshed token. The
    coupon settles on the bank's own signal (the AS signature the engine sees), never the agent's report —
    the §P8 invariant. A declined/cancelled step-up aborts the call with an ``McpError`` and pays nothing.
    """
    auth = _authorize(ctx, "pay_interest")
    position = await _settle_with_stepup_sca(
        ctx,
        "PAY_INTEREST",
        lambda: engine().pay_interest(deposit_id, client_id=auth.client_id),
    )
    return DepositPosition(**position)


class PayInstallmentResult(BaseModel):
    """Structured tool output (ADR-IC-010 §P6) — a personal-loan installment command outcome.

    Every field is a structural, PII-free value (ADR-PC-004 §P2): the loan id, the folded lifecycle
    status, and the per-stream commit sequence (ADR-IC-005 §P3 read-your-writes token). It mirrors the
    engine's ``LoanCommandResponse`` shape — the loan command surface returns the command outcome, not a
    full position (UNLIKE the deposit money-movers, which return the folded ``DepositPosition``).
    """

    loan_id: str = Field(description="The loan id (UUID).")
    status: str = Field(
        description="Lifecycle state after the installment — ACTIVE while installments remain, SETTLED "
        "once the final installment clears the outstanding balance."
    )
    commit_sequence: int = Field(
        description="The per-stream version this installment committed (ADR-IC-005 §P3)."
    )


@mcp.tool()
async def pay_installment(
    loan_id: str, collection_account_ref: str, ctx: Context
) -> PayInstallmentResult:
    """Pay the next scheduled installment on a personal loan — collects one amortizing payment from the
    collection account and returns the loan's updated command outcome.

    ``loan_id`` is the engine-assigned UUID. ``collection_account_ref`` is the OPAQUE account token the
    installment is collected from (a reference the PII boundary already issued — NEVER a raw IBAN,
    ADR-PC-004 §P2). The engine derives WHICH installment (the next unpaid number) and the principal/
    interest split from the loan's amortization schedule — not supplied here. The result carries the
    folded ``status`` (ACTIVE while installments remain, SETTLED once the final one clears the balance)
    and the ``commit_sequence`` read-your-writes token.

    The installment is idempotent WITHOUT a caller key: the engine derives a stable, number-pinned
    idempotency key from the occurrence's own identity (``(loan, "pay_installment", number)``), so a
    repeat call for the SAME occurrence dedupes to ONE money leg and never double-collects (ADR-PC-036
    §Decision 1+3 / LCD-1; ADR-PC-029 slot 4, AMENDED). This tool therefore supplies NO key of its own
    (bd babelstone-6cpq.1) — UNLIKE ``constitute_deposit``, whose agent-channel command mints a per-call
    key.

    Requires ``deposits:write`` (ADR-IC-010 §P4 — the money-mover write scope; the loan tool reuses the
    existing write scope, see ``auth.TOOL_SCOPES``). Like ``mature_deposit``, the collection is
    irreversible, so under §P8 it is gated by real step-up SCA (Q-BE resolved, bd babelstone-ziu3.5): the
    ENGINE 422s the installment without FRESH gateway-attested SCA, this tool fires the URL-mode step-up
    elicitation, and RETRIES with the refreshed token. The installment settles on the bank's own signal
    (the AS signature the engine sees), never the agent's report — the §P8 invariant. A declined/cancelled
    step-up aborts the call with an ``McpError`` and collects nothing.
    """
    auth = _authorize(ctx, "pay_installment")
    outcome = await _settle_with_stepup_sca(
        ctx,
        "PAY_INSTALLMENT",
        lambda: engine().pay_installment(
            loan_id, collection_account_ref, client_id=auth.client_id
        ),
    )
    return PayInstallmentResult(
        loan_id=outcome["loan_id"],
        status=outcome["status"],
        commit_sequence=outcome["commit_sequence"],
    )


async def _settle_with_stepup_sca(
    ctx: Context,
    operation_code: str,
    settle: Callable[[], Awaitable[dict[str, object]]],
) -> dict[str, object]:
    """Run an irreversible money-mover behind the §P8 step-up-SCA gate (Q-BE resolved, bd babelstone-ziu3.5).

    ``settle`` is the engine call (``engine().mature(...)`` / ``engine().pay_interest(...)``). The flow:

      1. Call ``settle()``. If the engine settles (the caller already holds fresh gateway-attested SCA),
         return the position — no prompt, no over-prompting (this is why Q1 is engine-detected, not a
         proactive prompt-always).
      2. If the engine refuses with ``422 SCA_REQUIRED`` (``ScaRequiredError``), fire the URL-mode step-up
         elicitation: mint a fresh ``elicitation_id`` (a UUID, NEVER the deposit id — no business identity
         leaks), build the bank-controlled step-up URL for ``operation_code``, and ask the human to
         navigate. On decline/cancel, abort with a static PII-free ``McpError`` and DO NOT settle.
      3. On accept (the human re-authenticated at the bank and the agent now holds a REFRESHED token
         carrying a fresh ``acr``/``auth_time``), retry ``settle()`` ONCE. The retry's request carries the
         refreshed token → Kong re-attests fresh ``X-SCA-Acr``/``X-SCA-Auth-Time`` → the engine settles.

    THE GATE IS THE ENGINE, NOT THIS ELICITATION. The load-bearing §P8 invariant — "the irreversible action
    transitions on the bank's own out-of-band signal, not anything the agent reports back" — is satisfied
    because the engine's ``ScaPrecondition`` only settles on the AS-signed ``acr`` Kong validated. The
    elicitation here is just the human-facing step-up PROMPT; an agent that fabricated an accept without a
    genuinely refreshed token would still be 422'd on the retry (the second ``ScaRequiredError`` surfaces as
    an ``McpError``, never an unguarded settlement). So the gate cannot be bypassed from the client side.

    With ``ELICITATION_URL_MODE_ENABLED`` off, step 2 raises the ``McpError`` directly instead of
    eliciting — the engine still 422s, so the operation is still gated; the off posture is only for an
    environment that fronts the engine with a different step-up transport.
    """
    try:
        return await settle()
    except ScaRequiredError:
        pass

    # The engine demanded fresh SCA. With the step-up transport OFF there is no way to obtain a refreshed
    # token, so retrying would be pointless — surface the gate immediately rather than re-hit the 422. The
    # operation is still GATED (the engine refused); the off posture is only for an environment that fronts
    # the engine with a different step-up transport.
    if not ELICITATION_URL_MODE_ENABLED:
        raise aborted_error(
            "Step-up authentication is required and no step-up transport is enabled; the operation was "
            "not performed (ADR-IC-010 §P8 — the engine settles only on the bank's signed SCA claim)."
        )

    # Run the step-up prompt, then retry once with the refreshed token.
    elicitation_id = str(uuid.uuid4())  # a UUID, never the deposit id (no business identity leaks)
    try:
        await elicit_url_stepup(
            ctx, STEPUP_MSG, _stepup_url(operation_code, elicitation_id), elicitation_id
        )
    except ElicitationAborted:
        raise aborted_error(
            "User did not complete step-up authentication; the operation was not performed "
            "(ADR-IC-010 §P8 URL-mode step-up SCA)."
        ) from None

    try:
        return await settle()
    except ScaRequiredError:
        # The retry STILL lacks fresh SCA — the refreshed token never arrived. Surface a clean, PII-free
        # McpError; never settle on the agent's word (the bypass-resistance invariant).
        raise aborted_error(
            "Step-up authentication did not complete; the operation was not performed "
            "(ADR-IC-010 §P8 — the engine settles only on the bank's signed SCA claim)."
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
