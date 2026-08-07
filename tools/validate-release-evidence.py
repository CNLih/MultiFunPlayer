#!/usr/bin/env python3
"""Validate the machine-readable evidence manifest for a release candidate.

The manifest deliberately separates repository-local checks from target-host
evidence. A pending gate is useful during development, while
``--require-complete`` is the release-candidate gate and rejects every pending
or failed item.
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any


REQUIRED_GATES = (
    "p0-04-windows-ci",
    "p1-04-wpf-xaml",
    "p1-05-wpf-plugin",
    "p1-06-windows-framework-dependent",
    "p6-06-docker-multiarch",
    "p6-11-volume-upgrade",
    "p7-01-validation-matrix",
    "p7-02-eight-hour-soak",
    "p7-03-interruption-matrix",
    "p7-06-security-scan",
    "p7-07-sbom-license",
    "p7-09-fnos-upgrade-rollback",
    "p7-10-release-candidate",
    "accept-linux-docker",
    "accept-fnos-web-entry",
    "accept-output-protocols",
    "accept-config-key-persistence",
    "accept-physical-architectures",
    "accept-tier-evidence",
    "accept-wpf-rollback",
)
STATUSES = {"pass", "pending", "fail"}
EVIDENCE_KINDS = {
    "artifact",
    "command",
    "review",
    "target-device",
    "workflow-run",
}
EVIDENCE_PREFIXES = {
    "artifact": ("artifact:", "path:", "https://"),
    "command": ("command:",),
    "review": ("review:",),
    "target-device": ("device:", "artifact:", "https://"),
    "workflow-run": ("run:", "https://"),
}
PLACEHOLDER_REFS = {"local-check", "pending", "todo", "replace-me"}


def _validate_evidence(gate_id: str, index: int,
                       item: Any, errors: list[str]) -> None:
    prefix = f"{gate_id}.evidence[{index}]"
    if not isinstance(item, dict):
        errors.append(f"{prefix} must be an object")
        return
    kind = item.get("kind")
    if kind not in EVIDENCE_KINDS:
        errors.append(f"{prefix}.kind must be a supported evidence type")
    ref = item.get("ref")
    if not isinstance(ref, str) or not ref.strip():
        errors.append(f"{prefix}.ref is required")
    elif ref.strip().lower() in PLACEHOLDER_REFS:
        errors.append(f"{prefix}.ref cannot be a placeholder")
    elif kind in EVIDENCE_PREFIXES and not ref.startswith(EVIDENCE_PREFIXES[kind]):
        errors.append(f"{prefix}.ref has an invalid prefix for {kind}")
    observed = item.get("observedAtUtc")
    if not isinstance(observed, str) or not observed.strip():
        errors.append(f"{prefix}.observedAtUtc is required")
    else:
        try:
            parsed = datetime.fromisoformat(observed.replace("Z", "+00:00"))
            if parsed.tzinfo is None:
                raise ValueError("timezone required")
        except ValueError:
            errors.append(f"{prefix}.observedAtUtc must be an ISO-8601 timestamp with timezone")


def validate_manifest(data: Any, require_complete: bool = False) -> dict[str, Any]:
    errors: list[str] = []
    if not isinstance(data, dict):
        return {"result": "fail", "errors": ["manifest root must be an object"]}
    if data.get("schemaVersion") != 1:
        errors.append("schemaVersion must be 1")
    release = data.get("release")
    if not isinstance(release, str) or not release.strip():
        errors.append("release must be a non-empty string")

    raw_gates = data.get("gates")
    if not isinstance(raw_gates, list):
        errors.append("gates must be an array")
        raw_gates = []

    gates: dict[str, dict[str, Any]] = {}
    for index, raw_gate in enumerate(raw_gates):
        if not isinstance(raw_gate, dict):
            errors.append(f"gates[{index}] must be an object")
            continue
        gate_id = raw_gate.get("id")
        if not isinstance(gate_id, str) or not gate_id:
            errors.append(f"gates[{index}].id must be a non-empty string")
            continue
        if gate_id in gates:
            errors.append(f"duplicate gate: {gate_id}")
            continue
        gates[gate_id] = raw_gate
        if gate_id not in REQUIRED_GATES:
            errors.append(f"unknown gate: {gate_id}")

        status = raw_gate.get("status")
        if status not in STATUSES:
            errors.append(f"{gate_id}.status must be one of pass, pending, fail")
        evidence = raw_gate.get("evidence", [])
        if not isinstance(evidence, list):
            errors.append(f"{gate_id}.evidence must be an array")
        else:
            for evidence_index, item in enumerate(evidence):
                _validate_evidence(gate_id, evidence_index, item, errors)
        if status == "pass" and not evidence:
            errors.append(f"{gate_id}.pass requires at least one evidence item")

    missing = [gate_id for gate_id in REQUIRED_GATES if gate_id not in gates]
    errors.extend(f"missing gate: {gate_id}" for gate_id in missing)
    pending = [gate_id for gate_id in REQUIRED_GATES
               if gates.get(gate_id, {}).get("status") == "pending"]
    failed = [gate_id for gate_id in REQUIRED_GATES
              if gates.get(gate_id, {}).get("status") == "fail"]
    if require_complete:
        errors.extend(f"gate is not complete: {gate_id}" for gate_id in pending)
        errors.extend(f"gate failed: {gate_id}" for gate_id in failed)

    passed = [gate_id for gate_id in REQUIRED_GATES
              if gates.get(gate_id, {}).get("status") == "pass"]
    return {
        "result": "pass" if not errors else "fail",
        "release": release if isinstance(release, str) else None,
        "required": len(REQUIRED_GATES),
        "passed": len(passed),
        "pending": len(pending),
        "failed": len(failed),
        "errors": errors,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--require-complete", action="store_true",
                        help="fail unless every required gate is passed")
    parser.add_argument("--json", action="store_true", dest="as_json",
                        help="emit the result as JSON")
    args = parser.parse_args()
    try:
        data = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        result = {"result": "fail", "errors": [f"cannot read manifest: {error}"]}
    else:
        result = validate_manifest(data, args.require_complete)

    if args.as_json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print("Release evidence: " + result["result"])
        if "required" in result:
            print("gates={required} passed={passed} pending={pending} failed={failed}".format(**result))
        for error in result.get("errors", []):
            print(f"ERROR: {error}")
    return 0 if result["result"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
