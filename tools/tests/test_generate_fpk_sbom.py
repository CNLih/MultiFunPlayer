import importlib.util
import io
import tarfile
import tempfile
import unittest
from pathlib import Path


_module_path = Path(__file__).resolve().parents[1] / "generate-fpk-sbom.py"
_module_spec = importlib.util.spec_from_file_location("generate_fpk_sbom", _module_path)
assert _module_spec is not None and _module_spec.loader is not None
generate_fpk_sbom = importlib.util.module_from_spec(_module_spec)
_module_spec.loader.exec_module(generate_fpk_sbom)


def tar_bytes(name: str, content: bytes) -> bytes:
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w:gz") as archive:
        member = tarfile.TarInfo(name)
        member.size = len(content)
        archive.addfile(member, io.BytesIO(content))
    return stream.getvalue()


class GenerateFpkSbomTests(unittest.TestCase):
    def test_collects_outer_and_nested_files_with_hashes_and_licenses(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "multifunplayer.fpk"
            app = tar_bytes("ui/config", b"{}\n")
            with tarfile.open(package, mode="w:gz") as archive:
                for name, content in (("manifest", b"appname = multifunplayer\n"), ("app.tgz", app)):
                    member = tarfile.TarInfo(name)
                    member.size = len(content)
                    archive.addfile(member, io.BytesIO(content))

            components, digest = generate_fpk_sbom.collect(package)

            self.assertEqual(64, len(digest))
            self.assertEqual({"fpk/manifest", "fpk/app.tgz", "app.tgz/ui/config"},
                             {item["name"] for item in components})
            self.assertTrue(all(item["licenses"] for item in components))
            self.assertTrue(all(item["hashes"][0]["alg"] == "SHA-256" for item in components))

    def test_rejects_traversal_and_symbolic_link_members(self):
        with tempfile.TemporaryDirectory() as directory:
            for name, make_member in (
                ("traversal.fpk", lambda archive: self._add_file(archive, "../escape", b"x")),
                ("link.fpk", self._add_link),
            ):
                package = Path(directory) / name
                with tarfile.open(package, mode="w:gz") as archive:
                    make_member(archive)
                with self.assertRaises(ValueError):
                    generate_fpk_sbom.collect(package)

    @staticmethod
    def _add_file(archive: tarfile.TarFile, name: str, content: bytes) -> None:
        member = tarfile.TarInfo(name)
        member.size = len(content)
        archive.addfile(member, io.BytesIO(content))

    @staticmethod
    def _add_link(archive: tarfile.TarFile) -> None:
        member = tarfile.TarInfo("manifest-link")
        member.type = tarfile.SYMTYPE
        member.linkname = "manifest"
        archive.addfile(member)


if __name__ == "__main__":
    unittest.main()
