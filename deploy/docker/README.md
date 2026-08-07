# Linux container

Build and start the combined server/UI image from the repository root:

```sh
export MFP_AUTH_TOKEN='replace-with-a-long-random-token'
docker compose -f deploy/docker/compose.yaml up --build -d
```

Production deployments must set either `MFP_AUTH_TOKEN` or the documented
`MFP_PAIRING_TOKEN` before exposing the port. Leaving both empty is intended
only for a local, isolated development run.

The container listens on port `8080` and serves the SPA and `/api/v1` from one
origin. Named volumes preserve `/config`, `/keys`, `/scripts`, and `/logs`.
The default image is non-root and read-only; add a narrowly scoped serial device
mapping only when a Linux output adapter is configured.

For serial discovery, map the relevant `/dev/serial/by-id` tree or set
`MFP_SERIAL_BY_ID_ROOT` to the mapped path. The service lists stable links via
`/api/v1/devices/serial`; opening and writing still requires the configured
device to be passed through and readable/writable by the container user.

Compose uses a bridge network by default and waits up to 30 seconds for a
graceful stop. The service is capped at two CPUs and 512 MiB; adjust the
deployment override for a host with different output-loop requirements. A
host-network override is documented in the compose file for remote protocols
that depend on LAN discovery, and should be used only when bridge networking
cannot reach the target.

The Dockerfile has not been executed on this host because Docker is unavailable.
The first hardware validation must cover both `linux/amd64` and `linux/arm64`.

For a local protocol smoke target, start the optional simulators with:

```sh
docker compose -f deploy/docker/compose.yaml --profile simulators up --build
```

The TCP receiver is available to the application as `fake-tcp:18000`; the UDP
receiver is `fake-udp:18001`. Both print bounded payload previews and have no
device or filesystem access. This simulator profile is not enabled by default.

Before invoking Docker, run the repository-owned static contract check:

```sh
./deploy/validate-packaging.sh
```

This verifies the Dockerfile, compose security defaults, persistent volume
names, and the structural fnOS manifest. It does not build an image or replace
Docker Compose, Buildx, Trivy, or fnOS/fnpack validation on a target host.

For a headless Linux soak, build the Release server first and run the portable
harness from the repository root:

```sh
python3 tools/run-portable-soak.py \
  --dotnet /path/to/dotnet \
  --duration-seconds 28800 \
  --max-revision-interval-ms 100 \
  --output artifacts/soak/portable-soak.json
```

The harness starts an isolated server with temporary config, key, script, and
log roots, then records output sent/dropped/failed counts, reconnects, status
revision intervals, process RSS, CPU time, and graceful shutdown. A short
duration is suitable for CI smoke checks; only a target Linux/fnOS run can
provide the release-required eight-hour evidence. The p95 revision interval
budget is optional. Dropped/coalesced frames remain diagnostic by default; add
`--max-dropped-frames 0` only when the target's output policy requires zero
drops. The command exits non-zero for failed frames, no output, abnormal
shutdown, or an explicitly exceeded budget.

To rehearse a process-level upgrade while preserving the same four roots, run:

```sh
python3 tools/run-portable-upgrade.py \
  --dotnet /path/to/dotnet \
  --output artifacts/upgrade/portable-upgrade.json
```

This starts independent server instances, performs a graceful stop and a
forced process interruption, then verifies scripts, device settings, logs, and
all persistent root directories after restart. It is process-level evidence
only; it does not replace Docker volume migration or fnOS upgrade/rollback
testing. To compare separate builds and exercise rollback, add
`--baseline-server /path/to/previous/Server.dll` and
`--rollback-server /path/to/rollback/Server.dll`.
