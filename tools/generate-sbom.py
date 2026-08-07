#!/usr/bin/env python3
"""Generate a dependency-only CycloneDX SBOM for the repository.

The generator consumes restored NuGet assets and committed npm lockfiles, so
the output describes the exact dependency graph used by a build rather than a
best-effort scan of source text. It does not inspect a container image; image
SBOM and vulnerability scanning remain release-environment gates.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import quote


def license_entry(value: str | None) -> dict | None:
    if not value:
        return None
    value = value.strip()
    if not value:
        return None
    if re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]*(?:\s+WITH\s+[A-Za-z0-9.-]+)?", value):
        return {"license": {"id": value}}
    return {"license": {"name": value}}


def nuget_license(root: Path, package_id: str, version: str) -> str | None:
    package_root = root / package_id.lower() / version.lower()
    if not package_root.is_dir():
        return None
    nuspecs = sorted(package_root.glob("*.nuspec"))
    if not nuspecs:
        return None
    try:
        document = ET.parse(nuspecs[0])
    except (ET.ParseError, OSError):
        return None
    for element in document.iter():
        if element.tag.rsplit("}", 1)[-1] == "license":
            return (element.text or "").strip() or None
    return None


def add_component(components: dict[str, dict], name: str, version: str,
                  purl: str, license_name: str | None = None) -> None:
    if not name or not version:
        return
    item = components.setdefault(purl, {
        "type": "library",
        "name": name,
        "version": version,
        "purl": purl,
    })
    if license_name and "licenses" not in item:
        entry = license_entry(license_name)
        if entry:
            item["licenses"] = [entry]


def collect_nuget(root: Path, components: dict[str, dict]) -> int:
    package_root = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    assets_count = 0
    for assets in sorted(root.glob("Source/**/obj/project.assets.json")):
        try:
            document = json.loads(assets.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        assets_count += 1
        for key, metadata in document.get("libraries", {}).items():
            if metadata.get("type") != "package" or "/" not in key:
                continue
            package_id, version = key.rsplit("/", 1)
            add_component(
                components,
                package_id,
                version,
                f"pkg:nuget/{quote(package_id, safe='.-_')}@{quote(version, safe='.-_+')}",
                nuget_license(package_root, package_id, version),
            )
    return assets_count


def collect_npm(root: Path, components: dict[str, dict]) -> int:
    lockfile_count = 0
    for lockfile in sorted(root.glob("**/package-lock.json")):
        try:
            document = json.loads(lockfile.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        lockfile_count += 1
        for path, metadata in document.get("packages", {}).items():
            if not path.startswith("node_modules/") or not metadata.get("version"):
                continue
            package_path = path.removeprefix("node_modules/")
            if package_path.startswith("@"):
                name = "/".join(package_path.split("/")[:2])
            else:
                name = package_path.split("/")[0]
            version = str(metadata["version"])
            add_component(
                components,
                name,
                version,
                f"pkg:npm/{quote(name, safe='@._-/')}@{quote(version, safe='.-_+')}",
                metadata.get("license"),
            )
    return lockfile_count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    components: dict[str, dict] = {}
    assets_count = collect_nuget(root, components)
    lockfile_count = collect_npm(root, components)
    if assets_count == 0 or lockfile_count == 0:
        raise SystemExit(
            "SBOM input is incomplete: restore .NET projects and keep committed npm lockfiles present."
        )
    ordered = [components[key] for key in sorted(components)]
    digest = hashlib.sha256(
        json.dumps(ordered, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    bom = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, 'multifunplayer-sbom:' + digest)}",
        "version": 1,
        "metadata": {
            "tools": [{"vendor": "MultiFunPlayer", "name": "generate-sbom.py", "version": "1"}],
            "component": {"type": "application", "name": "MultiFunPlayer", "version": "workspace"},
        },
        "components": ordered,
    }
    output = args.output if args.output.is_absolute() else root / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(bom, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"SBOM: {len(ordered)} components -> {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
