import importlib.util
import json
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from urllib.parse import urlsplit


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "run_target_acceptance", ROOT / "tools" / "run-target-acceptance.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("run-target-acceptance.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


TOKEN = "target-acceptance-secret"
PAIRING = "target-pairing-secret"


class FakeTargetHandler(BaseHTTPRequestHandler):
    paired = False
    media_sources = {}

    def log_message(self, format, *args):  # noqa: A002 - BaseHTTPRequestHandler API
        return

    def _write(self, status, payload, content_type="application/json"):
        if isinstance(payload, str):
            body = payload.encode("utf-8")
        else:
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _authorized(self):
        return self.headers.get("Authorization") == f"Bearer {TOKEN}"

    def do_GET(self):  # noqa: N802 - BaseHTTPRequestHandler API
        path = urlsplit(self.path).path
        if path == "/health/live":
            return self._write(200, {"status": "live"})
        if path == "/health/ready":
            return self._write(200, {"status": "ready"})
        if path == "/":
            return self._write(200, "<title>MultiFunPlayer</title>", "text/html")
        if not self._authorized():
            return self._write(401, {"title": "Authentication required"})
        if path == "/api/v1/settings":
            return self._write(200, {"configRoot": "/config", "keysRoot": "/keys",
                                      "scriptsRoot": "/scripts", "logsRoot": "/logs"})
        if path == "/api/v1/capabilities":
            return self._write(200, {"apiVersion": "v1", "commands": ["load"]})
        if path == "/api/v1/adapters":
            return self._write(200, {"apiVersion": "v1", "schemaVersion": "1.0",
                                     "adapters": [{"id": "output.tcp"}],
                                     "transports": [{"id": "tcp"}]})
        if path == "/api/v1/devices/serial":
            return self._write(200, [])
        if path == "/api/v1/media-sources":
            return self._write(200, list(FakeTargetHandler.media_sources.values()))
        if path == "/api/v1/logs":
            return self._write(200, {"items": [{"message": "safe"}]})
        return self._write(404, {"title": "Not found"})

    def do_POST(self):  # noqa: N802 - BaseHTTPRequestHandler API
        path = urlsplit(self.path).path
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        if path == "/api/v1/auth/pair":
            FakeTargetHandler.paired = True
            return self._write(200, {"status": "paired"})
        if not self._authorized():
            return self._write(401, {"title": "Authentication required"})
        if path == "/api/v1/scripts":
            return self._write(201, {"id": "target-acceptance.funscript"})
        if path == "/api/v1/media-sources":
            payload = json.loads(body.decode("utf-8"))
            source = {key: payload[key] for key in
                      ("id", "name", "kind", "endpoint", "enabled", "autoConnect", "deviceId")}
            source.update({"integration": "direct", "implementationStatus": "ready",
                           "connected": False, "lastError": None})
            FakeTargetHandler.media_sources[payload["id"]] = source
            return self._write(201, source)
        if path == "/api/v1/scripts/load":
            return self._write(200, {"axes": {"L0": 0.5, "R2": 0.5}})
        if path in {"/api/v1/player/seek", "/api/v1/player/play", "/api/v1/player/stop"}:
            return self._write(200, {"status": "ok"})
        return self._write(404, {"title": "Not found"})

    def do_PUT(self):  # noqa: N802 - BaseHTTPRequestHandler API
        path = urlsplit(self.path).path
        if not self._authorized():
            return self._write(401, {"title": "Authentication required"})
        prefix = "/api/v1/media-sources/"
        if not path.startswith(prefix):
            return self._write(404, {"title": "Not found"})
        media_id = path[len(prefix):]
        if media_id not in FakeTargetHandler.media_sources:
            return self._write(404, {"title": "Not found"})
        length = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(length).decode("utf-8"))
        source = FakeTargetHandler.media_sources[media_id]
        source.update({key: payload[key] for key in
                       ("id", "name", "kind", "endpoint", "enabled", "autoConnect", "deviceId")})
        return self._write(200, source)

    def do_DELETE(self):  # noqa: N802 - BaseHTTPRequestHandler API
        path = urlsplit(self.path).path
        if not self._authorized():
            return self._write(401, {"title": "Authentication required"})
        prefix = "/api/v1/media-sources/"
        if path.startswith(prefix) and path[len(prefix):] in FakeTargetHandler.media_sources:
            del FakeTargetHandler.media_sources[path[len(prefix):]]
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        return self._write(404, {"title": "Not found"})


class FakeTarget:
    def __enter__(self):
        FakeTargetHandler.paired = False
        FakeTargetHandler.media_sources = {}
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeTargetHandler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return f"http://127.0.0.1:{self.server.server_port}"

    def __exit__(self, exc_type, exc_value, traceback):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


class TargetAcceptanceTests(unittest.TestCase):
    def test_full_http_acceptance_is_secret_free(self):
        with FakeTarget() as base_url:
            report = MODULE.run_acceptance(base_url, TOKEN, None, 2, "target-acceptance.funscript")
        self.assertEqual("pass", report["result"])
        self.assertNotIn(TOKEN, json.dumps(report))
        self.assertTrue(all(item["status"] == "pass" for item in report["checks"]))

    def test_pairing_and_read_only_mode_are_reported(self):
        with FakeTarget() as base_url:
            report = MODULE.run_acceptance(base_url, TOKEN, PAIRING, 2,
                                           "target-acceptance.funscript", no_playback=True)
        self.assertEqual("pass", report["result"])
        self.assertTrue(FakeTargetHandler.paired)
        skipped = next(item for item in report["checks"] if item["id"] == "script-playback")
        self.assertEqual("skipped", skipped["status"])

    def test_missing_token_fails_protected_checks_without_leaking_values(self):
        with FakeTarget() as base_url:
            report = MODULE.run_acceptance(base_url, None, None, 2,
                                           "target-acceptance.funscript", no_playback=True)
        self.assertEqual("fail", report["result"])
        self.assertIn("authentication-input", {item["id"] for item in report["checks"]})
        self.assertNotIn(TOKEN, json.dumps(report))

    def test_redirects_are_rejected_before_a_token_can_leave_the_target(self):
        client = MODULE.TargetClient("http://127.0.0.1:8080", TOKEN, 2)
        with patch.object(client.opener, "open",
                          side_effect=MODULE.AcceptanceError("target returned an unexpected redirect")):
            with self.assertRaises(MODULE.AcceptanceError):
                client.request("GET", "/api/v1/status")


if __name__ == "__main__":
    unittest.main()
