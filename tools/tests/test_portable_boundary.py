import importlib.util
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "check_portable_boundary", ROOT / "tools" / "check-portable-boundary.py")
if SPEC is None or SPEC.loader is None:
    raise ImportError("check-portable-boundary.py could not be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PortableBoundaryTests(unittest.TestCase):
    def test_current_portable_projects_pass(self):
        result = MODULE.scan(ROOT)
        self.assertEqual("pass", result["result"])
        self.assertGreater(result["filesScanned"], 0)
        self.assertEqual([], result["violations"])

    def test_forbidden_source_and_project_references_are_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for project in MODULE.PORTABLE_PROJECTS:
                project_path = root / project
                project_path.mkdir(parents=True)
                (project_path / "Allowed.cs").write_text(
                    "namespace Portable; public sealed class Allowed {}\n",
                    encoding="utf-8")
                (project_path / "Portable.csproj").write_text(
                    "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                    "</PropertyGroup></Project>", encoding="utf-8")
            (root / MODULE.PORTABLE_PROJECTS[0] / "Forbidden.cs").write_text(
                "using System.Windows;\npublic sealed class Forbidden {}\n",
                encoding="utf-8")
            (root / MODULE.PORTABLE_PROJECTS[1] / "Portable.csproj").write_text(
                "<Project><PackageReference Include=\"Stylet\" /></Project>",
                encoding="utf-8")

            result = MODULE.scan(root)

        self.assertEqual("fail", result["result"])
        reasons = " ".join(item["reason"] for item in result["violations"])
        self.assertIn("WPF", reasons)
        self.assertIn("Stylet", reasons)


if __name__ == "__main__":
    unittest.main()
