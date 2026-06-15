#!/usr/bin/env python3
"""engine-mcp-stub.py — the STUB ENGINE for the MCP-edge runtime-contract harness.

In plain English: when an agent calls the MCP tool ``get_deposit`` through Kong, the MCP
server turns that into a plain GET against the engine's read surface. This stub stands in for
that engine. It does two jobs: (1) it always answers ``GET /v1/deposits/{id}`` with a VALID,
hardcoded deposit position so the MCP tool's parse path never throws, and (2) it RECORDS the
headers it received on that GET so the harness can read them back from ``GET /echo/headers``
and prove that the gateway-attested ``X-Client-Id`` (the OAuth ``sub``) was forwarded all the
way through to the engine call (the A4 IDOR end-to-end observation, ADR-IC-010 §P3 /
Document 11).

It listens on PLAIN HTTP :8080 under the docker network alias ``engine`` — the hostname the
MCP server dials via ``BABELSTONE_ENGINE_URL=http://engine:8080``. This stub-to-MCP link is
internal and not the boundary under test (the mTLS boundary under test is Kong→mcp-server),
so plain HTTP here keeps the harness simple. No PII: the deposit position uses fictional
hardcoded values (no names, no real account numbers).

NOT for production. POC test double only.
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

# A valid DepositPosition the MCP server can deserialize into its pydantic model unchanged.
# Fictional values only (no PII). principal_cents is a round POC figure.
_POSITION = {
    "deposit_id": "d-echo-test",
    "sor": "engine",
    "principal_cents": 1_000_000,
    "tan_basis_points": 300,
    "rate_sheet_version_id": "pt-deposits-2026.1",
    "product_code": "dpz_pt_12m_juros_venc",
    "term_days": 365,
    "start_date": "2026-01-15",
    "maturity_date": "2027-01-15",
    "interest_variant": "AT_MATURITY",
    "auto_renewal_policy": "NONE",
    "payment_period_months": 0,
    "accrued_gross_interest_cents": 0,
    "withholding_to_date_cents": 0,
    "net_interest_cents": 0,
    "total_payout_cents": 0,
    "coupons_paid": 0,
    "lifecycle": "Active",
    "last_sequence": 0,
    "last_updated": "2026-01-15T00:00:00+00:00",
}

# The ONLY request headers this stub echoes back. EngineClient forwards exactly these three
# boundary headers (X-Client-Id, Idempotency-Key, If-Min-Sequence) and NEVER the bearer — so an
# allowlist (rather than echoing every received header) means that even if a future change wrongly
# forwarded Authorization, this throwaway double would not reflect a bearer over the harness's plain
# HTTP. Lower-cased for case-insensitive matching; the A4 assertion reads X-Client-Id back.
_ALLOWED_ECHO_HEADERS = frozenset({"x-client-id", "idempotency-key", "if-min-sequence"})


def _record_allowed(headers) -> dict[str, str]:
    """Keep only the allowlisted boundary headers, preserving the sender's original casing."""
    return {k: v for k, v in headers.items() if k.lower() in _ALLOWED_ECHO_HEADERS}


# Process-wide record of the headers the LAST GET /v1/deposits/{id} received. The harness
# reads it back via GET /echo/headers after firing the tools/call. ThreadingHTTPServer shares
# one handler-class namespace; a module-level dict is the simplest cross-request store.
_LAST_DEPOSIT_HEADERS: dict[str, str] = {}


class EngineHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, status: int, payload) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _drain(self) -> None:
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length:
            self.rfile.read(length)

    def do_GET(self) -> None:  # noqa: N802
        global _LAST_DEPOSIT_HEADERS
        path = urlparse(self.path).path

        # Read-back endpoint for the harness: what did the last engine read receive?
        if path == "/echo/headers":
            self._json(200, {"received_headers": dict(_LAST_DEPOSIT_HEADERS)})
            return

        # Liveness for the compose healthcheck.
        if path == "/v1/deposits/health":
            self._json(200, {"status": "ok"})
            return

        prefix = "/v1/deposits/"
        if path.startswith(prefix):
            # RECORD every header (so the harness can assert X-Client-Id was forwarded) and
            # always return a VALID position for any id — the MCP tool's pydantic parse must not
            # throw, whatever deposit id the harness chose.
            _LAST_DEPOSIT_HEADERS = _record_allowed(self.headers)
            instance = path[len(prefix):]
            self._json(200, {**_POSITION, "deposit_id": instance})
            return

        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        # The engine command surface (constitute/mature/interest). Record headers too and answer
        # with a minimal valid result so any write tool the harness exercises does not throw.
        global _LAST_DEPOSIT_HEADERS
        self._drain()
        _LAST_DEPOSIT_HEADERS = _record_allowed(self.headers)
        path = urlparse(self.path).path
        if path.endswith("/maturity") or path.endswith("/interest"):
            self._json(200, {**_POSITION})
        else:
            self._json(201, {"deposit_id": "d-echo-test", "status": "ACTIVE", "commit_sequence": 0})

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("engine-mcp-stub %s\n" % (fmt % args))


def main() -> None:
    port = 8080
    httpd = ThreadingHTTPServer(("0.0.0.0", port), EngineHandler)
    sys.stderr.write("engine-mcp-stub listening on http://0.0.0.0:%d\n" % port)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
