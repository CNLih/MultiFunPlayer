#!/usr/bin/env python3
"""Render a deterministic third-party license inventory from CycloneDX JSON.

The SBOM remains the authoritative component-to-license mapping. This report
is a reviewer-friendly summary and deliberately keeps components without a
license declaration visible instead of guessing a license from package names.
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path


def license_names(component: dict) -> list[str]:
    names: list[str] = []
    for entry in component.get("licenses", []):
        license_data = entry.get("license", {}) if isinstance(entry, dict) else {}
        if not isinstance(license_data, dict):
            continue
        value = license_data.get("id") or license_data.get("name")
        if value:
            names.append(str(value).strip())
    return sorted(set(name for name in names if name))


def render(document: dict) -> str:
    components = [item for item in document.get("components", []) if isinstance(item, dict)]
    by_license: dict[str, list[dict]] = defaultdict(list)
    unknown: list[dict] = []
    for component in sorted(components, key=lambda item: item.get("purl", "")):
        names = license_names(component)
        if not names:
            unknown.append(component)
            continue
        for name in names:
            by_license[name].append(component)

    known_count = len(components) - len(unknown)
    lines = [
        "# Third-party license inventory",
        "",
        "Generated from the supplied CycloneDX SBOM. The exact "
        "runtime image and fnOS FPK may add platform packages and require a "
        "separate release-environment review.",
        "",
        f"- Components: {len(components)}",
        f"- Components with a license declaration: {known_count}",
        f"- Components without a license declaration: {len(unknown)}",
        "",
        "## License summary",
        "",
        "| License | Components |",
        "| --- | ---: |",
    ]
    for name in sorted(by_license):
        escaped = name.replace("|", "\\|")
        lines.append(f"| `{escaped}` | {len(by_license[name])} |")

    lines.extend(["", "## Components by license", ""])
    for name in sorted(by_license):
        lines.extend([f"### `{name}`", ""])
        for component in by_license[name]:
            lines.append(f"- `{component.get('purl', component.get('name', 'unknown'))}`")
        lines.append("")

    lines.extend(["## Missing declarations", ""])
    if unknown:
        lines.append("These components need release-owner review before distribution:")
        lines.append("")
        for component in unknown:
            lines.append(f"- `{component.get('purl', component.get('name', 'unknown'))}`")
    else:
        lines.append("None.")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="CycloneDX JSON input")
    parser.add_argument("--output", type=Path, required=True, help="Markdown output")
    args = parser.parse_args()
    document = json.loads(args.input.read_text(encoding="utf-8"))
    if document.get("bomFormat") != "CycloneDX":
        raise SystemExit("license report input must be a CycloneDX document")
    output = args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(render(document), encoding="utf-8")
    print(f"License report: {len(document.get('components', []))} components -> {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
