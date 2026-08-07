#!/usr/bin/env python3
"""Run black-box HTTP acceptance checks against a deployed MultiFunPlayer host.

The harness intentionally verifies only behavior observable through the Web/API
boundary. It does not claim serial, Bluetooth, fnOS firmware, or physical
output coverage; those checks require a target-specific procedure and evidence.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from time import monotonic
from typing import Any, Callable
from urllib.parse import urlsplit, urlunsplit


MAX_RESPONSE_BYTES = 2 * 1024 * 1024


class AcceptanceError(RuntimeError):
    """A target check failed with a user-actionable but secret-free reason."""


@dataclass(frozen=True)
class HttpResponse:
    status: int
    body: bytes

    def json(self) -> Any:
        try:
            return json.loads(self.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise AcceptanceError("target returned invalid JSON") from error


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, new_url):
        raise AcceptanceError("target returned an unexpected redirect")


class TargetClient:
    def __init__(self, base_url: str, token: str | None, timeout: float) -> None:
        parsed = urlsplit(base_url)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError("base URL must use http or https and include a host")
        if parsed.query or parsed.fragment or parsed.username or parsed.password:
            raise ValueError("base URL must not contain credentials, query, or fragment")
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout
        self.opener = urllib.request.build_opener(_NoRedirectHandler())

    def request(self, method: str, path: str, payload: Any = None,
                authenticated: bool = True) -> HttpResponse:
        if not path.startswith("/"):
            raise ValueError("API paths must start with '/'")
        body = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if authenticated:
            if not self.token:
                raise AcceptanceError("an authentication token is required for this check")
            headers["Authorization"] = f"Bearer {self.token}"
        request = urllib.request.Request(
            f"{self.base_url}{path}", data=body, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                body = response.read(MAX_RESPONSE_BYTES + 1)
                if len(body) > MAX_RESPONSE_BYTES:
                    raise AcceptanceError("target response exceeds the 2 MiB acceptance limit")
                return HttpResponse(response.status, body)
        except urllib.error.HTTPError as error:
            body = error.read(MAX_RESPONSE_BYTES + 1)
            if len(body) > MAX_RESPONSE_BYTES:
                raise AcceptanceError("target error response exceeds the 2 MiB acceptance limit") from error
            return HttpResponse(error.code, body)
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise AcceptanceError(f"target request failed: {type(error).__name__}") from error


def safe_url(value: str) -> str:
    parsed = urlsplit(value)
    return urlunsplit((parsed.scheme, parsed.netloc, parsed.path.rstrip("/"), "", ""))


def require_status(response: HttpResponse, *statuses: int) -> None:
    if response.status not in statuses:
        raise AcceptanceError(f"unexpected HTTP status {response.status}")


def require_object(response: HttpResponse) -> dict[str, Any]:
    value = response.json()
    if not isinstance(value, dict):
        raise AcceptanceError("target returned a JSON value instead of an object")
    return value


def run_acceptance(base_url: str, token: str | None, pairing_token: str | None,
                   timeout: float, script_id: str, no_playback: bool = False) -> dict[str, Any]:
    client = TargetClient(base_url, token, timeout)
    report: dict[str, Any] = {
        "schemaVersion": 1,
        "baseUrl": safe_url(base_url),
        "observedAtUtc": datetime.now(timezone.utc).isoformat(),
        "checks": [],
        "result": "pass",
    }

    def check(name: str, action: Callable[[], str]) -> bool:
        started = monotonic()
        item: dict[str, Any] = {"id": name}
        try:
            item["status"] = "pass"
            item["detail"] = action()
        except (AcceptanceError, ValueError) as error:
            item["status"] = "fail"
            item["detail"] = str(error)
            report["result"] = "fail"
        except Exception as error:  # pragma: no cover - defensive target boundary
            item["status"] = "fail"
            item["detail"] = f"unexpected {type(error).__name__}"
            report["result"] = "fail"
        item["durationSeconds"] = round(monotonic() - started, 3)
        report["checks"].append(item)
        return item["status"] == "pass"

    if pairing_token:
        check("pairing", lambda: _pair(client, pairing_token, token))
    elif not token:
        check("authentication-input", lambda: (_raise("provide --token or --pairing-token with --token")))

    check("health-live", lambda: _health_live(client))
    check("health-ready", lambda: _health_ready(client))
    check("spa-fallback", lambda: _spa(client))
    check("authentication-enforced", lambda: _authentication_enforced(client))
    check("settings-redaction", lambda: _settings(client, token))
    check("capabilities", lambda: _capabilities(client))
    check("adapter-inventory", lambda: _adapters(client))
    check("serial-discovery", lambda: _serial(client))

    playback_started = False
    try:
        if no_playback:
            report["checks"].append({"id": "script-playback", "status": "skipped",
                                     "detail": "disabled by --no-playback", "durationSeconds": 0})
        else:
            check("script-upload", lambda: _upload(client, script_id))
            check("script-load", lambda: _load(client, script_id))
            check("player-seek", lambda: _seek(client))
            playback_started = check("player-play", lambda: _play(client))
            stopped = check("player-stop", lambda: _stop(client))
            playback_started = playback_started and not stopped
    finally:
        if playback_started:
            try:
                _stop(client)
            except AcceptanceError:
                pass

    check("logs-redaction", lambda: _logs(client, token))
    return report


def _raise(message: str) -> str:
    raise AcceptanceError(message)


def _pair(client: TargetClient, pairing_token: str, token: str | None) -> str:
    if not token:
        raise AcceptanceError("--pairing-token requires --token for the resulting bearer token")
    response = client.request("POST", "/api/v1/auth/pair",
                              {"pairingToken": pairing_token, "authToken": token}, authenticated=False)
    require_status(response, 200)
    return "one-time pairing accepted"


def _health_live(client: TargetClient) -> str:
    response = client.request("GET", "/health/live", authenticated=False)
    payload = require_object(response)
    require_status(response, 200)
    if payload.get("status") != "live":
        raise AcceptanceError("liveness response did not report live")
    return "live"


def _health_ready(client: TargetClient) -> str:
    response = client.request("GET", "/health/ready", authenticated=False)
    require_status(response, 200)
    payload = require_object(response)
    if payload.get("status") != "ready":
        raise AcceptanceError("readiness response did not report ready")
    return "ready"


def _spa(client: TargetClient) -> str:
    response = client.request("GET", "/", authenticated=False)
    require_status(response, 200)
    if b"MultiFunPlayer" not in response.body:
        raise AcceptanceError("SPA fallback did not contain the application marker")
    return "SPA served"


def _authentication_enforced(client: TargetClient) -> str:
    response = client.request("GET", "/api/v1/status", authenticated=False)
    require_status(response, 401)
    return "protected API rejects unauthenticated requests"


def _settings(client: TargetClient, token: str | None) -> str:
    response = client.request("GET", "/api/v1/settings")
    payload = require_object(response)
    require_status(response, 200)
    if any(key in payload for key in ("authToken", "pairingToken", "secret", "apiKey")):
        raise AcceptanceError("settings response exposed a secret field")
    if not all(isinstance(payload.get(key), str) for key in ("configRoot", "keysRoot", "scriptsRoot", "logsRoot")):
        raise AcceptanceError("settings response omitted a persistent root")
    if token and token.encode("utf-8") in response.body:
        raise AcceptanceError("settings response exposed the bearer token")
    return "non-secret settings and four persistent roots returned"


def _capabilities(client: TargetClient) -> str:
    response = client.request("GET", "/api/v1/capabilities")
    payload = require_object(response)
    require_status(response, 200)
    if payload.get("apiVersion") != "v1" or not isinstance(payload.get("commands"), list):
        raise AcceptanceError("capability contract is incomplete")
    return "v1 capability contract returned"


def _adapters(client: TargetClient) -> str:
    response = client.request("GET", "/api/v1/adapters")
    payload = require_object(response)
    require_status(response, 200)
    adapters = payload.get("adapters")
    transports = payload.get("transports")
    if not isinstance(adapters, list) or not isinstance(transports, list):
        raise AcceptanceError("adapter inventory is missing adapters or transports")
    return f"{len(adapters)} adapters and {len(transports)} transports returned"


def _serial(client: TargetClient) -> str:
    response = client.request("GET", "/api/v1/devices/serial")
    payload = response.json()
    require_status(response, 200)
    if not isinstance(payload, list):
        raise AcceptanceError("serial discovery response is not an array")
    return f"{len(payload)} stable serial entries returned"


def _upload(client: TargetClient, script_id: str) -> str:
    content = json.dumps({
        "actions": [{"at": 0, "pos": 25}, {"at": 1000, "pos": 75}],
        "axes": [{"id": "R2", "actions": [{"at": 0, "pos": 10}, {"at": 1000, "pos": 90}]}],
    }, separators=(",", ":"))
    response = client.request("POST", "/api/v1/scripts",
                              {"id": script_id, "content": content, "overwrite": True})
    require_status(response, 201)
    return "script uploaded to the configured server-side root"


def _load(client: TargetClient, script_id: str) -> str:
    response = client.request("POST", "/api/v1/scripts/load", {"id": script_id})
    payload = require_object(response)
    require_status(response, 200)
    axes = payload.get("axes")
    if not isinstance(axes, dict) or not {"L0", "R2"}.issubset(axes):
        raise AcceptanceError("loaded script did not produce the expected L0 and R2 axes")
    return "multi-axis script loaded with L0 and R2"


def _seek(client: TargetClient) -> str:
    response = client.request("POST", "/api/v1/player/seek", {"positionSeconds": 0})
    require_status(response, 200)
    return "seek accepted"


def _play(client: TargetClient) -> str:
    response = client.request("POST", "/api/v1/player/play")
    require_status(response, 200)
    return "play accepted"


def _stop(client: TargetClient) -> str:
    response = client.request("POST", "/api/v1/player/stop")
    require_status(response, 200)
    return "stop accepted"


def _logs(client: TargetClient, token: str | None) -> str:
    response = client.request("GET", "/api/v1/logs?limit=20")
    payload = require_object(response)
    require_status(response, 200)
    if not isinstance(payload.get("items"), list):
        raise AcceptanceError("log response is missing items")
    if token and token.encode("utf-8") in response.body:
        raise AcceptanceError("log response exposed the bearer token")
    return f"{len(payload['items'])} bounded redacted log entries returned"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True, help="deployed server URL, for example http://nas:8080")
    parser.add_argument("--token", default=os.environ.get("MFP_ACCEPTANCE_TOKEN")
                        or os.environ.get("MFP_AUTH_TOKEN"),
                        help="bearer token; defaults to MFP_ACCEPTANCE_TOKEN or MFP_AUTH_TOKEN")
    parser.add_argument("--pairing-token", default=os.environ.get("MFP_PAIRING_TOKEN"),
                        help="optional one-time pairing token for a fresh target")
    parser.add_argument("--script-id", default="target-acceptance.funscript")
    parser.add_argument("--timeout-seconds", type=float, default=10)
    parser.add_argument("--no-playback", action="store_true",
                        help="skip script upload/load and player mutation checks")
    parser.add_argument("--output", type=Path, default=Path("artifacts/target/target-acceptance.json"))
    args = parser.parse_args()
    if args.timeout_seconds <= 0:
        parser.error("timeout must be positive")
    try:
        report = run_acceptance(args.base_url, args.token, args.pairing_token,
                                args.timeout_seconds, args.script_id, args.no_playback)
    except (ValueError, OSError) as error:
        print(f"Target acceptance: error -> {error}", file=sys.stderr)
        return 2
    output = args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    summary = {status: sum(1 for item in report["checks"] if item["status"] == status)
               for status in ("pass", "fail", "skipped")}
    print(f"Target acceptance: {report['result']} -> {output} "
          f"(pass={summary['pass']} fail={summary['fail']} skipped={summary['skipped']})")
    return 0 if report["result"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
