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
- DeoVR, HereSphere, and Whirligig when their remote endpoints are reachable from the NAS. Local process detection must not be required.

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
- The local validation environment uses `/home/henry/.dotnet` (.NET SDK 10.0.302), Node 22, and has no Docker/fnpack or WindowsDesktop runtime; Docker, fnOS, ARM, serial hardware, and Windows UI execution therefore remain external validation gates.

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
- [ ] P2-05 Replace direct WPF dialog calls in shared flows with structured errors and operation results.
- [ ] P2-06 Replace Stylet event dependencies in shared code with an application-owned event/command contract.
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
POST   /api/v1/media-sources/{id}/commands
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
| DeoVR/HereSphere/Whirligig | Conditional network adapter; no NAS-side process check |
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
- [ ] P5-12 Add browser end-to-end tests and screenshot checks at desktop and mobile viewports.

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
- [ ] P6-08 Create the fnOS Docker application project with `fnpack`.
- [ ] P6-09 Add FPK manifest, icons, Web entry, port, volume, upgrade, and uninstall behavior.
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
- [ ] P7-07 Produce an SBOM and list third-party licenses for distributed assets.
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
- Core tests: 8/8; Application tests: 6/6; Infrastructure tests: 28/28;
  Server tests: 24/24.
- Web `typecheck`, static contract check, and Vite production build pass.
- Docusaurus migration documentation builds successfully; only the existing
  Browserslist database freshness warning is reported.
- GitHub Actions now includes an Ubuntu portable job that performs locked
  restore, Core/Application/Infrastructure/Server tests, solution build, and
  SPA type/static/production checks. Windows runner execution remains required
  before P0-04/P1 WPF acceptance can be checked.
- The remote SPA now consumes bounded runtime logs, non-secret settings, and
  stable serial discovery, and renders those diagnostics without taking
  ownership of backend playback state.
- The script view binds its axis visualization to normalized `status.axes`
  values (with a deterministic demo fallback); offset is applied to seek
  requests and the selected clock mode is retained in SPA state. Full
  media-clock arbitration and per-axis editing remain open P5 work.
- The Infrastructure migration reader accepts the sanitized desktop JSON shape,
  emits versioned portable records, normalizes path separators, and omits
  secret fields without modifying the source document. An explicit
  `MFP_LEGACY_CONFIG_PATH` startup import writes the versioned record once under
  the config root and exposes a protected migration status endpoint.
- The Server exposes a versioned read-only adapter capability inventory and
  explicitly labels each transport by implementation status. Browser
  WebSocket binary/invalid text frames are ignored without affecting the
  playback API.
- Script repository reads and uploads reject traversal and reparse points;
  uploads are JSON-validated, bounded, atomic, and opt-in overwrite only.
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
  behavior.
- A persistent media-source registry now exposes CRUD and lifecycle routes for
  the Internal timeline plus explicit capability states for network and
  external-player integrations. Internal sources connect directly; planned or
  blocked entries persist safely, reject embedded credentials, and surface an
  unavailable-adapter error instead of claiming a live connection. Server tests:
  24/24, including The Handy registry integration, device-axis persistence,
  network media registry behavior, and redacted JSONL log persistence/download.
- P4-01 now includes portable Infrastructure HTTP adapters for Emby,
  Jellyfin, and Plex wired into the media-source registry. Emby/Jellyfin
  validate `/System/Ping` and parse `/Sessions` `PlayState`/
  `NowPlayingItem` responses, then expose Pause/Unpause/Seek commands. Plex
  parses `/player/timeline/poll` XML and the referenced metadata `Part`, then
  exposes the documented playback commands. The adapter polls a real session
  or timeline response before reporting connected; health checks alone do not
  claim a media clock. `secretRef` stores only an environment variable name
  (`MFP_EMBY_API_KEY`, `MFP_JELLYFIN_API_KEY`, or `MFP_PLEX_TOKEN` by default),
  while `deviceId` selects the remote session/client. Bounded JSON/XML parsing,
  XML DTD rejection, malformed response handling, cancellation, and protocol
  simulator tests cover all three sources (Infrastructure 28/28). Registry
  integration verifies normalized `mediaState` and that token values never
  reach `media-sources.json`; MPV/VLC/MPC and other process/proxy-bound sources
  remain blocked.
- `MultiFunPlayer.Application` now exposes platform-neutral ports for media
  adapters, output adapters, configuration, secrets, rooted files, and serial
  discovery. Implementations remain in Infrastructure/Server and can be
  replaced without changing Core timing or script evaluation.
- The SPA now loads media/output registries and device-axis policies from the
  server, provides persisted create/connect/disconnect/delete workflows, and
  renders planned/blocked capability errors. Script upload remains available
  in both demo and remote modes; browser screenshot automation remains open.
- Local server smoke: anonymous liveness, protected API, OpenAPI, inline
  multi-axis Funscript load, play, seek, SPA fallback, authenticated
  WebSocket snapshot, settings, serial discovery, and redacted bounded logs
  all pass. WebSocket snapshots use camelCase to match REST.
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
- The packaging contract validator reports 40/40 checks passed for the
  multi-stage Dockerfile, non-root/read-only compose defaults, four persistent
  roots, health check, secret interpolation, and the structural fnOS manifest.
  The same validator runs in the packaging CI job. Docker Buildx, `fnpack`,
  official fnOS schema validation, image scanning, and target-NAS execution are
  intentionally still external gates.
- Current dependency checks report no vulnerable .NET packages and no
  production Web-package vulnerabilities. The Docs site still uses the locked
  Docusaurus 3.1 dependency graph: its audit reports 62 development-only
  findings (6 low, 33 moderate, 21 high, 2 critical). A lockfile-only update
  was attempted but npm registry resolution timed out without changing the
  lockfile, so P7-06 remains open for a controlled Docusaurus upgrade and
  container scan before a release candidate.
- `tools/generate-sbom.py` now produces a deterministic CycloneDX 1.5
  dependency SBOM from restored NuGet assets and committed npm lockfiles; the
  current workspace contains 1,325 deduplicated components and CI uploads the
  artifact. The exact runtime image/FPK SBOM and license review remain open
  until those artifacts can be built in their target environments.
- The output view now shows per-target sent/dropped/failed frame counts,
  reconnects, and last-send time; the diagnostics view consumes migration and
  adapter capability endpoints and exposes repository, general, security,
  serial, and log state with a real refresh action. Device-axis editing covers
  range, inversion, interpolation, and optional limits with persisted REST
  settings and Application-level evaluation tests.
- P4-03 now includes the direct Buttplug protocol-v3 WebSocket output adapter.
  It performs server-info negotiation, current-device enumeration, scanning,
  strict device selection, Scalar/Linear/Rotate/Stop commands, bounded queue
  handling, cancellation, malformed-message rejection, and fault propagation.
  The adapter has no WPF, Stylet, or Buttplug SDK dependency, and its simulator
  tests pass alongside the existing TCP/UDP/WebSocket, The Handy, file, and
  serial coverage.

Still open and intentionally not advertised as complete:

- The Handy HTTP adapter, Buttplug protocol-v3 output adapter, and
  Emby/Jellyfin/Plex network media adapters are directly wired and
  capability-reported. The remaining CRUD/registry entries clearly report
  planned or blocked capabilities. Initial pairing is implemented, but
  upgrade/backup rehearsal and
  recovery documentation remain open. Serial open/write is implemented, but
  physical reconnect and permission validation remain external gates. Data
  Protection persistence is implemented for an explicitly mounted key root;
  key rotation/removal and upgrade rehearsal remain open.
- Browser Playwright/screenshot tests and full UI settings forms.
- Docker Buildx execution, fnpack/official fnOS schema validation, physical
  amd64/arm64 NAS checks, serial hardware, and long-duration soak evidence.
- Docs dependency remediation and Trivy or an equivalent image scan remain
  release-environment work. The repository now has dependency-level SBOM
  generation, but the static packaging contract is evidence of structure, not
  a substitute for image/FPK scans.

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
- [ ] First-run authentication/pairing is enabled and documented.
- [ ] A user can configure at least one supported remote media source.
- [ ] A user can load a local-volume Funscript and observe correct multi-axis state.
- [ ] Serial and at least TCP, UDP, and WebSocket outputs pass integration tests.
- [ ] Backend playback/output continues when all browsers disconnect.
- [ ] Configuration and keys survive restart and upgrade.
- [ ] Existing supported desktop configuration has a tested migration or a documented safe re-entry path.
- [ ] No Core/Application project references Windows desktop assemblies.
- [ ] amd64 is physically validated; arm64 is only advertised after physical validation.
- [ ] Tier 1-4 evidence is complete for the published compatibility matrix.
- [ ] Installation, backup, restore, upgrade, rollback, security, and troubleshooting documents are published.
- [ ] Existing WPF release remains available as rollback during the beta period.

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
