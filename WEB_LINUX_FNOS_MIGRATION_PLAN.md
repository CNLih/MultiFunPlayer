# MultiFunPlayer Web/Linux/fnOS Migration Plan

## 1. Plan Metadata

- Plan date: 2026-08-06
- Task slug: `web-linux-fnos-migration`
- Repository: `/home/henry/MultiFunPlayer`
- Baseline branch: `master`
- Baseline commit: `36c08fbb`
- Recommended implementation branch: `feature/web-linux-fnos-migration`
- Recommended worktree: `/home/henry/MultiFunPlayer_worktrees/web-linux-fnos-migration`
- Status: implementation in progress on `feature/web-linux-fnos-migration`; Phases 0-3 and the Web/packaging foundation are implemented, while hardware-specific adapters and release acceptance remain pending.
- Target runtime: .NET 10 LTS
- Target UI: TypeScript SPA/PWA served by ASP.NET Core
- Target platforms: Linux `amd64` and `arm64`, Docker, fnOS FPK

## 2. Objective

Build a headless, cross-platform MultiFunPlayer service that preserves the existing C# script, interpolation, synchronization, motion, and output protocol behavior while replacing WPF with a browser UI.

The first community release must:

- Run without a graphical desktop on Linux and fnOS.
- Be installable as one Docker application and one fnOS FPK package.
- Serve its own Web UI; users must not install a separate client.
- Keep timing-sensitive script evaluation and device output in the backend.
- Support remote network media sources and the main network/serial output targets.
- Preserve existing user settings where the underlying feature remains supported.
- Expose documented REST and real-time APIs for future community clients.

## 3. Scope

### 3.1 Community MVP

Core behavior:

- Funscript and CSV parsing.
- Axis mapping, interpolation, offset, range limiting, smart limiting, transition, and synchronization.
- Motion providers that have no WPF rendering dependency.
- Script repositories: local directory, Stash, and XBVR where compatible.
- Configuration migration from an existing supported desktop configuration.
- Backend-owned high-frequency update and output loops.

Media sources:

- Internal timeline.
- Emby, Jellyfin, and Plex.
- MPC and VLC HTTP interfaces.
- OFS WebSocket.
- DeoVR, HereSphere, and Whirligig remain deferred until a documented remote control contract is implemented. Local process detection must not be required.

Output targets:

- Serial.
- TCP, UDP, and WebSocket.
- The Handy.
- Buttplug.io over a reachable WebSocket endpoint.
- File output to a configured server-side volume.

Web UI:

- Runtime dashboard and connection status.
- Media source configuration and control.
- Script selection, playback state, axis values, offset, and synchronization controls.
- Output target creation, configuration, connection, and diagnostics.
- Device axis settings.
- Script repository settings.
- General settings, logs, health, version, and diagnostics.
- Responsive desktop/mobile layouts and installable PWA metadata.

Deployment:

- One ASP.NET Core process serving APIs, real-time events, and SPA assets.
- Linux Docker images for `linux/amd64` and `linux/arm64`.
- Persistent configuration, keys, scripts, and logs.
- fnOS FPK with Web entry, volume selection, port configuration, upgrade preservation, and documented device permissions.

### 3.2 Explicit Non-goals for MVP

- Pixel-for-pixel reproduction of the WPF interface.
- Native Flutter, mobile, or desktop clients.
- Native AOT or aggressive trimming.
- PotPlayer Win32 message integration.
- Existing local MPV named-pipe integration.
- Windows Named Pipe output.
- Windows Raw Input and XInput parity.
- Automatic launching or detection of applications on another computer.
- Compatibility with existing plugins that depend on WPF or Stylet UI types.
- A general-purpose browser video player or media streaming server.
- Internet-facing multi-tenant hosting.

### 3.3 Post-MVP Options

- Windows companion agent for PotPlayer, local MPV, Raw Input, XInput, and process control: estimated 20-35 person-days.
- Server plugin API and Web extension manifest replacing WPF plugin views: estimated 15-25 person-days.
- Advanced visual parity for curve editing, heat maps, markers, and every WPF settings surface: estimated 20-35 person-days.
- Native mobile client consuming the public API: separate project.

## 4. Current Baseline and Constraints

- Baseline project targeted `net9.0-windows`, `win-x64`, and WPF; the retained desktop host now targets `net10.0-windows` and the portable services target `net10.0`.
- Source size is approximately 26,367 lines of C# and 13,288 lines of XAML.
- There are 77 XAML files; XAML is reference material for Web behavior, not directly reusable code.
- Approximately 100 of 253 C# files contain direct WPF, Win32, WMI, registry, DPAPI, or UI-thread dependencies.
- `ScriptViewModel` is approximately 2,483 lines and currently mixes the timing engine, script state, settings, actions, and WPF event handlers.
- `AbstractMediaSource` and `AbstractOutputTarget` inherit Stylet `Screen` and report failures through WPF dialogs.
- Serial discovery uses WMI and registry device identifiers.
- Secret protection uses Windows DPAPI `CurrentUser` scope.
- Current tests contain approximately 984 lines and do not cover the complete timing and output pipeline.
- CI now installs .NET 9 and pinned .NET 10.0.302, and `global.json` selects the .NET 10 SDK for local builds.
- Package versions are pinned and NuGet lock files are checked for every project.
- The local validation environment uses `/home/henry/.dotnet` (.NET SDK 10.0.302)
  and Node 22. It has no Docker daemon, installed fnpack, WindowsDesktop
  runtime, target fnOS host, ARM hardware, or serial device; the official
  fnpack binary can be supplied explicitly for package-only checks, while
  Docker, fnOS, ARM, serial, and Windows UI execution remain external gates.

## 5. Target Architecture

```text
Browser / PWA
    |  HTTPS + REST + SignalR/WebSocket
    v
MultiFunPlayer.Server                         net10.0
    |-- API, authentication, health, logs
    |-- UI state projection and rate limiting
    |-- hosted service lifecycle
    |
    +--> MultiFunPlayer.Application           net10.0
    |       |-- commands and queries
    |       |-- media/output orchestration
    |       |-- configuration coordination
    |
    +--> MultiFunPlayer.Core                  net10.0
    |       |-- script parsing and models
    |       |-- timing and synchronization engine
    |       |-- interpolation and motion
    |       |-- axis state and TCode formatting
    |
    +--> MultiFunPlayer.Infrastructure        net10.0
            |-- media source adapters
            |-- output target adapters
            |-- files, JSON, secrets, serial discovery
            |-- Linux/platform implementations

MultiFunPlayer.Web                            TypeScript
    |-- generated or typed REST client
    |-- real-time state store
    |-- dashboard and configuration screens

MultiFunPlayer.Windows                        net10.0-windows
    |-- existing WPF host retained during transition

MultiFunPlayer.WindowsAgent                   optional, post-MVP
    |-- local player IPC and Windows-only input
```

Architecture rules:

1. `Core` must not reference WPF, Stylet, ASP.NET Core, serial libraries, filesystem dialogs, or platform APIs.
2. `Application` may depend on `Core`, logging abstractions, dependency injection abstractions, and explicit ports/interfaces.
3. `Infrastructure` implements platform and protocol adapters but must not contain Web UI state.
4. `Server` owns process lifetime, API authentication, API DTOs, health checks, and real-time state projection.
5. Web clients must never drive the 2-20 ms output loop. They send commands and receive throttled snapshots.
6. Persisted configuration DTOs must be separated from UI view models and versioned explicitly.
7. Every platform-specific capability must report availability instead of failing application startup.

## 6. Inputs, Outputs, Dependencies, and Assumptions

### Inputs

- Current source tree and Git history.
- Representative settings from recent desktop releases with secrets removed.
- Representative single-axis and multi-axis Funscript files.
- Access to at least one network media source and one simulated endpoint per protocol.
- Access to a serial TCode device or loopback emulator for final validation.
- An x86 fnOS installation; ARM fnOS hardware or an accepted ARM validation substitute is required for ARM release claims.

### Outputs

- Refactored .NET solution with Core, Application, Infrastructure, Server, Web, and retained Windows projects.
- Versioned REST API and real-time event contract.
- Web SPA/PWA production bundle.
- Multi-architecture Docker image and compose example.
- Installable fnOS FPK.
- Configuration migration utility or startup migration path.
- Automated tests, protocol simulators, deployment documentation, and release checklist.

### Dependencies

- .NET 10 SDK and supported runtime/container images.
- Node.js LTS and the selected SPA toolchain.
- Docker Buildx for multi-architecture builds.
- `fnpack` and current fnOS application-development documentation.
- Windows CI runner for retained WPF compatibility.
- Linux amd64 and arm64 CI runners or emulation plus physical release validation.

### Assumptions

- The service is primarily used on a trusted home LAN, but authentication is still enabled by default.
- Players and output devices expose endpoints reachable from the NAS.
- Users explicitly map server-side script/media directories into the container.
- USB serial devices connected to the NAS can be passed into the container.
- Existing WPF releases remain available during migration and provide rollback.
- The first release may omit features that fundamentally require code running on a Windows player machine.

## 7. Effort Summary

| Phase | Work | Person-days |
|---|---|---:|
| 0 | Reproducible baseline and behavior capture | 6-10 |
| 1 | Isolated .NET 10/toolchain upgrade | 4-7 |
| 2 | Core and application extraction | 12-18 |
| 3 | ASP.NET Core server and API | 10-15 |
| 4 | Cross-platform adapters | 10-16 |
| 5 | Web SPA/PWA | 18-28 |
| 6 | Docker and fnOS packaging | 5-8 |
| 7 | Hardening, documentation, and release validation | 10-15 |
| **Community MVP total** | | **75-117** |

Calendar estimate:

- One experienced full-stack engineer: approximately 4-6 months.
- Two engineers split between backend/platform and Web: approximately 10-14 weeks, allowing for integration overhead.
- Proof of concept limited to Internal source plus TCP output and one dashboard: 15-25 person-days.
- Near-complete WPF parity including a Windows agent and plugin redesign: 150-220 person-days total.

Estimates include implementation, tests, review fixes, and documentation. They exclude app-store review delay and extensive support for unknown third-party hardware.

## 8. Implementation Phases

### Phase 0 - Reproducible Baseline and Behavior Capture

Estimate: 6-10 person-days.

Tasks:

- [x] P0-01 Install or provision the .NET 9 and .NET 10 SDKs in CI/development environments.
- [x] P0-02 Add `global.json` and replace package wildcards with reviewed pinned versions.
- [x] P0-03 Add NuGet lock files or equivalent deterministic restore policy.
- [ ] P0-04 Repair CI so the existing WPF project builds and tests on a Windows runner.
- [x] P0-05 Capture sanitized configuration fixtures from supported desktop versions.
- [x] P0-06 Add golden tests for Funscript parsing, interpolation, axis mapping, limits, offsets, synchronization, and TCode output.
- [x] P0-07 Add deterministic fake clocks and output sinks around timing-sensitive tests.
- [x] P0-08 Record protocol traces for representative media sources and output targets.
- [x] P0-09 Define the MVP feature matrix and mark unsupported capabilities in the existing documentation.

Primary modification locations:

- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props` or existing project files
- `.github/workflows/`
- `Source/MultiFunPlayer.Tests/`
- `TestData/` or a new test-fixture directory

Completion conditions:

- Existing desktop behavior has a passing reproducible baseline.
- Restore does not silently change dependency versions.
- Golden output fixtures are reviewable and contain no private data.
- CI uses explicit SDK versions and produces a Windows artifact.

Rollback:

- Revert only build/test infrastructure commits; no production behavior is changed in this phase.
- Preserve captured fixtures even if the toolchain update is delayed.

### Phase 1 - Isolated .NET 10 Upgrade

Estimate: 4-7 person-days.

Tasks:

- [x] P1-01 Change desktop and test targets to `net10.0-windows` in a dedicated change.
- [x] P1-02 Align `System.*`, test SDK, analyzer, Fody, and build package versions.
- [x] P1-03 Remove obsolete or redundant direct framework package references where verified.
- [ ] P1-04 Compile all XAML and test every view/resource dictionary under .NET 10.
- [ ] P1-05 Smoke-test plugin compilation, loading, configuration migration, and application shutdown.
- [ ] P1-06 Publish and run a framework-dependent Windows build.

Primary modification locations:

- `Source/MultiFunPlayer/MultiFunPlayer.csproj`
- `Source/MultiFunPlayer.Tests/MultiFunPlayer.Tests.csproj`
- WPF XAML resources affected by .NET 10 validation
- `.github/workflows/`

Completion conditions:

- Current WPF application builds, starts, opens every major view, and passes tests on .NET 10.
- This phase contains no architectural extraction or Web implementation.

Rollback:

- Restore the .NET 9 project target and package set as one isolated commit.
- Do not migrate user configuration irreversibly during this phase.

### Phase 2 - Extract Core and Application Services

Estimate: 12-18 person-days.

Tasks:

- [x] P2-01 Create `MultiFunPlayer.Core` and move platform-neutral script/model/math code.
- [x] P2-02 Split `ScriptViewModel` into a headless `ScriptEngine`, playback state, axis state, settings, and UI projection.
- [x] P2-03 Introduce clock/timer abstractions only where needed for deterministic tests and shutdown.
- [x] P2-04 Create application interfaces for media sources, output targets, configuration, secrets, files, and serial discovery.
- [x] P2-05 Replace direct WPF dialog calls in shared flows with structured errors and operation results.
- [x] P2-06 Replace Stylet event dependencies in shared code with an application-owned event/command contract.
- [x] P2-07 Keep the WPF host operational by adapting existing view models to the extracted services.
- [x] P2-08 Separate persisted configuration DTOs from WPF view models while maintaining a migration reader.
- [x] P2-09 Move output update contexts and TCode formatting into testable platform-neutral services.

Primary modification locations:

- New `Source/MultiFunPlayer.Core/`
- New `Source/MultiFunPlayer.Application/`
- `Source/MultiFunPlayer/UI/Controls/ViewModels/ScriptViewModel.cs`
- `Source/MultiFunPlayer/MediaSource/AbstractMediaSource.cs`
- `Source/MultiFunPlayer/OutputTarget/AbstractOutputTarget.cs`
- `Source/MultiFunPlayer/Settings/`

Completion conditions:

- Core and Application compile for plain `net10.0` on Linux.
- They have no references to `System.Windows`, Stylet, WMI, registry, DPAPI, or ASP.NET Core.
- WPF still passes the Phase 0 golden tests through adapters.
- A headless test can load a script, advance a fake media clock, and produce expected axis/TCode values.

Rollback:

- Perform extraction in small move/adapt commits so WPF can return to the pre-extraction service wiring.
- Do not delete the old WPF path until equivalent golden tests pass.

### Phase 3 - ASP.NET Core Server and Public Contracts

Estimate: 10-15 person-days.

Tasks:

- [x] P3-01 Create `MultiFunPlayer.Server` using ASP.NET Core 10 and Microsoft DI.
- [x] P3-02 Implement hosted-service startup, auto-connect, graceful cancellation, and ordered shutdown.
- [x] P3-03 Define versioned DTOs and REST endpoints for status, media sources, scripts, outputs, devices, settings, logs, and commands.
- [x] P3-04 Add SignalR or a documented WebSocket contract for state changes, notifications, and connection diagnostics.
- [x] P3-05 Coalesce UI updates to a configurable maximum of 10-20 Hz without changing backend output rates.
- [x] P3-06 Add OpenAPI generation and checked-in API examples.
- [x] P3-07 Add health, readiness, version, runtime, and capability endpoints.
- [x] P3-08 Implement initial-admin setup or pairing-token authentication.
- [x] P3-09 Protect WebSocket/SignalR, configuration changes, file access, and secret fields.
- [x] P3-10 Add structured logs with secret redaction and bounded retention.

Suggested API surface:

```text
GET    /api/v1/status
GET    /api/v1/capabilities
GET    /api/v1/media-sources
POST   /api/v1/media-sources/{id}/connect
POST   /api/v1/media-sources/{id}/play
POST   /api/v1/media-sources/{id}/pause
POST   /api/v1/media-sources/{id}/seek
GET    /api/v1/output-targets
POST   /api/v1/output-targets
POST   /api/v1/output-targets/{id}/connect
GET    /api/v1/scripts
POST   /api/v1/scripts/load
GET    /api/v1/settings
PUT    /api/v1/settings
GET    /api/v1/logs
GET    /health/live
GET    /health/ready
/hubs/state
```

Completion conditions:

- The server runs headlessly on Linux.
- A test client can configure an Internal source, load a script, and drive a fake output.
- Browser disconnection does not stop playback or output.
- API authentication and secret redaction tests pass.

Rollback:

- Server is additive; WPF remains the supported host until deployment acceptance.
- API changes remain under `/api/v1` and breaking contract experiments are not published.

### Phase 4 - Cross-platform Media, Output, and Storage Adapters

Estimate: 10-16 person-days.

Tasks:

- [x] P4-01 Port network-native media sources without process detection requirements.
- [x] P4-02 Add capability reporting for sources that require remote player configuration.
- [x] P4-03 Port TCP, UDP, WebSocket, The Handy, Buttplug, and File outputs.
- [x] P4-04 Implement Linux serial enumeration using stable `/dev/serial/by-id` paths when available.
- [x] P4-05 Keep serial open/write behavior behind an adapter and preserve TCode timing semantics.
- [x] P4-06 Replace DPAPI with ASP.NET Data Protection or an explicit key-store abstraction backed by a persistent volume.
- [x] P4-07 Restrict filesystem browsing to administrator-configured roots and prevent path traversal/symlink escape.
- [x] P4-08 Define Linux and Windows path normalization rules for existing settings.
- [x] P4-09 Replace UI file dialogs with server-side directory selection, upload, or repository operations.
- [x] P4-10 Validate reconnect, timeout, cancellation, partial message, and malformed message handling.

MVP media compatibility matrix:

| Source | MVP treatment |
|---|---|
| Internal | Direct port; no WPF dialogs |
| Emby/Jellyfin/Plex | Direct network adapter |
| VLC/MPC | Direct HTTP adapter; document remote binding/firewall |
| OFS | Direct WebSocket adapter |
| DeoVR/HereSphere/Whirligig | Deferred until a documented remote adapter exists; no NAS-side process check |
| MPV | Deferred unless a documented remote TCP/Unix-socket mode is added |
| PotPlayer | Deferred to Windows agent |

MVP output compatibility matrix:

| Output | MVP treatment |
|---|---|
| Serial | Linux adapter plus Docker device mapping |
| TCP/UDP/WebSocket | Direct port |
| The Handy | Direct port |
| Buttplug.io | Direct when endpoint is remotely reachable |
| File | Write only under configured volume roots |
| Named Pipe | Deferred or replaced by Unix socket in a later release |

Completion conditions:

- Every advertised adapter has integration tests against a simulator or real endpoint.
- Unsupported adapters are omitted or explicitly report unavailable; they do not crash startup.
- Existing secrets can be re-entered or migrated safely without logging plaintext.
- Serial reconnect survives device removal and reinsertion where the OS provides a stable path.

Rollback:

- Adapters are registered independently and can be disabled by capability/feature flags.
- Preserve original configuration values when migration cannot decrypt a Windows DPAPI value; request re-entry instead of overwriting it.

### Phase 5 - Web SPA/PWA

Estimate: 18-28 person-days.

Recommended initial stack: Vue 3, TypeScript, Vite, Pinia, a restrained component library, and a chart/canvas library selected after a prototype. React plus TypeScript is an acceptable substitute if contributor availability favors it.

Tasks:

- [x] P5-01 Create application shell, routing, authentication bootstrap, API client, and real-time store.
- [x] P5-02 Build the runtime dashboard with media state, connection state, output state, and health.
- [x] P5-03 Build media source list, configuration forms, connection controls, and capability messages.
- [x] P5-04 Build script browser/load controls, time/offset/sync controls, and axis value visualization.
- [x] P5-05 Build output target creation, configuration, connection, update-rate, and diagnostics screens.
- [x] P5-06 Build device axis, range, inversion, interpolation, and limit settings.
- [x] P5-07 Build repository, general, security, log, and diagnostic settings.
- [x] P5-08 Add responsive layouts for desktop, tablet, and phone without changing backend state ownership.
- [x] P5-09 Add accessible keyboard navigation, labels, focus states, validation, loading, empty, error, and reconnect states.
- [x] P5-10 Add PWA manifest and explicit cache/update behavior.
- [x] P5-11 Generate the production assets and serve them from ASP.NET Core with SPA fallback.
- [x] P5-12 Add browser end-to-end tests and screenshot checks at desktop and mobile viewports.

Completion conditions:

- A user can complete the full MVP workflow without editing JSON manually.
- Refreshing or closing the page does not reset backend state.
- Long labels, translated text, dynamic values, and reconnect notices do not overlap controls.
- Desktop and mobile Playwright journeys pass.
- SPA assets are versioned and old clients receive a detectable update path.

Rollback:

- Web assets are independently versioned and can be rolled back with the server image tag.
- API compatibility is preserved across one prior UI bundle during beta releases.

### Phase 6 - Docker and fnOS Packaging

Estimate: 5-8 person-days.

Tasks:

- [x] P6-01 Add a multi-stage Dockerfile that builds .NET and Web assets into one runtime image.
- [x] P6-02 Run as a non-root user by default and document the minimum serial-device permission exception.
- [x] P6-03 Define `/config`, `/keys`, `/scripts`, and `/logs` volumes.
- [x] P6-04 Add configurable listen address/port, health check, graceful stop, and resource limits.
- [x] P6-05 Document bridge networking and an opt-in host-network mode for protocols that require it.
- [ ] P6-06 Build and test `linux/amd64` and `linux/arm64` images.
- [x] P6-07 Add a compose example with a fake TCP/UDP endpoint for first-run validation.
- [x] P6-08 Create the fnOS Docker application project with `fnpack`.
- [x] P6-09 Add FPK manifest, icons, Web entry, port, volume, upgrade, and uninstall behavior.
- [x] P6-10 Add fnOS-specific serial mapping and permission documentation.
- [ ] P6-11 Verify upgrades retain settings, keys, scripts, and logs according to policy.

Suggested container contract:

```text
HTTP_PORT       configurable, one published Web/API port
/config         application settings and schema version
/keys           persistent data-protection keys
/scripts        user-selected script library root
/logs           bounded diagnostic logs
/dev/ttyUSB*    optional explicit device mapping
/dev/ttyACM*    optional explicit device mapping
```

Completion conditions:

- The same image starts on standard Debian/Ubuntu Docker and fnOS.
- Installation does not require an interactive shell.
- The fnOS desktop entry opens the correct Web URL.
- Container restart and FPK upgrade preserve required data.
- Serial permissions are explicit; privileged mode is not the default solution.

Rollback:

- Keep immutable versioned image tags and the previous FPK.
- Back up configuration before schema migration.
- Document how to reinstall the previous image using the unchanged volumes.

### Phase 7 - Hardening and Community Release

Estimate: 10-15 person-days.

Tasks:

- [ ] P7-01 Run unit, integration, contract, browser, container, and target-device matrices.
- [ ] P7-02 Run an 8-hour playback/output soak test and record jitter, reconnects, memory, and CPU.
- [ ] P7-03 Validate behavior under browser disconnect, network interruption, player restart, device removal, and container restart.
- [x] P7-04 Add first-run setup, pairing/authentication, backup/restore, and troubleshooting documentation.
- [x] P7-05 Publish API/OpenAPI, WebSocket/SignalR contract, architecture, and contribution guides.
- [ ] P7-06 Add security policy, secret-redaction checks, dependency audit, and container vulnerability scan.
- [x] P7-07 Produce an SBOM and list third-party licenses for distributed assets.
- [x] P7-08 Publish a beta compatibility matrix that distinguishes direct, conditional, agent-required, and unsupported integrations.
- [ ] P7-09 Run upgrade and rollback rehearsals on Linux and fnOS.
- [ ] P7-10 Tag a release candidate only after Tier 1-4 validation passes.

Completion conditions:

- No known critical/high security findings remain without an explicit release exception.
- Soak tests meet the agreed output jitter and stability budget.
- A new user can install, pair, load a script, connect a supported source/output, and collect diagnostic logs using documentation alone.
- Published feature claims match the validated platform/architecture matrix.

Rollback:

- Release candidate tags remain immutable.
- Roll back image/FPK and restore the pre-upgrade configuration backup.
- Keep the WPF release available until the Web release exits beta.

## 9. Current Iteration Status

The repository is now on the implementation branch named in this plan. The
portable path is operational through Core, Application, Infrastructure, and
Server projects; the retained WPF host remains a Windows compatibility target.
The Web bundle can run standalone in deterministic demo mode or, when served
by the Server, switch to the authenticated REST and bounded WebSocket store.

Validated in the current Linux environment:

- Locked .NET restore and Release solution build: 0 errors; WPF/XAML compiles
  with `EnableWindowsTargeting=true`. Legacy Core/WPF analyzer warnings remain
  outside the portable migration boundary and are not hidden by this work.
- The desktop and desktop-test NuGet lock files now include their existing
  `MultiFunPlayer.Application` project references. A clean
  `dotnet restore --locked-mode --disable-parallel` was rerun after refreshing
  those entries; this no longer depends on stale `obj` assets.
- `Source/MultiFunPlayer.Tests/PluginTests.cs` now provides a Windows-runner
  smoke for dynamic plugin source compilation, collectible loading, JSON
  settings save/load, and disposal. Plugin disposal also tolerates a compile
  result that has not yet been attached to a WPF event aggregator. The test
  assembly compiles on Linux, but execution remains part of the WindowsDesktop
  P1-05 gate.
- `Source/MultiFunPlayer.Tests/WpfXamlTests.cs` now loads every compiled view
  and resource dictionary on a dedicated STA thread after creating the WPF
  application resource tree. The test compiles with the portable solution and
  is explicitly invoked by the Windows CI Release job; its runtime result is
  still required before P1-04/P0-04 can be checked off.
- `tools/run-windows-framework-dependent-smoke.ps1` now starts the published
  framework-dependent EXE, waits for its WPF main window, keeps it alive for a
  short smoke interval, requests a graceful close, and verifies exit code zero.
  Both Windows CI and tag publishing invoke it; a successful Windows runner
  artifact is still required before P1-06 is checked off.
- Core tests: 8/8; Application tests: 10/10; Infrastructure tests: 43/43;
  Server tests: 42/42, including the Data Protection service wiring test and
  registry-level VLC/MPC adapter coverage.
  A Linux cross-target solution build with `EnableWindowsTargeting=true` also
  completed with 0 errors; its 58 existing nullable/analyzer warnings do not
  replace the WindowsDesktop runtime and WPF UI execution gates.
- Web `typecheck`, static contract check, and Vite production build pass.
- Docusaurus migration documentation builds successfully with the upgraded
  3.10.2 toolchain and no build warnings. The Playwright desktop/mobile
  journeys pass 10/10 when the host browser libraries are available.
- GitHub Actions now includes an Ubuntu portable job that performs locked
  restore, Core/Application/Infrastructure/Server tests, solution build, and
  SPA type/static/production checks. Windows runner execution remains required
  before P0-04/P1 WPF acceptance can be checked.
- The current `ci.yml` and `publish.yml` pass actionlint 1.7.12, including
  their matrix expressions, conditionals, and embedded shell blocks. This is
  workflow-syntax evidence only; the WindowsDesktop jobs still need a real
  runner result.
- The tag publishing workflow now uses the Windows runner's native
  PowerShell `Test-Path` for the release-evidence existence check before the
  Python completeness validator; the previous POSIX `test -f` form could fail
  before validation on Windows.
- The change-detection job now reruns the pinned actionlint binary (SHA-256
  verified) before fan-out, so future workflow edits cannot bypass this syntax
  check.
- All third-party GitHub Actions used by CI and publishing are now pinned to
  full commit SHAs with their human-readable release tags retained in comments;
  this prevents a mutable tag from silently changing the release pipeline.
- CI now grants Pages write and OIDC token permissions only to the Docs job;
  build, portable, packaging, and container jobs inherit read-only repository
  access. This keeps the workflow security boundary least-privilege while
  preserving GitHub Pages deployment.
- Docs builds and audits still run on pull requests, but the Pages deployment
  step is now restricted to push and explicit workflow-dispatch events; a PR
  cannot publish the documentation environment.
- The remote SPA now consumes bounded runtime logs, non-secret settings, and
  stable serial discovery, and renders those diagnostics without taking
  ownership of backend playback state.
- The script view binds its axis visualization to normalized `status.axes`
  values (with a deterministic demo fallback); offset is applied to seek
  requests and the selected clock mode is retained in SPA state. Persisted
  range, inversion, interpolation, and limit editing is covered; broader
  media-clock arbitration and device/browser matrix validation remain open.
- The Infrastructure migration reader accepts the sanitized desktop JSON shape,
  emits versioned portable records, normalizes path separators, and omits
  secret fields without modifying the source document. An explicit
  `MFP_LEGACY_CONFIG_PATH` startup import writes the versioned record once under
  the config root and exposes a protected migration status endpoint.
- The repository now contains an official-template-shaped `deploy/fnos/fnpack`
  Docker project. Its `manifest`, `config/resource`, privilege declaration,
  Web URL entry, icons, lifecycle scripts, and four persistent shares are
  validated statically; the official `fnpack` 1.2.3 binary has built both x86
  and ARM FPK archives locally. The latest rebuild also unpacked both
  `app.tgz` files and confirmed the fnOS Compose read-only root, bounded
  non-executable `/tmp`, no-new-privileges, graceful-stop, and resource-limit
  settings are present. Each archive has a 22-component file SBOM, a complete
  license report, and a SHA-256 sidecar. The FPK build is source/image evidence
  only; clean fnOS installation, Web launch, upgrade, rollback, and uninstall
  remain Tier 4 gates.
- The Server exposes a versioned read-only adapter capability inventory and
  explicitly labels each transport by implementation status. Browser
  WebSocket binary/invalid text frames are ignored without affecting the
  playback API.
- Script repository reads and uploads reject traversal and reparse points;
  uploads are JSON-validated, bounded, atomic, and opt-in overwrite only. The
  server now resolves both `MFP_SCRIPTS_ROOT` and the Docker-compatible
  `MFP_SCRIPTS_PATH` alias consistently with the other persistent roots.
- A persistent output-target registry now exposes CRUD and lifecycle routes for
  direct TCP, UDP, WebSocket, File, Serial, and The Handy HTTP HDSP v2 output.
  The Handy uses the existing desktop API contract, takes a configured
  `sourceAxis`, reads its connection key only from
  `MFP_THE_HANDY_CONNECTION_KEY`, and never persists the key. File and serial
  endpoint constraints are validated before persistence; each target persists
  an `updateRateHz` from 1 to 20 and samples frames independently before
  formatting transport output. Connection failures are represented in the
  target state. Simulator tests cover handshake, malformed/timeout responses,
  caller cancellation, send failure, explicit reconnect, and rate-limit/seek
  behavior; TCP and WebSocket loopback tests close/reopen the peer to simulate
  a player restart before sending the next frame. The Server registry also has
  a TCP/UDP/WebSocket loopback test that creates, connects, sends a multi-axis
  engine frame, verifies diagnostics, and disconnects all three targets.
- TCode transport writes now run on a bounded worker owned by each target;
  engine ticks only format and enqueue a frame, so a slow TCP/UDP/WebSocket,
  file, or serial peer cannot block other output targets. Queue saturation and
  asynchronous transport failures still surface through the existing failed
  frame/last-error diagnostics and trigger the normal reconnect path.
  Infrastructure coverage verifies asynchronous fault notification and a
  slow-target engine tick.
- Linux serial discovery is verified against stable `/dev/serial/by-id`
  identities and re-enumerates cleanly after a device link is removed. A Linux
  pseudo-terminal integration test now opens the real `SerialOutputTransport`
  and reads a TCode payload from the master side; physical device permissions
  and hardware reconnect behavior remain external validation gates.
- A persistent media-source registry now exposes CRUD and lifecycle routes for
  the Internal timeline plus explicit capability states for network and
  external-player integrations. Internal sources connect directly; planned or
  blocked entries persist safely, reject embedded credentials, and surface an
  unavailable-adapter error instead of claiming a live connection. Server tests:
  42/42, including The Handy, TCP/UDP/WebSocket, and VLC/MPC registry integrations,
  device-axis persistence,
  network media registry behavior, redacted JSONL log persistence/download,
  explicit key-root reuse across independent host instances, and rejection of
  old protected data after the explicit key root is removed. A dedicated host
  restart test also uploads a script, writes device settings and a redacted
  log, protects a value, and verifies all four persistent roots through an
  independent host instance.
- `RateLimitedOutputSink` now tolerates up to 10% scheduler jitter at the
  configured sample boundary, preventing normal `PeriodicTimer` early ticks
  from being reported as output loss. The boundary behavior is covered by an
  Infrastructure test; transport failures remain counted separately.
- P4-01 now includes portable Infrastructure HTTP adapters for Emby,
  Jellyfin, Plex, VLC, and MPC-HC/BE wired into the media-source registry. Emby/Jellyfin
  validate `/System/Ping` and parse `/Sessions` `PlayState`/
  `NowPlayingItem` responses, then expose Pause/Unpause/Seek commands. Plex
  parses `/player/timeline/poll` XML and the referenced metadata `Part`, then
  exposes the documented playback commands. The adapter polls a real session
  or timeline response before reporting connected; health checks alone do not
  claim a media clock. `secretRef` stores only an environment variable name
  (`MFP_EMBY_API_KEY`, `MFP_JELLYFIN_API_KEY`, `MFP_PLEX_TOKEN`, or the optional
  `MFP_VLC_PASSWORD` by default), while `deviceId` selects the remote
  session/client. VLC validates API v3/v4 status and playlist JSON, while
  MPC-HC/BE parses bounded `/variables.html` fields and sends the documented
  HTTP commands. Bounded JSON/XML/HTML parsing, XML DTD rejection, malformed
  response handling, cancellation, and protocol simulator tests cover all five
  sources. Registry integration verifies normalized `mediaState` and that
  token values never reach `media-sources.json`; MPV/VR players and other
  process/proxy-bound sources remain blocked.
- `MultiFunPlayer.Application` now exposes platform-neutral ports for media
  adapters, output adapters, configuration, secrets, rooted files, and serial
  discovery. Implementations remain in Infrastructure/Server and can be
  replaced without changing Core timing or script evaluation.
- Shared desktop media/output flows now consume an application-owned typed
  message bus, structured operation-error sink, and host-neutral confirmation
  prompt. The WPF host binds Stylet/Dialog adapters at the composition root;
  Core/Application remain free of WPF and Stylet references, and the contract
  tests pass 10/10.
- The SPA now loads media/output registries and device-axis policies from the
  server, provides persisted create/connect/disconnect/delete workflows, and
  renders planned/blocked capability errors. Script upload remains available
  in both demo and remote modes. Media configuration now exposes the
  non-secret remote `deviceId` and environment-variable-only `secretRef`;
  API responses never return the secret reference, and ordinary updates retain
  it when omitted. Ten desktop/mobile Playwright executions now exercise
  media, output, axis, diagnostics CRUD, protected-server authentication, and
  authenticated remote REST/WebSocket hydration paths; the latest run passed
  all ten executions using user-local Chromium runtime libraries. The base
  host still lacks `libnspr4.so`, so CI continues to install the browser image
  dependencies explicitly.
- Network-native media sources now expose authenticated REST playback controls
  (`play`, `pause`, and bounded `seek`) through the same registry used by the
  Web SPA. Emby/Jellyfin/Plex HTTP commands and OFS WebSocket commands remain
  backend-owned; the browser sends commands and receives the next throttled
  media-state snapshot. Registry forwarding and negative seek validation are
  covered by the Server test suite.
- The Web `Device settings` route now surfaces the server-managed repository,
  effective runtime, security, and legacy-migration panels alongside axis
  policies. These panels are deliberately read-only because root paths,
  cadence, and authentication bootstrap are process-bound settings; the
  remote browser journey asserts all four panels without moving ownership of
  restart-required configuration into the SPA. The mobile journey also checks
  page-level width on media, output, and logs routes while allowing intentional
  table-local horizontal scrolling.
- The remote store now turns a state-WebSocket close/error into an explicit API
  connection warning, records a reconnect-required activity event, and keeps
  the engine-loop check green with the backend-output-independent message;
  the authenticated browser journey covers this visible disconnect state.
- Local server smoke: anonymous liveness, protected API, OpenAPI, inline
  multi-axis Funscript load, play, seek, SPA fallback, authenticated
  WebSocket snapshot, settings, serial discovery, and redacted bounded logs
  all pass. WebSocket snapshots use camelCase to match REST.
- The checked-in OpenAPI document now links `GET /api/v1/output-targets` to an
  `OutputTargetList` array schema and an `OutputTarget` example containing both
  canonical `coalescedFrames` and historical `droppedFrames`; Server contract
  tests validate the references and serialized component names.
- Local HTTP smoke on Release Server also passed SPA fallback, media-source
  create/list, file-output create/connect, script upload, and fixed-size
  persisted log download. The log endpoint snapshots JSONL under its lock so
  ongoing request logging cannot change the response content length.
- Final Release server smoke after the network/axis changes returned 200 for
  liveness, adapter capabilities, device-axis listing, OpenAPI, and SPA
  fallback; the Emby/Jellyfin/Plex and Buttplug entries reported `ready`, an
  axis policy PUT persisted successfully, and Ctrl-C shutdown completed
  cleanly.
- Headless operations, architecture, and contribution guides now document
  pairing, four-volume backup/restore, upgrade rollback, troubleshooting,
  REST/WebSocket ownership, and portable boundary checks. Docker/fnOS upgrade
  rehearsal remains an external validation gate.
- The packaging contract validator reports 124/124 checks passed for the
  multi-stage Dockerfile, non-root/read-only compose defaults, four persistent
  roots, health check, secret interpolation, the structural fnOS manifest, and
  the canonical `fnpack` project; all nine fnOS lifecycle scripts also pass
  POSIX `sh -n` syntax validation. The fnOS Compose service now carries the
  same runtime boundary as the portable deployment: read-only root, bounded
  non-executable `/tmp`, `no-new-privileges`, a 30-second graceful-stop budget,
  and 2 CPU/512 MiB resource limits. The validator checks these fnOS-specific
  invariants as well. The same validator runs in the packaging CI job, which
  now builds and inspects x86/ARM FPK archives with pinned official `fnpack`
  1.2.3. Docker Buildx, image scanning, official target-firmware validation,
  and target-NAS execution remain external gates.
- A local rootless BuildKit v0.32.2 probe can start an amd64 OCI worker, but
  building the image stops while extracting the base image because this host
  has no `newuidmap`/`newgidmap` helper for the configured subordinate-ID
  ranges; both overlayfs and native snapshotters report an unmapped UID/GID
  `lchown` failure. A follow-up probe using an isolated user and mount
  namespace (`unshare -Ur -m`) resolved the pinned base metadata and started
  the amd64 worker, but the first `WORKDIR` mount failed while remapping
  `/etc/shadow` to UID 0/GID 42 (`lchown ... invalid argument`); no OCI image
  was exported. This is environment evidence only, not a passed image build,
  so P6-06 remains pending until Docker Buildx or an equivalently privileged
  runner produces the amd64/arm64 artifacts.
- A RID-specific framework-dependent Server publish for `linux-arm64` also
  succeeds locally (using an explicit RID restore); its apphost and
  `System.IO.Ports` native library identify as AArch64 ELF files. The default
  locked restore remains host-neutral, while an ARM publish must use its
  explicit RID restore/publish path. This proves the .NET cross-RID publish
  path only, not container startup, ARM runtime dependencies, or physical fnOS
  ARM support; P6-06 and the physical architecture acceptance gate remain
  open.
- The runtime Docker health check uses Debian's native `busybox-static` package.
  It no longer copies Alpine's musl-linked `busybox` into the Debian ASP.NET
  image, avoiding an architecture/runtime-loader mismatch that would make a
  built image fail health checks before target acceptance. Both the root
  Compose example and the fnOS Compose project now invoke the same
  `/bin/busybox` path, and the packaging validator guards that invariant.
- The Docker build context now excludes Web browser reports, test results,
  generated SPA/Docs output, and both Web/Docs dependency trees. The packaging
  contract checks these exclusions so local diagnostics cannot inflate or
  accidentally enter a runtime build context. The entire `.agent` directory
  is also excluded because it is orchestration metadata, not application input.
- The three Docker base images are pinned to reviewed multi-architecture
  manifest digests; the packaging contract rejects a floating base tag so an
  image rebuild cannot silently change the .NET or Node runtime.
- `deploy/validate-fpk.sh` now inspects the Compose file inside `app.tgz` and
  rejects packages that lose the read-only, bounded `/tmp`, privilege,
  graceful-stop, or CPU/memory constraints during archive creation. Both
  freshly rebuilt x86 and ARM packages pass this nested contract check.
- The FPK wrapper now normalizes relative output directories before invoking
  `fnpack` and writes a portable `multifunplayer.fpk.sha256` sidecar. A local
  relative-output build, checksum verification, and FPK content validation
  pass; the packaging CI artifact includes both architecture-specific packages
  and their checksums.
- CI now contains a separate amd64/arm64 Buildx matrix that builds each image,
  verifies the loaded image OS/architecture, starts it with temporary persistent roots, checks anonymous liveness,
  authenticated adapter access, and SPA fallback, then removes the container.
  Each matrix leg now also runs the pinned Trivy action against the loaded image
  and uploads a JSON vulnerability report before smoke testing. This is an
  executable release gate, but it remains unchecked here until an Ubuntu runner
  produces both build, scan, and smoke results.
- Each container matrix leg now also mounts independent `/config`, `/keys`,
  `/scripts`, and `/logs` volumes, pairs once to persist an authentication hash,
  writes an axis policy and script, gracefully stops and replaces the container,
  and verifies the authenticated state, resources, volume markers, and all four
  mount points after replacement. It confirms the persisted authentication hash
  and compares the Data Protection XML key-ring digest before and after the
  replacement, while rejecting plaintext bearer-token storage in `/config`.
  Each leg uploads a non-sensitive JSON summary; local Docker-volume and fnOS
  upgrade/rollback evidence remains external.
- The Windows build matrix now uses the same non-parallel xUnit collection
  setting as the portable job, and the temporary-directory helper retries only
  transient cleanup races. A Windows runner result is still required before
  P0-04 is marked complete.
- Current dependency checks report no vulnerable .NET packages, no production
  Web-package vulnerabilities, and no Docs-package vulnerabilities. The Docs
  toolchain remains on Docusaurus 3.10.2, pins safe `serialize-javascript` and
  `uuid` overrides, requires Node 20+, and builds cleanly. Because the archived
  `image-size` 2.0.2 dependency has no upstream patched release, Docs now uses
  a locally vendored `2.0.3-mfp.1` fork that rejects zero-length ICNS and
  HEIF/JXL parser entries; a focused security check covers malformed inputs and
  valid PNG dimensions. CI now runs full high/critical audits for both Web and
  Docs instead of omitting development tooling; P7-06 remains open only for the
  exact runtime-image scan and release-environment review.
- The Web lockfile now pins Playwright 1.55.1 and Vite 6.4.3 (including the
  patched esbuild line). A clean `npm ci --ignore-scripts`, Web audit, Vite
  build, and the ten desktop/mobile browser executions all pass with zero npm
  audit findings. CI now uploads the full Web audit JSON and uses the exact
  Web/Docs lockfile paths for npm cache keys; the host still needs the
  documented Chromium shared-library path or CI's installed browser
  dependencies.
- `tools/generate-sbom.py` now produces a deterministic CycloneDX 1.5
  dependency SBOM from restored NuGet assets and committed npm lockfiles,
  falling back to the integrity-pinned npm package manifests and unambiguous
  license files for legacy metadata, including local `file:` packages such as
  the vendored `image-size` fork. The current workspace contains 1,402
  deduplicated components and the generated inventory reports zero components
  without a license declaration, satisfying the repository-side P7-07
  generation task. The container matrix now requests a separate
  CycloneDX SBOM for each loaded runtime image and uploads it beside the Trivy
  report and now generates a license inventory beside each runtime SBOM; exact
  runtime-image review and release-owner review of FPK artifacts remain open
  until a runner produces and reviews those artifacts.
- `tools/generate-fpk-sbom.py` now produces a file-level CycloneDX inventory for
  both the outer FPK and nested `app.tgz`, rejects unsafe archive members, and
  records SHA-256 plus MIT license evidence. Local x86 and ARM FPKs each
  produced 22 components with zero missing declarations; packaging CI now
  generates the corresponding x86/ARM SBOM and license artifacts. Runtime image
  scanning and release-owner review remain external.
- The Web project now includes a pinned Playwright 1.55.1 configuration with
  desktop and 393x727 mobile Chromium journeys, viewport overflow assertions,
  service-worker API bypass coverage, and CI screenshot artifacts. The five
  tests are enumerated for both projects; the latest local run passed 10/10
  executions after adding user-local Chromium runtime libraries. The
  authenticated hydration journey also serves a legacy output response without
  `coalescedFrames` and verifies the UI falls back to `droppedFrames` on both
  desktop and mobile projects. The base host remains missing `libnspr4.so`,
  while CI installs browser and host dependencies before running them.
- `tools/run-portable-soak.py` now provides a repeatable Linux headless soak
  entry point with temporary four-volume roots, output diagnostics, revision
  interval statistics, RSS/CPU sampling, graceful-stop reporting, explicit
  acceptance results, optional p95/coalescing budgets, and JSON evidence. It takes
  a bounded final status/output snapshot immediately before shutdown and records
  whether that snapshot succeeded. Its report now exposes `coalescedFrames`
  explicitly while retaining the historical `droppedFrames` alias; its CLI uses
  `--max-coalesced-frames` and accepts the old `--max-dropped-frames` option as
  a compatibility alias. Transport failures remain in `failedFrames`. Each
  report also records the repository revision, Server assembly SHA-256, and
  assembly size so long-running evidence can be tied to an exact build. It
  records playback start/end timestamps and the measured `durationSeconds`, so
  the requested duration is independently reviewable instead of inferred from
  the harness start/finish timestamps. The optional
  `--min-duration-seconds` acceptance budget turns an early stop into a failed
  report; the documented eight-hour command sets it to `28800`. The
  five-minute local smoke passed with 5,987 sent, 8 coalesced, 0 failed, and
  0 reconnects under an explicit 60-frame (1% of a 20 Hz five-minute run)
  coalescing budget; p95 revision interval was 50.360 ms under a 100 ms budget, peak
  RSS was about 117 MiB, CPU time was 9.27 s, and shutdown was graceful. A
  reproducible follow-up from the current branch (`/tmp/mfp-portable-soak-5m-final.json`)
  completed graceful shutdown with 5,932 sent, `droppedFrames=63` in the old
  schema (canonical interpretation: 63 sampled/coalesced), 0 failed, and 0
  reconnects; p95 revision interval was 50.347 ms, peak RSS about 127 MiB, and
  CPU time 9.14 s. That run failed only the diagnostic 60-frame coalescing
  budget; it does not indicate transport loss (`failedFrames` stayed zero), but
  it also does not satisfy the budgeted soak gate. CI runs a 10-second smoke with
  the same p95/coalescing budgets and uploads the report. Neither run replaces the
  required eight-hour Linux/fnOS soak or target-device failure matrix. A fresh
  eight-hour Linux run was started on 2026-08-08 05:26:47 CST with
  `--duration-seconds 28800`, a 100 ms revision-interval budget, and a 5,760
  coalesced-frame budget. Its detached process is still active; health and
  `Playing` status probes remain successful, but no final JSON evidence exists
  yet, so P7-02 remains unchecked until graceful completion and report review.
  That process predates the new minimum-duration option; its final evidence
  must therefore be checked against the measured timestamps/elapsed process
  time, while subsequent runs use `--min-duration-seconds 28800` directly.
- `tools/run-portable-upgrade.py` now starts independent Server processes
  against the same four roots, exercises both graceful shutdown and a forced
  process interruption, and verifies restoration of an uploaded script, an
  axis policy, runtime logs, and all persistent directories. The local
  rehearsal provides process-level restart evidence, and optional baseline and
  rollback DLL arguments allow a versioned upgrade rehearsal. A current-branch
  run using the same Release DLL for baseline, upgrade, and rollback passed all
  three phases (including forced interruption and state restoration); this
  validates the harness path only, not a different-version upgrade. A
  versioned local rehearsal using the `e3f2f8a3` Server publish as baseline and
  rollback, with the current branch as upgrade
  (`/tmp/mfp-versioned-upgrade-c4e91a59.json`), also passed all phases and
  restored scripts, axis policy, logs, and all four persistent roots after
  upgrade and rollback. This remains process-level
  evidence only; Docker volume and fnOS package upgrade/rollback evidence
  remains external. The report binds each baseline, upgrade, and rollback DLL
  to its SHA-256 and size, and records the repository revision; the forced-stop
  helper has a unit test.
- The output view and `GET /api/v1/output-targets` now show per-target
  sent/coalesced/failed frame counts (`coalescedFrames` is canonical and
  `droppedFrames` remains a historical response alias),
  reconnects, and last-send time; the diagnostics view consumes migration and
  adapter capability endpoints and exposes repository, general, security,
  serial, and log state with a real refresh action. Device-axis editing covers
  range, inversion, interpolation, and optional limits with persisted REST
  settings and Application-level evaluation tests. The checked-in Web
  architecture contract includes a redacted output-target JSON example for
  this response compatibility rule. Failed sink writes now
  expose a transient `lastError` in the output-target contract and clear it
  after a successful write, with Infrastructure coverage for failure and
  recovery. `CoalescedFrames` is an added init-only response property rather
  than a new positional constructor parameter, preserving the existing .NET
  response construction shape for older output plugins. The error detail is
  an optional companion interface, preserving the existing public diagnostics
  contract for older output plugins.
- The Web entry now handles a protected server without a token explicitly:
  HTTP 401 promotes an access-token/one-time-pairing form, rejected tokens do
  not fall back to demo state, and successful pairing stores only the browser
  bearer token. The focused Playwright suite now lists ten tests across the
  desktop and mobile projects, including the no-token/authentication path;
  actual browser execution remains dependent on the host/CI Chromium system
  libraries.
- P4-03 now includes the direct Buttplug protocol-v3 WebSocket output adapter.
  It performs server-info negotiation, current-device enumeration, scanning,
  strict device selection, Scalar/Linear/Rotate/Stop commands, bounded queue
  handling, cancellation, malformed-message rejection, and fault propagation.
  The adapter has no WPF, Stylet, or Buttplug SDK dependency, and its simulator
  tests pass alongside the existing TCP/UDP/WebSocket, The Handy, file, and
  serial coverage.
- Acceptance evidence now includes pairing/authentication tests, simulated
  Jellyfin/Emby/Plex configuration and connection, scripts-root upload followed
  by multi-axis load, legacy configuration migration, the Core/Application
  desktop-reference scan, backend playback/output after browser disconnect,
  including a state-WebSocket connect/close followed by continued file-output
  frames, TCP and WebSocket peer restart/reconnect simulations, Linux PTY
  serial open/write, and the published operations/security documentation.
- Configuration migration acceptance now rejects non-object JSON roots,
  preserves the source byte-for-byte, rejects missing/oversized/reparse-point
  sources and targets, honors cancellation, and proves the explicit Data
  Protection key root can decrypt data after a separate host instance starts;
  removing that key root is also proven to reject old protected data. An
  independent-host persistence test covers configuration, keys, scripts, and
  logs together; actual Docker/fnOS upgrade and rollback rehearsal remains
  external.
- `tools/validate-release-evidence.py` and
  `deploy/release-evidence.example.json` now define the twenty release and
  target-acceptance gates in machine-readable form. Development validation may
  retain pending gates; `--require-complete` rejects any pending/failed gate or
  passed gate without an evidence reference.
- The tag-based publish workflow now requires a separate
  `deploy/release-evidence.json` and runs the complete evidence gate before
  building release archives; the current branch intentionally does not provide
  a fabricated complete manifest.
- `tools/check-portable-boundary.py` now scans Core/Application source and
  project files for WPF, Stylet, Win32/WMI, registry, and DPAPI references; the
  current scan passes and is enforced in the portable CI job.
- Persistent Data Protection now uses an explicit 90-day automatic key
  lifetime, bounded by `MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS` (7-3650 days),
  with resolver and ASP.NET `KeyManagementOptions` integration tests plus
  operations documentation requiring old key files to remain available during
  rotation. Server tests that mutate process-level environment variables share
  a non-parallel collection so the integration evidence is deterministic.
- `tools/run-validation-matrix.py` now executes the reproducible local unit,
  integration, contract, Web/browser, boundary, packaging, and release-template
  checks and writes a redacted JSON report. The latest 2026-08-08 Linux rerun
  with user-local Chromium host libraries on `LD_LIBRARY_PATH` passed 24
  checks with 0 failures and covered .NET/Web/Docs high-severity dependency
  audits, all ten Playwright desktop/mobile executions, and a 10-second Linux
  soak smoke with explicit p95 and coalescing budgets. The report now records
  its top-level `repositoryRevision`, `endedAtUtc`, and `durationSeconds`; the
  current run is bound to `4c9bbef6b97f3d6cd6c3e4e2d2644e42d123cf94` and took
  56.313 seconds, while four target-environment gates remain pending. The
  interruption subset covers active-playback browser
  disconnect, output-endpoint recovery, TCP/WS peer restart, network-player
  restart protocol simulation, Buttplug device removal, and persistent-volume
  container replacement. The process persistence check also exercises the
  same-build baseline/rollback branch; it remains process-level evidence only.
  The Ubuntu CI job installs browser
  dependencies and uploads the same artifact for each run. Command output is
  decoded as UTF-8 with replacement for malformed bytes, so a localized or
  binary diagnostic cannot abort evidence collection. When a local Playwright
  process fails specifically because the host browser shared libraries are
  absent, the matrix records that check as pending with an actionable reason;
  actual browser assertion failures remain failures.
- The CI workflow files also pass `actionlint` v1.7.12 using the same
  checksum-pinned binary download configured by the `detect-changes` job;
  this validates the workflow expressions and shell blocks locally, while
  runner execution remains an external gate.
- `tools/run-target-acceptance.py` provides a dependency-free black-box HTTP
  acceptance harness for an installed Linux/fnOS Web endpoint. It checks
  authentication, health, SPA fallback, capabilities, adapter/serial
  discovery, media-source device/session configuration and secret redaction,
  script upload/load, playback commands, and log redaction without placing
  bearer tokens in the report. The container CI job runs it for both
  architectures and uploads the report; physical output and firmware checks
  remain target-specific.
- The current local Release Server build passed that target harness 15/15 on
  an isolated port with separate config, key, script, and log roots. The run
  verified liveness/readiness, SPA fallback, authentication, redacted settings,
  capabilities, adapter/serial discovery, media-source create/update/delete
  with `deviceId` retention and `secretRef` redaction, multi-axis script
  upload/load, seek/play/stop, and bounded redacted logs; it is local evidence
  only, not a Docker, fnOS, or physical-device acceptance.
- The supporting Python validation/tooling suite passes 41/41, including
  target-harness media CRUD/redaction, browser-host dependency classification,
  and explicit soak coalescing-budget evidence tests.

Still open and intentionally not advertised as complete:

- The Handy HTTP adapter, Buttplug protocol-v3 output adapter, and
  Emby/Jellyfin/Plex network media adapters are directly wired and
  capability-reported. The remaining CRUD/registry entries clearly report
  planned or blocked capabilities. Initial pairing is implemented, but
  Docker/fnOS upgrade/rollback rehearsal remains open; key rotation policy is
  implemented and documented for the persistent Data Protection root.
  Serial discovery and open/write are
  implemented, but physical reconnect and permission validation remain
  external gates. Data
  Protection persistence is implemented for an explicitly mounted key root;
  removal of an explicit key root is covered by a negative decryption test.
- Host-level restart-required settings forms and the broader physical
  browser/device matrix remain open. The resource/axis forms, protected-server
  bootstrap, service-worker API boundary, and focused Playwright harness are
  implemented; the latest desktop/mobile screenshot run passed locally with
  user-local browser libraries, while CI still supplies the reproducible
  browser environment.
- The local validation matrix is now reproducible and machine-readable, but its
  four target-environment entries remain pending until Docker/Buildx, fnOS,
  physical output, and container restart evidence is attached.
- Docker Buildx execution, physical amd64/arm64 NAS checks, serial hardware,
  clean fnOS installation/upgrade/rollback, and long-duration soak evidence.
- Docs dependency remediation is complete: the full documentation audit is
  clean at the current lockfile, and the CI job now fails on high/critical
  findings. Trivy or an equivalent scan of the exact runtime images, plus
  release-owner review of image/FPK artifacts, remain release-environment
  work. The repository has dependency and file-level SBOM generation, but the
  static packaging contract is evidence of structure, not a substitute for
  those scans.
- The current local dependency audit reports no vulnerable packages across all
  .NET projects and zero NPM vulnerabilities in both Web and Docs lockfiles.
  This is repository dependency evidence only; exact published runtime image
  scanning and artifact review remain external P7-06 gates.

These are external or higher-scope gates rather than reasons to roll back the
portable foundation. The acceptance checklist below remains unchecked until
the corresponding evidence is attached.

## 10. Validation Matrix

### Tier 1 - Static and Unit Validation

- Build all .NET projects with warnings reviewed.
- Type-check, lint, and production-build the Web project.
- Verify Core/Application contain no WPF, Stylet, Win32, WMI, registry, or DPAPI references.
- Run parser, interpolation, synchronization, limits, axis mapping, and TCode golden tests.
- Validate JSON schemas, configuration migration fixtures, OpenAPI generation, Dockerfile, compose, and FPK manifest.
- Scan logs/fixtures for credentials and personal paths.

Exit condition: deterministic builds and unit tests pass on clean runners.

### Tier 2 - Component and Integration Validation

- Start the headless engine with a fake clock and fake media/output adapters.
- Test each media/output adapter against a simulator or controlled endpoint.
- Test connect, disconnect, timeout, reconnect, cancellation, malformed input, and graceful shutdown.
- Test settings round-trip and migration from sanitized desktop fixtures.
- Test secret-key persistence and expected failure after key removal.
- Test serial discovery and stable device identity on Linux.
- Test REST authorization, validation, concurrency, and SignalR reconnect/state resynchronization.

Exit condition: components recover predictably and do not leak tasks, handles, sockets, secrets, or unbounded queues.

### Tier 3 - End-to-End Product Validation

- Use Playwright to complete first-run setup, login/pairing, media configuration, script load, output configuration, playback, reconnect, and log download.
- Run desktop and mobile viewport screenshot checks.
- Confirm browser refresh/closure does not interrupt backend output.
- Confirm UI state is throttled while backend output keeps the configured frequency.
- Run a real network player to simulated TCP/UDP/WebSocket outputs.
- Validate multi-axis scripts, offsets, seeking, pause/resume, speed change, looping, and auto-connect.

Exit condition: the documented MVP workflow is usable without shell access or direct configuration editing.

### Tier 4 - Target Environment Acceptance

- Debian/Ubuntu amd64 Docker install, restart, upgrade, rollback, and log review.
- Linux arm64 install and runtime verification on physical hardware before claiming ARM support.
- fnOS x86 FPK install from a clean system, Web entry launch, volume selection, restart, upgrade, rollback, and uninstall.
- fnOS ARM validation before publishing an ARM FPK/image claim.
- Real NAS USB serial pass-through and reconnect.
- Network media source on another machine plus at least one real device/output.
- Eight-hour soak test with CPU, memory, GC, output interval, jitter, network errors, and reconnect count captured.

Exit condition: target-environment evidence is attached to the release candidate and no platform claim depends only on emulation.

## 11. Risk Register

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| NAS cannot access local Windows player IPC | High | High | MVP favors network protocols; design optional Windows agent separately |
| Script engine remains coupled to WPF state | High | High | Extract behind golden tests before adding APIs |
| Configuration embeds old CLR type names | High | High | Version DTOs; migration fixtures; preserve unreadable input |
| Container scheduling increases output jitter | Medium | High | Backend-only loops; soak tests; configurable intervals; document hardware limits |
| Serial device names/permissions change | High | Medium | Prefer `/dev/serial/by-id`; explicit mapping and reconnect handling |
| Remote player binds only to localhost | Medium | High | Compatibility matrix and setup documentation; agent where required |
| Existing WPF plugins cannot load headlessly | High | Medium | Declare MVP incompatibility; define plugin v2 after API stabilizes |
| Browser UI receives excessive event volume | Medium | Medium | Coalesce snapshots to 10-20 Hz; never stream every engine tick |
| File browser exposes arbitrary NAS files | Medium | High | Constrained roots, canonical-path checks, authorization, symlink tests |
| LAN deployment is unintentionally exposed | Medium | High | Authentication by default; secure headers; explicit bind/reverse-proxy docs |
| ARM dependency incompatibility | Medium | Medium | Multi-arch CI plus physical ARM acceptance before publishing support |
| Low current test coverage hides regressions | High | High | Phase 0 golden tests are a gate, not optional cleanup |

## 12. Observability and Failure Handling

- Use structured logs with component, instance ID, endpoint type, connection state, and correlation ID.
- Never log API keys, tokens, protected strings, Authorization headers, or full configuration documents containing secrets.
- Track media position update age, output update rate, jitter, queue depth, reconnect count, last error, and dropped/coalesced UI updates.
- Expose liveness separately from readiness; a disconnected optional device must not make the process dead.
- Surface adapter failures through API state and logs rather than UI dialogs.
- Use bounded log retention and bounded real-time channels.
- Cancel and await all adapter tasks during shutdown; force termination only after a documented timeout.
- Back up configuration before every schema migration and retain the prior schema version.
- On an unsupported platform capability, return `Unavailable` with a reason; do not crash startup.

## 13. Release Acceptance Criteria

The community MVP is complete only when all of the following are true:

- [ ] A clean Linux host can start the documented Docker deployment without a desktop environment.
- [ ] fnOS can install the FPK and open the application from its Web entry.
- [x] First-run authentication/pairing is enabled and documented.
- [x] A user can configure at least one supported remote media source.
- [x] A user can load a local-volume Funscript and observe correct multi-axis state.
- [x] Serial and at least TCP, UDP, and WebSocket outputs pass integration tests.
- [x] Backend playback/output continues when all browsers disconnect.
- [ ] Configuration and keys survive restart and upgrade.
- [x] Existing supported desktop configuration has a tested migration or a documented safe re-entry path.
- [x] No Core/Application project references Windows desktop assemblies.
- [ ] amd64 is physically validated; arm64 is only advertised after physical validation.
- [ ] Tier 1-4 evidence is complete for the published compatibility matrix.
- [x] Installation, backup, restore, upgrade, rollback, security, and troubleshooting documents are published.
- [ ] Existing WPF release remains available as rollback during the beta period.

The output-protocol acceptance item is backed by the Linux pseudo-terminal serial
test, TCP/UDP/WebSocket transport loopback tests, and the Server output-registry
integration that sends a multi-axis frame to all three network transports. This
does not replace physical device, permission, or reconnect acceptance on a NAS.

## 14. First Execution Slice

The first implementation slice should prove the architecture without starting the full UI rewrite:

1. Establish Phase 0 golden tests and deterministic restore.
2. Upgrade the WPF baseline to .NET 10 in isolation.
3. Extract parser, interpolation, axis state, synchronization, and TCode formatting into `MultiFunPlayer.Core`.
4. Create a headless engine test using Internal media time and a fake TCP output.
5. Create a minimal ASP.NET Core server exposing status, load, play/pause, seek, and axis snapshots.
6. Build a minimal status page only after the headless end-to-end test passes.
7. Run the server in a local Linux container.

First-slice exit condition:

```text
Funscript fixture
  -> headless ScriptEngine
  -> deterministic axis values
  -> TCode formatter
  -> fake TCP receiver
  -> values visible through REST/SignalR
```

This slice is expected to take 15-25 person-days and determines whether the remaining MVP estimates need revision.
