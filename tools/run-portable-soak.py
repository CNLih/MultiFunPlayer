#!/usr/bin/env python3
"""Run a repeatable headless playback/output soak and write JSON evidence.

The harness is Linux-oriented and intentionally keeps the server process and
all four storage roots in a temporary directory. A short duration is useful
for CI smoke checks; the default is the eight-hour release soak from the
migration plan. It does not claim container, fnOS, hardware, or ARM coverage.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import signal
import socket
import statistics
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def request_json(base_url: str, path: str, token: str | None = None,
                 method: str = "GET", payload: dict[str, Any] | None = None) -> Any:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(base_url + path, data=body, method=method)
    if body is not None:
        request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            raw = response.read()
            return json.loads(raw.decode("utf-8")) if raw else None
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{method} {path} returned HTTP {error.code}: {detail}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"{method} {path} failed: {error.reason}") from error


def wait_for_live(base_url: str, timeout_seconds: float) -> None:
    deadline = time.monotonic() + timeout_seconds
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            response = request_json(base_url, "/health/live")
            if response.get("status") == "live":
                return
        except (RuntimeError, AttributeError) as error:
            last_error = error
        time.sleep(0.1)
    raise RuntimeError(f"server did not become live: {last_error}")


def process_metrics(pid: int) -> tuple[int | None, float | None]:
    """Return RSS bytes and process CPU seconds on Linux, if still available."""
    try:
        status = Path(f"/proc/{pid}/status").read_text(encoding="utf-8")
        rss_bytes = None
        for line in status.splitlines():
            if line.startswith("VmRSS:"):
                rss_bytes = int(line.split()[1]) * 1024
                break

        stat = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
        tail = stat[stat.rfind(")") + 2:].split()
        clock_ticks = os.sysconf(os.sysconf_names["SC_CLK_TCK"])
        cpu_seconds = (int(tail[11]) + int(tail[12])) / clock_ticks
        return rss_bytes, cpu_seconds
    except (FileNotFoundError, IndexError, OSError, ValueError):
        return None, None


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, math.ceil(fraction * len(ordered)) - 1))
    return ordered[index]


def stop_process(process: subprocess.Popen[Any]) -> bool:
    if process.poll() is not None:
        return process.returncode == 0
    try:
        process.send_signal(signal.SIGINT)
        process.wait(timeout=15)
    except (OSError, subprocess.TimeoutExpired):
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
    return process.returncode == 0


def run(args: argparse.Namespace) -> dict[str, Any]:
    token = "portable-soak-token-1234567890"
    port = args.port or free_port()
    server = Path(args.server).resolve()
    if not server.is_file():
        raise FileNotFoundError(f"server DLL does not exist: {server}")

    report: dict[str, Any] = {
        "schemaVersion": 1,
        "durationSecondsRequested": args.duration_seconds,
        "pollSeconds": args.poll_seconds,
        "server": str(server),
        "startedAtUtc": datetime.now(timezone.utc).isoformat(),
        "samples": [],
    }
    with tempfile.TemporaryDirectory(prefix="mfp-portable-soak-") as root_name:
        root = Path(root_name)
        environment = os.environ.copy()
        environment.update({
            "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
            "MFP_AUTH_TOKEN": token,
            "MFP_CONFIG_ROOT": str(root / "config"),
            "MFP_KEYS_ROOT": str(root / "keys"),
            "MFP_SCRIPTS_ROOT": str(root / "scripts"),
            "MFP_LOGS_ROOT": str(root / "logs"),
            "MFP_TICK_HZ": "20",
        })
        server_log = root / "server.log"
        with server_log.open("w", encoding="utf-8") as log:
            process = subprocess.Popen(
                [args.dotnet, str(server)],
                cwd=server.parent,
                env=environment,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
        base_url = f"http://127.0.0.1:{port}"
        graceful_stop = False
        try:
            wait_for_live(base_url, args.startup_timeout_seconds)
            request_json(base_url, "/api/v1/output-targets", token, "POST", {
                "id": "portable-soak-file",
                "name": "Portable soak file",
                "protocol": "file",
                "endpoint": "portable-soak.tcode",
                "enabled": True,
                "autoConnect": True,
                "updateRateHz": 20,
            })
            duration_ms = max(10_000, int(args.duration_seconds * 1000) + 10_000)
            request_json(base_url, "/api/v1/scripts/load", token, "POST", {
                "name": "portable-soak.funscript",
                "axis": "L0",
                "content": {
                    "actions": [
                        {"at": 0, "pos": 0},
                        {"at": duration_ms, "pos": 100},
                    ]
                },
            })
            request_json(base_url, "/api/v1/player/play", token, "POST", {})

            started = time.monotonic()
            previous: tuple[float, int] | None = None
            revision_intervals: list[float] = []
            while time.monotonic() - started < args.duration_seconds:
                observed_at = time.monotonic()
                status = request_json(base_url, "/api/v1/status", token)
                targets = request_json(base_url, "/api/v1/output-targets", token)
                target = next(item for item in targets if item["id"] == "portable-soak-file")
                rss_bytes, cpu_seconds = process_metrics(process.pid)
                if previous is not None:
                    wall_delta = observed_at - previous[0]
                    revision_delta = int(status["revision"]) - previous[1]
                    if revision_delta > 0:
                        revision_intervals.extend([wall_delta / revision_delta] * revision_delta)
                previous = (observed_at, int(status["revision"]))
                report["samples"].append({
                    "elapsedSeconds": observed_at - started,
                    "observedAtUtc": datetime.now(timezone.utc).isoformat(),
                    "revision": status["revision"],
                    "state": status["state"],
                    "positionSeconds": status["positionSeconds"],
                    "sentFrames": target["sentFrames"],
                    "droppedFrames": target["droppedFrames"],
                    "failedFrames": target["failedFrames"],
                    "reconnects": target["reconnects"],
                    "rssBytes": rss_bytes,
                    "cpuSeconds": cpu_seconds,
                })
                time.sleep(args.poll_seconds)

            graceful_stop = stop_process(process)
            output = report["samples"][-1] if report["samples"] else {}
            report["result"] = "pass" if output.get("sentFrames", 0) > 0 and output.get("failedFrames", 0) == 0 else "fail"
            report["output"] = {
                key: output.get(key, 0)
                for key in ("sentFrames", "droppedFrames", "failedFrames", "reconnects")
            }
            report["process"] = {
                "peakRssBytes": max((sample["rssBytes"] or 0 for sample in report["samples"]), default=0),
                "cpuSeconds": output.get("cpuSeconds"),
                "gracefulStop": graceful_stop,
            }
            report["revisionIntervalMilliseconds"] = {
                "samples": len(revision_intervals),
                "mean": statistics.fmean(revision_intervals) * 1000 if revision_intervals else None,
                "min": min(revision_intervals) * 1000 if revision_intervals else None,
                "max": max(revision_intervals) * 1000 if revision_intervals else None,
                "p95": percentile(revision_intervals, 0.95) * 1000 if revision_intervals else None,
                "stdev": statistics.pstdev(revision_intervals) * 1000 if len(revision_intervals) > 1 else None,
            }
        except Exception as error:
            graceful_stop = stop_process(process)
            report["result"] = "error"
            report["error"] = str(error)
            report["serverLogTail"] = server_log.read_text(encoding="utf-8", errors="replace")[-4000:]
        finally:
            report["gracefulStop"] = graceful_stop
            if process.poll() is None:
                stop_process(process)

    report["endedAtUtc"] = datetime.now(timezone.utc).isoformat()
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default="Source/MultiFunPlayer.Server/bin/Release/net10.0/MultiFunPlayer.Server.dll")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--duration-seconds", type=float, default=8 * 60 * 60)
    parser.add_argument("--poll-seconds", type=float, default=1.0)
    parser.add_argument("--startup-timeout-seconds", type=float, default=30.0)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--output", type=Path, default=Path("artifacts/soak/portable-soak.json"))
    args = parser.parse_args()
    if args.duration_seconds <= 0 or args.poll_seconds <= 0:
        parser.error("duration and poll intervals must be positive")
    report = run(args)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Soak result: {report.get('result', 'error')} -> {output}")
    return 0 if report.get("result") == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
