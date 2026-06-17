"""Unit tests for ``build_tls_kwargs`` — the env→uvicorn-SSL translation (ADR-IC-010 §P5).

In plain English: the MCP server speaks HTTPS (and demands a client cert) only when the right TLS
env vars are set; otherwise it stays plain HTTP so a developer can hit it directly. These tests pin
that contract: no env ⇒ plain HTTP; cert+key ⇒ HTTPS; +CA ⇒ mutual TLS, defaulting to CERT_REQUIRED
(the fail-closed lock, bd babelstone-29ic), with an explicit override honoured.
"""

from __future__ import annotations

import ssl

import pytest

from babelstone_mcp.__main__ import build_tls_kwargs


def test_no_env_returns_empty() -> None:
    # No cert/key ⇒ no SSL kwargs ⇒ uvicorn serves plain HTTP (the dev-direct path).
    assert build_tls_kwargs({}) == {}


def test_partial_env_no_certfile_returns_empty() -> None:
    # A keyfile with no certfile is incomplete ⇒ still plain HTTP (never half-configured TLS).
    assert build_tls_kwargs({"MCP_TLS_KEYFILE": "/k"}) == {}


def test_partial_env_no_keyfile_returns_empty() -> None:
    assert build_tls_kwargs({"MCP_TLS_CERTFILE": "/c"}) == {}


def test_certfile_and_keyfile_without_ca_returns_server_tls_only() -> None:
    # Server TLS but no client-cert verification (no CA to verify against).
    r = build_tls_kwargs({"MCP_TLS_CERTFILE": "/c", "MCP_TLS_KEYFILE": "/k"})
    assert r["ssl_certfile"] == "/c"
    assert r["ssl_keyfile"] == "/k"
    assert "ssl_ca_certs" not in r
    assert "ssl_cert_reqs" not in r


def test_with_ca_defaults_to_cert_required() -> None:
    # A CA ⇒ mutual TLS, defaulting to CERT_REQUIRED (the 29ic fail-closed lock).
    r = build_tls_kwargs(
        {"MCP_TLS_CERTFILE": "/c", "MCP_TLS_KEYFILE": "/k", "MCP_TLS_CA_CERTS": "/ca"}
    )
    assert r["ssl_ca_certs"] == "/ca"
    assert r["ssl_cert_reqs"] == ssl.CERT_REQUIRED


def test_cert_reqs_override_to_optional() -> None:
    r = build_tls_kwargs(
        {
            "MCP_TLS_CERTFILE": "/c",
            "MCP_TLS_KEYFILE": "/k",
            "MCP_TLS_CA_CERTS": "/ca",
            "MCP_TLS_CERT_REQS": "1",
        }
    )
    assert r["ssl_cert_reqs"] == ssl.CERT_OPTIONAL


def test_cert_reqs_ignored_without_ca() -> None:
    # Without a CA there is nothing to verify a client cert against, so cert_reqs is not emitted.
    r = build_tls_kwargs(
        {"MCP_TLS_CERTFILE": "/c", "MCP_TLS_KEYFILE": "/k", "MCP_TLS_CERT_REQS": "2"}
    )
    assert "ssl_cert_reqs" not in r


# ── MCP_TLS_CERT_REQS guard (bd babelstone-ziu3.3 nit 3) ──────────────────────────────
# A bad MCP_TLS_CERT_REQS must fail with an actionable message naming the variable + legal
# values, not the bare ValueError that ``ssl.VerifyMode(int(raw))`` would raise.
_BASE_TLS = {"MCP_TLS_CERTFILE": "/c", "MCP_TLS_KEYFILE": "/k", "MCP_TLS_CA_CERTS": "/ca"}


def test_cert_reqs_non_numeric_raises_clear_error() -> None:
    with pytest.raises(ValueError, match=r"MCP_TLS_CERT_REQS must be an integer.*'foo'"):
        build_tls_kwargs({**_BASE_TLS, "MCP_TLS_CERT_REQS": "foo"})


def test_cert_reqs_out_of_range_raises_clear_error() -> None:
    with pytest.raises(ValueError, match=r"MCP_TLS_CERT_REQS=99 is not a valid ssl.VerifyMode"):
        build_tls_kwargs({**_BASE_TLS, "MCP_TLS_CERT_REQS": "99"})


def test_cert_reqs_none_is_accepted() -> None:
    # 0 (CERT_NONE) is a legal VerifyMode int and must round-trip without raising.
    r = build_tls_kwargs({**_BASE_TLS, "MCP_TLS_CERT_REQS": "0"})
    assert r["ssl_cert_reqs"] == ssl.CERT_NONE
