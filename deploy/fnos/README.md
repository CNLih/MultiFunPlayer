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
