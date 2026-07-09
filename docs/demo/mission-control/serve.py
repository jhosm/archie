#!/usr/bin/env python3
"""
Mission Control dev server — serves the UI and reverse-proxies the backend APIs.

Why this exists: the babelstone services have no CORS, so a browser page on a different
origin can't call them directly. This tiny server puts the UI and the backends behind ONE
origin (http://localhost:9000), so the browser sees same-origin — no CORS, no preflight,
live mode "just works". It is stdlib-only except for the OPTIONAL read-only Postgres routes
below (psycopg, lazily imported — every other route works with no third-party package). It
proxies these backends:

  • /v1/*       → the ENGINE (LIVE·engine mode): the engine's own command/query surface
                  (POST /v1/deposits, GET /v1/deposits/{id}, …). Engine-DIRECT, ADR-PC-029.

  • /api/v1/*   → the ORCHESTRATOR edge (LIVE·saga mode): the constitution-saga front door
                  (POST /api/v1/deposits/constitute → 202 + process_id + SSE stream,
                  ADR-IC-006 §P4 / Document 05). This server ATTESTS the X-Client-Id the
                  orchestrator's edge authz expects, mirroring Kong's algorithm: in oidc mode
                  it sets X-Client-Id from the OIDC-validated session `sub` (the same claim
                  Kong would propagate from a validated token — real attestation, not a forged
                  id); in dev mode it uses the static DEMO_CLIENT_ID. The browser's EventSource
                  cannot set headers, so injecting here is what lets the SSE stream's per-process
                  ownership check (which binds to the SAME client id as the start) pass. This is
                  an acknowledged ADR-IC-006 §P2/§P4 exception for the demo BFF (it does NOT
                  front the path with Kong's rate-limit/validation/SCA — 2026-07-08 amendment);
                  full Kong-fronted conformance is epic bd babelstone-zla1.10.9.

  • /agent/*    → the real-Claude AGENT host (LIVE·agent mode, bd babelstone-f0ic.6): POST
                  /agent/stream {"instruction": "…"} → a text/event-stream of the model's
                  narration + REAL MCP tool calls/results. The agent loop and the Anthropic
                  API key live in that host, server-side — never here, never the browser. The
                  /stream suffix routes it through the same long-lived SSE relay below.

  • /pandaproxy/* → Redpanda's HTTP Proxy (pandaproxy / Kafka REST, bd babelstone-f0ic.15.4):
                  the Topic·Avro lens reads REAL topic records over HTTP (the consumer-group
                  dance), so the browser needs no Kafka client. Strip the /pandaproxy prefix so
                  the upstream sees its own path. Records are read in BINARY format: only the
                  CloudEvents headers + the Avro wire schema-id are surfaced; the payload body is
                  NEVER decoded here, so no PII can leak (carry references only).

  • /sr/*       → the Confluent-API Schema Registry (bd babelstone-f0ic.15.4): the Topic·Avro
                  lens resolves a record's wire schema-id to its subject/version for the
                  "catalog ✓ · Avro · schema #id" badge. Strip the /sr prefix. Read-only GETs of
                  structural schema metadata — no PII.

  • /loki/*     → Grafana Loki's query API (Logs lens, bd babelstone-f0ic.15.7): the Logs lens reads
                  REAL structured logs at /loki/api/v1/query_range, correlated by the trace id the UI
                  already captures per write (Tempo↔Loki share the OTel trace_id/span_id stamp). Loki's
                  native API already lives under /loki/…, so the path is forwarded UNCHANGED. Defence in
                  depth on PII: the JSON response is passed through a BFF-side STRUCTURAL-FIELD ALLOWLIST
                  before it reaches the browser — only structural label/field names survive, and any
                  field whose NAME matches a PII substring is dropped — so we do NOT rely solely on the
                  emit-time OTel guard (the collector has no redaction yet — Epic K). References only.

  • /pg/*       → READ-ONLY Postgres (Inspector lenses, bd babelstone-f0ic.15.1): a guarded,
                  dev-only, 127.0.0.1-only window onto the engine + orchestrator databases.
                  Every query selects from a STRUCTURAL-column allowlist that forbids the
                  PII-bearing columns (payload / *detail* / BYTEA/ciphertext) — references only,
                  never PII. A non-local DSN is refused; the connection is opened read-only.
                  Requires psycopg (lazily imported) and the MC_*_DSN env vars.

DEMO mode needs none of this — index.html is fully self-contained. You only need this
server for LIVE·engine (start the engine, scripts/demo-mcp.sh) or LIVE·saga (start the
orchestrator + ACL stub, scripts/demo-saga.sh).

Authentication (bd babelstone-zla1.10.8.1 / .2). Two modes, selected by MC_AUTH_MODE:

  • dev  (default) — NO login gate. Byte-for-byte the behaviour above: every route is
                     served open. This is the laptop-dev posture. A FAIL-SAFE refuses this
                     mode on a PUBLIC (non-loopback) bind unless MC_ALLOW_UNAUTHENTICATED=1
                     is set — the insecure "public + ungated" state must be opted into, not
                     reached by accident (the inverse of the /pg/* auto-off default).

  • oidc          — an interactive OpenID-Connect login gate stands in FRONT of every route
                     (the UI, and every proxied backend prefix — ADR-IC-021 Boundary-1
                     owned-channel login at the BFF). It is PROVIDER-AGNOSTIC: on startup it
                     reads {OIDC_ISSUER}/.well-known/openid-configuration (RFC 8414) and uses
                     the discovered authorization/token/jwks/end-session endpoints — no IdP
                     path is hardcoded. An unauthenticated navigation is 302-redirected into
                     the Authorization-Code + PKCE (S256) flow; /callback exchanges the code
                     on a direct TLS backchannel to the discovered token_endpoint, fully
                     validates the returned id_token (iss / aud / exp / nonce), and sets an
                     HMAC-signed HttpOnly session cookie. It is FAIL-CLOSED: if discovery is
                     unreachable or required config is missing/invalid, serve.py REFUSES to
                     start — it never silently degrades to ungated (the deliberate inversion
                     of the AGENT_URL degrade-open behaviour).

Usage:
    python3 docs/demo/mission-control/serve.py
    # open http://localhost:9000 and flip the Mode toggle to LIVE·engine or LIVE·saga

Options (env vars):
    MC_PORT           port to serve the UI on                  (default 9000)
    MC_BIND           interface to bind                        (default 127.0.0.1;
                      the container image sets 0.0.0.0 so the kube Service/probes
                      can reach it via the pod IP)
    MC_AUTH_MODE      'dev' (ungated) or 'oidc' (login gate)   (default dev)
    MC_ALLOW_UNAUTHENTICATED  accept a PUBLIC + dev (ungated)  (default off — a public,
                      bind explicitly (fail-safe override)     ungated bind is REFUSED)
    OIDC_ISSUER       OIDC issuer base URL (discovery is at     (oidc mode; required)
                      {issuer}/.well-known/openid-configuration)
    OIDC_CLIENT_ID    OAuth client id registered at the IdP    (oidc mode; required)
    OIDC_CLIENT_SECRET  the confidential-client secret          (oidc mode; injected at
                      (client_secret_post) — never committed    deploy, never committed)
    OIDC_SCOPES       space-separated scopes requested          (default 'openid profile email')
    OIDC_REDIRECT_URL the exact /callback redirect_uri          (else derived from
                                                                 MC_PUBLIC_BASE_URL + /callback)
    MC_PUBLIC_BASE_URL  public origin used to derive the        (oidc mode; required unless
                      redirect_uri when OIDC_REDIRECT_URL unset  OIDC_REDIRECT_URL is set)
    MC_SESSION_SIGNING_KEY  HMAC key signing the session cookie (oidc mode; required)
    MC_SESSION_TTL    session lifetime in seconds               (default 3600)
    ENGINE_URL        base URL of the engine (LIVE·engine)     (default http://localhost:8080)
    ORCHESTRATOR_URL  base URL of the orchestrator (LIVE·saga) (default http://localhost:8090)
    TEMPO_URL         base URL of Grafana Tempo's query API     (default http://localhost:3200)
                      (LIVE·engine Telemetry tab → /tempo/api/traces/{id})
    DEMO_CLIENT_ID    dev-mode X-Client-Id on /api/v1/* (an       (default CLI-DEMO-0001)
                      OPAQUE reference, never PII); in oidc mode
                      X-Client-Id is attested from the session sub
    AGENT_URL         base URL of the real-Claude agent host    (default http://localhost:8091)
                      (LIVE·agent mode, /agent/* → POST /agent/stream)
    PANDAPROXY_URL    base URL of Redpanda's HTTP Proxy         (default http://localhost:18082)
                      (Topic·Avro lens, /pandaproxy/* → Kafka REST)
    SCHEMA_REGISTRY_URL  base URL of the Schema Registry        (default http://localhost:18081)
                      (Topic·Avro lens schema badge, /sr/*)
    LOKI_URL          base URL of Grafana Loki's query API      (default http://localhost:3100)
                      (Logs lens, /loki/* → /loki/api/v1/query_range by trace id)
    MC_ENGINE_DSN     read-only DSN for the engine DB           (default postgresql://babelstone:
                      babelstone@127.0.0.1:5432/babelstone) — /pg/* Inspector lenses
    MC_ORCH_DSN       read-only DSN for the orchestrator DB     (default …/babelstone_orchestrator)
    MC_PG_ENABLE      force-enable /pg/* when NOT bound to       (default off; auto-on when MC_BIND
                      loopback (dev only — keep off in a pod)   is 127.0.0.1/::1/localhost)
"""
import os
import sys
import ssl
import http.server
import socketserver
import urllib.request
import urllib.error
import urllib.parse
import json
import base64
import hashlib
import hmac
import secrets
import time
from http.cookies import SimpleCookie

PORT = int(os.environ.get("MC_PORT", "9000"))
# Bind interface. Default 127.0.0.1 keeps `python3 serve.py` on a laptop localhost-only
# (no LAN exposure); the container image overrides this to 0.0.0.0 (ENV in the Dockerfile)
# so the kube Service and readiness/liveness probes can reach it via the pod IP.
MC_BIND = os.environ.get("MC_BIND", "127.0.0.1")
ENGINE_URL = os.environ.get("ENGINE_URL", "http://localhost:8080").rstrip("/")
ORCHESTRATOR_URL = os.environ.get("ORCHESTRATOR_URL", "http://localhost:8090").rstrip("/")
TEMPO_URL = os.environ.get("TEMPO_URL", "http://localhost:3200").rstrip("/")
AGENT_URL = os.environ.get("AGENT_URL", "http://localhost:8091").rstrip("/")
PANDAPROXY_URL = os.environ.get("PANDAPROXY_URL", "http://localhost:18082").rstrip("/")
SCHEMA_REGISTRY_URL = os.environ.get("SCHEMA_REGISTRY_URL", "http://localhost:18081").rstrip("/")
LOKI_URL = os.environ.get("LOKI_URL", "http://localhost:3100").rstrip("/")
# The local OCI registry that distributes the signed regulatory packs (ADR-PC-007; host port 5001
# in infra/compose.yaml). The /registry/* arm is GET-ONLY: the provenance strip reads Distribution
# v2 manifest/referrer metadata (a "signature referrer exists" badge) — it never pushes.
REGISTRY_URL = os.environ.get("REGISTRY_URL", "http://localhost:5001").rstrip("/")
# Prometheus inside the grafana-lgtm appliance (host port 9090, infra/compose.yaml — the P-PORTS
# prereq, bd babelstone-f0ic.15.2). The /prom/* arm is GET-ONLY (Metrics lens, bd f0ic.15.6): the
# UI runs instant/range queries over the engine's SLI series. PII is stripped at EMIT by the
# metric View allowlist (AddBabelstonePiiGuard — only admitted structural dimensions survive), so
# no BFF-side response filter is needed here: what Prometheus stores is already references-only.
PROM_URL = os.environ.get("PROM_URL", "http://localhost:9090").rstrip("/")
DEMO_CLIENT_ID = os.environ.get("DEMO_CLIENT_ID", "CLI-DEMO-0001")
ROOT = os.path.dirname(os.path.abspath(__file__))

# ── Caller-side internal mTLS on the engine + orchestrator proxy hops ─────────────────────────
# (bd babelstone-zla1.12.10; ADR-IC-006 §P5 Boundary 2 / ADR-IC-016 plane (i)). This BFF proxies the
# browser's /v1/* → the engine and /api/v1/* → the orchestrator. Once those two hosts are flipped to
# HTTPS-with-a-REQUIRED-client-cert (the gated overlays/staging/internal-mtls.patch.yaml), the proxy
# must present a client cert signed by the shared internal CA and pin the server cert to that same CA
# on THOSE two hops — or the handshake is rejected. It is OFF unless MC_INTERNAL_CA_CERTS is set (the
# ENGINE_URL/ORCHESTRATOR_URL env already carry https://…:8080 in staging), so the laptop dev default
# (plain http upstreams) is byte-for-byte unchanged and only the two internal hops use the context —
# every OTHER arm (Tempo/Loki/Prometheus/pandaproxy/registry) keeps urllib's default TLS handling.
#   MC_INTERNAL_CA_CERTS   — the internal CA PEM the engine/orchestrator server cert must chain to.
#   MC_INTERNAL_CLIENT_CERT / MC_INTERNAL_CLIENT_KEY — the proxy's own PEM client cert + key
#                            (the cert-manager Secret's tls.crt / tls.key), presented on the handshake.
MC_INTERNAL_CA_CERTS = os.environ.get("MC_INTERNAL_CA_CERTS", "").strip()
MC_INTERNAL_CLIENT_CERT = os.environ.get("MC_INTERNAL_CLIENT_CERT", "").strip()
MC_INTERNAL_CLIENT_KEY = os.environ.get("MC_INTERNAL_CLIENT_KEY", "").strip()


def _build_internal_mtls_context():
    """The SSL context for the engine + orchestrator proxy hops, or None when not configured.

    Returns None unless MC_INTERNAL_CA_CERTS is set (the plain-http laptop default). When set, the
    context verifies the peer's SERVER cert against that CA ONLY (the system trust store is not
    consulted) and, when the client cert+key pair is set, presents the proxy's own client cert for the
    peer's RequireCertificate check (mutual TLS). Fail-loud on an unreadable file — a mis-mounted Secret
    must not silently degrade to no-verify."""
    if not MC_INTERNAL_CA_CERTS:
        return None
    context = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=MC_INTERNAL_CA_CERTS)
    if MC_INTERNAL_CLIENT_CERT and MC_INTERNAL_CLIENT_KEY:
        context.load_cert_chain(certfile=MC_INTERNAL_CLIENT_CERT, keyfile=MC_INTERNAL_CLIENT_KEY)
    return context


# Built once at import (the cert material does not change over the process lifetime). The two internal
# hops pass it to urlopen; every other hop passes None (urllib's default handling).
_INTERNAL_MTLS_CONTEXT = _build_internal_mtls_context()
# The exact base URLs that ride the internal-mTLS context — the two in-cluster hops this BFF secures.
_INTERNAL_MTLS_BASE_URLS = frozenset({ENGINE_URL, ORCHESTRATOR_URL})

# Sentinel returned by _route() when the /api/v1/* arm is asked to attest a caller id in oidc
# mode but no authenticated session/sub is resolvable. It is DISTINCT from None (static file) and
# from a route tuple, so the verb handlers can turn it into a 403 rather than forge the demo id.
_REFUSE = object()

# User-Agent for serve.py's server-side OIDC backchannel calls (discovery + code→token exchange).
# Python urllib defaults to "Python-urllib/<ver>", which the WAF/CDN in front of the issuer
# (Cloudflare, on auth.babelstone.dev) 403-blocks as a bot signature — that made the fail-closed gate
# crash-loop on discovery in staging. A descriptive, non-bot UA passes cleanly; the browser-facing
# authorize redirect is unaffected (that request is the user's browser, not this process).
# See bd babelstone-zla1.10.12.
_OIDC_USER_AGENT = "babelstone-mission-control/1.0"

# ── Read-only Postgres window for the Inspector lenses (bd babelstone-f0ic.15.1) ─────────────
# These DSNs point at the engine + orchestrator databases. They are READ-ONLY by construction
# (the connection is opened read-only and only allowlisted structural columns are ever selected)
# and DEV-ONLY: the routes refuse a non-local DSN and are disabled unless serve.py is bound to
# loopback (override with MC_PG_ENABLE for a deliberate local non-loopback bind). No PII ever
# leaves these tables — the allowlist forbids the payload / *detail* / BYTEA columns that carry it.
ENGINE_DSN = os.environ.get("MC_ENGINE_DSN", "postgresql://babelstone:babelstone@127.0.0.1:5432/babelstone")
ORCH_DSN = os.environ.get("MC_ORCH_DSN", "postgresql://babelstone:babelstone@127.0.0.1:5432/babelstone_orchestrator")
_LOOPBACK = {"127.0.0.1", "::1", "localhost", "0.0.0.0"}


def _is_loopback(host):
    return (host or "").strip() in _LOOPBACK


# /pg/* is on only when serve.py is bound to loopback (the laptop dev default) — or explicitly
# forced with MC_PG_ENABLE. In a pod (MC_BIND=0.0.0.0) it stays OFF unless forced, so the
# read-only window is never reachable off-box by accident.
PG_ENABLE = (os.environ.get("MC_PG_ENABLE", "").lower() in ("1", "true", "yes")) or _is_loopback(MC_BIND)

# Extra DSN hosts the /pg/* window may reach BEYOND loopback — an EXPLICIT operator opt-in for the
# in-cluster deployment (staging sets MC_PG_ALLOW_HOSTS=postgres). Empty by default, so the laptop
# posture stays strictly 127.0.0.1-only. The compensating controls that make a named in-cluster host
# safe are: a dedicated read-only DB role (SELECT-only, no DML/DDL), the mission-control→postgres
# NetworkPolicy, the OIDC gate in front of every route, and the SELECT-only allowlist + PII column
# firewall below (bd babelstone-zla1.17.3).
PG_ALLOW_HOSTS = {h.strip() for h in os.environ.get("MC_PG_ALLOW_HOSTS", "").split(",") if h.strip()}

# ── Mission Control authentication (bd babelstone-zla1.10.8.1 / .2) ───────────────────────────
# MC_AUTH_MODE selects the front-door posture: 'dev' (no gate — the historical behaviour) or
# 'oidc' (an interactive OpenID-Connect login in front of EVERY route). The whole auth surface is
# env-driven with defaults, matching the config idiom above. NB the OIDC_* values are only
# CONSULTED in oidc mode — in dev mode they are read but ignored, so a dev laptop needs none of them.
MC_AUTH_MODE = os.environ.get("MC_AUTH_MODE", "dev").strip().lower()

# The fail-safe override (bd babelstone-zla1.10.8.2). This INVERTS the /pg/* idiom above: there the
# safe default (window off) is automatic and the operator opts IN to the dev convenience; here the
# INSECURE state (a public bind with no auth) is the thing that must be opted into. A non-loopback
# bind in dev mode is REFUSED at startup unless this is set (see _preflight below).
MC_ALLOW_UNAUTHENTICATED = os.environ.get("MC_ALLOW_UNAUTHENTICATED", "").lower() in ("1", "true", "yes")

# OIDC gate configuration (oidc mode only). No IdP-specific endpoint path is hardcoded — the
# authorization/token/jwks/end-session endpoints are DISCOVERED from OIDC_ISSUER at startup
# (RFC 8414 / OIDC Discovery). The client_secret is injected at deploy (never committed); a public
# client that relies on PKCE alone may leave it empty.
OIDC_ISSUER = os.environ.get("OIDC_ISSUER", "").rstrip("/")
OIDC_CLIENT_ID = os.environ.get("OIDC_CLIENT_ID", "")
OIDC_CLIENT_SECRET = os.environ.get("OIDC_CLIENT_SECRET", "")
OIDC_SCOPES = os.environ.get("OIDC_SCOPES", "openid profile email")
OIDC_REDIRECT_URL = os.environ.get("OIDC_REDIRECT_URL", "").strip()
MC_PUBLIC_BASE_URL = os.environ.get("MC_PUBLIC_BASE_URL", "").rstrip("/")
MC_SESSION_SIGNING_KEY = os.environ.get("MC_SESSION_SIGNING_KEY", "")
try:
    MC_SESSION_TTL = int(os.environ.get("MC_SESSION_TTL", "3600"))
except ValueError:
    MC_SESSION_TTL = 3600

# The live OIDC gate, built by _preflight() in oidc mode (fetches discovery, validates config).
# It STAYS None in dev mode, and the request handler treats "AUTH is None" as "no gate" — so dev
# mode is byte-for-byte the pre-auth behaviour (no code path changes when AUTH is None).
AUTH = None


def _is_public_bind(host):
    """True when MC_BIND exposes the server OFF-box (any non-loopback interface). 0.0.0.0 and ::
    mean 'all interfaces' → PUBLIC; only a genuine loopback address (127.0.0.0/8, ::1, localhost,
    or the empty default) is private. Deliberately NOT reusing _is_loopback(): that set treats
    0.0.0.0 as loopback for the /pg/* DSN-guarded feature, but for the auth fail-safe 0.0.0.0 is
    exactly the public bind we must catch."""
    h = (host or "").strip().lower()
    if h in ("", "localhost", "::1", "::ffff:127.0.0.1"):
        return False
    if h.startswith("127."):
        return False
    return True


# headers we must not blindly copy when relaying
_HOP_BY_HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailers", "transfer-encoding", "upgrade", "content-length", "host"}

# ── PII firewall for the read-only Postgres window ───────────────────────────────────────────
# Any column whose name contains one of these substrings is REFUSED, even if a future edit
# mistakenly adds it to an allowlist below. These are the columns that carry ciphertext PII or a
# free-form detail blob (events.payload / outbox.payload / saga_outbox.payload are BYTEA; a
# read_model.*.detail column is a free-form blob) — references only ever cross this boundary
# (no-PII-on-the-bus / the OpenBao boundary stays in the engine).
_PG_FORBIDDEN_SUBSTR = ("payload", "detail", "ciphertext", "cipher", "secret", "nif", "iban")

# Per-(db, table) allowlist of STRUCTURAL columns the Inspector lenses may read. Every column
# named here is a structural id, a type name, a status, a sequence, a state label, or a DB-clock
# stamp — drawn directly from the migration column contracts (engine 0001/0012/0015/0016;
# orchestrator 0001/0002). The BYTEA payload columns are deliberately ABSENT.
_PG_ALLOWLIST = {
    ("engine", "events"): {
        "event_id", "stream_id", "sequence_number", "event_type", "event_schema_version",
        "family", "partition_key", "pack_version", "schema_version", "valid_time",
        "transaction_time", "causation_id", "correlation_id",
        # NB: payload_schema_id is deliberately omitted — the PII firewall refuses any column whose
        # name contains "payload", so it could never be selected here. The outbox's schema_id covers
        # the schema-badge need without tripping the firewall.
    },
    ("engine", "outbox"): {
        "event_id", "aggregate_type", "aggregate_id", "sequence_number", "event_type",
        "schema_id", "status", "created_at", "published_at",
    },
    ("engine", "inbox"): {"message_id", "source_topic", "processed_at", "result_summary"},
    # The durable pack-version registry (ADR-PC-007 §P3, migration 0006): resolves a pinned
    # pack_version string to its immutable OCI coordinates. Digests / refs / version strings are
    # structural facts; registered_by (an operator identity) is deliberately ABSENT.
    ("engine", "pack_versions"): {
        "pack_id", "pack_version", "oci_ref", "image_digest", "signature_digest", "registered_at",
    },
    ("engine", "command_dedup"): {"command_id", "stream_id", "commit_sequence", "created_at"},
    ("orchestrator", "saga_state"): {
        "process_id", "saga_type", "state", "version", "correlation_id", "created_at", "updated_at",
        # The client-facing PROC-… handle (migration 0005): the key the UI holds, resolved here to
        # the internal UUID for the transition/leg reads. An opaque reference, never PII.
        "public_process_id",
    },
    ("orchestrator", "saga_transition"): {
        "id", "process_id", "from_state", "to_state", "event_type", "message_id", "note", "occurred_at",
    },
    ("orchestrator", "saga_outbox"): {
        "seq", "message_id", "process_id", "command_type", "causation_id", "correlation_id",
        "status", "created_at", "published_at",
        # Outbound W3C Trace Context (migration 0003: "opaque 00-<trace-id>-<span-id>-<flags>;
        # operational, NOT PII") — the saga-leg → Tempo deep-link key (bd babelstone-f0ic.15.9).
        "traceparent",
    },
    ("orchestrator", "inbox"): {"message_id", "source_topic", "processed_at", "result_summary"},
}

_PG_DSN = {"engine": ENGINE_DSN, "orchestrator": ORCH_DSN}

# ── PII firewall for the Logs lens (Grafana Loki proxy, bd babelstone-f0ic.15.7) ─────────────
# Loki returns each matching stream with a label set plus its raw log lines. The emit-time OTel
# guard is the FIRST line of defence, but the collector has no redaction yet (Epic K), so the BFF
# ALSO enforces a structural-field allowlist here (belt-and-suspenders, mirroring the /pg/* window):
# before a Loki response reaches the browser, every stream-label key AND — when a log line is a JSON
# object — every field key is checked. A key survives only if it is a known STRUCTURAL name (an id, a
# level/severity, a logger/service name, a trace/span id, a status, a sequence, a schema id, or a
# clock stamp); anything else is dropped, and any key whose name contains a PII substring is refused
# outright even if a future edit adds it to the allowlist. Values are never inspected for PII — the
# discipline is on field NAMES (structural references only ever cross this boundary); a free-form
# `msg`/`body` line stays intact because that is the human-readable log text the lens must show.
_LOKI_FORBIDDEN_SUBSTR = _PG_FORBIDDEN_SUBSTR  # payload / detail / cipher / secret / nif / iban
_LOKI_ALLOWED_FIELDS = {
    # timestamp / severity / origin
    "ts", "time", "timestamp", "observed_timestamp", "level", "severity", "severity_text",
    "logger", "logger_name", "service_name", "service", "scope_name",
    # the human-readable message (free-form text, but a structurally-named field)
    "msg", "message", "body",
    # OTel trace correlation — the whole point of the lens
    "trace_id", "traceid", "span_id", "spanid", "trace_flags",
    # structural domain references (opaque ids / labels — never PII)
    "family", "event_type", "status", "correlation_id", "causation_id", "process_id",
    "stream_id", "sequence_number", "schema_id", "message_id", "source_topic",
    "aggregate_type", "aggregate_id", "partition", "offset",
    # common Loki/OTel-exporter structural labels
    "job", "exporter", "detected_level", "container", "namespace", "pod",
}


def _loki_allow(fields):
    """Return a copy of a label/field dict keeping ONLY allowlisted STRUCTURAL keys and dropping any
    key whose name matches a forbidden PII substring. Field NAMES are the gate; values are untouched."""
    out = {}
    for k, v in fields.items():
        kl = str(k).lower()
        if any(bad in kl for bad in _LOKI_FORBIDDEN_SUBSTR):
            continue                       # PII-named field — refuse outright
        if kl in _LOKI_ALLOWED_FIELDS:
            out[k] = v
    return out


def _loki_filter_line(line):
    """A Loki log line is opaque text OR a JSON object of fields. Plain text (the `msg`) passes through
    unchanged; a JSON object is re-emitted through the structural allowlist so no PII-named field rides
    along in the body. Non-JSON / non-object lines are left exactly as-is."""
    s = line.strip()
    if not (s.startswith("{") and s.endswith("}")):
        return line
    try:
        obj = json.loads(s)
    except Exception:
        return line
    if not isinstance(obj, dict):
        return line
    return json.dumps(_loki_allow(obj))


def _loki_filter(raw):
    """Apply the BFF structural-field allowlist to a Loki query_range response body (bytes → bytes).
    Filters both the per-stream label set and any JSON-object log line. A body we can't parse (an error
    string, an unexpected shape) is returned UNCHANGED — the allowlist only ever removes, never adds."""
    try:
        doc = json.loads(raw)
    except Exception:
        return raw
    data = doc.get("data") if isinstance(doc, dict) else None
    result = data.get("result") if isinstance(data, dict) else None
    if not isinstance(result, list):
        return raw
    for stream in result:
        if not isinstance(stream, dict):
            continue
        labels = stream.get("stream")
        if isinstance(labels, dict):
            stream["stream"] = _loki_allow(labels)
        values = stream.get("values")
        if isinstance(values, list):
            for pair in values:
                if isinstance(pair, list) and len(pair) >= 2 and isinstance(pair[1], str):
                    pair[1] = _loki_filter_line(pair[1])
    return json.dumps(doc).encode()


class PgError(Exception):
    """A refusal (bad DSN, non-allowlisted column/table, missing driver) surfaced as 4xx/5xx."""

    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


def _require_psycopg():
    try:
        import psycopg  # noqa: F401  (lazy: only the /pg/* routes need it)
        return psycopg
    except ImportError:
        raise PgError(501, "psycopg is not installed — `pip install -r requirements.txt` to enable the /pg/* Inspector lenses")


def _dsn_host(dsn):
    """Best-effort host extraction for the non-local guard. URL-form DSNs parse with stdlib;
    keyword/value DSNs fall back to psycopg's conninfo parser."""
    if "://" in dsn:
        import urllib.parse
        return (urllib.parse.urlsplit(dsn).hostname or "").strip()
    # keyword form (host=… port=… dbname=…) — let psycopg parse it authoritatively
    psycopg = _require_psycopg()
    from psycopg.conninfo import conninfo_to_dict
    info = conninfo_to_dict(dsn)
    return (info.get("host") or "").split(",")[0].strip()


def _assert_local_dsn(dsn):
    """Refuse any DSN whose host is neither loopback nor an EXPLICITLY allowlisted host. A unix-socket
    DSN (host begins with '/') or an empty host (default local socket) is local; 127.0.0.1/::1/localhost
    are local; a host named in MC_PG_ALLOW_HOSTS is an explicit operator opt-in (the in-cluster
    deployment sets it to the postgres Service). Everything else is rejected — the default posture is
    that the /pg/* window never reaches off-box, and reaching a named host is an opt-in guarded by the
    read-only role + NetworkPolicy + OIDC gate."""
    host = _dsn_host(dsn)
    if host == "" or host.startswith("/"):
        return  # local unix socket / default
    if host not in ("127.0.0.1", "::1", "localhost") and host not in PG_ALLOW_HOSTS:
        raise PgError(403, "non-local DSN refused (%r) — /pg/* is 127.0.0.1-only unless the host is named in MC_PG_ALLOW_HOSTS" % host)


def _pg_columns(db, table, columns):
    """Validate every requested column against the structural allowlist AND the PII firewall.
    Returns the (unchanged) column list on success; raises PgError otherwise."""
    allowed = _PG_ALLOWLIST.get((db, table))
    if allowed is None:
        raise PgError(404, "table not allowlisted: %s.%s" % (db, table))
    for c in columns:
        cl = c.lower()
        if any(bad in cl for bad in _PG_FORBIDDEN_SUBSTR):
            raise PgError(403, "forbidden (PII / non-structural) column refused: %s" % c)
        if c not in allowed:
            raise PgError(403, "column not allowlisted for %s.%s: %s" % (db, table, c))
    return columns


def _pg_guard(db):
    """The shared /pg/* admission check: routes enabled, db known, DSN local. Returns the DSN."""
    if not PG_ENABLE:
        raise PgError(403, "/pg/* is disabled (serve.py is not bound to loopback; set MC_PG_ENABLE=1 to force)")
    if db not in _PG_DSN:
        raise PgError(404, "unknown db: %s (expected 'engine' or 'orchestrator')" % db)
    dsn = _PG_DSN[db]
    _assert_local_dsn(dsn)
    return dsn


def _pg_run(db, query, params=None):
    """Open a READ-ONLY connection to the chosen DB and run ONE query (a psycopg sql.Composed built
    from allowlisted identifiers, or a module-level FIXED SQL string — never caller-assembled text).
    Caller VALUES only ever travel as bound parameters. Returns rows as dicts."""
    dsn = _pg_guard(db)
    psycopg = _require_psycopg()
    try:
        with psycopg.connect(dsn, autocommit=True, connect_timeout=3) as conn:
            with conn.cursor() as cur:
                # Belt-and-braces: pin the SESSION read-only at the server so ANY write (even a
                # mistaken future query) is rejected by Postgres itself, not just by our SELECT-only
                # code path. autocommit=True makes this SET persist for the connection's lifetime.
                cur.execute("SET default_transaction_read_only = on")
                cur.execute(query, params)
                names = [d.name for d in cur.description]
                return [dict(zip(names, row)) for row in cur.fetchall()]
    except PgError:
        raise
    except Exception as e:  # psycopg.OperationalError etc. — surface as a clean 502
        raise PgError(502, "Postgres query failed against %s DB: %s" % (db, e))


def pg_select(db, table, columns, where=None, order=None, descending=True, limit=50):
    """SELECT the allowlisted structural columns over the read-only window. Identifiers are emitted
    only from the static allowlist (and quoted via psycopg.sql.Identifier), so no caller string ever
    becomes SQL; `where` is a list of (column, value) equality filters whose COLUMN must be
    allowlisted and whose VALUE is always a bound parameter (never interpolated)."""
    _pg_guard(db)
    cols = _pg_columns(db, table, columns)
    if order is not None:
        _pg_columns(db, table, [order])  # the ORDER BY column must be allowlisted too
    where = where or []
    if where:
        _pg_columns(db, table, [c for c, _ in where])  # WHERE columns must be allowlisted too
    psycopg = _require_psycopg()
    from psycopg import sql

    parts = [sql.SQL("SELECT "), sql.SQL(", ").join(sql.Identifier(c) for c in cols),
             sql.SQL(" FROM "), sql.Identifier(table)]
    params = []
    for i, (c, v) in enumerate(where):
        parts += [sql.SQL(" WHERE " if i == 0 else " AND "), sql.Identifier(c), sql.SQL(" = %s")]
        params.append(v)
    if order is not None:
        parts += [sql.SQL(" ORDER BY "), sql.Identifier(order),
                  sql.SQL(" DESC") if descending else sql.SQL(" ASC")]
    parts += [sql.SQL(" LIMIT "), sql.Literal(int(limit))]
    return _pg_run(db, sql.Composed(parts), params or None)


def pg_smoke():
    """A harmless structural smoke read: the most recent engine outbox rows, STRUCTURAL columns
    only (no payload). Proves the read-only cursor + allowlist + guard all work end to end."""
    rows = pg_select("engine", "outbox",
                     ["event_id", "aggregate_type", "event_type", "status", "created_at", "published_at"],
                     order="created_at", descending=True, limit=10)
    return {"db": "engine", "table": "outbox", "count": len(rows), "rows": rows}


# ── Outbox·Inbox lens (bd babelstone-f0ic.15.5) ──────────────────────────────────────────────
# The publish-lag SQL, VERBATIM from the engine's OutboxLagObserver.cs (the ADR-IC-004 SLI): the
# age in seconds of the OLDEST PENDING outbox row, computed entirely in the DB (single-clock, so
# no host/DB skew can bias it; 0 when the backlog is empty). Reusing the exact statement means the
# lens shows the SAME number the outbox_publish_lag_seconds gauge exports — not a re-derivation
# that could drift from it.
_OUTBOX_LAG_SQL_VERBATIM = """SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - MIN(created_at)), 0)
FROM outbox
WHERE status = 'PENDING';"""

# Row counts by drain status — the transactional-outbox state at a glance (PENDING backlog vs
# PUBLISHED history). Fixed, module-level SQL over one allowlisted structural column.
_OUTBOX_COUNTS_SQL = "SELECT status, COUNT(*) AS n FROM outbox GROUP BY status;"

# The recent tail with the per-row publish latency (published_at − created_at, both DB-stamped —
# the same single-clock discipline as the lag SQL; NULL while a row is still PENDING). Every named
# column is on the structural allowlist; the payload BYTEA is deliberately absent. LIMIT is bound.
_OUTBOX_RECENT_SQL = """SELECT event_id, aggregate_type, aggregate_id, sequence_number, event_type, schema_id,
       status, created_at, published_at,
       EXTRACT(EPOCH FROM (published_at - created_at)) AS publish_latency_seconds
FROM outbox
ORDER BY created_at DESC
LIMIT %s;"""


def pg_outbox_summary(limit=20):
    """The Outbox·Inbox lens's engine-outbox read: counts by status + the VERBATIM ADR-IC-004
    publish-lag SQL + the recent tail with per-row publish latency. Structural columns only."""
    counts = {r["status"]: r["n"] for r in _pg_run("engine", _OUTBOX_COUNTS_SQL)}
    lag_rows = _pg_run("engine", _OUTBOX_LAG_SQL_VERBATIM)
    lag_seconds = list(lag_rows[0].values())[0] if lag_rows else 0
    recent = _pg_run("engine", _OUTBOX_RECENT_SQL, (int(limit),))
    return {
        "counts": counts,
        "publish_lag_seconds": lag_seconds,
        "publish_lag_sql": _OUTBOX_LAG_SQL_VERBATIM,
        "rows": recent,
    }


def pg_inbox_tail(db="engine", limit=20):
    """The consumer-inbox dedup ledger tail (engine or orchestrator): rows are the RESULTING state
    (one row per logical message) — a dedup is a SILENT PK collision, so there is no per-replay row
    here; an actual replay hit is only visible via the dispatcher/inbox OTel counters."""
    rows = pg_select(db, "inbox", ["message_id", "source_topic", "processed_at", "result_summary"],
                     order="processed_at", descending=True, limit=limit)
    return {"db": db, "table": "inbox", "count": len(rows), "rows": rows}


def pg_command_dedup_tail(limit=20):
    """The engine command-idempotency ledger tail (ADR-PC-029 slot 4): one row per logical command,
    with the stream + commit sequence its original apply reached. Same silent-collision semantics as
    the inbox — the ledger shows state, not replay events."""
    rows = pg_select("engine", "command_dedup", ["command_id", "stream_id", "commit_sequence", "created_at"],
                     order="created_at", descending=True, limit=limit)
    return {"db": "engine", "table": "command_dedup", "count": len(rows), "rows": rows}


# ── Config-provenance strip (bd babelstone-f0ic.15.8) ────────────────────────────────────────
def pg_provenance(stream_id):
    """The REAL signed/pinned pack identity for one instance: the head pin (events.pack_version is
    a top-level non-PII column on EVERY event — latest by sequence is the instance's current pin,
    ADR-PC-009) joined through public.pack_versions to its immutable OCI coordinates (ADR-PC-007
    §P3). Structural facts only. NB product_config_version is NOT SQL-readable (it lives inside the
    Avro payload BYTEA) — the UI reads it off GET /v1/deposits/{id}, never through this window."""
    head = pg_select("engine", "events",
                     ["pack_version", "sequence_number", "event_type", "transaction_time"],
                     where=[("stream_id", stream_id)], order="sequence_number", descending=True, limit=1)
    pin = head[0] if head else None
    pack = None
    if pin and pin.get("pack_version"):
        rows = pg_select("engine", "pack_versions",
                         ["pack_id", "pack_version", "oci_ref", "image_digest", "signature_digest", "registered_at"],
                         where=[("pack_version", pin["pack_version"])], limit=1)
        pack = rows[0] if rows else None
    return {"stream_id": stream_id, "pin": pin, "pack": pack}


# ── Topology lens history reads (bd babelstone-f0ic.15.3) ────────────────────────────────────
def pg_resolve_process(handle):
    """Resolve a process handle to its saga_state row. Accepts the client-facing PROC-… reference
    (resolved via public_process_id) or the internal UUID. Raises 404 when unknown."""
    key = "public_process_id" if str(handle).startswith("PROC-") else "process_id"
    rows = pg_select("orchestrator", "saga_state",
                     ["process_id", "public_process_id", "saga_type", "state", "version",
                      "correlation_id", "created_at", "updated_at"],
                     where=[(key, handle)], limit=1)
    if not rows:
        raise PgError(404, "unknown process: %s" % handle)
    return rows[0]


def pg_process_transitions(handle):
    """The REAL path a saga took: its saga_state row + the ordered saga_transition legs + the
    dispatched saga_outbox command legs — everything the Topology lens needs to light the actual
    journey instead of a canned sequence. Structural columns only; no payload ever crosses."""
    process = pg_resolve_process(handle)
    pid = process["process_id"]
    transitions = pg_select("orchestrator", "saga_transition",
                            ["id", "process_id", "from_state", "to_state", "event_type",
                             "message_id", "note", "occurred_at"],
                            where=[("process_id", pid)], order="id", descending=False, limit=200)
    legs = pg_select("orchestrator", "saga_outbox",
                     ["seq", "message_id", "process_id", "command_type", "causation_id",
                      "correlation_id", "status", "created_at", "published_at", "traceparent"],
                     where=[("process_id", pid)], order="seq", descending=False, limit=200)
    return {"process": process, "transitions": transitions, "legs": legs}


def pg_stream_events(stream_id, limit=200):
    """One stream's REAL event chain (engine public.events), ascending — event types, sequence
    numbers and the causation/correlation references. The payload BYTEA is refused by the
    allowlist/PII firewall; only structural envelope columns cross the engine boundary."""
    rows = pg_select("engine", "events",
                     ["event_id", "stream_id", "sequence_number", "event_type", "family",
                      "pack_version", "valid_time", "transaction_time", "causation_id", "correlation_id"],
                     where=[("stream_id", stream_id)], order="sequence_number", descending=False, limit=limit)
    return {"db": "engine", "table": "events", "stream_id": stream_id, "count": len(rows), "rows": rows}


def pg_saga_outbox_tail(process_id=None, limit=20):
    """The orchestrator's saga_outbox tail — the saga's dispatched command legs. Optionally filtered
    to one process (the internal UUID; the PROC-… public handle resolves via /pg/processes/*). The
    payload BYTEA never crosses; structural columns only."""
    where = []
    if process_id:
        where.append(("process_id", process_id))
    rows = pg_select("orchestrator", "saga_outbox",
                     ["seq", "message_id", "process_id", "command_type", "causation_id",
                      "correlation_id", "status", "created_at", "published_at", "traceparent"],
                     where=where, order="seq", descending=True, limit=limit)
    return {"db": "orchestrator", "table": "saga_outbox", "count": len(rows), "rows": rows}


def _json_default(o):
    """Serialise UUID / datetime / Decimal etc. as strings for the JSON response."""
    return str(o)


# ── Topology manifest — derived from the estate, not re-hardcoded (bd babelstone-f0ic.15.3) ──
# The node/edge manifest the Topology lens renders in LIVE modes is DERIVED from the repo's C4 L2
# PlantUML sources (the architecture's own model — docs/…/product_concepts/diagrams/) and enriched
# with the LIVE topic list read off Redpanda through the pandaproxy arm. So the picture the lens
# draws is sourced from the same artefacts the architecture docs render, and a C4 edit flows into
# the lens without touching this file. Everything here is structural (service names, topic names,
# relationship labels) — no PII surface exists in a C4 model.
import re as _re

_C4_DIAGRAM_DIR = os.path.normpath(os.path.join(
    ROOT, "..", "..", "product-management", "product_concepts", "diagrams"))
_C4_SOURCES = (
    "c4-l2-runtime-write-read.puml",        # engine / orchestrator / ACL / core / Kong / Redpanda
    "c4-l2-event-backbone-consumers.puml",  # downstream consumers: GL / IFRS 9 / reporting / notification
    "c4-l2-agent-channel.puml",             # the MCP server + agent channel
)
# Container(id, "label", "tech", "desc"…) / ContainerDb / System / System_Ext / Person / Person_Ext.
_C4_NODE_RE = _re.compile(
    r'^\s*(Person_Ext|Person|System_Ext|System|ContainerDb|Container)\(\s*([A-Za-z0-9_]+)\s*,\s*"([^"]*)"\s*(?:,\s*"([^"]*)")?')
_C4_REL_RE = _re.compile(
    r'^\s*(?:Bi)?Rel(?:_[A-Za-z]+)?\(\s*([A-Za-z0-9_]+)\s*,\s*([A-Za-z0-9_]+)\s*,\s*"([^"]*)"')


def _c4_parse(text):
    """Parse ONE C4-PlantUML source into (nodes, edges). Node plane: 'ext' for System_Ext/Person*,
    'estate' when the line carries $tags=\"estate\", else 'build' (the engine-boundary deliverable)."""
    nodes, edges = {}, []
    for line in text.splitlines():
        m = _C4_NODE_RE.match(line)
        if m:
            kind, node_id, label, tech = m.group(1), m.group(2), m.group(3), m.group(4) or ""
            if kind in ("System_Ext", "Person", "Person_Ext"):
                plane = "ext"
            elif '$tags="estate"' in line:
                plane = "estate"
            else:
                plane = "build"
            nodes[node_id] = {"id": node_id, "label": label, "tech": tech, "plane": plane}
            continue
        m = _C4_REL_RE.match(line)
        if m:
            edges.append([m.group(1), m.group(2), m.group(3)])
    return nodes, edges


def _c4_columns(nodes, edges):
    """Assign a left-to-right column hint per node: BFS depth from the source nodes (no inbound
    edge), capped at 5 — the same 6-column stage the lens lays out. Unreachable nodes fall back to
    a plane-typical column so nothing lands off-grid."""
    inbound = {n: 0 for n in nodes}
    adjacency = {n: [] for n in nodes}
    for a, b, _ in edges:
        if a in nodes and b in nodes:
            adjacency[a].append(b)
            inbound[b] += 1
    frontier = [n for n, deg in inbound.items() if deg == 0] or list(nodes)[:1]
    depth = {n: 0 for n in frontier}
    queue = list(frontier)
    # Shortest-path BFS (first visit wins): the Rel graph cycles through the bus (engine→redpanda→
    # orch→engine), so a longest-path rank would drag every node to the deep end — the hop count
    # from the callers is the stable left-to-right reading order.
    while queue:
        cur = queue.pop(0)
        for nxt in adjacency.get(cur, []):
            if nxt not in depth:
                depth[nxt] = depth[cur] + 1
                queue.append(nxt)
    fallback = {"ext": 5, "estate": 3, "build": 3}
    for node_id, node in nodes.items():
        node["col"] = min(depth.get(node_id, fallback[node["plane"]]), 5)
    return nodes


def _live_topics():
    """Best-effort LIVE topic list off Redpanda's HTTP proxy (the same arm the Topic·Avro lens
    uses). Internal topics are filtered; unreachable broker → None (the manifest says so)."""
    try:
        with urllib.request.urlopen(PANDAPROXY_URL + "/topics", timeout=2) as resp:
            names = json.loads(resp.read())
        return [t for t in names if isinstance(t, str) and not t.startswith("_")]
    except Exception:
        return None


def topology_manifest():
    """The estate-derived node/edge manifest (bd babelstone-f0ic.15.3): C4 L2 sources parsed into
    nodes/edges (+ column hints), topics read live where the broker answers."""
    nodes, edges, parsed = {}, [], []
    for name in _C4_SOURCES:
        path = os.path.join(_C4_DIAGRAM_DIR, name)
        try:
            with open(path, "r", encoding="utf-8") as f:
                file_nodes, file_edges = _c4_parse(f.read())
        except OSError:
            continue                      # a container image without the docs tree — degrade
        parsed.append(name)
        for node_id, node in file_nodes.items():
            nodes.setdefault(node_id, node)   # first definition wins (the runtime diagram leads)
        seen = {(a, b) for a, b, _ in edges}
        for a, b, label in file_edges:
            if (a, b) not in seen:
                edges.append([a, b, label])
                seen.add((a, b))
    _c4_columns(nodes, edges)
    topics = _live_topics()
    return {
        "source": {"diagrams": parsed, "topics": "pandaproxy" if topics is not None else "unavailable"},
        "nodes": list(nodes.values()),
        "edges": edges,
        "topics": topics or [],
    }


# ── OIDC login gate (bd babelstone-zla1.10.8.1) ──────────────────────────────────────────────
# A provider-agnostic OpenID-Connect Authorization-Code + PKCE gate that stands in front of every
# route in oidc mode (ADR-IC-021 Boundary-1: the owned-channel operator login lives at the BFF).
# Design choices worth calling out:
#
#   • PROVIDER-AGNOSTIC. Every IdP endpoint (authorization/token/jwks/end-session) is DISCOVERED
#     from {OIDC_ISSUER}/.well-known/openid-configuration (RFC 8414 / OIDC Discovery) at startup —
#     no Logto-specific (or any-IdP-specific) path is baked in. Point OIDC_ISSUER at any conformant
#     issuer and the flow follows the discovered endpoints.
#
#   • STATELESS + DOMAIN-SEPARATED COOKIES. There is no server-side session store (serve.py is a
#     threaded single process that a deployment may scale out). Both the in-flight login transaction
#     (verifier/state/nonce/return-to) and the established session are carried in HMAC-signed cookies,
#     so any instance can validate any request. The HMAC (stdlib hmac/hashlib, constant-time compare)
#     is an integrity seal, not encryption — the payloads carry no secret, only an opaque subject id
#     and a short profile, and are HttpOnly+Secure+SameSite=Lax. The two cookie families are KEY- AND
#     TYPE-SEPARATED to defeat cross-cookie type confusion: each is signed with its OWN key derived
#     from MC_SESSION_SIGNING_KEY (HKDF-style, distinct info strings) AND carries a mandatory `typ`
#     ("tx"/"sess") that _read_signed checks before anything else — so a validly-signed tx blob (handed
#     to every anonymous visitor on the login 302) can NEVER be replayed under the session-cookie name,
#     and vice-versa.
#
#   • id_token VERIFICATION — the deliberate stdlib-only choice. We do NOT verify the id_token's JWKS
#     RS256 signature; instead we trust the DIRECT TLS BACKCHANNEL to the discovered token_endpoint
#     plus claim validation. Exactly which claims are checked: iss == issuer, aud contains client_id,
#     azp == client_id when aud is multi-valued (OIDC Core §3.1.3.7 items 4-5), exp not past (± leeway),
#     and nonce == the login transaction's nonce. The SIGNATURE is intentionally NOT verified. This is
#     blessed by OIDC Core 1.0 §3.1.3.7 item 6: when the id_token is obtained by direct Client↔Token-
#     Endpoint communication (exactly this code flow), TLS server-cert validation MAY stand in for
#     verifying the token signature. The load-bearing anchor is therefore the TLS-authenticated channel
#     to the discovered TOKEN_ENDPOINT (not merely the issuer): urllib validates the server certificate
#     by default for https URLs, and both _build_oidc_gate (the issuer) AND discover() (the discovered
#     authorization/token/jwks endpoints) refuse a non-loopback http URL — so the token can only arrive
#     over an authenticated backchannel, never an untrusted front channel. We keep serve.py dependency-
#     light (no PyJWT / cryptography — mirroring the "stdlib-only except the lazily-imported psycopg"
#     ethos of this file); upgrading to JWKS RS256 verification is a drop-in in _validate_id_token.

_TX_COOKIE = "mc_oidc_tx"        # short-lived signed cookie carrying the in-flight login transaction
_SESSION_COOKIE = "mc_session"   # signed cookie carrying the established session
_TX_TTL = 600                    # a login round-trip must complete within 10 minutes
_CLAIM_LEEWAY = 60               # clock-skew tolerance (seconds) on the id_token exp check

# Domain-separation for the two cookie families (defeats cross-cookie type confusion). Each cookie
# is signed with its OWN key derived from MC_SESSION_SIGNING_KEY via a one-step HMAC-KDF over a
# distinct info string, and carries a mandatory `typ` that _read_signed checks. A tx blob therefore
# cannot verify under the session key at all, and even a hypothetical key reuse is caught by `typ`.
_TX_PURPOSE = b"mc-tx-v1"
_SESSION_PURPOSE = b"mc-session-v1"
_TYP_TX = "tx"
_TYP_SESSION = "sess"


def _derive_key(base, purpose):
    """A distinct signing key per cookie family: HMAC(MC_SESSION_SIGNING_KEY, info). Returns hex so
    it is a str the cookie signer can .encode()."""
    return hmac.new(base.encode("utf-8"), purpose, hashlib.sha256).hexdigest()


class OidcConfigError(Exception):
    """Raised when oidc-mode config is missing/invalid or discovery is unreachable. _preflight turns
    this into a hard startup refusal — the fail-CLOSED contract: serve.py never runs ungated in oidc
    mode."""


class OidcAuthError(Exception):
    """A per-request login failure (bad state, token exchange failed, invalid id_token) surfaced as
    a 400 at /callback."""


def _b64u(raw):
    """URL-safe base64 WITHOUT padding (the JOSE/JWT convention)."""
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def _b64u_decode(s):
    pad = "=" * (-len(s) % 4)
    return base64.urlsafe_b64decode(s + pad)


def _sign_cookie(payload, key):
    """Serialise a dict to a signed, tamper-evident cookie value `<b64(json)>.<b64(hmac_sha256)>`.
    The HMAC seals integrity; there is nothing secret in the payload to hide."""
    body = _b64u(json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8"))
    sig = _b64u(hmac.new(key.encode("utf-8"), body.encode("ascii"), hashlib.sha256).digest())
    return body + "." + sig


def _unsign_cookie(value, key):
    """Verify + decode a value produced by _sign_cookie. Returns the dict, or None if the signature
    is absent/forged/garbled. Uses a constant-time compare so a bad signature leaks no timing."""
    try:
        body, sig = value.split(".", 1)
        expected = _b64u(hmac.new(key.encode("utf-8"), body.encode("ascii"), hashlib.sha256).digest())
        if not hmac.compare_digest(sig, expected):
            return None
        return json.loads(_b64u_decode(body))
    except Exception:
        return None


def _pkce_pair():
    """A fresh PKCE (RFC 7636) verifier + its S256 challenge. The verifier is 43 URL-safe chars of
    CSPRNG entropy; the challenge is base64url(sha256(verifier))."""
    verifier = secrets.token_urlsafe(32)
    challenge = _b64u(hashlib.sha256(verifier.encode("ascii")).digest())
    return verifier, challenge


def _cookie_attrs(name, value, max_age):
    """Build a Set-Cookie header value with the hardened flags. Secure is unconditional (oidc mode
    assumes TLS termination in front — the deployment provides it); HttpOnly keeps it off JS; Lax
    lets the IdP's top-level redirect back to /callback carry the cookie while blocking cross-site
    POSTs. max_age=0 clears the cookie."""
    return ("%s=%s; Max-Age=%d; Path=/; HttpOnly; Secure; SameSite=Lax"
            % (name, value, max_age))


class _OidcGate:
    """Holds the discovered endpoints + client config and drives the login flow. One instance is
    built by _preflight() in oidc mode and stored in the module global AUTH."""

    def __init__(self, issuer, client_id, client_secret, scopes, redirect_url, signing_key, session_ttl):
        self.issuer = issuer
        self.client_id = client_id
        self.client_secret = client_secret
        self.scopes = scopes
        self.redirect_url = redirect_url
        self.signing_key = signing_key
        # Per-family signing keys — the tx cookie and the session cookie are cryptographically
        # distinct, so neither can be replayed as the other (auth-bypass fix, bd zla1.10.8.*).
        self.tx_key = _derive_key(signing_key, _TX_PURPOSE)
        self.session_key = _derive_key(signing_key, _SESSION_PURPOSE)
        self.session_ttl = session_ttl
        # filled by discover()
        self.authorization_endpoint = None
        self.token_endpoint = None
        self.jwks_uri = None
        self.end_session_endpoint = None

    # ── startup: discovery ──────────────────────────────────────────────────────────────────
    def discover(self):
        """Fetch the issuer's discovery document and pull the endpoints we need. Raises
        OidcConfigError on any failure (unreachable, malformed, issuer mismatch, missing endpoint)
        — the fail-closed contract: a gate that cannot discover its IdP must not start."""
        url = self.issuer + "/.well-known/openid-configuration"
        try:
            req = urllib.request.Request(
                url, headers={"Accept": "application/json", "User-Agent": _OIDC_USER_AGENT})
            with urllib.request.urlopen(req, timeout=10) as resp:
                doc = json.loads(resp.read())
        except Exception as e:
            raise OidcConfigError("OIDC discovery failed at %s: %s" % (url, e))
        # RFC 8414 makes `issuer` REQUIRED and mandates an exact match with the config value — a doc
        # that omits it (or disagrees) is not trustworthy discovery metadata, so we fail closed.
        disc_issuer = (doc.get("issuer") or "").rstrip("/")
        if not disc_issuer:
            raise OidcConfigError("discovery document at %s is missing the required 'issuer' (RFC 8414)" % url)
        if disc_issuer != self.issuer:
            raise OidcConfigError("discovery issuer %r does not match OIDC_ISSUER %r"
                                  % (disc_issuer, self.issuer))
        self.authorization_endpoint = doc.get("authorization_endpoint")
        self.token_endpoint = doc.get("token_endpoint")
        self.jwks_uri = doc.get("jwks_uri")
        self.end_session_endpoint = doc.get("end_session_endpoint")
        missing = [k for k in ("authorization_endpoint", "token_endpoint", "jwks_uri")
                   if not getattr(self, k)]
        if missing:
            raise OidcConfigError("discovery document at %s is missing: %s" % (url, ", ".join(missing)))
        # The "skip JWKS signature verification" premise rests on a TLS-authenticated backchannel to
        # the DISCOVERED endpoints (the token_endpoint above all). A discovery doc that advertises an
        # http endpoint on a non-loopback host would void that anchor, so we refuse it — fail-closed.
        for name in ("authorization_endpoint", "token_endpoint", "jwks_uri"):
            ep = getattr(self, name)
            parts = urllib.parse.urlsplit(ep)
            if parts.scheme != "https" and not _is_loopback(parts.hostname or ""):
                raise OidcConfigError(
                    "discovered %s must be https for a non-loopback host (got %r)" % (name, ep))
        return self

    # ── per-request: session check ──────────────────────────────────────────────────────────
    def session_for(self, cookies):
        """Return the session dict for a valid, unexpired SESSION cookie, else None. Verified with the
        session key AND required to carry typ=="sess" and a non-empty `sub` — a tx blob (different key
        and typ) can never satisfy this, closing the cross-cookie type-confusion bypass."""
        data = self._read_signed(cookies.get(_SESSION_COOKIE), self.session_key, _TYP_SESSION)
        if not data or not data.get("sub"):
            return None
        return data

    def read_tx(self, cookies):
        """Return the in-flight login-transaction for a valid, unexpired TX cookie, else None. Verified
        with the tx key AND required to carry typ=="tx"."""
        return self._read_signed(cookies.get(_TX_COOKIE), self.tx_key, _TYP_TX)

    def _read_signed(self, value, key, expected_typ):
        """Verify a signed cookie with the FAMILY key, enforce its declared `typ` BEFORE anything else,
        then the exp. A signature made with the other family's key fails the HMAC; a payload with the
        wrong (or absent) `typ` is rejected here even if the keys were somehow shared."""
        if not value:
            return None
        data = _unsign_cookie(value, key)
        if not data:
            return None
        if data.get("typ") != expected_typ:
            return None
        if int(data.get("exp", 0)) <= int(time.time()):
            return None
        return data

    # ── begin login: 302 → authorization_endpoint with PKCE ─────────────────────────────────
    def begin_login(self, return_to):
        """Mint a PKCE verifier + state + nonce, stash them in a signed tx cookie, and build the
        authorization redirect. Returns (location, set_cookie_header)."""
        verifier, challenge = _pkce_pair()
        state = secrets.token_urlsafe(24)
        nonce = secrets.token_urlsafe(24)
        tx = {"typ": _TYP_TX, "v": 1, "state": state, "nonce": nonce, "verifier": verifier,
              "return_to": _safe_return_to(return_to), "exp": int(time.time()) + _TX_TTL}
        cookie = _cookie_attrs(_TX_COOKIE, _sign_cookie(tx, self.tx_key), _TX_TTL)
        params = {
            "response_type": "code",
            "client_id": self.client_id,
            "redirect_uri": self.redirect_url,
            "scope": self.scopes,
            "state": state,
            "nonce": nonce,
            "code_challenge": challenge,
            "code_challenge_method": "S256",
        }
        sep = "&" if "?" in self.authorization_endpoint else "?"
        location = self.authorization_endpoint + sep + urllib.parse.urlencode(params)
        return location, cookie

    # ── complete login: /callback ───────────────────────────────────────────────────────────
    def complete_login(self, code, tx):
        """Exchange the code at the token_endpoint (direct TLS backchannel), validate the returned
        id_token, and return (session_set_cookie, return_to). Raises OidcAuthError on any failure."""
        tok = self._exchange_code(code, tx["verifier"])
        id_token = tok.get("id_token")
        if not id_token:
            raise OidcAuthError("token response carried no id_token")
        claims = self._validate_id_token(id_token, tx["nonce"])
        sub = claims.get("sub")
        if not sub:
            raise OidcAuthError("id_token has no subject (sub)")
        now = int(time.time())
        session = {
            "typ": _TYP_SESSION,
            "v": 1,
            "sub": sub,
            "email": claims.get("email"),
            "name": claims.get("name") or claims.get("preferred_username"),
            "iat": now,
            "exp": now + self.session_ttl,
        }
        cookie = _cookie_attrs(_SESSION_COOKIE, _sign_cookie(session, self.session_key), self.session_ttl)
        return cookie, _safe_return_to(tx.get("return_to"))

    def _exchange_code(self, code, verifier):
        fields = {
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": self.redirect_url,
            "client_id": self.client_id,
            "code_verifier": verifier,
        }
        if self.client_secret:
            fields["client_secret"] = self.client_secret   # client_secret_post
        data = urllib.parse.urlencode(fields).encode("ascii")
        req = urllib.request.Request(
            self.token_endpoint, data=data, method="POST",
            headers={"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json",
                     "User-Agent": _OIDC_USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return json.loads(resp.read())
        except urllib.error.HTTPError as e:
            raise OidcAuthError("token exchange rejected (%s): %s" % (e.code, e.read()[:256]))
        except Exception as e:
            raise OidcAuthError("token exchange failed: %s" % e)

    def _validate_id_token(self, id_token, expected_nonce):
        """Claim validation of the id_token received over the TLS backchannel — iss, aud, azp (when
        multi-audience), exp, and nonce. The JWKS signature is intentionally NOT verified; the
        TLS-authenticated direct token-endpoint backchannel stands in for it (OIDC Core §3.1.3.7
        item 6) — see the module note above."""
        parts = id_token.split(".")
        if len(parts) != 3:
            raise OidcAuthError("malformed id_token (not a JWT)")
        try:
            claims = json.loads(_b64u_decode(parts[1]))
        except Exception as e:
            raise OidcAuthError("id_token payload is not decodable JSON: %s" % e)
        if (claims.get("iss") or "").rstrip("/") != self.issuer:
            raise OidcAuthError("id_token iss mismatch")
        aud = claims.get("aud")
        auds = aud if isinstance(aud, list) else [aud]
        if self.client_id not in auds:
            raise OidcAuthError("id_token aud does not contain client_id")
        # OIDC Core §3.1.3.7 items 4-5: with multiple audiences the `azp` (authorized party) MUST be
        # present and equal to our client_id. This matters MORE here precisely because we do not verify
        # the signature — it stops a token minted for a different (co-audienced) client being accepted.
        if isinstance(aud, list) and len(aud) > 1:
            if claims.get("azp") != self.client_id:
                raise OidcAuthError("multi-audience id_token requires azp == client_id")
        now = int(time.time())
        if int(claims.get("exp", 0)) <= now - _CLAIM_LEEWAY:
            raise OidcAuthError("id_token is expired")
        if claims.get("nonce") != expected_nonce:
            raise OidcAuthError("id_token nonce mismatch (possible replay)")
        return claims

    # ── cookie clears ───────────────────────────────────────────────────────────────────────
    def clear_tx_cookie(self):
        return _cookie_attrs(_TX_COOKIE, "", 0)

    def clear_session_cookie(self):
        return _cookie_attrs(_SESSION_COOKIE, "", 0)


def _safe_return_to(path):
    """Only ever bounce back to a SAME-ORIGIN relative path — never an attacker-supplied absolute URL,
    a protocol-relative //host, or a backslash form like /\\host that browsers normalise to //host
    (open-redirect guard). Anything suspicious collapses to '/'."""
    if not path or not isinstance(path, str):
        return "/"
    if "\\" in path:                       # browsers treat '\' as '/', so /\evil.com → //evil.com
        return "/"
    if not path.startswith("/") or path.startswith("//"):
        return "/"
    parts = urllib.parse.urlsplit(path)    # belt-and-braces: no scheme, no host may survive
    if parts.scheme or parts.netloc:
        return "/"
    return path


def _build_oidc_gate():
    """Validate the oidc-mode config from the module globals, then construct + discover the gate.
    Raises OidcConfigError (→ hard startup refusal) on any missing/invalid config — fail-closed."""
    missing = [name for name, val in (("OIDC_ISSUER", OIDC_ISSUER),
                                      ("OIDC_CLIENT_ID", OIDC_CLIENT_ID),
                                      ("MC_SESSION_SIGNING_KEY", MC_SESSION_SIGNING_KEY))
               if not val]
    if missing:
        raise OidcConfigError("oidc mode requires: %s" % ", ".join(missing))
    scheme = urllib.parse.urlsplit(OIDC_ISSUER).scheme
    host = urllib.parse.urlsplit(OIDC_ISSUER).hostname or ""
    if scheme != "https" and not _is_loopback(host):
        # The whole id_token-trust model rests on a TLS-authenticated backchannel to the issuer;
        # a non-loopback http issuer would void it, so we refuse rather than run insecurely.
        raise OidcConfigError("OIDC_ISSUER must be https for a non-loopback host (got %r)" % OIDC_ISSUER)
    redirect_url = OIDC_REDIRECT_URL
    if not redirect_url:
        if not MC_PUBLIC_BASE_URL:
            raise OidcConfigError("set OIDC_REDIRECT_URL or MC_PUBLIC_BASE_URL to derive the /callback redirect_uri")
        redirect_url = MC_PUBLIC_BASE_URL + "/callback"
    gate = _OidcGate(OIDC_ISSUER, OIDC_CLIENT_ID, OIDC_CLIENT_SECRET, OIDC_SCOPES,
                     redirect_url, MC_SESSION_SIGNING_KEY, MC_SESSION_TTL)
    return gate.discover()


def _preflight():
    """Startup gate — run BEFORE binding the socket (from main()). Enforces:
      • the fail-safe (bd zla1.10.8.2): dev mode on a PUBLIC bind is REFUSED unless
        MC_ALLOW_UNAUTHENTICATED=1 (and then it warns loudly);
      • the oidc gate build (bd zla1.10.8.1): config validated + discovery fetched, or a hard
        refusal — fail-closed, never a silent ungated fallback.
    Raises SystemExit on refusal; in oidc mode sets the module global AUTH on success."""
    global AUTH
    if MC_AUTH_MODE == "dev":
        if _is_public_bind(MC_BIND):
            if not MC_ALLOW_UNAUTHENTICATED:
                sys.stderr.write(
                    "\nREFUSING TO START: MC_BIND=%s is a PUBLIC interface but MC_AUTH_MODE=dev\n"
                    "(no authentication). A public, ungated bind exposes the UI and EVERY proxied\n"
                    "backend to the network. Fix ONE of:\n"
                    "  - set MC_AUTH_MODE=oidc to require an interactive login (recommended), or\n"
                    "  - bind to loopback (MC_BIND=127.0.0.1) for local-only dev, or\n"
                    "  - set MC_ALLOW_UNAUTHENTICATED=1 to accept the exposure explicitly.\n\n"
                    % MC_BIND)
                raise SystemExit(2)
            sys.stderr.write(
                "\n" + "=" * 74 + "\n"
                "WARNING: Mission Control is bound to a PUBLIC interface (%s) with\n"
                "         MC_AUTH_MODE=dev — the UI and ALL proxied backends are served\n"
                "         UNAUTHENTICATED to every host that can reach this port.\n"
                "         MC_ALLOW_UNAUTHENTICATED=1 is set, so startup continues.\n"
                "         Set MC_AUTH_MODE=oidc to require login, or bind to loopback.\n"
                % MC_BIND + "=" * 74 + "\n\n")
        AUTH = None
        return
    if MC_AUTH_MODE == "oidc":
        try:
            AUTH = _build_oidc_gate()
        except OidcConfigError as e:
            sys.stderr.write(
                "\nREFUSING TO START: MC_AUTH_MODE=oidc but the login gate could not be brought up.\n"
                "  %s\n"
                "serve.py fails CLOSED — it will not serve any route ungated. Fix the OIDC_* config\n"
                "and/or make the issuer's discovery endpoint reachable, then restart.\n\n" % e)
            raise SystemExit(2)
        return
    sys.stderr.write("REFUSING TO START: unknown MC_AUTH_MODE=%r (expected 'dev' or 'oidc')\n" % MC_AUTH_MODE)
    raise SystemExit(2)


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    # quieter logs
    def log_message(self, fmt, *args):
        sys.stderr.write("  %s\n" % (fmt % args))

    # ── OIDC login gate (bd babelstone-zla1.10.8.1) ─────────────────────────────────────────
    def _cookies(self):
        jar = SimpleCookie()
        raw = self.headers.get("Cookie")
        if raw:
            try:
                jar.load(raw)
            except Exception:
                return {}
        return {k: m.value for k, m in jar.items()}

    def _wants_html(self):
        """A top-level browser NAVIGATION (which we can usefully 302 to the IdP), vs an XHR/fetch/
        asset request (which we answer with a 401 — an opaque cross-origin redirect would just fail
        for it). Detected from Sec-Fetch-Mode / the Accept header."""
        if (self.headers.get("Sec-Fetch-Mode") or "").lower() == "navigate":
            return True
        return "text/html" in (self.headers.get("Accept") or "")

    def _send_json(self, status, obj):
        payload = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(payload)

    def _authgate(self):
        """The login gate, called FIRST from every verb handler. Returns True when it has handled
        (blocked/redirected/answered) the request — the caller must then return without routing.
        Returns False to let the request proceed (dev mode, or an authenticated oidc session).

        In dev mode AUTH is None, so this is a no-op returning False → the pre-auth behaviour is
        byte-for-byte unchanged. In oidc mode it enforces a valid session before ANY route."""
        if AUTH is None:
            return False
        method = self.command
        path = self.path.split("?", 1)[0]
        # Auth-plumbing endpoints are reachable without a session (they establish/clear one, or are
        # the liveness probe). Everything else — the UI and every proxied prefix — is gated.
        if method == "GET" and path == "/callback":
            self._oidc_callback()
            return True
        if method == "GET" and path == "/logout":
            self._oidc_logout()
            return True
        if path == "/healthz":
            self._send_json(200, {"status": "ok", "auth": "oidc"})
            return True
        if AUTH.session_for(self._cookies()):
            return False   # authenticated → let the request through to the normal routing
        # Unauthenticated. A navigation starts the login redirect; anything else gets a clean 401.
        if method == "GET" and self._wants_html():
            location, cookie = AUTH.begin_login(self.path)
            self.send_response(302)
            self.send_header("Location", location)
            self.send_header("Set-Cookie", cookie)
            self.send_header("Content-Length", "0")
            self.end_headers()
        else:
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.send_header("WWW-Authenticate", "OIDC")
            body = b'{"title":"authentication required","detail":"log in at / to obtain a session"}'
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)
        return True

    def _oidc_callback(self):
        """The OAuth redirect target: verify state against the tx cookie, exchange the code, validate
        the id_token, set the session cookie, and bounce back to where the user was headed."""
        split = urllib.parse.urlsplit(self.path)
        qs = urllib.parse.parse_qs(split.query)
        code = (qs.get("code") or [None])[0]
        state = (qs.get("state") or [None])[0]
        idp_error = (qs.get("error") or [None])[0]
        tx = AUTH.read_tx(self._cookies())
        try:
            if idp_error:
                raise OidcAuthError("identity provider returned error: %s" % idp_error)
            if not code or not state:
                raise OidcAuthError("callback missing code/state")
            if tx is None:
                raise OidcAuthError("no valid login transaction (expired or missing tx cookie)")
            if not hmac.compare_digest(state, tx.get("state", "")):
                raise OidcAuthError("state mismatch (possible CSRF)")
            session_cookie, return_to = AUTH.complete_login(code, tx)
        except OidcAuthError as e:
            self._send_json(400, {"title": "login failed", "detail": str(e)})
            return
        self.send_response(302)
        self.send_header("Location", return_to)
        self.send_header("Set-Cookie", session_cookie)
        self.send_header("Set-Cookie", AUTH.clear_tx_cookie())
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _oidc_logout(self):
        """Clear the session and, if the IdP advertised one, bounce to its end_session_endpoint."""
        self.send_response(302)
        self.send_header("Location", AUTH.end_session_endpoint or "/")
        self.send_header("Set-Cookie", AUTH.clear_session_cookie())
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _api_client_id(self):
        """Resolve the X-Client-Id to attest on the /api/v1/* (orchestrator edge) arm.

        Returns the caller id string, or None when oidc mode cannot resolve an authenticated
        session `sub` (the caller MUST then refuse the request — never fall back to the static
        demo id). In oidc mode this MIRRORS Kong's algorithm: attest X-Client-Id from the
        OIDC-validated session `sub` — the same claim Kong would propagate from a validated
        token — so this is REAL attestation, not a forged stand-in (ADR-IC-006 §P2/§P4 demo-BFF
        exception, 2026-07-08 amendment). In dev mode (AUTH is None) it keeps the static
        DEMO_CLIENT_ID, byte-for-byte the pre-auth behaviour.

        Demo-data note: a freshly-logged-in operator now carries their OWN `sub`, so data owned
        by the old static CLI-DEMO-0001 id would not be visible to them — a non-issue in practice,
        since nothing is pre-seeded under that id. There is deliberately no id-mapping shim."""
        if AUTH is None:
            return DEMO_CLIENT_ID
        session = AUTH.session_for(self._cookies())
        # _authgate already validated the session before routing, so this is defence-in-depth:
        # if for any reason no session/sub is resolvable in oidc mode, refuse — do NOT forge.
        if not session or not session.get("sub"):
            return None
        return session["sub"]

    def _route(self):
        """Map the request path to a backend. Returns (base_url, injected_headers, upstream_path),
        None for a static file served locally, or the _REFUSE sentinel when the /api/v1/* arm
        cannot attest a caller id in oidc mode (the verb handler turns that into a 403)."""
        if self.path.startswith("/api/v1/"):
            # The orchestrator edge. This BFF same-origin-proxies to the internal orchestrator
            # rather than routing through Kong (an acknowledged ADR-IC-006 §P2/§P4 exception for
            # this Traefik-fronted demo host — see the 2026-07-08 amendment). It attests
            # X-Client-Id the SAME WAY Kong does: from the OIDC-validated session `sub` in oidc
            # mode, or the static DEMO_CLIENT_ID in dev mode. This is real attestation, not a
            # forged id. (Full Kong-fronted conformance for this path is epic bd babelstone-
            # zla1.10.9.) Note: a real operator carries their own `sub`; nothing is pre-seeded
            # under the old CLI-DEMO-0001 id, so there is no demo-data visibility gap to bridge.
            client_id = self._api_client_id()
            if client_id is None:
                return _REFUSE  # oidc mode with no resolvable session/sub — refuse, never forge
            return ORCHESTRATOR_URL, {"X-Client-Id": client_id}, self.path
        if self.path.startswith("/v1/"):
            return ENGINE_URL, None, self.path
        if self.path.startswith("/agent/"):
            # The real-Claude agent host (LIVE·agent mode, bd babelstone-f0ic.6). No header
            # injection: the agent host holds its OWN identity + Anthropic key and connects to the
            # MCP server itself. POST /agent/stream returns text/event-stream; the /stream suffix
            # routes it through the long-lived, no-deadline SSE relay below.
            return AGENT_URL, None, self.path
        if self.path.startswith("/tempo/"):
            # Grafana Tempo's query API (LIVE·engine Telemetry tab, bd babelstone-f0ic.9): the UI
            # fetches the REAL trace by id at /tempo/api/traces/{id}; strip the /tempo prefix so the
            # upstream sees its own /api/... path. Read-only GETs of an opaque trace id — no PII.
            return TEMPO_URL, None, self.path[len("/tempo"):]
        if self.path.startswith("/pandaproxy/"):
            # Redpanda's HTTP Proxy (Kafka REST, bd babelstone-f0ic.15.4): the Topic·Avro lens runs
            # the consumer-group dance here (create consumer → subscribe → poll records → delete) to
            # read REAL topic records. Strip the /pandaproxy prefix so the upstream sees its own path.
            # Records are read in BINARY format upstream — the payload body is never decoded here.
            return PANDAPROXY_URL, None, self.path[len("/pandaproxy"):]
        if self.path.startswith("/sr/"):
            # The Schema Registry (Confluent API, bd babelstone-f0ic.15.4): the Topic·Avro lens
            # resolves a record's wire schema-id to its subject/version for the catalog badge. Strip
            # the /sr prefix. Read-only GETs of structural schema metadata — no PII.
            return SCHEMA_REGISTRY_URL, None, self.path[len("/sr"):]
        if self.path.startswith("/registry/"):
            # The OCI registry's Distribution v2 API (config-provenance strip, bd babelstone-
            # f0ic.15.8): the UI checks the pinned image digest is PRESENT and that a cosign
            # signature referrer exists — surfacing signature_digest as the fact. Full cosign
            # VERIFY stays a CI/deploy attestation (ADR-PC-007), never re-run in a browser. Strip
            # the /registry prefix so the upstream sees /v2/…. GET-only (enforced in do_POST etc.):
            # this window can read pack metadata, it can never push. Digests are structural — no PII.
            return REGISTRY_URL, None, self.path[len("/registry"):]
        if self.path.startswith("/prom/"):
            # Prometheus's query API (Metrics lens, bd babelstone-f0ic.15.6): the five SLI cards run
            # instant/range queries here (mirrors the /tempo arm — strip the /prom prefix so the
            # upstream sees its own /api/v1/… path). GET-only (enforced in do_POST etc.). PII was
            # already stripped at emit by the metric View allowlist — references only in the store.
            return PROM_URL, None, self.path[len("/prom"):]
        if self.path.startswith("/loki/"):
            # Grafana Loki's query API (Logs lens, bd babelstone-f0ic.15.7): the Logs lens fetches the
            # REAL structured logs at /loki/api/v1/query_range, correlated by the active trace id. Loki's
            # native API ALREADY lives under /loki/…, so the path is forwarded UNCHANGED (no prefix to
            # strip). Defence in depth: the JSON response is run through the BFF structural-field
            # allowlist (_loki_filter, applied in _relay) before it reaches the browser — no PII crosses.
            return LOKI_URL, None, self.path
        return None

    def _dispatch(self, method):
        """Resolve the route and act on it. Returns True when the request was handled here (relayed
        upstream, or refused with a 403 because oidc mode could not attest a caller id), False when
        there is no proxy route (the caller then serves a static file or answers 405)."""
        route = self._route()
        if route is _REFUSE:
            # oidc mode, /api/v1/* arm, no resolvable session/sub — refuse rather than forge the
            # static demo id (ADR-IC-006 §P2/§P4: the caller id must be attested, never invented).
            self.send_error(403, "no authenticated session to attest X-Client-Id from")
            return True
        if route is not None:
            self._relay(method, route[0], route[1], route[2])
            return True
        return False

    def _relay(self, method, base_url, inject, upstream_path):
        url = base_url + upstream_path
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else None

        req = urllib.request.Request(url, data=body, method=method)
        for k, v in self.headers.items():
            if k.lower() not in _HOP_BY_HOP:
                req.add_header(k, v)
        if inject:
            for k, v in inject.items():
                req.add_header(k, v)

        # The SSE stream is long-lived (it follows the saga to a terminal state), so it must
        # NOT use a read deadline — it streams until the saga finishes or the client leaves.
        is_stream = self.path.endswith("/stream")
        timeout = None if is_stream else 30

        # Caller-side internal mTLS (bd babelstone-zla1.12.10): the engine + orchestrator hops present
        # the proxy's client cert + pin the internal CA when configured; every other hop passes None
        # (urllib's default TLS). A plain-http base_url with a context is harmless (urllib ignores an
        # https-only context on an http URL), but scoping it to the two internal base URLs keeps the
        # intent explicit and leaves the observability/registry arms on stock handling.
        ssl_context = (
            _INTERNAL_MTLS_CONTEXT if base_url in _INTERNAL_MTLS_BASE_URLS else None
        )

        try:
            resp = urllib.request.urlopen(req, timeout=timeout, context=ssl_context)
        except urllib.error.HTTPError as e:
            # the backends' 4xx/5xx are meaningful (409, 422, 400, 403) — pass them through verbatim
            self._write_relay(e.code, e.headers, e.read())
            return
        except urllib.error.URLError as e:
            self.send_response(502)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(('{"title":"backend unreachable","detail":"%s — is it running on %s?"}'
                              % (str(e.reason), base_url)).encode())
            return

        ctype = resp.headers.get("Content-Type", "")
        if "text/event-stream" in ctype:
            self._stream_relay(resp)
        else:
            with resp:
                payload = resp.read()
            # Logs lens defence-in-depth: strip any non-structural / PII-named field from Loki's
            # response on the BFF before it reaches the browser (bd babelstone-f0ic.15.7).
            if self.path.startswith("/loki/"):
                payload = _loki_filter(payload)
            self._write_relay(resp.status, resp.headers, payload)

    def _write_relay(self, status, headers, payload):
        self.send_response(status)
        for k, v in headers.items():
            if k.lower() not in _HOP_BY_HOP:
                self.send_header(k, v)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        if payload:
            self.wfile.write(payload)

    def _stream_relay(self, resp):
        """Relay a Server-Sent Events response incrementally — flush each frame as it arrives
        rather than buffering to EOF (an SSE stream has no EOF until the saga terminates)."""
        self.send_response(resp.status)
        for k, v in resp.headers.items():
            if k.lower() not in _HOP_BY_HOP:
                self.send_header(k, v)
        self.end_headers()
        try:
            while True:
                chunk = resp.read1(4096)  # whatever is available now, no wait-to-fill
                if not chunk:
                    break                 # upstream closed (saga reached a terminal state)
                self.wfile.write(chunk)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass                          # the browser closed the EventSource — stop cleanly
        finally:
            resp.close()

    def _pg_handle(self):
        """The read-only Postgres Inspector routes (bd babelstone-f0ic.15.1/.15.5). Returns True if
        it handled the request. Every route reads STRUCTURAL allowlisted columns only (the PII
        firewall refuses payload/detail/BYTEA), read-only, loopback-only."""
        if not self.path.startswith("/pg/"):
            return False
        split = urllib.parse.urlsplit(self.path)
        path = split.path
        qs = urllib.parse.parse_qs(split.query)

        def q(name, default=None):
            vals = qs.get(name)
            return vals[0] if vals else default

        def qlimit(default=20, cap=200):
            try:
                return max(1, min(int(q("limit", default)), cap))
            except (TypeError, ValueError):
                raise PgError(400, "limit must be an integer")

        try:
            if path == "/pg/smoke":
                body = pg_smoke()
            elif path == "/pg/outbox/summary":
                # Outbox·Inbox lens (bd babelstone-f0ic.15.5): the real transactional-outbox drain —
                # counts by status, the VERBATIM ADR-IC-004 publish-lag SQL, and the recent tail.
                body = pg_outbox_summary(limit=qlimit())
            elif path == "/pg/inbox/tail":
                db = q("db", "engine")
                body = pg_inbox_tail(db=db, limit=qlimit())
            elif path == "/pg/command-dedup/tail":
                body = pg_command_dedup_tail(limit=qlimit())
            elif path == "/pg/saga-outbox/tail":
                body = pg_saga_outbox_tail(process_id=q("process_id"), limit=qlimit())
            elif path.startswith("/pg/processes/") and path.endswith("/transitions"):
                # Topology lens (bd babelstone-f0ic.15.3): the REAL path one saga took — its
                # ordered saga_transition legs + dispatched saga_outbox command legs. The handle is
                # the client-facing PROC-… reference (or the internal UUID).
                handle = path[len("/pg/processes/"):-len("/transitions")]
                body = pg_process_transitions(handle)
            elif path.startswith("/pg/streams/") and path.endswith("/events"):
                # Topology lens (bd babelstone-f0ic.15.3): one stream's real event chain —
                # structural envelope columns only (never events.payload).
                stream_id = path[len("/pg/streams/"):-len("/events")]
                body = pg_stream_events(stream_id, limit=qlimit(default=200))
            elif path.startswith("/pg/provenance/"):
                # Config-provenance strip (bd babelstone-f0ic.15.8): the instance's head pack pin
                # + its OCI coordinates from the durable pack_versions registry.
                body = pg_provenance(path[len("/pg/provenance/"):])
            else:
                raise PgError(404, "no such /pg route: %s" % path)
            payload = json.dumps(body, default=_json_default).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except PgError as e:
            payload = json.dumps({"title": "pg route refused", "detail": e.message}).encode()
            self.send_response(e.status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        return True

    def do_GET(self):
        if self._authgate():
            return
        if self.path.split("?", 1)[0] == "/topology/manifest":
            # The estate-derived Topology manifest (bd babelstone-f0ic.15.3): C4 L2 sources parsed
            # into nodes/edges + the live Redpanda topic list. Read-only, structural names only.
            payload = json.dumps(topology_manifest(), default=_json_default).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        if self._pg_handle():
            return
        if self._dispatch("GET"):
            return
        return super().do_GET()

    def do_HEAD(self):
        # HEAD must be gated too (bd zla1.10.8.*): without this override it would fall through to
        # SimpleHTTPRequestHandler.do_HEAD and leak the static UI tree's file existence/sizes with no
        # session check in oidc mode. Mirror do_GET — run the gate first.
        if self._authgate():
            return
        return super().do_HEAD()

    def _refuse_mutation(self):
        """The read-only proxy arms (the OCI /registry window; the Prometheus /prom, Loki /loki and
        Tempo /tempo query APIs; the Schema-Registry /sr metadata lookups): reads are the whole point,
        so a mutating verb through these arms is refused before any relay. This crucially keeps Loki's
        POST /loki/api/v1/push (log ingestion — same 3100 port as the query API) off the BFF, so the
        OTel Collector stays the single telemetry WRITE entry point (ADR-IC-007 §P1); likewise it keeps
        schema REGISTRATION off /sr (ADR-IC-002 §P3: registration is a CI gate, never a runtime op).
        /pandaproxy is deliberately NOT here — its Kafka-REST consumer-group dance needs POST/DELETE."""
        path = self.path.split("?", 1)[0]
        if path.startswith(("/registry/", "/prom/", "/loki/", "/tempo/", "/sr/")):
            self.send_error(405, "read-only arm: GET only")  # ASCII reason phrase (latin-1 status line)
            return True
        return False

    def do_POST(self):
        if self._authgate():
            return
        if self._refuse_mutation():
            return
        if self._dispatch("POST"):
            return
        self.send_error(405)

    def do_PUT(self):
        if self._authgate():
            return
        if self._refuse_mutation():
            return
        if self._dispatch("PUT"):
            return
        self.send_error(405)

    def do_DELETE(self):
        # The Topic·Avro lens deletes its pandaproxy consumer instance when it's done reading, so
        # the consumer-group dance cleans up after itself (no leaked consumers).
        if self._authgate():
            return
        if self._refuse_mutation():
            return
        if self._dispatch("DELETE"):
            return
        self.send_error(405)


def main():
    # Fail-safe + oidc gate init BEFORE we bind (bd babelstone-zla1.10.8.1 / .2). A refusal here
    # raises SystemExit, so the socket is never opened in an unsafe/misconfigured posture.
    _preflight()
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer((MC_BIND, PORT), Handler) as httpd:
        print("Babelstone Mission Control")
        print("  UI            http://localhost:%d" % PORT)
        if AUTH is not None:
            print("  auth          oidc (login gate ON — issuer %s, discovery OK)" % OIDC_ISSUER)
        print("  engine        %s  (proxied at /v1/*      — LIVE·engine)" % ENGINE_URL)
        print("  orchestrator  %s  (proxied at /api/v1/*  — LIVE·saga)" % ORCHESTRATOR_URL)
        print("  tempo         %s  (proxied at /tempo/*   — LIVE·engine real traces)" % TEMPO_URL)
        print("  agent         %s  (proxied at /agent/*   — LIVE·agent real Claude)" % AGENT_URL)
        print("  pandaproxy    %s  (proxied at /pandaproxy/* — Topic·Avro real records)" % PANDAPROXY_URL)
        print("  schema-reg    %s  (proxied at /sr/*      — Topic·Avro schema badge)" % SCHEMA_REGISTRY_URL)
        print("  loki          %s  (proxied at /loki/*   — Logs lens real logs, allowlisted)" % LOKI_URL)
        print("  registry      %s  (proxied at /registry/* — provenance strip, GET-only)" % REGISTRY_URL)
        print("  prometheus    %s  (proxied at /prom/*   — Metrics lens live SLIs, GET-only)" % PROM_URL)
        print("  postgres      /pg/* read-only Inspector lenses %s" % ("(ENABLED)" if PG_ENABLE else "(disabled — not bound to loopback)"))
        print("  mode          open the page, flip the toggle to LIVE·engine or LIVE·saga")
        print("  Ctrl-C to stop")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")


if __name__ == "__main__":
    main()
