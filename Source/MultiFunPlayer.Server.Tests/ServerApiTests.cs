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
        foreach (var id in new[] { "tcp", "udp", "websocket", "serial", "file" })
        {
            Assert.True(transports.ContainsKey(id));
            Assert.Equal("direct", transports[id].GetProperty("integration").GetString());
        }

        var adapters = payload.GetProperty("adapters").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("planned", adapters["output.tcp"].GetProperty("implementationStatus").GetString());
        Assert.Equal("external-proxy", adapters["output.buttplug"].GetProperty("integration").GetString());
        Assert.Equal("unsupported", adapters["output.pipe"].GetProperty("implementationStatus").GetString());
        Assert.Equal("blocked", adapters["media.mpv"].GetProperty("implementationStatus").GetString());
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
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/logs", out _));
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
}
