---
sidebar_position: 5
---

# Contributing To The Headless Path

Keep changes small and preserve the platform boundary. A new protocol should
be implemented behind an Application port, registered in Infrastructure, and
exposed through an explicit capability entry before the SPA offers it.

## Local checks

```sh
PATH=/home/henry/.dotnet:$PATH dotnet restore --locked-mode \
  --disable-parallel -p:EnableWindowsTargeting=true
PATH=/home/henry/.dotnet:$PATH dotnet test MultiFunPlayer.sln \
  --configuration Release --no-restore -p:EnableWindowsTargeting=true
PATH=/home/henry/.local/mfp-node/bin:$PATH npm --prefix Source/MultiFunPlayer.Web run typecheck
PATH=/home/henry/.local/mfp-node/bin:$PATH npm --prefix Source/MultiFunPlayer.Web run check:static
PATH=/home/henry/.local/mfp-node/bin:$PATH npm --prefix Source/MultiFunPlayer.Web run build
```

Protocol work must include a simulator or deterministic contract test for
connect, disconnect, timeout, cancellation, malformed/partial input, and
reconnect where the protocol supports them. Do not mark Docker, fnOS, ARM,
browser, or physical-device acceptance from a local build alone.

## Change checklist

- Keep portable code in `Core`, `Application`, `Infrastructure`, or `Server`.
- Add a versioned DTO/API example when changing a public route.
- Redact secrets in logs and avoid credentials in endpoint URLs.
- Bound queues, payload sizes, and retention; await adapter shutdown.
- Add a Web store mapping and loading/error state before adding a UI control.
- Update `WEB_LINUX_FNOS_MIGRATION_PLAN.md` and the compatibility matrix with
  evidence and remaining external gates.

The WPF project may retain Windows-only code, but new shared behavior should
enter through the portable ports so the Linux/fnOS host does not acquire a
desktop dependency.
