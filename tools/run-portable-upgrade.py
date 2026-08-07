#!/usr/bin/env python3
"""Rehearse a portable host upgrade across independent server processes.

The harness shares the four configured roots between independent server
instances, verifies both graceful and interrupted process restarts, and can
optionally launch a separate rollback build. It then checks that scripts, device
settings, and persisted logs remain available. It is useful in Linux CI and
local development, but does not claim a Docker image, fnOS package, filesystem
snapshot, or target-device upgrade.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

_SOAK_SPEC = importlib.util.spec_from_file_location(
    "portable_soak", Path(__file__).with_name("run-portable-soak.py"))
if _SOAK_SPEC is None or _SOAK_SPEC.loader is None:
    raise ImportError("run-portable-soak.py could not be loaded")
_SOAK = importlib.util.module_from_spec(_SOAK_SPEC)
_SOAK_SPEC.loader.exec_module(_SOAK)
free_port = _SOAK.free_port
request_json = _SOAK.request_json
stop_process = _SOAK.stop_process
wait_for_live = _SOAK.wait_for_live
repository_revision = _SOAK.repository_revision
sha256_file = _SOAK.sha256_file


TOKEN = "portable-upgrade-token-1234567890"


def wait_for_file(path: Path, timeout_seconds: float) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if path.is_file() and path.stat().st_size > 0:
            return
        time.sleep(0.1)
    raise RuntimeError(f"expected persisted file was not created: {path}")


def launch_server(server: Path, dotnet: str, port: int, root: Path,
                  log_path: Path) -> tuple[subprocess.Popen[Any], Any]:
    environment = os.environ.copy()
    environment.update({
        "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
        "MFP_AUTH_TOKEN": TOKEN,
        "MFP_CONFIG_ROOT": str(root / "config"),
        "MFP_KEYS_ROOT": str(root / "keys"),
        "MFP_SCRIPTS_ROOT": str(root / "scripts"),
        "MFP_LOGS_ROOT": str(root / "logs"),
        "MFP_TICK_HZ": "20",
    })
    log = log_path.open("w", encoding="utf-8")
    process = subprocess.Popen(
        [dotnet, str(server)],
        cwd=server.parent,
        env=environment,
        stdout=log,
        stderr=subprocess.STDOUT,
    )
    return process, log


def stop_server(process: subprocess.Popen[Any], log: Any) -> bool:
    try:
        return stop_process(process)
    finally:
        log.close()


def interrupt_server(process: subprocess.Popen[Any], log: Any) -> bool:
    """Stop without an application shutdown signal to simulate a restart."""
    try:
        if process.poll() is not None:
            return False
        process.kill()
        process.wait(timeout=10)
        return process.returncode != 0
    except (OSError, subprocess.TimeoutExpired):
        return False
    finally:
        log.close()


def verify_persistent_state(base_url: str, root: Path) -> dict[str, Any]:
    scripts = request_json(base_url, "/api/v1/scripts", TOKEN)
    axes = request_json(base_url, "/api/v1/device-axes", TOKEN)
    state = {
        "scriptRestored": any(
            item.get("id") == "upgrade-rehearsal.funscript"
            for item in scripts if isinstance(item, dict)
        ),
        "axisRestored": any(
            item.get("axis") == "L0" and item.get("inverted") is True
            for item in axes if isinstance(item, dict)
        ),
        "logRestored": (root / "logs" / "runtime.log").is_file(),
        "volumeRootsPresent": {
            name: (root / name).is_dir()
            for name in ("config", "keys", "scripts", "logs")
        },
    }
    return state


def assembly_evidence(path: Path) -> dict[str, Any]:
    return {
        "path": str(path),
        "sha256": sha256_file(path),
        "bytes": path.stat().st_size,
    }


def run(args: argparse.Namespace) -> dict[str, Any]:
    server = Path(args.server).resolve()
    baseline_server = Path(args.baseline_server or args.server).resolve()
    rollback_server = Path(args.rollback_server).resolve() if args.rollback_server else None
    for label, candidate in (("upgrade", server), ("baseline", baseline_server),
                             ("rollback", rollback_server)):
        if candidate is not None and not candidate.is_file():
            raise FileNotFoundError(f"{label} server DLL does not exist: {candidate}")

    report: dict[str, Any] = {
        "schemaVersion": 1,
        "server": str(server),
        "baselineServer": str(baseline_server),
        "rollbackServer": str(rollback_server) if rollback_server is not None else None,
        "buildEvidence": {
            "repositoryRevision": repository_revision(Path(__file__).resolve().parents[1]),
            "assemblies": {
                "baseline": assembly_evidence(baseline_server),
                "upgrade": assembly_evidence(server),
                **({"rollback": assembly_evidence(rollback_server)}
                   if rollback_server is not None else {}),
            },
        },
        "startedAtUtc": datetime.now(timezone.utc).isoformat(),
        "phases": [],
    }
    port = args.port or free_port()
    with tempfile.TemporaryDirectory(prefix="mfp-portable-upgrade-") as root_name:
        root = Path(root_name)
        base_url = f"http://127.0.0.1:{port}"
        log_path = root / "server.log"
        process: subprocess.Popen[Any] | None = None
        log: Any | None = None
        try:
            process, log = launch_server(baseline_server, args.dotnet, port, root, log_path)
            wait_for_live(base_url, args.startup_timeout_seconds)
            request_json(base_url, "/api/v1/scripts", TOKEN, "POST", {
                "id": "upgrade-rehearsal.funscript",
                "content": "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}",
            })
            request_json(base_url, "/api/v1/device-axes/L0", TOKEN, "PUT", {
                "axis": "L0",
                "minimum": 0.1,
                "maximum": 0.9,
                "inverted": True,
                "interpolation": "Linear",
                "limitEnabled": False,
            })
            wait_for_file(root / "logs" / "runtime.log", args.startup_timeout_seconds)
            first_stop = stop_server(process, log)
            process = None
            log = None
            report["phases"].append({
                "name": "before-upgrade",
                "gracefulStop": first_stop,
                "volumes": sorted(path.name for path in root.iterdir()),
            })

            process, log = launch_server(server, args.dotnet, port, root, log_path)
            wait_for_live(base_url, args.startup_timeout_seconds)
            interrupted_stop = interrupt_server(process, log)
            process = None
            log = None
            report["phases"].append({
                "name": "simulated-process-interruption",
                "forcedStop": interrupted_stop,
            })

            process, log = launch_server(server, args.dotnet, port, root, log_path)
            wait_for_live(base_url, args.startup_timeout_seconds)
            upgrade_state = verify_persistent_state(base_url, root)
            second_stop = stop_server(process, log)
            process = None
            log = None
            report["phases"].append({
                "name": "after-upgrade",
                "gracefulStop": second_stop,
                **upgrade_state,
            })
            rollback_state: dict[str, Any] | None = None
            rollback_stop = True
            if rollback_server is not None:
                process, log = launch_server(rollback_server, args.dotnet, port, root, log_path)
                wait_for_live(base_url, args.startup_timeout_seconds)
                rollback_state = verify_persistent_state(base_url, root)
                rollback_stop = stop_server(process, log)
                process = None
                log = None
                report["phases"].append({
                    "name": "after-rollback",
                    "gracefulStop": rollback_stop,
                    **rollback_state,
                })

            state_checks = [
                upgrade_state["scriptRestored"],
                upgrade_state["axisRestored"],
                upgrade_state["logRestored"],
                *upgrade_state["volumeRootsPresent"].values(),
            ]
            if rollback_state is not None:
                state_checks.extend([
                    rollback_state["scriptRestored"],
                    rollback_state["axisRestored"],
                    rollback_state["logRestored"],
                    *rollback_state["volumeRootsPresent"].values(),
                ])
            report["result"] = "pass" if all((
                first_stop, interrupted_stop, second_stop,
                rollback_stop, *state_checks,
            )) else "fail"
        except Exception as error:
            if process is not None and log is not None:
                stop_server(process, log)
                process = None
                log = None
            report["result"] = "error"
            report["error"] = str(error)
            report["serverLogTail"] = log_path.read_text(encoding="utf-8", errors="replace")[-4000:]
        finally:
            if process is not None and log is not None:
                stop_server(process, log)

    report["endedAtUtc"] = datetime.now(timezone.utc).isoformat()
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default="Source/MultiFunPlayer.Server/bin/Release/net10.0/MultiFunPlayer.Server.dll")
    parser.add_argument("--baseline-server",
                        help="optional previous-build Server DLL; defaults to --server")
    parser.add_argument("--rollback-server",
                        help="optional rollback-build Server DLL to verify after upgrade")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--startup-timeout-seconds", type=float, default=30.0)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--output", type=Path, default=Path("artifacts/upgrade/portable-upgrade.json"))
    args = parser.parse_args()
    if args.startup_timeout_seconds <= 0:
        parser.error("startup timeout must be positive")
    report = run(args)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Upgrade rehearsal: {report.get('result', 'error')} -> {output}")
    return 0 if report.get("result") == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
