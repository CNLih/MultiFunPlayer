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

`/hubs/state` is a text WebSocket stream of the same status shape returned by
`/api/v1/status`. Each subscriber has a one-item latest-value queue, so a slow
browser cannot retain unbounded snapshots or stop the player.

The contract is available at /openapi/v1.json. Browser WebSocket clients may
use ?access_token=<token> for the upgrade because browser APIs cannot attach a
custom header; query tokens are accepted only on WebSocket upgrades.
