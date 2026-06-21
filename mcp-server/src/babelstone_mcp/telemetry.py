"""OpenTelemetry wiring for the MCP server (ADR-IC-007 Layer 1, bd babelstone-scd2.1).

In plain English: this turns on distributed tracing for the MCP server, the same way the .NET
services already have it. Every request an agent makes becomes a span, every call the server makes to
the engine or the orchestrator becomes a child span, and they are all exported to the OTel Collector
so a single deposit the agent drives shows up as one connected trace in Grafana/Tempo — the MCP
SERVER span → the engine's deposit.* spans → the engine's Npgsql query spans.

Formally:
- **Resource (OBS-1, ADR-IC-007 §P1).** Every Babelstone host stamps its tracer's resource with
  ``service.name``, ``service.namespace == "babelstone"``, and a ``deployment.environment``. This
  module reproduces that EXACT contract for the Python service — the same three keys/values the .NET
  ``BabelstoneResource`` stamps — so the MCP server groups under the one estate namespace. As in the
  .NET hosts, ``deployment.environment`` resolution **fails fast**: a server with no
  ``DEPLOYMENT_ENVIRONMENT`` / ``DOTNET_ENVIRONMENT`` / ``ASPNETCORE_ENVIRONMENT`` set refuses to wire
  tracing rather than mis-attribute traces to an assumed environment.
- **Export to the Collector, never a backend (§P1).** The OTLP/HTTP span exporter targets the OTel
  Collector (``OTEL_EXPORTER_OTLP_ENDPOINT``, default ``http://localhost:4318`` — the dev collector
  boundary). The standard ``OTEL_EXPORTER_OTLP_*`` env vars the OTLP exporter honours apply.
- **The babelstone.* attribute contract (§P2/§P4).** :data:`BabelstoneAttributes` mirrors the .NET
  ``BabelstoneAttributes`` versioned span-key contract. Every key is in the §P4 *operational* tier —
  structural identifiers only, **never** PII (no NIF, IBAN, name, e-mail, deposit id is structural):
  the same discipline the .NET ``OBS_NO_PII_ATTRS`` fitness function enforces. A customer reference,
  when one is ever spanned, rides :data:`BabelstoneAttributes.SUBJECT_PSEUDONYM` as a salted one-way
  hash, never the raw id.

When the OTel SDK is not installed (e.g. a minimal unit-test environment) :func:`configure_tracing`
is a safe no-op and :func:`instrument_asgi_app` / :func:`get_async_client` return the app/client
unchanged — tracing is additive, never load-bearing for correctness.
"""

from __future__ import annotations

import os
from typing import Any

# The shared OTel resource contract (ADR-IC-007 §P1) — the SAME keys/values the .NET
# BabelstoneResource stamps, so the MCP server is attributable to a service, the babelstone estate,
# and an environment.
SERVICE_NAMESPACE = "babelstone"
SERVICE_NAME = "babelstone-mcp-server"

# OTel semantic-convention resource keys (match the .NET BabelstoneResource constants).
SERVICE_NAME_KEY = "service.name"
SERVICE_NAMESPACE_KEY = "service.namespace"
DEPLOYMENT_ENVIRONMENT_KEY = "deployment.environment"

# The default OTLP/HTTP endpoint: the dev OTel Collector boundary (infra/compose.yaml exposes
# 4318 for OTLP/HTTP). Overridable via the standard OTEL_EXPORTER_OTLP_ENDPOINT env var.
_DEFAULT_OTLP_ENDPOINT = "http://localhost:4318"


class BabelstoneAttributes:
    """The versioned ``babelstone.*`` span-attribute key contract (ADR-IC-007 §P2/§P4).

    A 1:1 mirror of the .NET ``Babelstone.Telemetry.BabelstoneAttributes`` keys the MCP server is
    likely to set — a wire contract read by Grafana/Tempo queries, so **never rename a key**; add a
    new one and deprecate the old. Every key is operational-tier (structural identifiers only); no
    PII rides these keys (ADR-PC-004 §P2). The subset here is what an MCP span can legitimately carry
    today; the full catalogue lives on the .NET side.
    """

    #: The product code a deposit command targets. Structural identifier, not PII.
    PRODUCT_CODE = "babelstone.product_code"
    #: The aggregate's partition key (v1: the stream id). Structural identifier, not PII.
    PARTITION_KEY = "babelstone.partition_key"
    #: A salted, one-way PSEUDONYM for a customer a span references (ADR-IC-016 §8) — NEVER the raw
    #: client id (which is PII). Mirrors the .NET key; the MCP server never sets a raw client id on a
    #: span.
    SUBJECT_PSEUDONYM = "babelstone.subject_pseudonym"


def resolve_environment(env: dict[str, str] | None = None) -> str:
    """Resolve ``deployment.environment``, failing fast when unset (ADR-IC-007 §P1).

    Reads ``DEPLOYMENT_ENVIRONMENT`` first, then the .NET-host variables
    ``DOTNET_ENVIRONMENT`` / ``ASPNETCORE_ENVIRONMENT`` so a shared dev/compose environment that sets
    one of those covers the Python service too. **Raises** when none is set to a non-blank value —
    the same fail-fast stance ``BabelstoneResource.ResolveEnvironment`` takes on the .NET side: a host
    must not start tracing with traces mis-attributed to an assumed environment.
    """
    source = env if env is not None else dict(os.environ)
    for key in ("DEPLOYMENT_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"):
        value = (source.get(key) or "").strip()
        if value:
            return value
    raise RuntimeError(
        "deployment.environment is unresolved: set DEPLOYMENT_ENVIRONMENT (or DOTNET_ENVIRONMENT / "
        "ASPNETCORE_ENVIRONMENT). The MCP server fails fast rather than mis-attribute traces to a "
        "default environment (ADR-IC-007 §P1)."
    )


def build_resource_attributes(env: dict[str, str] | None = None) -> dict[str, str]:
    """The OTel resource attribute map for this service (OBS-1) — service, namespace, environment.

    Exposed (and pure) so a fitness test can assert the MCP server reproduces the SAME three-key
    resource the .NET hosts stamp, without standing up an exporter.
    """
    return {
        SERVICE_NAME_KEY: SERVICE_NAME,
        SERVICE_NAMESPACE_KEY: SERVICE_NAMESPACE,
        DEPLOYMENT_ENVIRONMENT_KEY: resolve_environment(env),
    }


def configure_tracing(env: dict[str, str] | None = None) -> bool:
    """Stand up the global ``TracerProvider`` (OTLP/HTTP → Collector) for the MCP server.

    Idempotent and best-effort: returns ``True`` when a Babelstone tracer provider is now active,
    ``False`` when the OTel SDK is not installed (tracing then stays a no-op). Honours the standard
    ``OTEL_EXPORTER_OTLP_ENDPOINT`` (default the dev Collector at ``:4318``). Fails fast on an
    unresolved ``deployment.environment`` (ADR-IC-007 §P1) — that is a deliberate refusal, not a
    silent skip.
    """
    try:
        from opentelemetry import trace
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
        from opentelemetry.sdk.resources import Resource
        from opentelemetry.sdk.trace import TracerProvider
        from opentelemetry.sdk.trace.export import BatchSpanProcessor
    except ImportError:
        # The OTel SDK is not installed (e.g. a minimal test env): tracing is additive, so degrade to
        # a no-op rather than fail the server.
        return False

    # Resolve the environment FIRST so a misconfigured deployment fails fast before we register a
    # provider (ADR-IC-007 §P1).
    resource = Resource.create(build_resource_attributes(env))

    # Do not double-register: if a Babelstone provider is already active (a previous call, or a test),
    # leave it in place.
    existing = trace.get_tracer_provider()
    if isinstance(existing, TracerProvider) and getattr(existing, "_babelstone_configured", False):
        return True

    endpoint = (os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT") or _DEFAULT_OTLP_ENDPOINT).rstrip("/")
    exporter = OTLPSpanExporter(endpoint=f"{endpoint}/v1/traces")
    provider = TracerProvider(resource=resource)
    provider.add_span_processor(BatchSpanProcessor(exporter))
    # Marker so a re-entrant call (or a test) recognises our provider and does not stack a second one.
    provider._babelstone_configured = True  # type: ignore[attr-defined]
    trace.set_tracer_provider(provider)
    return True


def instrument_asgi_app(app: Any) -> Any:
    """Wrap an ASGI app so every inbound MCP request becomes a SERVER span (best-effort).

    Returns the app unchanged when the ASGI instrumentation is not installed, so callers can wire this
    unconditionally. The SERVER span roots the request's trace and joins any inbound W3C
    ``traceparent`` — the same join the .NET ``AddAspNetCoreInstrumentation`` performs — so the
    engine/orchestrator CLIENT spans the tools emit nest under it.
    """
    try:
        from opentelemetry.instrumentation.asgi import OpenTelemetryMiddleware
    except ImportError:
        return app
    return OpenTelemetryMiddleware(app)


def instrument_httpx() -> bool:
    """Globally instrument httpx so engine/orchestrator calls become CLIENT spans (best-effort).

    Hooks httpx's request path so each call the ``EngineClient`` / ``OrchestratorClient`` makes is a
    CLIENT span that injects the W3C ``traceparent`` — which is what stitches the MCP trace to the
    engine's server span. Returns ``False`` (no-op) when the httpx instrumentation is not installed.
    Idempotent: instrumenting twice is harmless.
    """
    try:
        from opentelemetry.instrumentation.httpx import HTTPXClientInstrumentor
    except ImportError:
        return False
    HTTPXClientInstrumentor().instrument()
    return True
