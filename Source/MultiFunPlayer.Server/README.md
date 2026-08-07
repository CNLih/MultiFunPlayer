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
Set `MFP_SCRIPTS_PATH` (or `MFP_SCRIPTS_ROOT`) to a directory containing
`.funscript` files. If
`MFP_AUTH_TOKEN` is non-empty, all API and WebSocket requests require either
`Authorization: Bearer <token>` or `X-MFP-Token: <token>`; health endpoints stay
anonymous. The token is never included in logs or response bodies.
Set `MFP_KEYS_PATH` (or `MFP_KEYS_ROOT`) to enable persistent ASP.NET Data
Protection keys in a mounted volume; without it, local development uses the
framework's ephemeral key repository. Persistent keys use ASP.NET Core's
automatic key rotation with a 90-day default lifetime. Set
`MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS` (or
`MultiFunPlayer:DataProtectionKeyLifetimeDays`) between 7 and 3650 days to
adjust future key generation. Keep older key files in the mounted `/keys` root
for decryption of existing protected data; do not delete them as part of
rotation.

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
20). The server samples engine frames per target at that rate; it tolerates up
to 10% scheduler jitter at the sample boundary so normal timer variance is not
counted as a drop. Seeking backwards resets the sampler so a seek is delivered
immediately. Set
`MFP_THE_HANDY_CONNECTION_KEY` in the
host environment; the key is sent as `X-Connection-Key` and is never persisted
or returned by the API. Connection failures remain visible as `lastError` and
do not terminate the host. A The Handy endpoint may point to a local HTTP
simulator for integration testing, but production devices should use the
official HTTPS API endpoint.

`GET /api/v1/output-targets` also returns bounded diagnostics for each target:
`sentFrames`, `droppedFrames`, `failedFrames`, `reconnects`, and
`lastSentAtUtc`, plus the latest `lastError` from a failed sink write. The
historical `droppedFrames` counter is the per-target rate-limiter's coalesced
sample count, not a transport-loss count; `failedFrames` records sink write
failures. A sent frame means the sink accepted it; a transport may
complete the physical write asynchronously. Counters accumulate for the
current server process across disconnect/reconnect and reset on restart. A
successful later write clears the transient `lastError`; reconnect remains an
explicit lifecycle operation.

Media sources are managed through `/api/v1/media-sources` and the matching
`connect`, `disconnect`, update, and delete subroutes. Connected network-native
sources additionally expose `POST /api/v1/media-sources/{id}/play`,
`POST /api/v1/media-sources/{id}/pause`, and
`POST /api/v1/media-sources/{id}/seek` with a JSON body such as
`{ "positionSeconds": 12.5 }`. These commands are serialized by the registry,
remain backend-owned, and return `502` when the configured adapter cannot
execute them. The `internal` source
is directly available to the headless timeline. OFS is a direct WebSocket
adapter implementing the event/command envelope observed in the desktop
client: `media_change`, `play_change`, `duration_change`, `time_change`,
`playbackspeed_change`, `funscript_change`, `funscript_remove`, and
`project_change`, with `change_play`, `change_time`, and
`change_playbackspeed` commands. OFS has no capability handshake in that
protocol, so the server reports only a successful WebSocket upgrade and does
not claim unobserved features.

Emby and Jellyfin use the real `/System/Ping` plus `/Sessions` JSON contract;
the adapter polls `PlayState`/`NowPlayingItem` and sends `Pause`, `Unpause`,
and `Seek` commands. Plex uses `/player/timeline/poll` and the timeline
metadata key to read the XML `Part` path/duration, then sends the documented
playback commands. A source is marked connected only after the session or
timeline response parses successfully; a server health response alone never
claims a media clock. `GET /api/v1/media-sources` includes the latest
normalized `mediaState` when available. Set `secretRef` to an environment
variable name, or use the defaults `MFP_EMBY_API_KEY`,
`MFP_JELLYFIN_API_KEY`, and `MFP_PLEX_TOKEN`; secret values are never stored
in `media-sources.json` or returned by the API. `deviceId` selects an Emby or
Jellyfin session and is used as the Plex target-client identifier. The Web UI
exposes both fields; it returns the non-secret `deviceId` for editing but never
returns `secretRef`. When an update omits `secretRef`, the existing reference
is retained so editing a name or endpoint cannot silently clear credentials.

VLC uses the documented `/requests/status.json` and `/requests/playlist.json`
HTTP endpoints and accepts the optional `MFP_VLC_PASSWORD` secret for Basic
authentication. MPC-HC/BE uses `/variables.html` plus `command.html` HTTP
commands and does not require a secret. Both adapters expose a normalized
media clock and play/pause/seek controls; the player web interfaces must be
bound to a reachable address and protected by the deployment network. MPV,
Whirligig, DeoVR, and HereSphere remain `blocked` external-proxy capabilities
until a documented remote adapter or companion process is provided. Source
endpoints accept only `http(s)` for network media and `ws(s)` for OFS, never
accept embedded credentials, and expose connection failures instead of
pretending to be connected.
