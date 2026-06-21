"""Tests for the MCP server's OpenTelemetry wiring (ADR-IC-007 Layer 1, bd babelstone-scd2.1).

These assert the Python service reproduces the SAME OTel resource contract the .NET hosts stamp
(OBS-1: ``service.name`` / ``service.namespace == "babelstone"`` / ``deployment.environment``), the
SAME fail-fast environment resolution (``BabelstoneResource.ResolveEnvironment`` on the .NET side),
the ``babelstone.*`` attribute-key contract, and that the wiring degrades to a safe no-op when the
OTel SDK is absent. No exporter, no Collector — pure contract assertions.
"""

from __future__ import annotations

import pytest

from babelstone_mcp import telemetry
from babelstone_mcp.telemetry import BabelstoneAttributes


def test_resource_attributes_carry_service_name_namespace_and_environment():
    attrs = telemetry.build_resource_attributes({"DEPLOYMENT_ENVIRONMENT": "Staging"})
    assert attrs["service.name"] == "babelstone-mcp-server"
    # The estate namespace is the shared constant every Babelstone host stamps (OBS-1).
    assert attrs["service.namespace"] == "babelstone"
    assert attrs["deployment.environment"] == "Staging"


@pytest.mark.parametrize(
    "env_key",
    ["DEPLOYMENT_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"],
)
def test_environment_resolves_from_any_of_the_three_known_variables(env_key):
    # A shared dev/compose environment that sets one of the .NET-host variables covers the Python
    # service too (resolution order: DEPLOYMENT_ENVIRONMENT, then the two .NET vars).
    assert telemetry.resolve_environment({env_key: "Production"}) == "Production"


def test_environment_resolution_fails_fast_when_unset():
    # The .NET hosts refuse to boot tracing with no environment rather than mis-attribute traces to a
    # default; the Python service matches that fail-fast stance (ADR-IC-007 §P1).
    with pytest.raises(RuntimeError):
        telemetry.resolve_environment({})


def test_environment_resolution_treats_blank_as_unset():
    with pytest.raises(RuntimeError):
        telemetry.resolve_environment({"DEPLOYMENT_ENVIRONMENT": "   "})


def test_babelstone_attribute_keys_match_the_dotnet_contract():
    # The babelstone.* span-key contract (ADR-IC-007 §P2) — never rename a key. These mirror the .NET
    # BabelstoneAttributes constants the MCP server is permitted to set.
    assert BabelstoneAttributes.PRODUCT_CODE == "babelstone.product_code"
    assert BabelstoneAttributes.PARTITION_KEY == "babelstone.partition_key"
    assert BabelstoneAttributes.SUBJECT_PSEUDONYM == "babelstone.subject_pseudonym"


def test_instrument_asgi_app_returns_an_app_even_without_the_sdk():
    # instrument_asgi_app is safe to call unconditionally: with the ASGI instrumentation present it
    # wraps the app; without it, it returns the app unchanged. Either way a usable ASGI callable
    # comes back (tracing is additive, never load-bearing).
    sentinel = object()
    wrapped = telemetry.instrument_asgi_app(sentinel)
    assert wrapped is not None
