"""Release-gate fault proxy: a TLS reverse proxy for the dev host in which every request passes
through one handler. A faulted request is answered here and never reaches the real server; every
other request is forwarded on a fresh upstream connection. Each request, and when it finished, is
logged to /gate/hits.log. Standard library only."""

import http.client
import http.server
import json
import ssl
import sys
import threading
import time
import traceback

UPSTREAM = sys.argv[1]
FAULT_FILE = "/gate/fault.json"
HITS_FILE = "/gate/hits.log"
_log_lock = threading.Lock()


def fault():
    try:
        with open(FAULT_FILE) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def target(path):
    if "/verify/" in path:
        return "verify"
    if "amount=" in path:
        return "callback"
    return "metadata"


def record(kind, path, outcome):
    line = json.dumps({"t": time.time(), "target": kind, "path": path, "outcome": outcome})
    with _log_lock:
        try:
            with open(HITS_FILE, "a") as f:
                f.write(line + "\n")
        except OSError:
            traceback.print_exc()
    print(line, flush=True)


def forward(path):
    connection = http.client.HTTPSConnection(UPSTREAM, 443, timeout=30, context=ssl.create_default_context())
    try:
        connection.request("GET", path, headers={"Host": UPSTREAM, "Accept": "application/json"})
        response = connection.getresponse()
        headers = {k: v for k, v in response.getheaders() if k.lower() in ("content-type", "retry-after")}
        return response.status, response.read(), headers
    finally:
        connection.close()


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def reply(self, status, body, headers=None):
        body = body if isinstance(body, bytes) else body.encode()
        self.send_response(status)
        for key, value in (headers or {"Content-Type": "application/json"}).items():
            self.send_header(key, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        kind = target(self.path)
        active = fault()
        if active.get("target") in (kind, "*"):
            try:
                self.faulted(kind, active)
            except Exception:
                traceback.print_exc()
                record(kind, self.path, "proxy-error")
                self.reply(599, "gate proxy error")
            return

        record(kind, self.path, "pass")
        try:
            status, body, headers = forward(self.path)
        except Exception:
            traceback.print_exc()
            record(kind, self.path, "pass-done")
            self.reply(502, "upstream unreachable")
            return
        record(kind, self.path, "pass-done")
        self.reply(status, body, headers)

    def faulted(self, kind, active):
        if active.get("blackhole"):
            # An outage: hold the request unanswered until the fault is cleared, then refuse it.
            record(kind, self.path, "blackhole")
            deadline = time.time() + 300
            while fault().get("blackhole") and time.time() < deadline:
                time.sleep(0.5)
            self.reply(504, "")
            return
        if "delay" in active:
            record(kind, self.path, "delay")
            time.sleep(active["delay"])
            self.reply(504, "")
            return
        if "status" in active or "body" in active:
            record(kind, self.path, "replace")
            self.reply(active.get("status", 200), active.get("body", ""),
                       {"Content-Type": "application/json", **active.get("headers", {})})
            return
        # A rewrite needs the real answer; it is fetched here and only ever returned rewritten.
        status, body, headers = forward(self.path)
        merged = json.loads(body)
        merged.update(active.get("rewrite", {}))
        record(kind, self.path, "rewrite")
        self.reply(status, json.dumps(merged), headers)

    def log_message(self, format, *args):
        pass


server = http.server.ThreadingHTTPServer(("0.0.0.0", 8443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain("/certs/leaf.pem", "/certs/leaf.key")
server.socket = context.wrap_socket(server.socket, server_side=True)
print(f"fault proxy for {UPSTREAM} listening on 8443", flush=True)
server.serve_forever()
