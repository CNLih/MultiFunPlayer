# MultiFunPlayer Server

The server is the Linux/fnOS host boundary for the portable `Core` and
`Application` assemblies. It owns HTTP, WebSocket, scheduling, storage and
authentication; it does not reference WPF, Stylet, registry, or Windows device
APIs.

Build the SPA first for a local combined host:

    npm --prefix Source/MultiFunPlayer.Web ci
    npm --prefix Source/MultiFunPlayer.Web run build
    dotnet run --project Source/MultiFunPlayer.Server

The server copies Source/MultiFunPlayer.Web/dist into its output and serves
the SPA with a fallback route. Without a built dist directory it serves the
small static preview instead.

Run from the repository root with `dotnet run --project Source/MultiFunPlayer.Server`.
Set `MFP_SCRIPTS_PATH` to a directory containing `.funscript` files. If
`MFP_AUTH_TOKEN` is non-empty, all API and WebSocket requests require either
`Authorization: Bearer <token>` or `X-MFP-Token: <token>`; health endpoints stay
anonymous. The token is never included in logs or response bodies.
Set `MFP_KEYS_PATH` (or `MFP_KEYS_ROOT`) to enable persistent ASP.NET Data
Protection keys in a mounted volume; without it, local development uses the
framework's ephemeral key repository.

For first-run pairing, set `MFP_PAIRING_TOKEN` and leave `MFP_AUTH_TOKEN` unset.
POST a JSON body containing `pairingToken` and a new `authToken` (at least 16
characters) to `/api/v1/auth/pair`. The server stores only a SHA-256 hash under
`/config/auth-token.json`; pairing is rejected after the first successful run.

`/hubs/state` is a text WebSocket stream of the same status shape returned by
`/api/v1/status`. Each subscriber has a one-item latest-value queue, so a slow
browser cannot retain unbounded snapshots or stop the player.

The contract is available at `/openapi/v1.json`. Browser WebSocket clients use
the `mfp-auth.<base64url-token>` subprotocol because browser APIs cannot attach
a custom header. The token is not placed in the WebSocket URL, avoiding the
usual request-URL access-log leak.

`GET /api/v1/logs?limit=100` returns structured runtime diagnostics. The
in-memory ring is bounded by `MultiFunPlayer:LogCapacity` (default 500); once
full, oldest entries are discarded and the response marks `truncated: true`.
Message fields and structured properties matching token, secret, password,
authorization, cookie, API key, or credential names are replaced with
`[REDACTED]` before retention.

`GET /api/v1/settings` exposes only non-secret effective server settings. The
serial device view is available at `GET /api/v1/devices/serial`; it enumerates
stable `/dev/serial/by-id` links (or `MFP_SERIAL_BY_ID_ROOT`) and never opens a
device. Serial output uses the separate bounded `SerialOutputTransport` and
requires an explicit device mapping and permissions in a container.

To import an existing desktop JSON once, set `MFP_LEGACY_CONFIG_PATH` to an
explicitly mounted file before starting the server. The server reads at most
4 MiB, omits secret-like fields, writes `migrated-configuration.json` under
the config volume, and never modifies the source. If that target already
exists it is left untouched. `GET /api/v1/configuration/migration` reports
`disabled`, `source-not-found`, `migrated`, `already-present`, or `failed`.

`GET /api/v1/adapters` is a versioned, read-only capability inventory. It
lists portable direct transport contracts (`tcp`, `udp`, `websocket`, `serial`,
and `file`) separately from adapter implementation status. Entries marked
`planned` or `contract-only` describe migration boundaries, not active
connections. Windows-player integrations are marked `external-proxy` or
`unsupported` with required services; this endpoint does not create pretend
media/output CRUD resources.
