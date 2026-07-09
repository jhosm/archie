"""Caller-side internal mTLS for the MCP server's outbound engine/orchestrator hops.

In plain English: the MCP server calls the engine and the orchestrator over HTTP to run its tools.
Once those two hosts are flipped to HTTPS-with-a-REQUIRED-client-cert (the gated staging patch
``overlays/staging/internal-mtls.patch.yaml``), a caller that presents no client cert — or one they
cannot chain to the shared internal CA — is rejected at the TLS handshake. This module builds the
``ssl.SSLContext`` the httpx clients dial with: it PINS the server cert to the internal CA (the
container already mounts ``/certs/ca.crt``) and PRESENTS the MCP server's own client cert.

It is the httpx-client twin of ``__main__.build_tls_kwargs`` (which is the SERVER side — uvicorn
verifying Kong's client cert). Same one-CA-underwrites-everything constraint (ADR-IC-006 §P5 / ADR-IC-016
plane (i), bd babelstone-zla1.12.10).

It is OFF by default and gated purely on env: with no ``BABELSTONE_INTERNAL_CA_CERTS`` set, the clients
dial plain HTTP exactly as before (the dev-direct path — demo-mcp.sh), and every test that injects its
own ``httpx.AsyncClient`` is untouched (the context is only built on the default-construction path).

Env:
- ``BABELSTONE_INTERNAL_CA_CERTS`` — the internal CA PEM the engine/orchestrator server cert must chain
  to (the mounted ``/certs/ca.crt``). Setting it ENABLES caller-side mTLS on the outbound hops.
- ``BABELSTONE_INTERNAL_CLIENT_CERT`` / ``BABELSTONE_INTERNAL_CLIENT_KEY`` — the MCP server's own PEM
  client cert + key (the cert-manager Secret's ``tls.crt`` / ``tls.key``), presented on the handshake.
  When only the CA is set, the context pins the server but presents nothing.
"""

from __future__ import annotations

import ssl
from typing import Mapping

CA_CERTS_ENV = "BABELSTONE_INTERNAL_CA_CERTS"
CLIENT_CERT_ENV = "BABELSTONE_INTERNAL_CLIENT_CERT"
CLIENT_KEY_ENV = "BABELSTONE_INTERNAL_CLIENT_KEY"


def build_client_ssl_context(env: Mapping[str, str]) -> ssl.SSLContext | None:
    """Build the outbound-hop SSL context, or ``None`` when internal mTLS is not configured.

    Returns ``None`` unless ``BABELSTONE_INTERNAL_CA_CERTS`` is set — the caller then dials plain HTTP
    (the default httpx behaviour). When the CA is set, the context verifies the peer's server cert
    against that CA ONLY (``load_verify_locations`` with ``verify_mode=CERT_REQUIRED`` and hostname
    checking on — the container's system trust store is not consulted), and, when the client cert+key
    pair is also set, presents the MCP server's own client cert for the peer's ``RequireCertificate``
    check (mutual TLS). Fail-loud on an unreadable CA / cert file (a mis-mounted Secret must not
    silently degrade to no-verify).
    """
    ca_certs = env.get(CA_CERTS_ENV)
    if not ca_certs:
        return None

    # Purpose SERVER_AUTH: we are the CLIENT verifying the engine/orchestrator SERVER cert. This
    # starts with the system defaults (verify_mode=CERT_REQUIRED, check_hostname=True) and we then
    # REPLACE the trust store with our internal CA alone — the ambient roots are dropped, so only a
    # cert chaining to the internal CA is accepted.
    context = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=ca_certs)

    client_cert = env.get(CLIENT_CERT_ENV)
    client_key = env.get(CLIENT_KEY_ENV)
    if client_cert and client_key:
        context.load_cert_chain(certfile=client_cert, keyfile=client_key)

    return context
