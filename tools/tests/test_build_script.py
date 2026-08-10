import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
BUILD_SCRIPT = ROOT / "build.sh"


class BuildScriptTests(unittest.TestCase):
    def run_script(self, *arguments: str, extra_env: dict[str, str] | None = None):
        environment = os.environ.copy()
        environment["MFP_BUILD_DRY_RUN"] = "1"
        if extra_env:
            environment.update(extra_env)
        return subprocess.run(
            ["bash", str(BUILD_SCRIPT), *arguments],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_shell_syntax_and_help_cover_run_and_package_modes(self):
        syntax = subprocess.run(
            ["bash", "-n", str(BUILD_SCRIPT)], cwd=ROOT, check=False)
        self.assertEqual(0, syntax.returncode)

        result = self.run_script("help")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("run server", result.stdout)
        self.assertIn("run web", result.stdout)
        self.assertIn("run docker", result.stdout)
        self.assertIn("package server", result.stdout)
        self.assertIn("package docker", result.stdout)
        self.assertIn("package fnos", result.stdout)

    def test_server_run_resolves_user_supplied_dotnet_and_local_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dotnet = root / "dotnet"
            dotnet.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            dotnet.chmod(0o755)
            result = self.run_script(
                "run", "server", "--no-restore", "--no-web-build",
                "--url", "http://127.0.0.1:18082",
                extra_env={"MFP_DOTNET": str(dotnet), "MFP_LOCAL_ROOT": str(root / "data")},
            )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("http://127.0.0.1:18082", result.stdout)
        self.assertIn(str(dotnet), result.stdout)
        self.assertIn("--no-restore", result.stdout)

    def test_docker_and_fnos_package_commands_are_available_in_dry_run(self):
        with tempfile.TemporaryDirectory() as directory:
            tool = Path(directory) / "tool"
            tool.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            tool.chmod(0o755)
            docker = self.run_script(
                "package", "docker", "arm64",
                extra_env={"MFP_DOCKER": str(tool)},
            )
            fnos = self.run_script(
                "package", "fnos", "arm", "--force",
                extra_env={"FNPACK": str(tool)},
            )

        self.assertEqual(0, docker.returncode, docker.stderr)
        self.assertIn("buildx build", docker.stdout)
        self.assertIn("linux/arm64", docker.stdout)
        self.assertEqual(0, fnos.returncode, fnos.stderr)
        self.assertIn("build-fpk.sh", fnos.stdout)
        self.assertIn("--platform arm", fnos.stdout)
        self.assertIn("validate-fpk.sh", fnos.stdout)


if __name__ == "__main__":
    unittest.main()
