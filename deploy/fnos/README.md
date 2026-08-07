# fnOS package scaffold

`manifest` describes the container contract expected by the fnOS Docker app
manager: HTTP port `8080`, persistent configuration/key/script/log volumes, and
a required bootstrap token. It deliberately does not claim to be an official
`.fpk` package because fnOS package schemas and `fnpack` versions vary by NAS
firmware.

Before publishing, validate the manifest with the target's official tooling,
build the image for the target architecture, and test upgrade preservation of
all four volumes. USB serial access must be an explicit device permission and
is not enabled by default.

Run the repository-owned static checks before using `fnpack`:

```sh
./deploy/validate-packaging.sh
```

The check confirms the documented port, volume, health, secret, and upgrade
invariants. It deliberately cannot certify an FPK: field names, Web-entry
metadata, signing, uninstall hooks, and architecture support must be checked
against the fnOS firmware and `fnpack` version used for release.
