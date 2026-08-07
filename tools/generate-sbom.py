#!/usr/bin/env python3
"""Generate a dependency-only CycloneDX SBOM for the repository.

The generator consumes restored NuGet assets and committed npm lockfiles, with
package metadata and unambiguous license files from the lockfile-pinned
``node_modules`` trees as a fallback for old npm packages whose license
declaration is omitted from the lockfile.
It does not inspect a container image; image SBOM and vulnerability scanning
remain release-environment gates.
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
    license_url: str | None = None
    for element in document.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        value = (element.text or "").strip()
        if tag == "license" and value:
            return value
        if tag == "licenseUrl" and value:
            license_url = value
    # Older NuGet packages use licenseUrl instead of the SPDX-aware license
    # element. Preserve that authoritative URL instead of silently reporting
    # the component as having no license metadata.
    return license_url


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


def npm_license_value(value: object) -> str | None:
    """Normalize current and legacy npm license metadata to one name."""
    if isinstance(value, str):
        return value.strip() or None
    if isinstance(value, dict):
        for key in ("type", "id", "name"):
            result = npm_license_value(value.get(key))
            if result:
                return result
        return None
    if isinstance(value, list):
        for item in value:
            result = npm_license_value(item)
            if result:
                return result
    return None


def npm_license_file(package_root: Path) -> str | None:
    """Recognize a small set of unambiguous license-file headers.

    Some old npm packages omit both ``license`` and ``licenses`` metadata but
    ship an authoritative file in the package tarball. Only exact, well-known
    text markers are accepted; package names and repository guesses are never
    used as license evidence.
    """
    for candidate in sorted(package_root.iterdir() if package_root.is_dir() else ()):
        if not candidate.is_file() or candidate.name.lower() not in {
            "license", "license.md", "license.txt", "copying", "copying.md", "copying.txt",
        }:
            continue
        try:
            text = candidate.read_text(encoding="utf-8", errors="ignore").lower()
        except OSError:
            continue
        if "permission is hereby granted, free of charge" in text and "the software is provided" in text:
            return "MIT"
        if "apache license" in text and "version 2.0" in text:
            return "Apache-2.0"
    return None


def npm_license(lockfile: Path, package_path: str, metadata: dict) -> str | None:
    """Read a lockfile license, then the installed package manifest.

    npm v7+ does not always copy legacy ``licenses`` arrays into a v3
    lockfile. The package manifest is safe to consult here because ``npm ci``
    materializes the exact integrity-pinned package before SBOM generation.
    """
    for key in ("license", "licenses"):
        result = npm_license_value(metadata.get(key))
        if result:
            return result

    package_json = lockfile.parent / package_path / "package.json"
    try:
        package_metadata = json.loads(package_json.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    if not isinstance(package_metadata, dict) or (
        str(package_metadata.get("version", "")) != str(metadata.get("version", ""))
    ):
        return None
    for key in ("license", "licenses"):
        result = npm_license_value(package_metadata.get(key))
        if result:
            return result
    return npm_license_file(package_json.parent)


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
        packages = document.get("packages", {})
        for path, metadata in packages.items():
            if not path.startswith("node_modules/"):
                continue
            package_path = path.removeprefix("node_modules/")
            license_path = path
            if not metadata.get("version"):
                # npm represents a local file dependency as a link entry under
                # node_modules plus a separate package entry at its resolved
                # path. Follow that target so vendored packages are visible in
                # the SBOM and retain their manifest/license evidence.
                if not metadata.get("link"):
                    continue
                target_path = metadata.get("resolved")
                target_metadata = packages.get(target_path)
                if not isinstance(target_path, str) or not isinstance(target_metadata, dict):
                    continue
                if not target_metadata.get("version"):
                    continue
                metadata = target_metadata
                license_path = target_path
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
                npm_license(lockfile, license_path, metadata),
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
