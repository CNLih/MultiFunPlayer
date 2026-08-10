---
sidebar_position: 5
---

# Contributing To The Headless Path

Keep changes small and preserve the platform boundary. A new protocol should
be implemented behind an Application port, registered in Infrastructure, and
exposed through an explicit capability entry before the SPA offers it.

## Local checks

```sh
dotnet restore --locked-mode \
  --disable-parallel -p:EnableWindowsTargeting=true

dotnet test Source/MultiFunPlayer.Core.Tests/MultiFunPlayer.Core.Tests.csproj \
  --configuration Release --no-restore -p:EnableWindowsTargeting=true
dotnet test Source/MultiFunPlayer.Application.Tests/MultiFunPlayer.Application.Tests.csproj \
  --configuration Release --no-restore -p:EnableWindowsTargeting=true
dotnet test Source/MultiFunPlayer.Infrastructure.Tests/MultiFunPlayer.Infrastructure.Tests.csproj \
  --configuration Release --no-restore -p:EnableWindowsTargeting=true
dotnet test Source/MultiFunPlayer.Server.Tests/MultiFunPlayer.Server.Tests.csproj \
  --configuration Release --no-restore -p:EnableWindowsTargeting=true \
  -- xunit.parallelizeTestCollections=false

npm run typecheck --prefix Source/MultiFunPlayer.Web
npm run check:static --prefix Source/MultiFunPlayer.Web
npm run build --prefix Source/MultiFunPlayer.Web

MFP_DOTNET="$(command -v dotnet)" MFP_NPM="$(command -v npm)" \
python3 tools/run-validation-matrix.py \
  --output artifacts/validation/validation-matrix.json
```

The validation matrix runs the Linux-portable unit, integration, contract,
browser, and interruption checks and records Docker/fnOS/physical-device
requirements as explicit pending entries. A Linux solution build can compile
the retained WPF project with `EnableWindowsTargeting=true`, but its WPF
testhost requires the WindowsDesktop runtime; run the full solution test
command on the Windows CI runner instead of treating a Linux testhost failure
as a product regression.

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
