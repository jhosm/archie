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
                  ADR-IC-006 §P4 / Document 05). This server also PLAYS THE GATEWAY: it
                  injects the X-Client-Id the orchestrator's edge authz expects (the claim
                  Kong would propagate, EdgeAuth). The browser's EventSource cannot set
                  headers, so injecting here is what lets the SSE stream's per-process
                  ownership check (which binds to the SAME client id as the start) pass.

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

  • /pg/*       → READ-ONLY Postgres (Inspector lenses, bd babelstone-f0ic.15.1): a guarded,
                  dev-only, 127.0.0.1-only window onto the engine + orchestrator databases.
                  Every query selects from a STRUCTURAL-column allowlist that forbids the
                  PII-bearing columns (payload / *detail* / BYTEA/ciphertext) — references only,
                  never PII. A non-local DSN is refused; the connection is opened read-only.
                  Requires psycopg (lazily imported) and the MC_*_DSN env vars.

DEMO mode needs none of this — index.html is fully self-contained. You only need this
server for LIVE·engine (start the engine, scripts/demo-mcp.sh) or LIVE·saga (start the
orchestrator + ACL stub, scripts/demo-saga.sh).

Usage:
    python3 docs/demo/mission-control/serve.py
    # open http://localhost:9000 and flip the Mode toggle to LIVE·engine or LIVE·saga

Options (env vars):
    MC_PORT           port to serve the UI on                  (default 9000)
    MC_BIND           interface to bind                        (default 127.0.0.1;
                      the container image sets 0.0.0.0 so the kube Service/probes
                      can reach it via the pod IP)
    ENGINE_URL        base URL of the engine (LIVE·engine)     (default http://localhost:8080)
    ORCHESTRATOR_URL  base URL of the orchestrator (LIVE·saga) (default http://localhost:8090)
    TEMPO_URL         base URL of Grafana Tempo's query API     (default http://localhost:3200)
                      (LIVE·engine Telemetry tab → /tempo/api/traces/{id})
    DEMO_CLIENT_ID    the gateway-attested caller injected on  (default CLI-DEMO-0001)
                      /api/v1/* (an OPAQUE reference, never PII)
    AGENT_URL         base URL of the real-Claude agent host    (default http://localhost:8091)
                      (LIVE·agent mode, /agent/* → POST /agent/stream)
    PANDAPROXY_URL    base URL of Redpanda's HTTP Proxy         (default http://localhost:18082)
                      (Topic·Avro lens, /pandaproxy/* → Kafka REST)
    SCHEMA_REGISTRY_URL  base URL of the Schema Registry        (default http://localhost:18081)
                      (Topic·Avro lens schema badge, /sr/*)
    MC_ENGINE_DSN     read-only DSN for the engine DB           (default postgresql://babelstone:
                      babelstone@127.0.0.1:5432/babelstone) — /pg/* Inspector lenses
    MC_ORCH_DSN       read-only DSN for the orchestrator DB     (default …/babelstone_orchestrator)
    MC_PG_ENABLE      force-enable /pg/* when NOT bound to       (default off; auto-on when MC_BIND
                      loopback (dev only — keep off in a pod)   is 127.0.0.1/::1/localhost)
"""
import os
import sys
import http.server
import socketserver
import urllib.request
import urllib.error
import json

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
DEMO_CLIENT_ID = os.environ.get("DEMO_CLIENT_ID", "CLI-DEMO-0001")
ROOT = os.path.dirname(os.path.abspath(__file__))

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
    ("engine", "command_dedup"): {"command_id", "stream_id", "commit_sequence", "created_at"},
    ("orchestrator", "saga_state"): {
        "process_id", "saga_type", "state", "version", "correlation_id", "created_at", "updated_at",
    },
    ("orchestrator", "saga_transition"): {
        "id", "process_id", "from_state", "to_state", "event_type", "message_id", "note", "occurred_at",
    },
    ("orchestrator", "saga_outbox"): {
        "seq", "message_id", "process_id", "command_type", "causation_id", "correlation_id",
        "status", "created_at", "published_at",
    },
    ("orchestrator", "inbox"): {"message_id", "source_topic", "processed_at", "result_summary"},
}

_PG_DSN = {"engine": ENGINE_DSN, "orchestrator": ORCH_DSN}


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
    """Refuse any DSN that is not loopback. A unix-socket DSN (host begins with '/') or an empty
    host (default local socket) is local; everything else is rejected — the /pg/* window must
    never reach off-box."""
    host = _dsn_host(dsn)
    if host == "" or host.startswith("/"):
        return  # local unix socket / default
    if host not in ("127.0.0.1", "::1", "localhost"):
        raise PgError(403, "non-local DSN refused (%r) — /pg/* is dev-only and 127.0.0.1-only" % host)


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


def pg_select(db, table, columns, order=None, descending=True, limit=50):
    """Open a READ-ONLY connection to the chosen DB and SELECT the allowlisted structural columns.
    Identifiers are emitted only from the static allowlist (and quoted via psycopg.sql.Identifier),
    so no caller string ever becomes SQL."""
    if not PG_ENABLE:
        raise PgError(403, "/pg/* is disabled (serve.py is not bound to loopback; set MC_PG_ENABLE=1 to force)")
    if db not in _PG_DSN:
        raise PgError(404, "unknown db: %s (expected 'engine' or 'orchestrator')" % db)
    dsn = _PG_DSN[db]
    _assert_local_dsn(dsn)
    cols = _pg_columns(db, table, columns)
    if order is not None:
        _pg_columns(db, table, [order])  # the ORDER BY column must be allowlisted too
    psycopg = _require_psycopg()
    from psycopg import sql

    parts = [sql.SQL("SELECT "), sql.SQL(", ").join(sql.Identifier(c) for c in cols),
             sql.SQL(" FROM "), sql.Identifier(table)]
    if order is not None:
        parts += [sql.SQL(" ORDER BY "), sql.Identifier(order),
                  sql.SQL(" DESC") if descending else sql.SQL(" ASC")]
    parts += [sql.SQL(" LIMIT "), sql.Literal(int(limit))]
    query = sql.Composed(parts)

    try:
        with psycopg.connect(dsn, autocommit=True, connect_timeout=3) as conn:
            with conn.cursor() as cur:
                # Belt-and-braces: pin the SESSION read-only at the server so ANY write (even a
                # mistaken future query) is rejected by Postgres itself, not just by our SELECT-only
                # code path. autocommit=True makes this SET persist for the connection's lifetime.
                cur.execute("SET default_transaction_read_only = on")
                cur.execute(query)
                names = [d.name for d in cur.description]
                return [dict(zip(names, row)) for row in cur.fetchall()]
    except PgError:
        raise
    except Exception as e:  # psycopg.OperationalError etc. — surface as a clean 502
        raise PgError(502, "Postgres query failed against %s DB: %s" % (db, e))


def pg_smoke():
    """A harmless structural smoke read: the most recent engine outbox rows, STRUCTURAL columns
    only (no payload). Proves the read-only cursor + allowlist + guard all work end to end."""
    rows = pg_select("engine", "outbox",
                     ["event_id", "aggregate_type", "event_type", "status", "created_at", "published_at"],
                     order="created_at", descending=True, limit=10)
    return {"db": "engine", "table": "outbox", "count": len(rows), "rows": rows}


def _json_default(o):
    """Serialise UUID / datetime / Decimal etc. as strings for the JSON response."""
    return str(o)


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    # quieter logs
    def log_message(self, fmt, *args):
        sys.stderr.write("  %s\n" % (fmt % args))

    def _route(self):
        """Map the request path to a backend. Returns (base_url, injected_headers, upstream_path)
        or None for a static file served locally."""
        if self.path.startswith("/api/v1/"):
            # The orchestrator edge. This server stands in for Kong: it injects the
            # gateway-attested caller id the edge authz binds ownership to (EdgeAuth).
            return ORCHESTRATOR_URL, {"X-Client-Id": DEMO_CLIENT_ID}, self.path
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
        return None

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

        try:
            resp = urllib.request.urlopen(req, timeout=timeout)
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
                self._write_relay(resp.status, resp.headers, resp.read())

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
        """The read-only Postgres Inspector routes (bd babelstone-f0ic.15.1). Returns True if it
        handled the request. Today: /pg/smoke (the structural smoke read). The lens-specific
        select routes (Outbox·Inbox, provenance, topology) build on pg_select() in later issues."""
        if not self.path.startswith("/pg/"):
            return False
        path = self.path.split("?", 1)[0]
        try:
            if path == "/pg/smoke":
                body = pg_smoke()
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
        if self._pg_handle():
            return
        route = self._route()
        if route is not None:
            return self._relay("GET", route[0], route[1], route[2])
        return super().do_GET()

    def do_POST(self):
        route = self._route()
        if route is not None:
            return self._relay("POST", route[0], route[1], route[2])
        self.send_error(405)

    def do_PUT(self):
        route = self._route()
        if route is not None:
            return self._relay("PUT", route[0], route[1], route[2])
        self.send_error(405)

    def do_DELETE(self):
        # The Topic·Avro lens deletes its pandaproxy consumer instance when it's done reading, so
        # the consumer-group dance cleans up after itself (no leaked consumers).
        route = self._route()
        if route is not None:
            return self._relay("DELETE", route[0], route[1], route[2])
        self.send_error(405)


def main():
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer((MC_BIND, PORT), Handler) as httpd:
        print("Babelstone Mission Control")
        print("  UI            http://localhost:%d" % PORT)
        print("  engine        %s  (proxied at /v1/*      — LIVE·engine)" % ENGINE_URL)
        print("  orchestrator  %s  (proxied at /api/v1/*  — LIVE·saga)" % ORCHESTRATOR_URL)
        print("  tempo         %s  (proxied at /tempo/*   — LIVE·engine real traces)" % TEMPO_URL)
        print("  agent         %s  (proxied at /agent/*   — LIVE·agent real Claude)" % AGENT_URL)
        print("  pandaproxy    %s  (proxied at /pandaproxy/* — Topic·Avro real records)" % PANDAPROXY_URL)
        print("  schema-reg    %s  (proxied at /sr/*      — Topic·Avro schema badge)" % SCHEMA_REGISTRY_URL)
        print("  postgres      /pg/* read-only Inspector lenses %s" % ("(ENABLED)" if PG_ENABLE else "(disabled — not bound to loopback)"))
        print("  mode          open the page, flip the toggle to LIVE·engine or LIVE·saga")
        print("  Ctrl-C to stop")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")


if __name__ == "__main__":
    main()
