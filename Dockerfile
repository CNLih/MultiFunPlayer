# Build the browser bundle separately so the runtime image contains only the
# published ASP.NET Core service and static assets.
FROM node:22-alpine AS web-build
WORKDIR /src/web
COPY Source/MultiFunPlayer.Web/package.json Source/MultiFunPlayer.Web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY Source/MultiFunPlayer.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY . .
COPY --from=web-build /src/web/dist/ Source/MultiFunPlayer.Server/wwwroot/
RUN dotnet restore Source/MultiFunPlayer.Server/MultiFunPlayer.Server.csproj --locked-mode
RUN dotnet publish Source/MultiFunPlayer.Server/MultiFunPlayer.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --property:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    MFP_SCRIPTS_PATH=/scripts \
    MFP_CONFIG_PATH=/config \
    MFP_KEYS_PATH=/keys \
    MFP_LOGS_PATH=/logs
COPY --from=server-build /app/publish ./
RUN apt-get update \
    && apt-get install --no-install-recommends --yes busybox-static \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config /keys /scripts /logs \
    && useradd --create-home --uid 10001 --shell /usr/sbin/nologin mfp \
    && chown -R mfp:mfp /app /config /keys /scripts /logs
USER mfp
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD /bin/busybox wget --quiet -O /dev/null http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "MultiFunPlayer.Server.dll"]
