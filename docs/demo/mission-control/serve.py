#!/usr/bin/env python3
"""
Mission Control dev server — serves the UI and reverse-proxies /v1/* to the engine.

Why this exists: the babelstone engine has no CORS, so a browser page on a different
origin can't call it directly. This tiny stdlib-only server puts the UI and the engine
behind ONE origin (http://localhost:9000): static files are served locally, and any
request under /v1/ is forwarded to the engine. The browser sees same-origin — no CORS,
no preflight, live mode "just works".

DEMO mode needs none of this — index.html is fully self-contained. You only need this
server for LIVE mode.

Usage:
    # 1. start the engine (see scripts/demo-mcp.sh) so it's listening on :8080
    # 2. then:
    python3 docs/demo/mission-control/serve.py
    # 3. open http://localhost:9000 and flip the Mode toggle to LIVE

Options (env vars):
    MC_PORT     port to serve the UI on            (default 9000)
    ENGINE_URL  base URL of the running engine      (default http://localhost:8080)
"""
import os
import sys
import http.server
import socketserver
import urllib.request
import urllib.error
from functools import partial

PORT = int(os.environ.get("MC_PORT", "9000"))
ENGINE_URL = os.environ.get("ENGINE_URL", "http://localhost:8080").rstrip("/")
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

    def _is_api(self):
        return self.path.startswith("/v1/")

    def _relay(self, method):
        url = ENGINE_URL + self.path
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else None

        req = urllib.request.Request(url, data=body, method=method)
        for k, v in self.headers.items():
            if k.lower() not in _HOP_BY_HOP:
                req.add_header(k, v)

        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                self._write_relay(resp.status, resp.headers, resp.read())
        except urllib.error.HTTPError as e:
            # the engine's 4xx/5xx are meaningful (409, 422, 400) — pass them through verbatim
            self._write_relay(e.code, e.headers, e.read())
        except urllib.error.URLError as e:
            self.send_response(502)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(('{"title":"engine unreachable","detail":"%s — is it running on %s?"}'
                              % (str(e.reason), ENGINE_URL)).encode())

    def _write_relay(self, status, headers, payload):
        self.send_response(status)
        for k, v in headers.items():
            if k.lower() not in _HOP_BY_HOP:
                self.send_header(k, v)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        if payload:
            self.wfile.write(payload)

    def do_GET(self):
        if self._is_api():
            return self._relay("GET")
        return super().do_GET()

    def do_POST(self):
        if self._is_api():
            return self._relay("POST")
        self.send_error(405)

    def do_PUT(self):
        if self._is_api():
            return self._relay("PUT")
        self.send_error(405)


def main():
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), Handler) as httpd:
        print("Babelstone Mission Control")
        print("  UI      http://localhost:%d" % PORT)
        print("  engine  %s  (proxied at /v1/*)" % ENGINE_URL)
        print("  mode    open the page, flip the toggle to LIVE")
        print("  Ctrl-C to stop")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")


if __name__ == "__main__":
    main()
