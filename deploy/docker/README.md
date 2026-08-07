# Linux container

Build and start the combined server/UI image from the repository root:

```sh
export MFP_AUTH_TOKEN='replace-with-a-long-random-token'
docker compose -f deploy/docker/compose.yaml up --build -d
```

The container listens on port `8080` and serves the SPA and `/api/v1` from one
origin. Named volumes preserve `/config`, `/keys`, `/scripts`, and `/logs`.
The default image is non-root and read-only; add a narrowly scoped serial device
mapping only when a Linux output adapter is configured.

Compose uses a bridge network by default and waits up to 30 seconds for a
graceful stop. The service is capped at two CPUs and 512 MiB; adjust the
deployment override for a host with different output-loop requirements. A
host-network override is documented in the compose file for remote protocols
that depend on LAN discovery, and should be used only when bridge networking
cannot reach the target.

The Dockerfile has not been executed on this host because Docker is unavailable.
The first hardware validation must cover both `linux/amd64` and `linux/arm64`.
