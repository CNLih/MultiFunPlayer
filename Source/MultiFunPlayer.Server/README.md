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
`[REDACTED]` before retention. The same sanitized entries are appended as
bounded JSONL under the configured logs root and can be downloaded from
`GET /api/v1/logs/file`; once the file reaches 4 MiB it is rotated to
`runtime.log.1`.

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
lists portable direct transport contracts (`tcp`, `udp`, `websocket`,
`ofs-websocket`, `serial`, and `file`) separately from adapter implementation status. Entries marked
`planned` or `contract-only` describe migration boundaries, not active
connections. Windows-player integrations are marked `external-proxy` or
`unsupported` with required services; this endpoint does not create pretend
media/output CRUD resources.

`POST /api/v1/scripts` accepts `{ "id": "name.funscript", "content": "..." }`
and writes only valid JSON below the configured scripts root. Uploads are
limited to 8 MiB, reject path traversal and symbolic links, and do not
overwrite an existing file unless `overwrite: true` is supplied.

Output targets are managed through `/api/v1/output-targets` and the
`connect`, `disconnect`, update, and delete subroutes. The direct registry
supports `tcp`, `udp`, `websocket`, `file`, `serial`, and `the-handy`; definitions
are stored as non-secret JSON below the config root. File endpoints are relative
to the logs root, while serial endpoints must use a stable Linux device path
or a COM name. The Handy endpoints use the existing Handy HTTP API v2 and
accept a `sourceAxis` (default `L0`) and `updateRateHz` from 1 to 20 (default
20). The server samples engine frames per target at that rate; seeking
backwards resets the sampler so a seek is delivered immediately. Set
`MFP_THE_HANDY_CONNECTION_KEY` in the
host environment; the key is sent as `X-Connection-Key` and is never persisted
or returned by the API. Connection failures remain visible as `lastError` and
do not terminate the host. A The Handy endpoint may point to a local HTTP
simulator for integration testing, but production devices should use the
official HTTPS API endpoint.

Media sources are managed through `/api/v1/media-sources` and the matching
`connect`, `disconnect`, update, and delete subroutes. The `internal` source
is directly available to the headless timeline. OFS is a direct WebSocket
adapter implementing the event/command envelope observed in the desktop
client: `media_change`, `play_change`, `duration_change`, `time_change`,
`playbackspeed_change`, `funscript_change`, `funscript_remove`, and
`project_change`, with `change_play`, `change_time`, and
`change_playbackspeed` commands. OFS has no capability handshake in that
protocol, so the server reports only a successful WebSocket upgrade and does
not claim unobserved features. Emby, Jellyfin, and Plex remain `planned`,
while MPV, VLC, MPC, Whirligig, DeoVR, and HereSphere are reported as
`blocked` external-proxy capabilities until a remote adapter or companion
process is provided. Source endpoints accept only `http(s)` or `ws(s)` URLs
and never accept embedded credentials; unsupported connections return a
visible error rather than pretending to be connected.
