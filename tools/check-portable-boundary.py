#!/usr/bin/env python3
"""Check that Core and Application stay free of desktop-only dependencies."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


PORTABLE_PROJECTS = (
    Path("Source/MultiFunPlayer.Core"),
    Path("Source/MultiFunPlayer.Application"),
)
FORBIDDEN_PATTERNS = (
    ("WPF", re.compile(r"\b(?:System\.Windows|WindowsBase|PresentationCore|PresentationFramework)\b")),
    ("Stylet", re.compile(r"\bStylet(?:\.|\b)")),
    ("Win32/WMI", re.compile(r"\b(?:Microsoft\.Win32|System\.Management)\b")),
    ("registry", re.compile(r"\bRegistry(?:Key|Value)?\b")),
    ("DPAPI", re.compile(r"\b(?:ProtectedData|DataProtectionScope)\b")),
)


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)
    return re.sub(r"//[^\r\n]*", "", text)


def scan(root: Path) -> dict[str, Any]:
    violations: list[dict[str, str]] = []
    files_scanned = 0
    for relative_project in PORTABLE_PROJECTS:
        project = root / relative_project
        if not project.is_dir():
            violations.append({"path": str(relative_project), "reason": "project directory is missing"})
            continue
        candidates = sorted(project.rglob("*.cs")) + sorted(project.glob("*.csproj"))
        for path in candidates:
            if any(part in {"bin", "obj"} for part in path.parts):
                continue
            files_scanned += 1
            content = strip_comments(path.read_text(encoding="utf-8-sig"))
            for line_number, line in enumerate(content.splitlines(), start=1):
                for name, pattern in FORBIDDEN_PATTERNS:
                    if pattern.search(line):
                        violations.append({
                            "path": str(path.relative_to(root)),
                            "line": str(line_number),
                            "reason": f"forbidden {name} reference",
                        })
    return {
        "result": "pass" if not violations else "fail",
        "projects": [str(path) for path in PORTABLE_PROJECTS],
        "filesScanned": files_scanned,
        "violations": violations,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path,
                        default=Path(__file__).resolve().parents[1])
    parser.add_argument("--json", action="store_true", dest="as_json")
    args = parser.parse_args()
    result = scan(args.root.resolve())
    if args.as_json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"Portable boundary: {result['result']} ({result['filesScanned']} files)")
        for violation in result["violations"]:
            location = violation["path"]
            if "line" in violation:
                location += ":" + violation["line"]
            print(f"ERROR: {location}: {violation['reason']}")
    return 0 if result["result"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
