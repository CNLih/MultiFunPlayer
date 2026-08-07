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

`/hubs/state` is a text WebSocket stream of the same status shape returned by
`/api/v1/status`. Each subscriber has a one-item latest-value queue, so a slow
browser cannot retain unbounded snapshots or stop the player.

The contract is available at /openapi/v1.json. Browser WebSocket clients use
the `mfp-auth.<base64url-token>` subprotocol because browser APIs cannot attach
a custom header. The token is not placed in the WebSocket URL, avoiding the
usual request-URL access-log leak.
