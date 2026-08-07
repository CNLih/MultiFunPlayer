---
sidebar_position: 3
---

# Headless Web Operations

This runbook applies to the ASP.NET Core server deployed directly on Linux or
inside the Docker/fnOS contract. The Windows/WPF host remains the rollback
path while the headless target is in beta.

## First run and pairing

For an isolated development instance, leave both authentication variables empty.
For a reachable LAN deployment, set `MFP_AUTH_TOKEN` to a random value of at
least 16 characters, or set `MFP_PAIRING_TOKEN` for one-time pairing. Pair with
`POST /api/v1/auth/pair`:

```json
{"pairingToken":"<bootstrap-token>","authToken":"<new-long-token>"}
```

The server stores only a SHA-256 hash below `/config/auth-token.json`. Use the
new token as a bearer token for REST and as the `mfp-auth.<base64url-token>`
WebSocket subprotocol. Do not put tokens in endpoint URLs or committed files.

## Data and backup policy

Back up the four persistent roots together so a restore keeps configuration,
Data Protection keys, scripts, and diagnostics consistent:

```text
/config   output/media definitions, auth hash, migration record
/keys     ASP.NET Data Protection key ring (when explicitly configured)
/scripts  user Funscript repository
/logs     bounded JSONL runtime diagnostics and file-output artifacts
```

When `/keys` is explicitly mounted, ASP.NET Core rotates Data Protection keys
automatically every 90 days by default. Set
`MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS` between 7 and 3650 days when policy
requires another lifetime. Rotation adds new key files; retain older keys in
the backup and never delete them during routine rotation.

Stop the service before taking a filesystem snapshot. For a running container,
create an archive from the volume mount points, excluding temporary files. A
restore is a replacement of all four roots from the same snapshot; never copy
only `/config` when encrypted values or scripts are required.

Before an upgrade, retain the current image/FPK tag and a dated backup. Start
the new version against a copy of the volumes first. If the health endpoint or
API migration status is not ready, stop it and restart the previous tag with
the untouched volumes.

## Troubleshooting

1. `401 Unauthorized`: verify the bearer token, pairing state, and that the
   browser local storage does not contain an old token. Health endpoints remain
   anonymous for diagnosis.
2. A target is `offline`, or reports `lastError`/a rising `failedFrames` count:
   check endpoint reachability from the container network, then use the target
   connect route. A later successful write clears the transient error. Serial
   targets also require an explicit device mapping and write permission.
3. The Handy reports a missing key: set
   `MFP_THE_HANDY_CONNECTION_KEY` in the host environment. It is intentionally
   never persisted by the server.
4. A media source is `planned` or `blocked`: inspect `/api/v1/adapters`. The
   capability inventory is authoritative; the server does not pretend an
   unimplemented player integration is connected.
5. The browser is stale after an upgrade: reload once and inspect the service
   worker cache version. Backend playback and output continue independently of
   browser connectivity.
6. Collect `/api/v1/logs` and download `/api/v1/logs/file`. Entries are bounded
   and redact token, password, cookie, API-key, authorization, and credential
   fields before retention.

## Health checks

Use `/health/live` for process liveness and `/health/ready` for the engine
state. `/api/v1/settings`, `/api/v1/configuration/migration`, and
`/api/v1/adapters` provide non-secret configuration and capability evidence.
