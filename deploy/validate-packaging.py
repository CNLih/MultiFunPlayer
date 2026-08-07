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
import shutil
import subprocess
import sys
from pathlib import Path


def validate_shell_syntax(path: Path, root: Path) -> tuple[bool, str]:
    shell = shutil.which("sh")
    if not shell:
        return False, "POSIX sh is required for lifecycle script syntax validation"
    syntax = subprocess.run(
        [shell, "-n", str(path)],
        cwd=root,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )
    if syntax.returncode == 0:
        return True, "lifecycle script passes POSIX shell syntax validation"
    return False, syntax.stderr.strip() or "POSIX shell syntax validation failed"


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

    def binary_file(self, relative: str) -> bytes | None:
        path = self.root / relative
        if not path.is_file():
            self.check(f"file:{relative}", False, "required file is missing")
            return None
        self.check(f"file:{relative}", True, "present")
        return path.read_bytes()

    def directory(self, relative: str) -> Path | None:
        path = self.root / relative
        if not path.is_dir():
            self.check(f"directory:{relative}", False, "required directory is missing")
            return None
        self.check(f"directory:{relative}", True, "present")
        return path
    def run(self) -> int:
        dockerfile = self.file("Dockerfile")
        compose = self.file("deploy/docker/compose.yaml")
        ignore = self.file(".dockerignore")
        fnos_manifest = self.file("deploy/fnos/manifest")
        fnos_readme = self.file("deploy/fnos/README.md")
        fnpack_manifest = self.file("deploy/fnos/fnpack/manifest")
        fnpack_compose = self.file("deploy/fnos/fnpack/app/docker/docker-compose.yaml")
        fnpack_ui = self.file("deploy/fnos/fnpack/app/ui/config")
        fnpack_privilege = self.file("deploy/fnos/fnpack/config/privilege")
        fnpack_resource = self.file("deploy/fnos/fnpack/config/resource")
        fnpack_build = self.file("deploy/fnos/build-fpk.sh")
        fnpack_validate = self.file("deploy/validate-fpk.sh")

        for relative in (
            "deploy/fnos/fnpack/app",
            "deploy/fnos/fnpack/app/docker",
            "deploy/fnos/fnpack/app/ui",
            "deploy/fnos/fnpack/app/ui/images",
            "deploy/fnos/fnpack/cmd",
            "deploy/fnos/fnpack/config",
            "deploy/fnos/fnpack/wizard",
        ):
            self.directory(relative)

        for relative in (
            "deploy/fnos/fnpack/ICON.PNG",
            "deploy/fnos/fnpack/ICON_256.PNG",
            "deploy/fnos/fnpack/app/ui/images/icon_64.png",
            "deploy/fnos/fnpack/app/ui/images/icon_256.png",
        ):
            image = self.binary_file(relative)
            self.check(f"fnpack:png:{relative}", image is not None and image.startswith(b"\x89PNG\r\n\x1a\n"),
                       "PNG icon is present")

        for name in (
            "main",
            "install_init",
            "install_callback",
            "upgrade_init",
            "upgrade_callback",
            "uninstall_init",
            "uninstall_callback",
            "config_init",
            "config_callback",
        ):
            relative = f"deploy/fnos/fnpack/cmd/{name}"
            path = self.root / relative
            self.check(f"fnpack:cmd:{name}", path.is_file() and bool(path.stat().st_mode & 0o111),
                       "lifecycle script is present and executable")
            if path.is_file():
                syntax_ok, syntax_detail = validate_shell_syntax(path, self.root)
                self.check(f"fnpack:cmd-syntax:{name}", syntax_ok, syntax_detail)
            else:
                self.check(f"fnpack:cmd-syntax:{name}", False, "lifecycle script is missing")
            if name.startswith("uninstall_") and path.is_file():
                uninstall_script = path.read_text(encoding="utf-8")
                self.check(f"fnpack:uninstall-preserves-data:{name}", not re.search(r"rm\s+-r[fF]", uninstall_script),
                           "uninstall lifecycle script does not recursively remove user data")

        if fnpack_manifest is not None:
            def manifest_value(key: str) -> str:
                match = re.search(rf"(?m)^{re.escape(key)}\s*=\s*(.+?)\s*$", fnpack_manifest)
                return match.group(1).strip() if match else ""

            self.check("fnpack:manifest-required", all(manifest_value(key) for key in (
                "appname", "version", "display_name", "service_port", "source",
            )), "manifest contains the required fnpack metadata")
            self.check("fnpack:manifest-no-placeholders", "{" not in fnpack_manifest,
                       "manifest has no unexpanded template placeholders")
            self.check("fnpack:manifest-appname", manifest_value("appname") == "multifunplayer",
                       "manifest appname matches the package directory")
            self.check("fnpack:manifest-port", manifest_value("service_port") == "8080",
                       "manifest service_port is 8080")
            self.check("fnpack:manifest-ui", manifest_value("desktop_uidir") == "ui",
                       "manifest points to the Web entry directory")

        if fnpack_compose is not None:
            for token in (
                "${TRIM_SERVICE_PORT",
                "${TRIM_PKGVAR",
                "${MFP_IMAGE",
                ":8080",
                "/config",
                "/keys",
                "/scripts",
                "/logs",
            ):
                self.check(f"fnpack:compose:{token}", token in fnpack_compose,
                           f"fnOS Compose contract contains {token}")
            self.check("fnpack:compose:no-privileged", "privileged:" not in fnpack_compose,
                       "fnOS Compose project does not request privileged mode")
            self.check("fnpack:compose:read-only", "    read_only: true" in fnpack_compose,
                       "fnOS main service filesystem is read-only")
            self.check("fnpack:compose:tmpfs", "/tmp:rw,noexec,nosuid,size=32m" in fnpack_compose,
                       "fnOS main service has a bounded non-executable temporary filesystem")
            self.check("fnpack:compose:no-new-privileges", "no-new-privileges:true" in fnpack_compose,
                       "fnOS main service disables privilege escalation")
            self.check("fnpack:compose:graceful-stop", "stop_grace_period: 30s" in fnpack_compose,
                       "fnOS main service has a graceful stop budget")
            self.check("fnpack:compose:resource-limits", 'cpus: "2.0"' in fnpack_compose and "memory: 512M" in fnpack_compose,
                       "fnOS main service declares CPU and memory limits")
            self.check("fnpack:compose:healthcheck", "/health/live" in fnpack_compose,
                       "fnOS Compose project has a liveness health check")
            self.check("fnpack:compose:healthcheck-static-tool",
                       '["CMD", "/bin/busybox", "wget"' in fnpack_compose,
                       "fnOS Compose health check uses the runtime-native busybox path")
            self.check("fnpack:compose:volume-count", fnpack_compose.count("TRIM_PKGVAR") == 4,
                       "exactly four persistent roots are mounted")

        if fnpack_ui is not None:
            try:
                ui_config = json.loads(fnpack_ui)
            except json.JSONDecodeError as exc:
                self.check("fnpack:ui:json", False, f"invalid JSON: {exc}")
                ui_config = None
            entry = ui_config.get(".url", {}).get("multifunplayer.Application", {}) if isinstance(ui_config, dict) else {}
            self.check("fnpack:ui:entry", isinstance(entry, dict) and entry.get("type") == "url",
                       "Web entry is declared as a URL")
            self.check("fnpack:ui:port", isinstance(entry, dict) and entry.get("port") == "8080",
                       "Web entry targets the container port")
            self.check("fnpack:ui:path", isinstance(entry, dict) and entry.get("url") == "/",
                       "Web entry opens the SPA root")

        for relative in ("deploy/fnos/fnpack/config/privilege", "deploy/fnos/fnpack/config/resource"):
            content = fnpack_privilege if relative.endswith("privilege") else fnpack_resource
            try:
                parsed = json.loads(content) if content is not None else None
                self.check(f"fnpack:json:{relative}", isinstance(parsed, dict), "valid JSON object")
            except json.JSONDecodeError as exc:
                self.check(f"fnpack:json:{relative}", False, f"invalid JSON: {exc}")

        if fnpack_resource is not None:
            try:
                resource = json.loads(fnpack_resource)
            except json.JSONDecodeError:
                resource = {}
            shares = resource.get("data-share", {}).get("shares", []) if isinstance(resource, dict) else []
            share_names = {share.get("name") for share in shares if isinstance(share, dict)}
            self.check("fnpack:resource:shares", share_names == {"config", "keys", "scripts", "logs"},
                       "resource declares all four persistent data shares")
            projects = resource.get("docker-project", {}).get("projects", []) if isinstance(resource, dict) else []
            self.check("fnpack:resource:docker", any(
                isinstance(project, dict) and project.get("name") == "multifunplayer" and project.get("path") == "docker"
                for project in projects
            ), "resource points fnOS to the Docker project")

        if fnpack_build is not None:
            self.check("fnpack:build-wrapper", "build --directory" in fnpack_build and "--platform" in fnpack_build,
                       "repository wrapper exposes explicit architecture builds")
            self.check("fnpack:build-relative-output", "output_dir=$(CDPATH= cd -- \"$output_dir\"" in fnpack_build,
                       "FPK output directory is normalized before fnpack runs")
            self.check("fnpack:build-checksum", "sha256sum" in fnpack_build and "checksum_file=" in fnpack_build,
                       "FPK build writes a portable SHA-256 sidecar")

        if fnpack_validate is not None:
            self.check("fnpack:validation-wrapper", "app.tgz" in fnpack_validate and "service_port" in fnpack_validate,
                       "repository wrapper validates the packaged app and manifest")

        if dockerfile is not None:
            self.check("docker:multi-stage", dockerfile.count("FROM ") >= 3,
                       "web build, server build, and runtime stages are present")
            base_images = re.findall(r"(?m)^FROM\s+([^\s]+)", dockerfile)
            self.check("docker:pinned-base-images",
                       len(base_images) >= 3
                       and all("@sha256:" in image and re.search(r"@sha256:[0-9a-f]{64}", image)
                               for image in base_images[:3]),
                       "all build and runtime base images use reviewed manifest digests")
            self.check("docker:non-root", re.search(r"(?m)^USER\s+mfp\s*$", dockerfile) is not None,
                       "runtime image declares the mfp user")
            self.check("docker:healthcheck", "HEALTHCHECK" in dockerfile and "/health/live" in dockerfile,
                       "runtime health check targets /health/live")
            self.check("docker:healthcheck-static-tool",
                       "busybox-static" in dockerfile
                       and "/bin/busybox wget" in dockerfile
                       and "COPY --from=web-build /bin/busybox" not in dockerfile,
                       "runtime health check uses a static binary native to the runtime image")
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
            for entry in (".git", ".agent", "**/bin", "**/obj", "Source/MultiFunPlayer.Web/node_modules",
                          "Source/MultiFunPlayer.Web/playwright-report",
                          "Source/MultiFunPlayer.Web/test-results", "Docs/node_modules",
                          "Docs/build", "Docs/.docusaurus"):
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
            self.check("compose:healthcheck-static-tool",
                       '["CMD", "/bin/busybox", "wget"' in compose,
                       "Compose health check uses the runtime-native busybox path")
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
            self.check("fnos:external-schema-warning", "does not replace installation" in fnos_readme,
                       "README keeps real fnOS installation validation as an external gate")

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
