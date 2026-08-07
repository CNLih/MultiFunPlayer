# fnOS package project

`fnpack/` is an official-template-shaped Docker application project for the
MultiFunPlayer Web server. It contains the manifest, Docker Compose project,
Web entry, icons, lifecycle scripts, resource/privilege declarations, and a
non-interactive wizard placeholder required by current `fnpack` validation.

The older JSON `manifest` beside this file remains a repository-owned contract
for Docker/fnOS static checks. It is not an FPK manifest and is intentionally
kept separate from `fnpack/manifest`.

Install the official [`fnpack`](https://developer.fnnas.com/docs/cli/fnpack/)
binary from the fnOS developer portal, then build an architecture-specific
package:

```sh
FNPACK=/path/to/fnpack ./deploy/fnos/build-fpk.sh --platform x86
FNPACK=/path/to/fnpack ./deploy/fnos/build-fpk.sh --platform arm
```

The wrapper maps fnOS `x86`/`arm` package labels to the Docker image's
`linux/amd64`/`linux/arm64` builds. It refuses to overwrite an existing FPK
unless `--force` is supplied. The Compose project uses fnOS's
`TRIM_SERVICE_PORT` and `TRIM_PKGVAR` variables and preserves separate
`config`, `keys`, `scripts`, and `logs` shares across upgrade and uninstall.

The image in the Compose project defaults to `multifunplayer:local` so local
development is deterministic. A release must set `MFP_IMAGE` to the published
multi-architecture image before installing the FPK. USB serial access remains
an explicit device permission and is not enabled by default.

Run the repository-owned static checks before using `fnpack`:

```sh
./deploy/validate-packaging.sh
```

This check confirms both the portable container contract and the canonical
fnpack project structure. Running `fnpack build` validates the package with the
installed official tool, but does not replace installation, upgrade, rollback,
uninstall, serial, or ARM tests on a real fnOS device.

After building, verify the archive contents before transferring it to fnOS:

```sh
./deploy/validate-fpk.sh artifacts/fnos/x86/multifunplayer.fpk x86
./deploy/validate-fpk.sh artifacts/fnos/arm/multifunplayer.fpk arm
(cd artifacts/fnos/x86 && sha256sum --check multifunplayer.fpk.sha256)
(cd artifacts/fnos/arm && sha256sum --check multifunplayer.fpk.sha256)
```

`build-fpk.sh` writes the matching `multifunplayer.fpk.sha256` sidecar next to
each package so the archive can be verified after transfer.
