#!/usr/bin/env python3
"""Validate the portable container/fnOS contract without Docker or fnpack.

This is intentionally a static gate. It checks repository-owned invariants and
does not pretend to validate a Docker image, an FPK, or a target NAS. Run the
same script before those external tools so failures are caught early.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path


class Validator:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.checks: list[dict[str, str]] = []

    def check(self, name: str, ok: bool, detail: str) -> None:
        self.checks.append({"name": name, "status": "pass" if ok else "fail", "detail": detail})

    def file(self, relative: str) -> str | None:
        path = self.root / relative
        if not path.is_file():
            self.check(f"file:{relative}", False, "required file is missing")
            return None
        self.check(f"file:{relative}", True, "present")
        return path.read_text(encoding="utf-8")

    def run(self) -> int:
        dockerfile = self.file("Dockerfile")
        compose = self.file("deploy/docker/compose.yaml")
        ignore = self.file(".dockerignore")
        fnos_manifest = self.file("deploy/fnos/manifest")
        fnos_readme = self.file("deploy/fnos/README.md")

        if dockerfile is not None:
            self.check("docker:multi-stage", dockerfile.count("FROM ") >= 3,
                       "web build, server build, and runtime stages are present")
            self.check("docker:non-root", re.search(r"(?m)^USER\s+mfp\s*$", dockerfile) is not None,
                       "runtime image declares the mfp user")
            self.check("docker:healthcheck", "HEALTHCHECK" in dockerfile and "/health/live" in dockerfile,
                       "runtime health check targets /health/live")
            self.check("docker:ports", re.search(r"(?m)^EXPOSE\s+8080\s*$", dockerfile) is not None,
                       "container exposes the documented HTTP port")
            for root_name in ("/config", "/keys", "/scripts", "/logs"):
                self.check(f"docker:volume:{root_name}", root_name in dockerfile,
                           f"runtime image declares {root_name}")
            self.check("docker:secret-not-baked", not re.search(
                r"(?im)^\s*(ARG|ENV)\s+MFP_(AUTH|PAIRING|.*TOKEN|.*KEY)\s*=\s*[^$\\s]",
                dockerfile,
            ), "no secret value is baked into Dockerfile ARG/ENV")

        if ignore is not None:
            for entry in (".git", "**/bin", "**/obj", "Source/MultiFunPlayer.Web/node_modules"):
                self.check(f"dockerignore:{entry}", entry in ignore,
                           f"build context excludes {entry}")

        if compose is not None:
            for service in ("multifunplayer:", "fake-tcp:", "fake-udp:"):
                self.check(f"compose:service:{service[:-1]}", service in compose,
                           f"compose file contains {service[:-1]} service")
            for volume in ("mfp-config:", "mfp-keys:", "mfp-scripts:", "mfp-logs:"):
                self.check(f"compose:volume:{volume[:-1]}", volume in compose,
                           f"compose file declares {volume[:-1]} volume")
            self.check("compose:read-only", "    read_only: true" in compose,
                       "main service filesystem is read-only")
            self.check("compose:no-new-privileges", "no-new-privileges:true" in compose,
                       "main service disables privilege escalation")
            self.check("compose:healthcheck", "/health/live" in compose,
                       "main service health check targets /health/live")
            self.check("compose:secret-interpolation", all(
                re.search(rf"^\s+MFP_{name}: \$\{{MFP_{name}:-\}}$", compose, re.MULTILINE)
                for name in ("AUTH_TOKEN", "PAIRING_TOKEN")
            ), "auth and pairing values are supplied through interpolation")
            active_devices = any(re.match(r"^\s+devices:\s*$", line)
                                 for line in compose.splitlines())
            self.check("compose:no-host-device-default", not active_devices,
                       "serial device mapping is opt-in")

        if fnos_manifest is not None:
            try:
                manifest = json.loads(fnos_manifest)
            except json.JSONDecodeError as exc:
                self.check("fnos:json", False, f"invalid JSON: {exc}")
                manifest = None
            if isinstance(manifest, dict):
                self.check("fnos:schema-version", manifest.get("schemaVersion") == 1,
                           "structural manifest uses schemaVersion 1")
                self.check("fnos:id", bool(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)+", str(manifest.get("id", "")))),
                           "application id uses a reverse-DNS-like lowercase form")
                self.check("fnos:version", bool(re.fullmatch(r"\d+\.\d+\.\d+", str(manifest.get("version", "")))),
                           "manifest version is a concrete semver")
                self.check("fnos:architectures", set(manifest.get("architecture", [])) == {"amd64", "arm64"},
                           "both target architectures are declared")
                ports = manifest.get("ports", [])
                self.check("fnos:http-port", any(
                    isinstance(port, dict) and port.get("container") == 8080 and port.get("protocol") == "tcp"
                    for port in ports
                ), "manifest declares TCP 8080")
                volumes = manifest.get("volumes", [])
                volume_paths = {v.get("container") for v in volumes if isinstance(v, dict)}
                self.check("fnos:volumes", volume_paths == {"/config", "/keys", "/scripts", "/logs"},
                           "all four persistent roots are declared exactly once")
                environment = manifest.get("environment", {})
                auth = environment.get("MFP_AUTH_TOKEN", {}) if isinstance(environment, dict) else {}
                self.check("fnos:secret-auth", isinstance(auth, dict) and
                           auth.get("required") is True and auth.get("secret") is True,
                           "auth token is required and marked secret")
                health = manifest.get("healthcheck", {})
                self.check("fnos:healthcheck", isinstance(health, dict) and
                           health.get("path") == "/health/live",
                           "manifest health check targets /health/live")
                upgrade = manifest.get("upgrade", {})
                self.check("fnos:upgrade-volumes", isinstance(upgrade, dict) and
                           upgrade.get("preserveVolumes") is True,
                           "upgrade contract preserves persistent volumes")

        if fnos_readme is not None:
            self.check("fnos:external-schema-warning", "does not claim to be an official" in fnos_readme,
                       "README does not overclaim FPK schema validation")

        failures = sum(result["status"] == "fail" for result in self.checks)
        return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="emit machine-readable results")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="repository root (defaults to the parent of deploy)")
    args = parser.parse_args()
    validator = Validator(args.root.resolve())
    code = validator.run()
    if args.json:
        print(json.dumps({"checks": validator.checks}, indent=2, sort_keys=True))
        return code
    failures = sum(result["status"] == "fail" for result in validator.checks)
    passed = len(validator.checks) - failures
    print(f"packaging checks: {passed} passed, {failures} failed")
    for result in validator.checks:
        if result["status"] == "fail":
            print(f"FAIL {result['name']}: {result['detail']}", file=sys.stderr)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
