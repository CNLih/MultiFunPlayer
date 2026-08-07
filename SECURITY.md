# Security policy

MultiFunPlayer is intended for a trusted home or lab network. The server
supports bearer authentication and one-time pairing; the Docker and fnOS
deployment contracts require an explicit bootstrap token. Anonymous mode is
only suitable for an isolated local development run. This is not a
multi-tenant internet service and it should not be exposed directly to the
public internet.

## Reporting a vulnerability

Please use a private GitHub Security Advisory for this repository when that
feature is available. Do not publish credentials, exploit details, or a working
proof of concept in a public issue. Include the affected version or commit,
deployment mode (Docker, fnOS, or desktop), reproduction steps, impact, and any
logs with secrets removed. If private advisories are unavailable, contact the
maintainer through the repository owner before disclosure.

## Deployment requirements

- Set `MFP_AUTH_TOKEN` to a random value of at least 16 characters, or set
  `MFP_PAIRING_TOKEN` for one-time bootstrap pairing. Never commit either value.
- Keep `/config`, `/keys`, `/scripts`, and `/logs` on private volumes. Back up
  them with access controls matching the host account.
- Do not use privileged containers. Pass only the required serial device and
  grant the container user the narrowest group permission needed to open it.
- Put TLS and LAN access controls at a trusted reverse proxy when traffic can
  leave the local network. Health endpoints intentionally remain unauthenticated
  for container orchestration.
- Keep media and script roots explicitly mapped. The server rejects traversal,
  symbolic-link escapes, and oversized uploads, but host filesystem permissions
  remain part of the security boundary.

## Secret handling

API keys and device tokens are read from environment variables by name and are
not written to registry JSON. Runtime logs redact bearer values, token-like
fields, and configured authentication tokens. Before a release, run the static
packaging check and inspect persisted JSONL logs and configuration fixtures for
credentials.

## Release checks

The repository-owned static gate is:

```sh
./deploy/validate-packaging.sh
```

It does not replace external validation. A release must also run `dotnet list
<solution> package --vulnerable --include-transitive`, `npm audit --omit=dev
--audit-level=high` for runtime Web assets, and `npm --prefix Docs audit
--audit-level=high` for the full documentation toolchain.
Also run `python3 tools/generate-sbom.py --output artifacts/sbom/multifunplayer.cyclonedx.json`,
`python3 tools/generate-license-report.py --input artifacts/sbom/multifunplayer.cyclonedx.json --output artifacts/sbom/THIRD_PARTY_LICENSES.md`,
and `python3 tools/generate-fpk-sbom.py` plus the license report for each
architecture-specific FPK,
and a scanner such as Trivy against the exact multi-arch image. Record tool
versions, findings, and accepted exceptions with the release candidate. fnOS
schema validation and FPK signing must use the `fnpack` version provided by the
target firmware.
