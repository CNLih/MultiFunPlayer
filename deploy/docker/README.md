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

The Dockerfile has not been executed on this host because Docker is unavailable.
The first hardware validation must cover both `linux/amd64` and `linux/arm64`.
