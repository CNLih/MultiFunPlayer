import importlib.util
import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "validate_release_evidence", ROOT / "tools" / "validate-release-evidence.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("validate-release-evidence.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def manifest(status: str = "pending") -> dict:
    return {
        "schemaVersion": 1,
        "release": "candidate-local",
        "gates": [
            {
                "id": gate_id,
                "status": status,
                "evidence": [{"kind": "test", "ref": "local-check"}]
                if status == "pass" else [],
            }
            for gate_id in MODULE.REQUIRED_GATES
        ],
    }


class ValidateReleaseEvidenceTests(unittest.TestCase):
    def test_pending_manifest_is_structurally_valid(self):
        result = MODULE.validate_manifest(manifest())
        self.assertEqual("pass", result["result"])
        self.assertEqual(len(MODULE.REQUIRED_GATES), result["pending"])

    def test_require_complete_rejects_pending_gates(self):
        result = MODULE.validate_manifest(manifest(), require_complete=True)
        self.assertEqual("fail", result["result"])
        self.assertTrue(any("not complete" in error for error in result["errors"]))

    def test_complete_manifest_requires_evidence_for_passes(self):
        data = manifest("pass")
        result = MODULE.validate_manifest(data, require_complete=True)
        self.assertEqual("pass", result["result"])
        self.assertEqual(len(MODULE.REQUIRED_GATES), result["passed"])

        data["gates"][0]["evidence"] = []
        result = MODULE.validate_manifest(data)
        self.assertEqual("fail", result["result"])
        self.assertIn("pass requires at least one evidence item", " ".join(result["errors"]))

    def test_missing_duplicate_and_unknown_gates_are_rejected(self):
        data = manifest()
        data["gates"] = data["gates"][1:]
        data["gates"].append({"id": "unknown", "status": "pending", "evidence": []})
        data["gates"].append({"id": MODULE.REQUIRED_GATES[1], "status": "pending", "evidence": []})
        result = MODULE.validate_manifest(data)
        self.assertEqual("fail", result["result"])
        self.assertTrue(any("unknown gate" in error for error in result["errors"]))
        self.assertTrue(any("duplicate gate" in error for error in result["errors"]))
        self.assertTrue(any("missing gate" in error for error in result["errors"]))

    def test_checked_in_example_covers_every_required_gate(self):
        example = json.loads(
            (ROOT / "deploy" / "release-evidence.example.json").read_text())
        result = MODULE.validate_manifest(example)
        self.assertEqual("pass", result["result"])
        self.assertEqual(len(MODULE.REQUIRED_GATES), result["pending"])


if __name__ == "__main__":
    unittest.main()
