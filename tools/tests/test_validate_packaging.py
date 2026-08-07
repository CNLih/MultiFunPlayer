import importlib.util
import shutil
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "validate_packaging", ROOT / "deploy" / "validate-packaging.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("validate-packaging.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


@unittest.skipUnless(shutil.which("sh"), "POSIX sh is required")
class ValidatePackagingTests(unittest.TestCase):
    def test_shell_syntax_accepts_valid_and_rejects_invalid_script(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            valid = root / "valid.sh"
            invalid = root / "invalid.sh"
            valid.write_text("#!/bin/sh\nset -eu\nexit 0\n", encoding="utf-8")
            invalid.write_text("#!/bin/sh\nif true; then\n", encoding="utf-8")

            self.assertEqual((True, "lifecycle script passes POSIX shell syntax validation"),
                             MODULE.validate_shell_syntax(valid, root))
            syntax_ok, detail = MODULE.validate_shell_syntax(invalid, root)
            self.assertFalse(syntax_ok)
            self.assertTrue(detail)


if __name__ == "__main__":
    unittest.main()
