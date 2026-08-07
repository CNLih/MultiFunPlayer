# MultiFunPlayer Web prototype

This is a backend-independent TypeScript SPA/PWA prototype for the Web/Linux/fnOS migration. It provides mock runtime, media source, script, output target, logs, and health workflows so the layout can be reviewed before the ASP.NET API exists.

## Preview

With Node.js and npm installed:

```sh
npm install
npm run dev
```

Open the Vite URL shown in the terminal. `npm run typecheck`, `npm run check:static`, and `npm run build` are the expected CI checks.

If npm is not available, open [`static/index.html`](static/index.html) directly in a browser for the no-build preview. It uses the same mock concepts and has no network or backend requirement.

## Container preview

```sh
docker compose up --build
```

The compose file builds only the static web preview and publishes `${HTTP_PORT:-8080}`. It intentionally does not refer to a Server container. `/config`, `/keys`, `/scripts`, and `/logs` are mounted as named volumes to match the future runtime contract. Set `MFP_API_BASE_URL` only when an API is available; the demo still works when it is empty.

## Scope notes

- State is held in `src/store.ts` and is deliberately mock-only until the API and real-time contract are implemented.
- `public/sw.js` uses a versioned shell cache and removes older cache names on activation.
- No credentials, tokens, private endpoints, or user data are embedded in the prototype.
