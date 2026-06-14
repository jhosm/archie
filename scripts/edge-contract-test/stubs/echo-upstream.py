#!/usr/bin/env python3
"""echo-upstream.py — the STUB ORCHESTRATOR upstream for the edge runtime-contract harness.

In plain English: Kong forwards a passed-through request to "the orchestrator". This stub
stands in for it. Its one job is to ECHO every request header it received back in the JSON
response body, so the test can assert exactly what header value Kong forwarded upstream —
crucially the gateway-attested X-Client-Id (ADR-IC-006 §P4). A plain 200/202 terminator
(e.g. request-termination) is not enough: we must SEE the forwarded headers to prove the
IDOR fix (X-Client-Id is overwritten from the JWT sub, never the client-supplied value).

It listens on TLS :8080 because the real kong.yml addresses its upstreams as
`https://orchestrator:8080` with mTLS / tls_verify:false (ADR-IC-006 §P5 Boundary 2). The
harness runs this container under the docker network alias `orchestrator`, so the REAL
kong.yml is used byte-for-byte — no Lua/route templating. The cert is a throwaway
self-signed cert generated at container start; Kong skips verification (tls_verify:false,
the POC posture in kong.yml until the internal CA bundle is mounted).

NOT for production. POC test double only.
"""
import json
import ssl
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class EchoHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _echo(self, status: int = 202) -> None:
        # Collect every received header. The test reads back the value Kong actually
        # forwarded (e.g. X-Client-Id) — proving the gateway overwrote the client value.
        received = {k: v for k, v in self.headers.items()}
        body = json.dumps(
            {
                "stub": "echo-upstream",
                "method": self.command,
                "path": self.path,
                "received_headers": received,
            }
        ).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # Drain any request body so the connection stays clean (constitute POSTs carry one).
    def _drain(self) -> None:
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length:
            self.rfile.read(length)

    def do_GET(self) -> None:  # noqa: N802
        self._echo(200)

    def do_POST(self) -> None:  # noqa: N802
        self._drain()
        # 202 Accepted mirrors the orchestrator's constitute response shape; the harness
        # only asserts a 2xx pass-through, not the body.
        self._echo(202)

    def log_message(self, fmt: str, *args) -> None:  # quiet, structured to stderr
        sys.stderr.write("echo-upstream %s\n" % (fmt % args))


def main() -> None:
    port = 8080
    certfile = "/certs/stub.crt"
    keyfile = "/certs/stub.key"
    httpd = ThreadingHTTPServer(("0.0.0.0", port), EchoHandler)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(certfile=certfile, keyfile=keyfile)
    httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True)
    sys.stderr.write("echo-upstream listening on https://0.0.0.0:%d\n" % port)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
