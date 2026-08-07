import importlib.util
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "portable_soak", ROOT / "tools" / "run-portable-soak.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("run-portable-soak.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def report(*, graceful=True, sent=10, dropped=0, failed=0, p95=50):
    return {
        "gracefulStop": graceful,
        "output": {
            "sentFrames": sent,
            "droppedFrames": dropped,
            "coalescedFrames": dropped,
            "failedFrames": failed,
        },
        "revisionIntervalMilliseconds": {"p95": p95},
    }


class PortableSoakTests(unittest.TestCase):
    def test_coalesced_count_prefers_canonical_field_for_samples(self):
        self.assertEqual(4, MODULE.coalesced_frame_count({
            "droppedFrames": 0,
            "coalescedFrames": 4,
        }))
        self.assertEqual(2, MODULE.coalesced_frame_count({"droppedFrames": 2}))

    def test_acceptance_passes_observable_smoke_invariants(self):
        result = MODULE.evaluate_acceptance(
            report(dropped=3), max_revision_interval_ms=100)
        self.assertTrue(result["passed"])
        self.assertEqual([], result["failures"])

    def test_acceptance_rejects_abnormal_stop_and_output_loss(self):
        result = MODULE.evaluate_acceptance(
            report(graceful=False, sent=0, dropped=1, failed=2),
            max_revision_interval_ms=100, max_dropped_frames=0)
        self.assertFalse(result["passed"])
        self.assertEqual(4, len(result["failures"]))
        self.assertTrue(any("coalesced 1 frames" in failure for failure in result["failures"]))

    def test_acceptance_prefers_explicit_coalesced_count(self):
        evidence = report(dropped=0)
        evidence["output"]["coalescedFrames"] = 4
        result = MODULE.evaluate_acceptance(evidence, max_dropped_frames=3)
        self.assertFalse(result["passed"])
        self.assertEqual(4, result["observedCoalescedFrames"])

    def test_optional_jitter_budget_is_enforced(self):
        result = MODULE.evaluate_acceptance(report(p95=125), max_revision_interval_ms=100)
        self.assertFalse(result["passed"])
        self.assertIn("p95 exceeds", result["failures"][0])


if __name__ == "__main__":
    unittest.main()
