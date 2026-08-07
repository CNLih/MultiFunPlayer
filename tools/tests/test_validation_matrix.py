import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "run_validation_matrix", ROOT / "tools" / "run-validation-matrix.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("run-validation-matrix.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ValidationMatrixTests(unittest.TestCase):
    def test_build_checks_has_local_and_external_categories(self):
        checks = MODULE.build_checks(ROOT, include_browser=False)
        categories = {check.category for check in checks}
        self.assertTrue({"unit", "integration", "contract", "browser", "interruption"}.issubset(categories))
        self.assertTrue({"container", "target-device"}.issubset(categories))
        playwright = next(check for check in checks if check.check_id == "browser-playwright")
        self.assertIsNone(playwright.command)
        self.assertIn("skipped", playwright.reason)

    def test_pending_check_is_explicit(self):
        check = MODULE.Check("external", "container", None, ROOT, "requires runner")
        result = MODULE.run_check(check, 1)
        self.assertEqual("pending", result["status"])
        self.assertEqual("requires runner", result["reason"])

    def test_missing_local_executable_is_a_failure(self):
        check = MODULE.Check("missing", "unit", ("mfp-command-that-does-not-exist",), ROOT)
        result = MODULE.run_check(check, 1)
        self.assertEqual("fail", result["status"])
        self.assertIn("executable not found", result["reason"])

    def test_playwright_host_dependency_failure_is_pending(self):
        with tempfile.TemporaryDirectory() as directory:
            check = MODULE.Check("browser-playwright", "browser", (sys.executable, "-c",
                "print('error while loading shared libraries: libnspr4.so'); raise SystemExit(1)"), Path(directory))
            result = MODULE.run_check(check, 5)
        self.assertEqual("pending", result["status"])
        self.assertIn("host browser dependencies", result["reason"])

    def test_playwright_assertion_failure_remains_a_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            check = MODULE.Check("browser-playwright", "browser", (sys.executable, "-c",
                "print('expectation failed')\nraise SystemExit(1)"), Path(directory))
            result = MODULE.run_check(check, 5)
        self.assertEqual("fail", result["status"])

    def test_command_output_is_redacted_and_status_is_recorded(self):
        with tempfile.TemporaryDirectory() as directory:
            check = MODULE.Check("echo", "contract", (sys.executable, "-c",
                "print('MFP_AUTH_TOKEN=secret-value')"), Path(directory))
            result = MODULE.run_check(check, 5)
        self.assertEqual("pass", result["status"])
        self.assertEqual(0, result["returnCode"])
        self.assertNotIn("secret-value", result["outputTail"])
        self.assertIn("<redacted>", result["outputTail"])

    def test_non_utf8_command_output_is_replaced_and_recorded(self):
        with tempfile.TemporaryDirectory() as directory:
            check = MODULE.Check("bytes", "contract", (sys.executable, "-c",
                "import sys; sys.stdout.buffer.write(b'prefix\\xff\\xfe')"), Path(directory))
            result = MODULE.run_check(check, 5)
        self.assertEqual("pass", result["status"])
        self.assertIn("prefix", result["outputTail"])
        self.assertIn("�", result["outputTail"])

    def test_summary_counts_statuses(self):
        summary = MODULE.summarize([
            {"status": "pass"}, {"status": "pending"}, {"status": "fail"},
            {"status": "pass"},
        ])
        self.assertEqual({"pass": 2, "pending": 1, "fail": 1}, summary)


if __name__ == "__main__":
    unittest.main()
