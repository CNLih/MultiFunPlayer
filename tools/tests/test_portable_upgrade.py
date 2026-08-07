import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "portable_upgrade", ROOT / "tools" / "run-portable-upgrade.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("run-portable-upgrade.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PortableUpgradeTests(unittest.TestCase):
    def test_interrupt_server_forces_nonzero_child_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            log_path = Path(directory) / "child.log"
            log = log_path.open("w", encoding="utf-8")
            process = subprocess.Popen(
                [sys.executable, "-c", "import time; time.sleep(60)"],
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            forced = MODULE.interrupt_server(process, log)

        self.assertTrue(forced)
        self.assertIsNotNone(process.returncode)
        self.assertNotEqual(process.returncode, 0)


if __name__ == "__main__":
    unittest.main()
