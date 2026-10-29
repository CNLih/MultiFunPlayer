---
sidebar_position: 4
---

# Headless Web Architecture

For a guided tour of the current code, runtime boundaries, playback flows,
Web/3D, and NAS deployment, see [当前整体框架与代码导读](./architecture-walkthrough.md).

The migration keeps timing and protocol behavior in C# while replacing the
desktop shell with an ASP.NET Core host and a browser client.

```text
Browser SPA/PWA
  REST /api/v1 + bounded WebSocket snapshots
ASP.NET Core Server
  hosted lifecycle, auth, storage, registries, logs, OpenAPI
Infrastructure
  bounded TCP/UDP/WebSocket/File/Serial transports
  The Handy HDSP v2 and OFS WebSocket adapters
Application ports
  media/output/configuration/secret/file/serial boundaries
Core
  script parsing, interpolation, axis mapping, timing, TCode formatting
```

The engine owns playback time and output dispatch. The browser owns only view
state and commands; refreshing or closing it cannot stop the backend loop.
Each output target has its own persisted `sourceAxis` and `updateRateHz` (1-20)
sampler; up to 10% scheduler jitter at the sample boundary is tolerated so
normal timer variance is not counted as a drop. Transport queues and log stores
are bounded. Adapter failures become structured state and logs instead of WPF
dialogs.

## Contracts

- REST schemas and routes are exposed at `/openapi/v1.json`.
- `/api/v1/status` and `/hubs/state` use the same camel-case status shape.
- Browser WebSocket authentication uses a subprotocol, never a query-string
  token.
- `/api/v1/adapters` distinguishes `ready`, `planned`, `blocked`, and
  `unsupported` capabilities; entries are not feature claims until their
  implementation status is `ready`.

The output-target diagnostics response keeps the historical `droppedFrames`
field for older clients while exposing the unambiguous `coalescedFrames` name:

```json
{
  "id": "studio-bridge",
  "protocol": "tcp",
  "connected": true,
  "sentFrames": 1200,
  "coalescedFrames": 12,
  "droppedFrames": 12,
  "failedFrames": 0,
  "reconnects": 1,
  "lastError": null
}
```

`coalescedFrames` and its historical alias count samples merged by the
per-target rate limiter; `failedFrames` is reserved for sink write failures.

## Ownership rules

- Core/Application cannot reference WPF, Stylet, registry, WMI, Win32, or
  DPAPI APIs.
- Infrastructure contains network/device implementations and can be replaced
  by a Linux-specific adapter without changing script evaluation.
- Server owns persistence, authorization, lifecycle, and public DTO mapping.
- The SPA must use the API client and never write configuration files directly.
- Secrets are injected through host-managed stores or environment variables;
  they are absent from persisted target definitions and logs.

The original WPF host remains in the solution as a Windows compatibility and
rollback target. It is not required by the portable Server project.
