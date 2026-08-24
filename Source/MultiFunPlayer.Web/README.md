# MultiFunPlayer Web prototype

This is a TypeScript SPA/PWA for the Web/Linux/fnOS migration. It provides runtime, media source, script, output target, logs, and health workflows. When served by `MultiFunPlayer.Server`, it discovers the versioned API and bounded WebSocket state stream on the same origin. When no API is reachable it keeps a deterministic local preview.

## Preview

With Node.js and npm installed:

```sh
npm install
npm run dev
```

Open the Vite URL shown in the terminal. `npm run typecheck`, `npm run check:static`, and `npm run build` are the expected CI checks.

Use `?demo=1` to force the local preview. For a protected server, pass a bootstrap token once as `?access_token=...`; the client removes it from the address bar, moves it to local storage, and uses bearer headers for REST. WebSocket state uses a base64url token subprotocol so the secret is not placed in the upgrade URL. When no token is available, the Web entry shows an access-token or one-time pairing form; a rejected token stays on that form instead of silently switching to demo data.

If npm is not available, open [`static/index.html`](static/index.html) directly in a browser for the no-build preview. It uses the same mock concepts and has no network or backend requirement.

## Container preview

```sh
docker compose up --build
```

The compose file in this directory builds only the static web preview. The combined server/UI deployment is defined at the repository root in `deploy/docker/compose.yaml`; it builds the SPA and ASP.NET server into one image.

## Scope notes

- State is held in `src/store.ts`; `src/api.ts` maps REST commands and the WebSocket state contract into the same store shape used by the preview. Connected Emby/Jellyfin/Plex/OFS media cards expose backend-owned play, pause, and bounded seek commands when a normalized remote media clock is available.
- Device settings lazy-load the MIT-licensed `osr-emu` SR6 model to preview and edit the six mechanical TCode axes. This preview only changes the existing axis configuration draft and never opens an output transport.
- `public/sw.js` uses a versioned shell cache and removes older cache names on activation.
- Third-party notices shipped with the Web build are recorded in `public/third-party-licenses.txt`.
- No credentials, tokens, private endpoints, or user data are embedded in the prototype.
