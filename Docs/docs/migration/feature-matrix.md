---
sidebar_position: 2
---

# Web/Linux Migration Feature Matrix

This matrix is the Phase 0 baseline for the headless Web/Linux/fnOS migration. `Baseline` means the current Windows desktop behavior is covered by the checked-in tests and remains supported while the service is developed. `MVP` means the capability is planned for the first headless release. `Deferred` is intentionally unsupported until a companion or a platform-specific adapter exists.

## Script and timing behavior

| Capability | Baseline | MVP treatment | Notes |
| --- | --- | --- | --- |
| Funscript and CSV parsing | Baseline | MVP | Parser fixtures and clamping behavior are golden-tested. |
| Linear, PCHIP, Makima, and step interpolation | Baseline | MVP | Keep evaluation in the backend output loop. |
| Axis mapping, offsets, limits, and synchronization | Baseline | MVP | Move the calculations to the platform-neutral Core in Phase 2. |
| Windows Raw Input and XInput | Baseline | Deferred | Requires a Windows companion agent. |
| WPF curve editors, heatmaps, and dialogs | Baseline | Deferred | Web controls will expose equivalent state and commands, not pixel parity. |

## Media sources

| Source | Baseline | MVP treatment | Required deployment condition |
| --- | --- | --- | --- |
| Internal timeline | Baseline | MVP | Scripts and media are read from configured server volumes. |
| Emby, Jellyfin, and Plex | Baseline | MVP | Network endpoint must be reachable from the service. |
| VLC and MPC HTTP | Baseline | MVP | Enable the player's remote HTTP interface and firewall access. |
| OFS WebSocket | Baseline | MVP | Configure the remote WebSocket endpoint. |
| DeoVR, HereSphere, and Whirligig | Baseline | MVP (conditional) | Remote control/server mode must be enabled; no local process detection. |
| MPV local integration | Baseline | Deferred | Requires a documented remote TCP/Unix-socket mode or Windows agent. |
| PotPlayer Win32 integration | Baseline | Deferred | Requires a Windows companion agent. |

## Output targets

| Target | Baseline | MVP treatment | Required deployment condition |
| --- | --- | --- | --- |
| Serial TCode | Baseline | MVP | Pass the serial device into Docker and use stable `/dev/serial/by-id` paths. |
| TCP, UDP, and WebSocket TCode | Baseline | MVP | Endpoint must be reachable from the service. |
| The Handy | Baseline | MVP | API endpoint and connection key are configured server-side. |
| Buttplug.io | Baseline | MVP (conditional) | Buttplug server WebSocket must be reachable from the service. |
| File output | Baseline | MVP | Writes are restricted to configured server volume roots. |
| Windows Named Pipe | Baseline | Deferred | Use a Unix socket or a Windows companion in a later release. |

The matrix is intentionally explicit about unsupported capabilities so that a deployment does not imply that a local Windows player or UI is available on Linux.
