#!/usr/bin/env python3
"""Run the reproducible local part of the migration validation matrix.

The migration plan deliberately separates checks that can run on a Linux
developer machine from evidence that requires Windows, Docker/Buildx, fnOS,
or a physical output device.  This command executes the former and records the
latter as explicit pending entries in a JSON report.  It is therefore useful
for CI artifacts and release review without claiming target-environment
coverage that was not exercised.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


@dataclass(frozen=True)
class Check:
    check_id: str
    category: str
    command: tuple[str, ...] | None
    cwd: Path
    reason: str | None = None


_SECRET_PATTERN = re.compile(
    r"(?i)(authorization\s*:\s*bearer\s+|mfp_(?:auth|pairing|.*token|.*key)\s*=\s*)[^\s,;]+"
)


def redact(text: str) -> str:
    return _SECRET_PATTERN.sub(r"\1<redacted>", text)


def _command(executable: str, *args: str) -> tuple[str, ...]:
    return (executable, *args)


def build_checks(root: Path, *, include_browser: bool) -> list[Check]:
    dotnet = os.environ.get("MFP_DOTNET", "dotnet")
    python = os.environ.get("MFP_PYTHON", sys.executable)
    npm = os.environ.get("MFP_NPM", "npm")
    checks = [
        Check(
            "unit-core",
            "unit",
            _command(dotnet, "test", "Source/MultiFunPlayer.Core.Tests/MultiFunPlayer.Core.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true"),
            root,
        ),
        Check(
            "unit-application",
            "unit",
            _command(dotnet, "test", "Source/MultiFunPlayer.Application.Tests/MultiFunPlayer.Application.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true"),
            root,
        ),
        Check(
            "integration-infrastructure",
            "integration",
            _command(dotnet, "test", "Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true"),
            root,
        ),
        Check(
            "integration-server",
            "integration",
            _command(dotnet, "test", "Source/MultiFunPlayer.Server.Tests/MultiFunPlayer.Server.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true", "--",
                     "xunit.parallelizeTestCollections=false"),
            root,
        ),
        Check(
            "contract-python-tools",
            "contract",
            _command(python, "-m", "unittest", "discover", "-s", "tools/tests", "-p", "test_*.py"),
            root,
        ),
        Check(
            "contract-portable-boundary",
            "contract",
            _command(python, "tools/check-portable-boundary.py"),
            root,
        ),
        Check(
            "contract-packaging",
            "contract",
            _command(python, "deploy/validate-packaging.py"),
            root,
        ),
        Check(
            "contract-release-evidence-template",
            "contract",
            _command(python, "tools/validate-release-evidence.py",
                     "deploy/release-evidence.example.json"),
            root,
        ),
        Check(
            "interruption-browser-disconnect",
            "interruption",
            _command(dotnet, "test", "Source/MultiFunPlayer.Server.Tests/MultiFunPlayer.Server.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true", "--filter",
                     "FullyQualifiedName~Backend_output_continues_after_state_websocket_disconnect", "--",
                     "xunit.parallelizeTestCollections=false"),
            root,
        ),
        Check(
            "interruption-tcp-peer-restart",
            "interruption",
            _command(dotnet, "test", "Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj",
                     "--no-restore", "--configuration", "Release",
                     "-p:EnableWindowsTargeting=true", "--filter",
                     "FullyQualifiedName~TcpTransport_reconnects_after_peer_socket_restart"),
            root,
        ),
        Check(
            "interruption-websocket-peer-restart",
            "interruption",
            _command(dotnet, "test", "Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj",
                     "--no-restore", "--configuration", "Release", "-p:EnableWindowsTargeting=true",
                     "--filter", "FullyQualifiedName~WebSocketTransport_reconnects_after_peer_restart"),
            root,
        ),
        Check(
            "interruption-device-removal",
            "interruption",
            _command(dotnet, "test", "Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj",
                     "--no-restore", "--configuration", "Release", "-p:EnableWindowsTargeting=true",
                     "--filter", "FullyQualifiedName~Device_removal_stops_commands_to_removed_device"),
            root,
        ),
        Check(
            "interruption-process-restart-persistence",
            "interruption",
            _command(python, "tools/run-portable-upgrade.py",
                     "--output", "artifacts/validation/portable-upgrade.json"),
            root,
        ),
        Check(
            "web-typecheck",
            "browser",
            _command(npm, "run", "typecheck", "--prefix", "Source/MultiFunPlayer.Web"),
            root,
        ),
        Check(
            "web-static-contract",
            "browser",
            _command(npm, "run", "check:static", "--prefix", "Source/MultiFunPlayer.Web"),
            root,
        ),
        Check(
            "web-production-build",
            "browser",
            _command(npm, "run", "build", "--prefix", "Source/MultiFunPlayer.Web"),
            root,
        ),
    ]
    if include_browser:
        checks.append(Check(
            "browser-playwright",
            "browser",
            _command(npm, "run", "test:e2e", "--prefix", "Source/MultiFunPlayer.Web"),
            root,
        ))
    else:
        checks.append(Check(
            "browser-playwright",
            "browser",
            None,
            root,
            "skipped by --skip-browser; run with host Chromium libraries for desktop/mobile evidence",
        ))
    checks.extend([
        Check(
            "container-linux-multiarch",
            "container",
            None,
            root,
            "requires Docker Buildx/QEMU execution on amd64 and arm64; see the container job in .github/workflows/ci.yml",
        ),
        Check(
            "interruption-container-restart",
            "interruption",
            None,
            root,
            "requires restarting the built Docker image with real persistent volumes; see the persistent-volume step in .github/workflows/ci.yml",
        ),
        Check(
            "target-fnos-device",
            "target-device",
            None,
            root,
            "requires a physical fnOS/NAS target and its Web-entry/device permissions",
        ),
        Check(
            "target-output-hardware",
            "target-device",
            None,
            root,
            "requires physical serial/Bluetooth/network output devices and reconnect testing",
        ),
    ])
    return checks


def run_check(check: Check, timeout_seconds: float) -> dict[str, Any]:
    started = time.monotonic()
    result: dict[str, Any] = {
        "id": check.check_id,
        "category": check.category,
        "command": list(check.command) if check.command else None,
        "cwd": str(check.cwd),
    }
    if check.command is None:
        result.update({"status": "pending", "reason": check.reason})
        return result

    executable = shutil.which(check.command[0])
    if executable is None and not Path(check.command[0]).is_file():
        result.update({"status": "fail", "returnCode": None,
                       "reason": f"executable not found: {check.command[0]}"})
        return result

    try:
        completed = subprocess.run(
            check.command,
            cwd=check.cwd,
            env=os.environ.copy(),
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout_seconds,
            check=False,
        )
        output = redact(completed.stdout or "")
        result.update({
            "status": "pass" if completed.returncode == 0 else "fail",
            "returnCode": completed.returncode,
            "outputTail": output[-4000:],
        })
    except subprocess.TimeoutExpired as error:
        output = redact((error.stdout or "") if isinstance(error.stdout, str) else "")
        result.update({"status": "fail", "returnCode": None, "outputTail": output[-4000:],
                      "reason": f"timed out after {timeout_seconds:g}s"})
    except OSError as error:
        result.update({"status": "fail", "returnCode": None, "reason": str(error)})
    result["durationSeconds"] = round(time.monotonic() - started, 3)
    return result


def summarize(results: Iterable[dict[str, Any]]) -> dict[str, int]:
    summary = {"pass": 0, "fail": 0, "pending": 0}
    for result in results:
        summary[result["status"]] = summary.get(result["status"], 0) + 1
    return summary


def run(args: argparse.Namespace) -> dict[str, Any]:
    root = args.root.resolve()
    checks = build_checks(root, include_browser=not args.skip_browser)
    results = [run_check(check, args.timeout_seconds) for check in checks]
    return {
        "schemaVersion": 1,
        "startedAtUtc": datetime.now(timezone.utc).isoformat(),
        "root": str(root),
        "summary": summarize(results),
        "checks": results,
        "result": "pass" if all(item["status"] != "fail" for item in results) else "fail",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path, default=Path("artifacts/validation/validation-matrix.json"))
    parser.add_argument("--skip-browser", action="store_true",
                        help="record Playwright as pending instead of running it")
    parser.add_argument("--timeout-seconds", type=float, default=900.0)
    parser.add_argument("--require-complete", action="store_true",
                        help="return failure when any target check remains pending")
    args = parser.parse_args()
    if args.timeout_seconds <= 0:
        parser.error("timeout must be positive")
    report = run(args)
    output = args.output if args.output.is_absolute() else args.root / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    summary = report["summary"]
    print(f"Validation matrix: {report['result']} -> {output} "
          f"(pass={summary['pass']} fail={summary['fail']} pending={summary['pending']})")
    if report["result"] == "fail":
        return 1
    if args.require_complete and summary["pending"]:
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
