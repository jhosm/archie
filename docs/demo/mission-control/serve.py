#!/usr/bin/env python3
"""
Mission Control dev server — serves the UI and reverse-proxies the backend APIs.

Why this exists: the babelstone services have no CORS, so a browser page on a different
origin can't call them directly. This tiny stdlib-only server puts the UI and the backends
behind ONE origin (http://localhost:9000), so the browser sees same-origin — no CORS, no
preflight, live mode "just works". It proxies two backends:

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
"""
import os
import sys
import http.server
import socketserver
import urllib.request
import urllib.error

PORT = int(os.environ.get("MC_PORT", "9000"))
# Bind interface. Default 127.0.0.1 keeps `python3 serve.py` on a laptop localhost-only
# (no LAN exposure); the container image overrides this to 0.0.0.0 (ENV in the Dockerfile)
# so the kube Service and readiness/liveness probes can reach it via the pod IP.
MC_BIND = os.environ.get("MC_BIND", "127.0.0.1")
ENGINE_URL = os.environ.get("ENGINE_URL", "http://localhost:8080").rstrip("/")
ORCHESTRATOR_URL = os.environ.get("ORCHESTRATOR_URL", "http://localhost:8090").rstrip("/")
TEMPO_URL = os.environ.get("TEMPO_URL", "http://localhost:3200").rstrip("/")
AGENT_URL = os.environ.get("AGENT_URL", "http://localhost:8091").rstrip("/")
DEMO_CLIENT_ID = os.environ.get("DEMO_CLIENT_ID", "CLI-DEMO-0001")
ROOT = os.path.dirname(os.path.abspath(__file__))

# headers we must not blindly copy when relaying
_HOP_BY_HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailers", "transfer-encoding", "upgrade", "content-length", "host"}


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

    def do_GET(self):
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


def main():
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer((MC_BIND, PORT), Handler) as httpd:
        print("Babelstone Mission Control")
        print("  UI            http://localhost:%d" % PORT)
        print("  engine        %s  (proxied at /v1/*      — LIVE·engine)" % ENGINE_URL)
        print("  orchestrator  %s  (proxied at /api/v1/*  — LIVE·saga)" % ORCHESTRATOR_URL)
        print("  tempo         %s  (proxied at /tempo/*   — LIVE·engine real traces)" % TEMPO_URL)
        print("  agent         %s  (proxied at /agent/*   — LIVE·agent real Claude)" % AGENT_URL)
        print("  mode          open the page, flip the toggle to LIVE·engine or LIVE·saga")
        print("  Ctrl-C to stop")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")


if __name__ == "__main__":
    main()
