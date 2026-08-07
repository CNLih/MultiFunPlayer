using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server.Tests;

public sealed class ServerApiTests : IClassFixture<WebApplicationFactory<MultiFunPlayer.Server.Program>>
{
    private readonly WebApplicationFactory<MultiFunPlayer.Server.Program> _factory;

    public ServerApiTests(WebApplicationFactory<MultiFunPlayer.Server.Program> factory)
    {
        _factory = factory;
        Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", null);
    }

    [Fact]
    public async Task Health_is_anonymous_and_ready()
    {
        using var client = _factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Status_and_capabilities_are_available()
    {
        using var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/status");
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");

        Assert.Equal("empty", status.GetProperty("state").GetString(), ignoreCase: true);
        Assert.Equal("v1", capabilities.GetProperty("apiVersion").GetString());
        Assert.InRange(capabilities.GetProperty("tickHz").GetInt32(), 1, 20);
    }

    [Fact]
    public async Task Adapter_capabilities_are_versioned_and_do_not_claim_unwired_crud()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/adapters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("v1", payload.GetProperty("apiVersion").GetString());
        Assert.Equal("1.0", payload.GetProperty("schemaVersion").GetString());
        var transports = payload.GetProperty("transports").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "tcp", "udp", "websocket", "serial", "file", "the-handy" })
        {
            Assert.True(transports.ContainsKey(id));
            Assert.Equal("direct", transports[id].GetProperty("integration").GetString());
        }
        Assert.Contains(transports.Keys, id => id.Equals("ofs-websocket", StringComparison.OrdinalIgnoreCase));

        var adapters = payload.GetProperty("adapters").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ready", adapters["output.tcp"].GetProperty("implementationStatus").GetString());
        Assert.Equal("external-proxy", adapters["output.buttplug"].GetProperty("integration").GetString());
        Assert.Equal("ready", adapters["output.the-handy"].GetProperty("implementationStatus").GetString());
        Assert.Equal("unsupported", adapters["output.pipe"].GetProperty("implementationStatus").GetString());
        Assert.Equal("blocked", adapters["media.mpv"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.ofs"].GetProperty("implementationStatus").GetString());
    }

    [Fact]
    public async Task State_websocket_ignores_malformed_client_frames()
    {
        var websocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await websocketClient.ConnectAsync(new Uri("ws://localhost/hubs/state"),
            CancellationToken.None);
        var buffer = new byte[4096];
        var initial = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, initial.MessageType);
        Assert.Contains("state", Encoding.UTF8.GetString(buffer, 0, initial.Count), StringComparison.Ordinal);

        await socket.SendAsync(new byte[] { 0xff, 0x00 }, WebSocketMessageType.Binary, true,
            CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{not-json"), WebSocketMessageType.Text, true,
            CancellationToken.None);

        using var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
    }

    [Fact]
    public async Task Settings_and_serial_device_contracts_are_available_without_secrets()
    {
        using var client = _factory.CreateClient();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var serial = await client.GetFromJsonAsync<JsonElement>("/api/v1/devices/serial");

        Assert.Equal(1, settings.GetProperty("schemaVersion").GetInt32());
        Assert.True(settings.GetProperty("scriptsRoot").GetString()!.Length > 0);
        Assert.False(settings.TryGetProperty("authToken", out _));
        Assert.Equal(JsonValueKind.Array, serial.ValueKind);
    }

    [Fact]
    public async Task Legacy_configuration_is_imported_once_without_overwriting_source()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable);
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "desktop.json");
        var configRoot = Path.Combine(temp.Path, "config");
        await File.WriteAllTextAsync(source,
            "{\"ConfigVersion\":7,\"OutputTarget\":{\"Items\":[]},\"ApiKey\":\"omit-me\"}");
        Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, source);
        try
        {
            var service = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal("migrated", service.Status.State);
            var migrated = await File.ReadAllTextAsync(Path.Combine(configRoot,
                ConfigurationMigrationHostedService.TargetFile));
            Assert.Contains("sourceConfigVersion", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("omit-me", migrated, StringComparison.Ordinal);
            Assert.Contains("ApiKey", await File.ReadAllTextAsync(source), StringComparison.Ordinal);

            var second = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);
            await second.StartAsync(CancellationToken.None);
            Assert.Equal("already-present", second.Status.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Script_catalog_upload_is_json_validated_and_symlink_safe()
    {
        using var temp = new TemporaryDirectory();
        var catalog = new ScriptCatalog(temp.Path);

        Assert.True(catalog.TryWrite("demo.funscript", "{\"actions\":[]}", false,
            out var descriptor, out _, out _));
        Assert.Equal("demo.funscript", descriptor!.Id);
        Assert.False(catalog.TryWrite("demo.funscript", "{\"actions\":[]}", false,
            out _, out _, out var conflict));
        Assert.True(conflict);
        Assert.False(catalog.TryWrite("bad.funscript", "not-json", false,
            out _, out var error, out _));
        Assert.Contains("valid JSON", error, StringComparison.Ordinal);

        var outside = Path.Combine(temp.Path, "outside.json");
        File.WriteAllText(outside, "{\"actions\":[]}");
        var linked = Path.Combine(temp.Path, "linked.funscript");
        File.CreateSymbolicLink(linked, outside);
        Assert.False(catalog.TryRead("linked.funscript", out _));
    }

    [Fact]
    public async Task Script_upload_endpoint_writes_below_the_configured_root()
    {
        var previous = Environment.GetEnvironmentVariable("MFP_SCRIPTS_PATH");
        using var temp = new TemporaryDirectory();
        Environment.SetEnvironmentVariable("MFP_SCRIPTS_PATH", temp.Path);
        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var client = factory.CreateClient();
            var upload = await client.PostAsJsonAsync("/api/v1/scripts", new
            {
                id = "http-upload.funscript",
                content = "{\"actions\":[]}"
            });
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            Assert.True(File.Exists(Path.Combine(temp.Path, "http-upload.funscript")));

            var conflict = await client.PostAsJsonAsync("/api/v1/scripts", new
            {
                id = "http-upload.funscript",
                content = "{\"actions\":[]}"
            });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            var traversal = await client.PostAsJsonAsync("/api/v1/scripts", new
            {
                id = "../outside.funscript",
                content = "{\"actions\":[]}"
            });
            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_SCRIPTS_PATH", previous);
        }
    }

    [Fact]
    public async Task Output_target_registry_persists_and_manages_file_transport()
    {
        using var temp = new TemporaryDirectory();
        var storage = new StorageOptions
        {
            ConfigRoot = Path.Combine(temp.Path, "config"),
            LogsRoot = Path.Combine(temp.Path, "logs")
        };
        using var engine = new MultiFunPlayer.Application.HeadlessPlayerEngine();
        await using var registry = new OutputTargetRegistry(storage, engine,
            NullLogger<OutputTargetRegistry>.Instance);
        await registry.StartAsync(CancellationToken.None);

        var invalid = await registry.CreateAsync(new OutputTargetDefinition(
            "outside", "Outside", "file", "../outside.tcode"));
        Assert.Equal(OutputTargetOperationStatus.Invalid, invalid.Status);

        var invalidRate = await registry.CreateAsync(new OutputTargetDefinition(
            "invalid-rate", "Invalid rate", "file", "invalid-rate.tcode", UpdateRateHz: 21));
        Assert.Equal(OutputTargetOperationStatus.Invalid, invalidRate.Status);

        var created = await registry.CreateAsync(new OutputTargetDefinition(
            "file-1", "File target", "file", "runtime.tcode", UpdateRateHz: 5));
        Assert.Equal(OutputTargetOperationStatus.Success, created.Status);
        Assert.True(created.Target!.Connected);
        Assert.Equal(5, created.Target.UpdateRateHz);
        Assert.True(File.Exists(Path.Combine(storage.LogsRoot, "runtime.tcode")));

        var duplicate = await registry.CreateAsync(new OutputTargetDefinition(
            "file-1", "Duplicate", "file", "duplicate.tcode"));
        Assert.Equal(OutputTargetOperationStatus.Conflict, duplicate.Status);
        Assert.Equal("file-1", Assert.Single(registry.List()).Id);

        await registry.DisconnectAsync("file-1");
        Assert.False(Assert.Single(registry.List()).Connected);
        Assert.Equal(0, Assert.Single(registry.List()).SentFrames);
        Assert.Equal(0, Assert.Single(registry.List()).DroppedFrames);
        Assert.Equal(0, Assert.Single(registry.List()).FailedFrames);
        await registry.StopAsync(CancellationToken.None);

        await using var restarted = new OutputTargetRegistry(storage, engine,
            NullLogger<OutputTargetRegistry>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal("file-1", Assert.Single(restarted.List()).Id);
        Assert.Equal(5, Assert.Single(restarted.List()).UpdateRateHz);
        Assert.Equal(OutputTargetOperationStatus.Success,
            (await restarted.DeleteAsync("file-1")).Status);
        Assert.Empty(restarted.List());
    }

    [Fact]
    public async Task Output_target_registry_routes_the_handy_adapter_without_persisting_key()
    {
        using var temp = new TemporaryDirectory();
        var storage = new StorageOptions
        {
            ConfigRoot = Path.Combine(temp.Path, "config"),
            LogsRoot = Path.Combine(temp.Path, "logs")
        };
        var paths = new List<string>();
        using var engine = new MultiFunPlayer.Application.HeadlessPlayerEngine();
        await using var registry = new OutputTargetRegistry(storage, engine,
            NullLogger<OutputTargetRegistry>.Instance, () => "test-secret-key",
            () => new HandyRegistryHandler(paths));
        await registry.StartAsync(CancellationToken.None);

        var created = await registry.CreateAsync(new OutputTargetDefinition(
            "handy-1", "Handy", "the-handy", "https://simulator.invalid/api/handy/v2/",
            SourceAxis: "L0"));
        Assert.Equal(OutputTargetOperationStatus.Success, created.Status);
        Assert.True(created.Target!.Connected);
        Assert.Equal(new[] { "/connected", "/info", "/mode", "/servertime" }, paths);

        await registry.DisconnectAsync("handy-1");
        Assert.False(Assert.Single(registry.List()).Connected);
        Assert.Equal(OutputTargetOperationStatus.Success,
            (await registry.ConnectAsync("handy-1")).Status);
        Assert.Equal(8, paths.Count);

        var persisted = await File.ReadAllTextAsync(Path.Combine(storage.ConfigRoot,
            "output-targets.json"));
        Assert.DoesNotContain("test-secret-key", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_target_http_contract_supports_create_connect_and_delete()
    {
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        var previousLogs = Environment.GetEnvironmentVariable("MFP_LOGS_ROOT");
        using var temp = new TemporaryDirectory();
        Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));
        Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", Path.Combine(temp.Path, "logs"));
        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var client = factory.CreateClient();
            var create = await client.PostAsJsonAsync("/api/v1/output-targets", new
            {
                id = "http-file",
                name = "HTTP file",
                protocol = "file",
                endpoint = "http.tcode",
                enabled = false,
                autoConnect = false
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var connect = await client.PostAsync("/api/v1/output-targets/http-file/connect", null);
            Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/output-targets");
            Assert.Contains(list.EnumerateArray(), item =>
                item.GetProperty("id").GetString() == "http-file"
                && item.GetProperty("connected").GetBoolean()
                && item.GetProperty("sentFrames").GetInt64() == 0
                && item.GetProperty("droppedFrames").GetInt64() == 0
                && item.GetProperty("failedFrames").GetInt64() == 0
                && item.GetProperty("reconnects").GetInt64() == 0
                && item.GetProperty("lastSentAtUtc").ValueKind == JsonValueKind.Null);

            var delete = await client.DeleteAsync("/api/v1/output-targets/http-file");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
            Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", previousLogs);
        }
    }

    [Fact]
    public async Task Media_source_registry_persists_direct_and_planned_capabilities()
    {
        using var temp = new TemporaryDirectory();
        var storage = new StorageOptions { ConfigRoot = Path.Combine(temp.Path, "config") };
        await using var registry = new MediaSourceRegistry(storage,
            NullLogger<MediaSourceRegistry>.Instance);
        await registry.StartAsync(CancellationToken.None);

        var internalSource = await registry.CreateAsync(new MediaSourceDefinition(
            "timeline", "Internal timeline", "internal", string.Empty));
        Assert.Equal(MediaSourceOperationStatus.Success, internalSource.Status);
        Assert.True(internalSource.Source!.Connected);
        Assert.Equal("ready", internalSource.Source.ImplementationStatus);

        var remote = await registry.CreateAsync(new MediaSourceDefinition(
            "jellyfin", "Jellyfin", "jellyfin", "https://media.example.local"));
        Assert.Equal(MediaSourceOperationStatus.Success, remote.Status);
        Assert.False(remote.Source!.Connected);
        Assert.Equal("planned", remote.Source.ImplementationStatus);
        Assert.Contains("not wired", remote.Source.LastError, StringComparison.OrdinalIgnoreCase);

        var credentials = await registry.CreateAsync(new MediaSourceDefinition(
            "unsafe", "Unsafe", "emby", "https://media.example.local/?token=secret"));
        Assert.Equal(MediaSourceOperationStatus.Invalid, credentials.Status);
        Assert.Equal(2, registry.List().Count);

        await registry.StopAsync(CancellationToken.None);
        await using var restarted = new MediaSourceRegistry(storage,
            NullLogger<MediaSourceRegistry>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal(new[] { "jellyfin", "timeline" },
            restarted.List().Select(source => source.Id).OrderBy(id => id).ToArray());
        Assert.True(restarted.List().Single(source => source.Id == "timeline").Connected);
    }

    [Fact]
    public async Task Media_source_http_contract_exposes_capability_state_and_rejects_credentials()
    {
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        using var temp = new TemporaryDirectory();
        Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));
        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var client = factory.CreateClient();
            var create = await client.PostAsJsonAsync("/api/v1/media-sources", new
            {
                id = "remote-jellyfin",
                name = "Remote Jellyfin",
                kind = "jellyfin",
                endpoint = "https://media.example.local",
                autoConnect = true
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("planned", created.GetProperty("implementationStatus").GetString());
            Assert.False(created.GetProperty("connected").GetBoolean());
            Assert.Contains("not wired", created.GetProperty("lastError").GetString(),
                StringComparison.OrdinalIgnoreCase);

            var connect = await client.PostAsync("/api/v1/media-sources/remote-jellyfin/connect", null);
            Assert.Equal(HttpStatusCode.BadGateway, connect.StatusCode);

            var invalid = await client.PostAsJsonAsync("/api/v1/media-sources", new
            {
                id = "unsafe-media",
                name = "Unsafe",
                kind = "emby",
                endpoint = "https://media.example.local/?api-key=secret"
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/media-sources");
            Assert.Contains(list.EnumerateArray(), item =>
                item.GetProperty("id").GetString() == "remote-jellyfin");
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync("/api/v1/media-sources/remote-jellyfin")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
        }
    }

    [Fact]
    public async Task Environment_tick_rate_overrides_configuration_section()
    {
        var previous = Environment.GetEnvironmentVariable("MFP_TICK_HZ");
        try
        {
            Environment.SetEnvironmentVariable("MFP_TICK_HZ", "7");
            using var client = _factory.CreateClient();
            var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");
            Assert.Equal(7, capabilities.GetProperty("tickHz").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_TICK_HZ", previous);
        }
    }

    [Fact]
    public void Auth_token_store_persists_only_a_hash_and_rejects_repairing()
    {
        var previousPairing = Environment.GetEnvironmentVariable("MFP_PAIRING_TOKEN");
        var previousAuth = Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN");
        var root = Path.Combine(Path.GetTempPath(), "mfp-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("MFP_PAIRING_TOKEN", "pairing-token");
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", null);
            var options = new MultiFunPlayer.Infrastructure.Storage.StorageOptions { ConfigRoot = root };
            var store = new AuthTokenStore(options);

            Assert.Equal(PairingResult.Paired, store.TryPair("pairing-token", "a-new-token-that-is-long"));
            Assert.True(store.Validate("a-new-token-that-is-long"));
            Assert.False(store.Validate("pairing-token"));
            Assert.Equal(PairingResult.AlreadyConfigured, store.TryPair("pairing-token", "another-token-that-is-long"));
            var persisted = File.ReadAllText(Path.Combine(root, "auth-token.json"));
            Assert.DoesNotContain("a-new-token-that-is-long", persisted, StringComparison.Ordinal);
            Assert.True(new AuthTokenStore(options).Validate("a-new-token-that-is-long"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_PAIRING_TOKEN", previousPairing);
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", previousAuth);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenApi_document_describes_versioned_surface()
    {
        using var client = _factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        Assert.Equal("3.0.3", document.GetProperty("openapi").GetString());
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/status", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/adapters", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/settings", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/configuration/migration", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/scripts", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/output-targets", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/output-targets/{id}", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources/{id}", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/logs", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/logs/file", out _));
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearerAuth", out _));
    }

    [Fact]
    public async Task Load_play_pause_seek_and_axes_are_operational()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}" , Encoding.UTF8, "application/json");
        var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
            "{\"name\":\"test.funscript\",\"content\":{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);

        var play = await client.PostAsync("/api/v1/player/play", null);
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);
        var pause = await client.PostAsync("/api/v1/player/pause", null);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var seek = await client.PostAsJsonAsync("/api/v1/player/seek", new { positionSeconds = 0.5 });
        Assert.Equal(HttpStatusCode.OK, seek.StatusCode);
        var axes = await client.GetAsync("/api/v1/axes");
        Assert.Equal(HttpStatusCode.OK, axes.StatusCode);
        Assert.Contains("default", await axes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Multi_axis_funscript_uses_portable_default_axis_catalog()
    {
        using var client = _factory.CreateClient();
        var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
            "{\"name\":\"multi.funscript\",\"content\":{\"actions\":[{\"at\":0,\"pos\":25}],\"axes\":[{\"id\":\"R2\",\"actions\":[{\"at\":1000,\"pos\":75}]}]}}",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        var status = await load.Content.ReadFromJsonAsync<JsonElement>();
        var axes = status.GetProperty("axes");
        Assert.True(axes.TryGetProperty("L0", out _));
        Assert.True(axes.TryGetProperty("R2", out _));
    }

    [Fact]
    public async Task Token_auth_keeps_health_public_and_protects_api()
    {
        var previous = Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", "test-only-token");
            using var client = _factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/status")).StatusCode);
            client.DefaultRequestHeaders.Add("X-MFP-Token", "test-only-token");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/status")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", previous);
        }
    }

    [Fact]
    public async Task Logs_are_bounded_structured_and_redacted()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MultiFunPlayer.Server.RuntimeLogStore>();
        store.Add(LogLevel.Warning, "test.component", new EventId(42, "Test"),
            "request token=plain-secret Bearer bearer-secret", new Dictionary<string, object?>
            {
                ["Authorization"] = "Bearer property-secret",
                ["port"] = 1234
            });

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/logs?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("plain-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("property-secret", json, StringComparison.Ordinal);
        Assert.Contains("test.component", json, StringComparison.Ordinal);
        Assert.Contains("42", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_logs_are_redacted_and_downloadable()
    {
        var previousLogs = Environment.GetEnvironmentVariable("MFP_LOGS_ROOT");
        using var temp = new TemporaryDirectory();
        Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", temp.Path);
        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<RuntimeLogStore>();
            store.Add(LogLevel.Warning, "persisted.test", new EventId(91),
                "token=private-value", new Dictionary<string, object?>
                {
                    ["password"] = "another-secret"
                });

            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/v1/logs/file");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("persisted.test", content, StringComparison.Ordinal);
            Assert.DoesNotContain("private-value", content, StringComparison.Ordinal);
            Assert.DoesNotContain("another-secret", content, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", previousLogs);
        }
    }

    [Fact]
    public void Runtime_log_store_evicts_oldest_entries_at_capacity()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MultiFunPlayer.Server.RuntimeLogStore>();
        for (var index = 0; index < store.Capacity + 3; index++)
            store.Add(LogLevel.Information, "test.retention", new EventId(index), $"entry {index}");

        Assert.Equal(store.Capacity, store.Count);
        Assert.True(store.DroppedCount > 0);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mfp-server-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class HandyRegistryHandler(List<string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.Split("/api/handy/v2/",
                StringSplitOptions.None).LastOrDefault() ?? string.Empty;
            paths.Add("/" + path.Trim('/'));
            var body = path switch
            {
                "connected" => "{\"connected\":true}",
                "info" => "{\"fwStatus\":0}",
                "mode" => "{\"result\":0}",
                "servertime" => "{\"serverTime\":1}",
                _ => "{\"result\":0}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
