import json
import importlib.util
import tempfile
import unittest
from pathlib import Path

_module_path = Path(__file__).resolve().parents[1] / "generate-sbom.py"
_module_spec = importlib.util.spec_from_file_location("generate_sbom", _module_path)
assert _module_spec is not None and _module_spec.loader is not None
generate_sbom = importlib.util.module_from_spec(_module_spec)
_module_spec.loader.exec_module(generate_sbom)


class GenerateSbomTests(unittest.TestCase):
    def test_collect_npm_reads_legacy_license_array_from_installed_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "Docs"
            (docs / "node_modules" / "legacy-package").mkdir(parents=True)
            (docs / "package-lock.json").write_text(json.dumps({
                "lockfileVersion": 3,
                "packages": {
                    "": {},
                    "node_modules/legacy-package": {"version": "1.2.3"},
                },
            }), encoding="utf-8")
            (docs / "node_modules" / "legacy-package" / "package.json").write_text(
                json.dumps({"name": "legacy-package", "version": "1.2.3",
                            "licenses": [{"type": "MIT"}]}),
                encoding="utf-8")

            components = {}
            self.assertEqual(1, generate_sbom.collect_npm(root, components))
            component = components["pkg:npm/legacy-package@1.2.3"]
            self.assertEqual({"id": "MIT"}, component["licenses"][0]["license"])

    def test_collect_npm_keeps_missing_license_metadata_unresolved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "Docs"
            (docs / "node_modules" / "unknown-package").mkdir(parents=True)
            (docs / "package-lock.json").write_text(json.dumps({
                "lockfileVersion": 3,
                "packages": {
                    "": {},
                    "node_modules/unknown-package": {"version": "0.0.1"},
                },
            }), encoding="utf-8")

            components = {}
            generate_sbom.collect_npm(root, components)
            self.assertNotIn("licenses", components["pkg:npm/unknown-package@0.0.1"])

    def test_collect_npm_ignores_stale_installed_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "Docs"
            package = docs / "node_modules" / "stale-package"
            package.mkdir(parents=True)
            (docs / "package-lock.json").write_text(json.dumps({
                "lockfileVersion": 3,
                "packages": {
                    "": {},
                    "node_modules/stale-package": {"version": "1.2.3"},
                },
            }), encoding="utf-8")
            (package / "package.json").write_text(
                json.dumps({"name": "stale-package", "version": "9.9.9",
                            "license": "MIT"}),
                encoding="utf-8")

            components = {}
            generate_sbom.collect_npm(root, components)
            self.assertNotIn("licenses", components["pkg:npm/stale-package@1.2.3"])

    def test_collect_npm_reads_unambiguous_license_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "Docs"
            package = docs / "node_modules" / "legacy-package"
            package.mkdir(parents=True)
            (docs / "package-lock.json").write_text(json.dumps({
                "lockfileVersion": 3,
                "packages": {
                    "": {},
                    "node_modules/legacy-package": {"version": "1.2.3"},
                },
            }), encoding="utf-8")
            (package / "package.json").write_text(
                json.dumps({"name": "legacy-package", "version": "1.2.3"}),
                encoding="utf-8")
            (package / "License").write_text(
                "Permission is hereby granted, free of charge, to any person obtaining a copy.\n"
                "THE SOFTWARE IS PROVIDED AS IS.\n",
                encoding="utf-8")

            components = {}
            generate_sbom.collect_npm(root, components)
            component = components["pkg:npm/legacy-package@1.2.3"]
            self.assertEqual({"id": "MIT"}, component["licenses"][0]["license"])


if __name__ == "__main__":
    unittest.main()
