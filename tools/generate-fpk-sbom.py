#!/usr/bin/env python3
"""Generate a file-level CycloneDX SBOM for a repository-built fnOS FPK.

The package contains repository-owned lifecycle/configuration files and an
``app.tgz`` archive that references, but does not bundle, the runtime image.
This inventory hashes both archive layers and rejects path traversal or links;
the exact container image still needs its own runtime SBOM.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import tarfile
import uuid
from pathlib import Path, PurePosixPath


def safe_name(name: str) -> str:
    path = PurePosixPath(name)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"unsafe archive path: {name}")
    return "/".join(path.parts)


def file_component(name: str, payload: bytes) -> dict:
    return {
        "type": "file",
        "name": name,
        "hashes": [{
            "alg": "SHA-256",
            "content": hashlib.sha256(payload).hexdigest(),
        }],
        "licenses": [{"license": {"id": "MIT"}}],
    }


def read_members(archive: tarfile.TarFile, prefix: str) -> list[dict]:
    components: list[dict] = []
    for member in sorted(archive.getmembers(), key=lambda item: item.name):
        name = safe_name(member.name)
        if member.isdir():
            continue
        if member.issym() or member.islnk() or not member.isfile():
            raise ValueError(f"unsupported non-regular archive member: {member.name}")
        stream = archive.extractfile(member)
        if stream is None:
            raise ValueError(f"could not read archive member: {member.name}")
        components.append(file_component(f"{prefix}/{name}", stream.read()))
    return components


def collect(package: Path) -> tuple[list[dict], str]:
    package_digest = hashlib.sha256(package.read_bytes()).hexdigest()
    with tarfile.open(package, mode="r:gz") as outer:
        components = read_members(outer, "fpk")
        app_member = next((item for item in outer.getmembers() if item.name == "app.tgz"), None)
        if app_member is None or not app_member.isfile():
            raise ValueError("FPK does not contain a regular app.tgz member")
        app_stream = outer.extractfile(app_member)
        if app_stream is None:
            raise ValueError("could not read app.tgz from FPK")
        import io
        with tarfile.open(fileobj=io.BytesIO(app_stream.read()), mode="r:gz") as app:
            components.extend(read_members(app, "app.tgz"))
    return sorted(components, key=lambda item: item["name"]), package_digest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="fnOS FPK input")
    parser.add_argument("--output", type=Path, required=True, help="CycloneDX JSON output")
    args = parser.parse_args()
    package = args.input.resolve()
    if not package.is_file():
        raise SystemExit(f"FPK does not exist: {package}")
    try:
        components, package_digest = collect(package)
    except (OSError, tarfile.TarError, ValueError) as error:
        raise SystemExit(f"could not inspect FPK: {error}") from error

    digest = hashlib.sha256(
        json.dumps(components, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    bom = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, 'multifunplayer-fpk-sbom:' + digest)}",
        "version": 1,
        "metadata": {
            "tools": [{"vendor": "MultiFunPlayer", "name": "generate-fpk-sbom.py", "version": "1"}],
            "component": {
                "type": "application",
                "name": "MultiFunPlayer fnOS FPK",
                "version": package.name,
                "hashes": [{"alg": "SHA-256", "content": package_digest}],
            },
        },
        "components": components,
    }
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(bom, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"FPK SBOM: {len(components)} files -> {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
