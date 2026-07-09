"""Unit tests for ``build_client_ssl_context`` — the caller-side internal-mTLS context builder.

In plain English: the MCP server's httpx tool clients speak internal mTLS to the engine/orchestrator
ONLY when the internal-CA env is set; otherwise they dial plain HTTP so a developer can hit them
directly (demo-mcp.sh). These tests pin that contract (bd babelstone-zla1.12.10, ADR-IC-006 §P5 /
ADR-IC-016 plane (i)): no CA env ⇒ None (plain HTTP); a CA that points at a real internal CA ⇒ a
context that verifies against that CA and (with a client pair) presents the client cert; a
misconfigured (unreadable) CA path fails loud rather than silently degrading to no-verify.
"""

from __future__ import annotations

import ssl

import pytest

from babelstone_mcp.internal_mtls import build_client_ssl_context


def test_no_ca_env_returns_none() -> None:
    # No internal CA configured ⇒ None ⇒ the client dials plain HTTP (the dev-direct default).
    assert build_client_ssl_context({}) is None


def test_empty_ca_env_returns_none() -> None:
    # An empty string is "not configured" — the same plain-HTTP default (never a half-configured TLS).
    assert build_client_ssl_context({"BABELSTONE_INTERNAL_CA_CERTS": ""}) is None


def test_ca_set_builds_verifying_context(tmp_path) -> None:
    # A CA path pointing at a real cert PEM yields a CERT_REQUIRED, hostname-checking context that
    # trusts ONLY that CA (the pinned trust anchor). Uses a self-signed cert as a stand-in CA PEM.
    ca_pem = _self_signed_pem(tmp_path)
    context = build_client_ssl_context({"BABELSTONE_INTERNAL_CA_CERTS": str(ca_pem)})
    assert context is not None
    assert context.verify_mode == ssl.CERT_REQUIRED
    assert context.check_hostname is True


def test_ca_and_client_pair_loads_client_cert(tmp_path) -> None:
    # With the client cert+key pair set, the context also presents the client cert (mutual TLS). We
    # cannot easily assert the loaded chain from the public API, but load_cert_chain must not raise on
    # a valid PEM cert+key pair — a broken pair would.
    cert_pem, key_pem = _self_signed_pem(tmp_path, with_key=True)
    context = build_client_ssl_context(
        {
            "BABELSTONE_INTERNAL_CA_CERTS": str(cert_pem),
            "BABELSTONE_INTERNAL_CLIENT_CERT": str(cert_pem),
            "BABELSTONE_INTERNAL_CLIENT_KEY": str(key_pem),
        }
    )
    assert context is not None


def test_unreadable_ca_fails_loud(tmp_path) -> None:
    # A CA path that does not exist must raise (fail-loud), never return a no-verify context — a
    # mis-mounted Secret is a hard misconfiguration, not a silent downgrade.
    missing = tmp_path / "nope.crt"
    with pytest.raises((FileNotFoundError, ssl.SSLError, OSError)):
        build_client_ssl_context({"BABELSTONE_INTERNAL_CA_CERTS": str(missing)})


def _self_signed_pem(tmp_path, with_key: bool = False):
    """Write a throwaway self-signed cert (and optionally its key) to ``tmp_path`` and return the
    path(s). Uses the ``cryptography`` lib when available, else skips the test — the context builder's
    behaviour under a real CA is what these certs exercise."""
    crypto = pytest.importorskip("cryptography")
    from datetime import datetime, timedelta, timezone

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = issuer = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "test-internal-ca")])
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.now(timezone.utc) - timedelta(days=1))
        .not_valid_after(datetime.now(timezone.utc) + timedelta(days=1))
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )
    cert_pem = tmp_path / "ca.crt"
    cert_pem.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    if not with_key:
        return cert_pem
    key_pem = tmp_path / "ca.key"
    key_pem.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.TraditionalOpenSSL,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    return cert_pem, key_pem
