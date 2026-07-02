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
import http.server
import socketserver
import urllib.request
import urllib.error
import urllib.parse
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
        route = self._route()
        if route is not None:
            return self._relay("GET", route[0], route[1], route[2])
        return super().do_GET()

    def _refuse_mutation(self):
        """The GET-only arms (the OCI /registry window; Prometheus /prom): reads are the whole
        point — a mutating verb through these arms is refused before any relay."""
        path = self.path.split("?", 1)[0]
        if path.startswith("/registry/") or path.startswith("/prom/"):
            self.send_error(405, "read-only arm — GET only")
            return True
        return False

    def do_POST(self):
        if self._refuse_mutation():
            return
        route = self._route()
        if route is not None:
            return self._relay("POST", route[0], route[1], route[2])
        self.send_error(405)

    def do_PUT(self):
        if self._refuse_mutation():
            return
        route = self._route()
        if route is not None:
            return self._relay("PUT", route[0], route[1], route[2])
        self.send_error(405)

    def do_DELETE(self):
        # The Topic·Avro lens deletes its pandaproxy consumer instance when it's done reading, so
        # the consumer-group dance cleans up after itself (no leaked consumers).
        if self._refuse_mutation():
            return
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
