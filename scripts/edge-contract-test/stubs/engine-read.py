#!/usr/bin/env python3
"""engine-read.py — the STUB ENGINE READ SURFACE for the edge runtime-contract harness.

In plain English: during coexistence the Kong SoR pre-function resolves which system owns a
given deposit instance by calling the engine's read surface, GET /v1/deposits/{id}, and
reading the `sor` field off the JSON body (ADR-PC-018 §1/§2, coexistence §6.2). It proxies
to the engine ONLY when sor == "engine"; every other outcome (legacy, 404, transport error,
non-200, non-table body) must fail closed 503 SOR_UNRESOLVED. This stub serves that read
surface and, by instance id, drives EACH of those branches deterministically so the harness
can prove the fail-closed-on-every-error-path guarantee end to end.

It also doubles as the engine upstream the SoR-routed op proxies to on the engine-SoR path:
when Kong resolves sor == engine and falls through to proxy POST .../operations to the engine
service, this stub answers that POST 200 — that 200 is the test's signal that the op REACHED
the engine (was not fail-closed).

It listens on TLS :8080 under the docker network alias `engine`, because the real kong.yml
hardcodes `https://engine:8080/v1/deposits/<id>` in the SoR Lua and addresses the engine
upstream as `https://engine:8080` (ADR-IC-006 §P5, ADR-PC-018). Using the real hostname lets
the harness run the REAL kong.yml byte-for-byte. ssl_verify is off on the Lua call and the
service (tls_verify:false POC posture), so a throwaway self-signed cert is fine.

Instance-id contract (the harness encodes the case it wants in the id):
    sor-engine   -> 200 {"sor":"engine", ...}   (resolves to engine; proxy proceeds)
    sor-legacy   -> 200 {"sor":"legacy", ...}   (legacy SoR; fail closed 503)
    sor-missing  -> 200 {...no sor field...}    (malformed/missing column; fail closed 503)
    sor-notable  -> 200 with a JSON ARRAY body  (non-table body; fail closed 503)
    sor-500      -> 500                          (engine error / non-200; fail closed 503)
    <anything else> (e.g. sor-404 / unknown)     -> 404 (unknown instance / projection lag; 503)

NOT for production. POC test double only.
"""
import json
import ssl
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse


class EngineHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, status: int, payload) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _raw(self, status: int, raw: bytes, ctype: str = "application/json") -> None:
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _drain(self) -> None:
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length:
            self.rfile.read(length)

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        # The SoR resolution read: GET /v1/deposits/{id}. Map the id to the case it encodes.
        prefix = "/v1/deposits/"
        if not path.startswith(prefix):
            self._json(404, {"error": "not found"})
            return
        instance = path[len(prefix):]

        if instance == "sor-engine":
            self._json(200, {"id": instance, "sor": "engine", "status": "ACTIVE"})
        elif instance == "sor-legacy":
            self._json(200, {"id": instance, "sor": "legacy", "status": "ACTIVE"})
        elif instance == "sor-missing":
            # 200 but no `sor` column — the SoR fn must fail closed (body.sor ~= "engine").
            self._json(200, {"id": instance, "status": "ACTIVE"})
        elif instance == "sor-notable":
            # 200 with a NON-TABLE (JSON array) body — cjson.decode yields a non-table.
            self._raw(200, b'["not","an","object"]')
        elif instance == "sor-500":
            # engine error / non-200 read path.
            self._json(500, {"error": "internal"})
        else:
            # unknown instance / projection lag — the 404 fail-closed branch.
            self._json(404, {"error": "unknown instance"})

    def do_POST(self) -> None:  # noqa: N802
        # The engine-SoR proxy target: once Kong resolves sor == engine it proxies the
        # .../operations POST here. A 200 is the harness's "reached the engine" signal.
        self._drain()
        received = {k: v for k, v in self.headers.items()}
        self._json(
            200,
            {
                "stub": "engine-read",
                "method": self.command,
                "path": self.path,
                "received_headers": received,
                "result": "engine-op-accepted",
            },
        )

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("engine-read %s\n" % (fmt % args))


def main() -> None:
    port = 8080
    certfile = "/certs/stub.crt"
    keyfile = "/certs/stub.key"
    httpd = ThreadingHTTPServer(("0.0.0.0", port), EngineHandler)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(certfile=certfile, keyfile=keyfile)
    httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True)
    sys.stderr.write("engine-read listening on https://0.0.0.0:%d\n" % port)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
