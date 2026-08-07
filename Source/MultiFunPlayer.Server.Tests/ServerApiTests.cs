using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MultiFunPlayer.Common;
using MultiFunPlayer.Infrastructure.Media;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Server.Tests;

[Collection("Server environment")]
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
        Assert.Contains("media.play", capabilities.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetString()));
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
        foreach (var id in new[] { "tcp", "udp", "websocket", "serial", "file", "the-handy", "buttplug" })
        {
            Assert.True(transports.ContainsKey(id));
            Assert.Equal("direct", transports[id].GetProperty("integration").GetString());
        }
        Assert.Contains(transports.Keys, id => id.Equals("ofs-websocket", StringComparison.OrdinalIgnoreCase));

        var adapters = payload.GetProperty("adapters").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ready", adapters["output.tcp"].GetProperty("implementationStatus").GetString());
        Assert.Equal("direct", adapters["output.buttplug"].GetProperty("integration").GetString());
        Assert.Equal("ready", adapters["output.buttplug"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["output.the-handy"].GetProperty("implementationStatus").GetString());
        Assert.Equal("unsupported", adapters["output.pipe"].GetProperty("implementationStatus").GetString());
        Assert.Equal("blocked", adapters["media.mpv"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.emby"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.jellyfin"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.plex"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.vlc"].GetProperty("implementationStatus").GetString());
        Assert.Equal("ready", adapters["media.mpc"].GetProperty("implementationStatus").GetString());
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
            var sourceBefore = await File.ReadAllBytesAsync(source);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal("migrated", service.Status.State);
            var migrated = await File.ReadAllTextAsync(Path.Combine(configRoot,
                ConfigurationMigrationHostedService.TargetFile));
            Assert.Contains("sourceConfigVersion", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("omit-me", migrated, StringComparison.Ordinal);
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(source));

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
    public async Task Legacy_configuration_reports_missing_source_without_writing_target()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable);
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "missing.json");
        var configRoot = Path.Combine(temp.Path, "config");
        Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, source);
        try
        {
            var service = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            Assert.Equal("source-not-found", service.Status.State);
            Assert.Equal("missing.json", service.Status.SourceFile);
            Assert.False(File.Exists(Path.Combine(configRoot, ConfigurationMigrationHostedService.TargetFile)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Legacy_configuration_rejects_oversized_source_without_writing_target()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable);
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "oversized.json");
        var configRoot = Path.Combine(temp.Path, "config");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            stream.SetLength(4 * 1024 * 1024 + 1);
        Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, source);
        try
        {
            var service = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            Assert.Equal("failed", service.Status.State);
            Assert.Contains("4 MiB", service.Status.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(configRoot, ConfigurationMigrationHostedService.TargetFile)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Legacy_configuration_rejects_source_and_target_symbolic_links()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable);
        using var temp = new TemporaryDirectory();
        var sourceContent = Path.Combine(temp.Path, "source-content.json");
        var sourceLink = Path.Combine(temp.Path, "source-link.json");
        var configRoot = Path.Combine(temp.Path, "config");
        await File.WriteAllTextAsync(sourceContent, "{\"ConfigVersion\":1}");
        try
        {
            File.CreateSymbolicLink(sourceLink, sourceContent);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, sourceLink);
        try
        {
            var sourceLinkService = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);
            await sourceLinkService.StartAsync(CancellationToken.None);
            Assert.Equal("failed", sourceLinkService.Status.State);
            Assert.Contains("symlink", sourceLinkService.Status.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(configRoot, ConfigurationMigrationHostedService.TargetFile)));

            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, sourceContent);
            Directory.CreateDirectory(configRoot);
            var outside = Path.Combine(temp.Path, "outside-target.json");
            await File.WriteAllTextAsync(outside, "keep");
            File.CreateSymbolicLink(Path.Combine(configRoot, ConfigurationMigrationHostedService.TargetFile), outside);
            var targetLinkService = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);
            await targetLinkService.StartAsync(CancellationToken.None);
            Assert.Equal("failed", targetLinkService.Status.State);
            Assert.Contains("symbolic", targetLinkService.Status.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Legacy_configuration_honors_cancellation_before_reading_source()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable);
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "cancelled.json");
        var configRoot = Path.Combine(temp.Path, "config");
        await File.WriteAllTextAsync(source, "{\"ConfigVersion\":1}");
        Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, source);
        try
        {
            var service = new ConfigurationMigrationHostedService(
                new StorageOptions { ConfigRoot = configRoot },
                NullLogger<ConfigurationMigrationHostedService>.Instance);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.StartAsync(cancellation.Token));
            Assert.False(File.Exists(Path.Combine(configRoot, ConfigurationMigrationHostedService.TargetFile)));
            Assert.Equal("disabled", service.Status.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigurationMigrationHostedService.LegacyPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Explicit_key_root_survives_a_host_restart()
    {
        var previousKeys = Environment.GetEnvironmentVariable("MFP_KEYS_ROOT");
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        using var temp = new TemporaryDirectory();
        var keysRoot = Path.Combine(temp.Path, "keys");
        try
        {
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", keysRoot);
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));

            string protectedValue;
            using (var first = new WebApplicationFactory<MultiFunPlayer.Server.Program>())
            {
                var provider = first.Services.GetRequiredService<IDataProtectionProvider>();
                protectedValue = provider.CreateProtector("migration-test").Protect("persistent-payload");
            }

            var keyFiles = Directory.GetFiles(keysRoot, "*.xml");
            Assert.NotEmpty(keyFiles);

            using var restarted = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            var restartedProvider = restarted.Services.GetRequiredService<IDataProtectionProvider>();
            var restored = restartedProvider.CreateProtector("migration-test").Unprotect(protectedValue);
            Assert.Equal("persistent-payload", restored);
            Assert.Equal(keyFiles.Select(Path.GetFileName).OrderBy(name => name),
                Directory.GetFiles(keysRoot, "*.xml").Select(Path.GetFileName).OrderBy(name => name));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", previousKeys);
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
        }
    }

    [Fact]
    public void Removing_explicit_key_root_rejects_old_protected_data()
    {
        var previousKeys = Environment.GetEnvironmentVariable("MFP_KEYS_ROOT");
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        using var temp = new TemporaryDirectory();
        var keysRoot = Path.Combine(temp.Path, "keys");
        try
        {
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", keysRoot);
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));

            string protectedValue;
            using (var first = new WebApplicationFactory<MultiFunPlayer.Server.Program>())
            {
                var provider = first.Services.GetRequiredService<IDataProtectionProvider>();
                protectedValue = provider.CreateProtector("migration-test").Protect("removed-payload");
            }

            foreach (var keyFile in Directory.GetFiles(keysRoot, "*.xml"))
                File.Delete(keyFile);

            using var restarted = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            var restartedProvider = restarted.Services.GetRequiredService<IDataProtectionProvider>();
            Assert.Throws<CryptographicException>(() =>
                restartedProvider.CreateProtector("migration-test").Unprotect(protectedValue));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", previousKeys);
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
        }
    }

    [Fact]
    public async Task Persistent_volumes_survive_independent_host_instances()
    {
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        var previousKeys = Environment.GetEnvironmentVariable("MFP_KEYS_ROOT");
        var previousScripts = Environment.GetEnvironmentVariable("MFP_SCRIPTS_ROOT");
        var previousLogs = Environment.GetEnvironmentVariable("MFP_LOGS_ROOT");
        var previousAuth = Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN");
        var previousPairing = Environment.GetEnvironmentVariable("MFP_PAIRING_TOKEN");
        using var temp = new TemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", Path.Combine(temp.Path, "keys"));
            Environment.SetEnvironmentVariable("MFP_SCRIPTS_ROOT", Path.Combine(temp.Path, "scripts"));
            Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", Path.Combine(temp.Path, "logs"));
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("MFP_PAIRING_TOKEN", null);

            string protectedValue;
            using (var first = new WebApplicationFactory<MultiFunPlayer.Server.Program>())
            {
                using var client = first.CreateClient();
                var upload = await client.PostAsJsonAsync("/api/v1/scripts", new
                {
                    id = "restart.funscript",
                    content = "{\"actions\":[{\"at\":0,\"pos\":50}]}"
                });
                Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

                var axis = await client.PutAsJsonAsync("/api/v1/device-axes/L0", new
                {
                    axis = "L0",
                    minimum = 0.1,
                    maximum = 0.9,
                    inverted = true,
                    interpolation = "Linear",
                    limitEnabled = false
                });
                Assert.Equal(HttpStatusCode.OK, axis.StatusCode);

                var logs = first.Services.GetRequiredService<RuntimeLogStore>();
                logs.Add(LogLevel.Warning, "restart.test", new EventId(93),
                    "persistent volume marker");

                var provider = first.Services.GetRequiredService<IDataProtectionProvider>();
                protectedValue = provider.CreateProtector("restart-test").Protect("persistent-payload");
            }

            using var restarted = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var restartedClient = restarted.CreateClient();
            var scripts = await restartedClient.GetFromJsonAsync<JsonElement>("/api/v1/scripts");
            Assert.Contains(scripts.EnumerateArray(), item =>
                item.GetProperty("id").GetString() == "restart.funscript");

            var deviceAxes = await restartedClient.GetFromJsonAsync<JsonElement>("/api/v1/device-axes");
            Assert.Contains(deviceAxes.EnumerateArray(), item =>
                item.GetProperty("axis").GetString() == "L0"
                && item.GetProperty("inverted").GetBoolean());

            var persistedLogs = await restartedClient.GetAsync("/api/v1/logs/file");
            Assert.Equal(HttpStatusCode.OK, persistedLogs.StatusCode);
            Assert.Contains("restart.test", await persistedLogs.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            var restartedProvider = restarted.Services.GetRequiredService<IDataProtectionProvider>();
            Assert.Equal("persistent-payload",
                restartedProvider.CreateProtector("restart-test").Unprotect(protectedValue));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", previousKeys);
            Environment.SetEnvironmentVariable("MFP_SCRIPTS_ROOT", previousScripts);
            Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", previousLogs);
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", previousAuth);
            Environment.SetEnvironmentVariable("MFP_PAIRING_TOKEN", previousPairing);
        }
    }

    [Fact]
    public async Task Backend_output_continues_after_state_websocket_disconnect()
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
                id = "browser-disconnect-file",
                name = "Browser disconnect file",
                protocol = "file",
                endpoint = "browser-disconnect.tcode",
                enabled = true,
                autoConnect = true,
                updateRateHz = 20
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var outputPath = Path.Combine(temp.Path, "logs", "browser-disconnect.tcode");
            var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
                "{\"name\":\"disconnect.funscript\",\"axis\":\"L0\",\"content\":{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":10000,\"pos\":100}]}}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, load.StatusCode);

            // Connect the browser while playback is still stopped. This
            // proves that the output loop remains independent after a client
            // disconnects during an active stream, rather than only after a
            // playback run that never had a browser attached.
            var websocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await websocketClient.ConnectAsync(new Uri("ws://localhost/hubs/state"),
                CancellationToken.None);
            var initialBuffer = new byte[4096];
            var initial = await socket.ReceiveAsync(initialBuffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, initial.MessageType);

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/player/play", null)).StatusCode);

            var startedOutput = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (File.Exists(outputPath)
                    && (await File.ReadAllTextAsync(outputPath)).Contains("L", StringComparison.Ordinal))
                {
                    startedOutput = true;
                    break;
                }

                await Task.Delay(50);
            }

            Assert.True(startedOutput, "The output target did not receive a frame while the browser was connected.");
            var beforeDisconnectLength = new FileInfo(outputPath).Length;

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "browser closed",
                CancellationToken.None);
            var broadcaster = factory.Services.GetRequiredService<StateBroadcaster>();
            for (var attempt = 0; attempt < 20 && broadcaster.ClientCount != 0; attempt++)
                await Task.Delay(25);
            Assert.Equal(0, broadcaster.ClientCount);

            var receivedOutputAfterDisconnect = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (new FileInfo(outputPath).Length > beforeDisconnectLength)
                {
                    receivedOutputAfterDisconnect = true;
                    break;
                }

                await Task.Delay(50);
            }

            Assert.True(receivedOutputAfterDisconnect,
                "The output target stopped receiving frames after the browser disconnected.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
            Environment.SetEnvironmentVariable("MFP_LOGS_ROOT", previousLogs);
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

            var multiAxis = await client.PostAsJsonAsync("/api/v1/scripts", new
            {
                id = "multi-axis.funscript",
                content = "{\"actions\":[{\"at\":0,\"pos\":25}],\"axes\":[{\"id\":\"R2\",\"actions\":[{\"at\":1000,\"pos\":75}]}]}"
            });
            Assert.Equal(HttpStatusCode.Created, multiAxis.StatusCode);

            var loaded = await client.PostAsJsonAsync("/api/v1/scripts/load", new
            {
                id = "multi-axis.funscript"
            });
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
            var status = await loaded.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(status.GetProperty("axes").TryGetProperty("L0", out _));
            Assert.True(status.GetProperty("axes").TryGetProperty("R2", out _));
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
        var outputPath = Path.Combine(storage.LogsRoot, "runtime.tcode");
        Assert.True(File.Exists(outputPath));

        var duplicate = await registry.CreateAsync(new OutputTargetDefinition(
            "file-1", "Duplicate", "file", "duplicate.tcode"));
        Assert.Equal(OutputTargetOperationStatus.Conflict, duplicate.Status);
        Assert.Equal("file-1", Assert.Single(registry.List()).Id);

        await registry.DisconnectAsync("file-1");
        Assert.False(Assert.Single(registry.List()).Connected);
        Assert.Equal(0, Assert.Single(registry.List()).SentFrames);
        Assert.Equal(0, Assert.Single(registry.List()).DroppedFrames);
        Assert.Equal(0, Assert.Single(registry.List()).FailedFrames);
        Assert.Equal(OutputTargetOperationStatus.Success,
            (await registry.ConnectAsync("file-1")).Status);
        Assert.Equal(1, Assert.Single(registry.List()).Reconnects);

        var parsed = FunscriptReader.Default.FromText("runtime.funscript", "test",
            "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}");
        Assert.True(parsed.IsSuccess);
        engine.Load(parsed.Resource, "L0");
        engine.Play();
        engine.Tick(TimeSpan.FromMilliseconds(500));
        Assert.Contains("L0500", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);

        await registry.DisconnectAsync("file-1");
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
    public async Task Output_target_recovers_when_tcp_endpoint_becomes_available()
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

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var definition = new OutputTargetDefinition("tcp-recovery", "TCP recovery", "tcp",
            $"127.0.0.1:{port}", Enabled: true, AutoConnect: false);
        Assert.Equal(OutputTargetOperationStatus.Success,
            (await registry.CreateAsync(definition)).Status);

        var failed = await registry.ConnectAsync(definition.Id);
        Assert.Equal(OutputTargetOperationStatus.Failed, failed.Status);
        Assert.False(failed.Target!.Connected);
        Assert.False(string.IsNullOrWhiteSpace(failed.Target.LastError));

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var acceptedTask = listener.AcceptTcpClientAsync();
        var recovered = await registry.ConnectAsync(definition.Id);
        Assert.Equal(OutputTargetOperationStatus.Success, recovered.Status);
        Assert.True(recovered.Target!.Connected);
        using var accepted = await acceptedTask.WaitAsync(TimeSpan.FromSeconds(2));

        await registry.DisconnectAsync(definition.Id);
        Assert.False(Assert.Single(registry.List()).Connected);
    }

    [Fact]
    public async Task Output_target_registry_routes_tcp_udp_and_websocket_frames()
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

        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var tcpPort = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        var tcpAccept = tcpListener.AcceptTcpClientAsync();

        using var udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpReceiver.Client.LocalEndPoint!).Port;

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var webSocketPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var webSocketAccepted = new TaskCompletionSource<WebSocket>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var webSocketFrame = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, webSocketPort));
        var webApp = builder.Build();
        webApp.UseWebSockets();
        webApp.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            webSocketAccepted.TrySetResult(socket);
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open
                    && !context.RequestAborted.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
                    {
                        var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (payload.Contains("L0500", StringComparison.Ordinal))
                            webSocketFrame.TrySetResult(payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Kestrel cancels the request when the simulator is stopped.
            }
            finally
            {
                socket.Dispose();
            }
        });
        await webApp.StartAsync();

        try
        {
            foreach (var definition in new[]
            {
                new OutputTargetDefinition("tcp-loopback", "TCP loopback", "tcp",
                    $"127.0.0.1:{tcpPort}", Enabled: true, AutoConnect: false),
                new OutputTargetDefinition("udp-loopback", "UDP loopback", "udp",
                    $"127.0.0.1:{udpPort}", Enabled: true, AutoConnect: false),
                new OutputTargetDefinition("websocket-loopback", "WebSocket loopback", "websocket",
                    $"ws://127.0.0.1:{webSocketPort}/output", Enabled: true, AutoConnect: false)
            })
            {
                var created = await registry.CreateAsync(definition);
                Assert.Equal(OutputTargetOperationStatus.Success, created.Status);
                var connected = await registry.ConnectAsync(definition.Id);
                Assert.Equal(OutputTargetOperationStatus.Success, connected.Status);
                Assert.True(connected.Target!.Connected);
            }

            var acceptedWebSocket = await webSocketAccepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            DeviceAxis.InitializeDefaults();
            var parsed = FunscriptReader.Default.FromText("network.funscript", "test",
                "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}],"
                + "\"axes\":[{\"id\":\"R2\",\"actions\":[{\"at\":0,\"pos\":100},"
                + "{\"at\":1000,\"pos\":0}]}]}");
            Assert.True(parsed.IsMultiAxis);
            engine.Load(parsed.Resources);
            engine.Play();
            engine.Tick(TimeSpan.FromMilliseconds(500));

            using var acceptedTcp = await tcpAccept.WaitAsync(TimeSpan.FromSeconds(2));
            var tcpBuffer = new byte[256];
            var tcpText = new StringBuilder();
            for (var attempt = 0; attempt < 8
                && !tcpText.ToString().Contains("L0500", StringComparison.Ordinal); attempt++)
            {
                var tcpCount = await acceptedTcp.GetStream().ReadAsync(tcpBuffer)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if (tcpCount == 0)
                    break;
                tcpText.Append(Encoding.UTF8.GetString(tcpBuffer, 0, tcpCount));
            }

            var udpText = string.Empty;
            for (var attempt = 0; attempt < 8
                && !udpText.Contains("L0500", StringComparison.Ordinal); attempt++)
            {
                var udpPayload = await udpReceiver.ReceiveAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2));
                udpText = Encoding.UTF8.GetString(udpPayload.Buffer);
            }
            var webSocketPayload = await webSocketFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("L0500", tcpText.ToString(), StringComparison.Ordinal);
            Assert.Contains("L0500", udpText, StringComparison.Ordinal);
            Assert.Contains("L0500", webSocketPayload, StringComparison.Ordinal);
            foreach (var target in registry.List())
            {
                Assert.True(target.Connected);
                Assert.True(target.SentFrames >= 1);
                Assert.Equal(0, target.FailedFrames);
            }

            foreach (var id in new[] { "tcp-loopback", "udp-loopback", "websocket-loopback" })
            {
                var disconnected = await registry.DisconnectAsync(id);
                Assert.Equal(OutputTargetOperationStatus.Success, disconnected.Status);
                Assert.False(disconnected.Target!.Connected);
            }
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
            await webApp.StopAsync();
            await webApp.DisposeAsync();
        }
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
                autoConnect = false,
                deviceName = "Simulated Toy",
                deviceIndex = 2,
                actuatorType = "Linear",
                actuatorIndex = 1,
                durationMilliseconds = 125
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var connect = await client.PostAsync("/api/v1/output-targets/http-file/connect", null);
            Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/output-targets");
            Assert.Contains(list.EnumerateArray(), item =>
                item.GetProperty("id").GetString() == "http-file"
                && item.GetProperty("connected").GetBoolean()
                && item.GetProperty("sentFrames").GetInt64() >= 0
                && item.GetProperty("droppedFrames").GetInt64() >= 0
                && item.GetProperty("coalescedFrames").GetInt64()
                    == item.GetProperty("droppedFrames").GetInt64()
                && item.GetProperty("failedFrames").GetInt64() >= 0
                && item.GetProperty("reconnects").GetInt64() == 0
                && (item.GetProperty("lastSentAtUtc").ValueKind is JsonValueKind.Null
                    or JsonValueKind.String)
                && item.GetProperty("deviceName").GetString() == "Simulated Toy"
                && item.GetProperty("deviceIndex").GetUInt32() == 2
                && item.GetProperty("actuatorType").GetString() == "Linear"
                && item.GetProperty("actuatorIndex").GetUInt32() == 1
                && item.GetProperty("durationMilliseconds").GetInt32() == 125);

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
    public async Task Media_source_registry_persists_direct_network_capabilities()
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
        Assert.Equal("ready", remote.Source.ImplementationStatus);
        Assert.Contains("API key", remote.Source.LastError, StringComparison.OrdinalIgnoreCase);

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
    public async Task Media_source_registry_connects_jellyfin_adapter_without_persisting_token()
    {
        var previous = Environment.GetEnvironmentVariable("MFP_TEST_MEDIA_TOKEN");
        Environment.SetEnvironmentVariable("MFP_TEST_MEDIA_TOKEN", "token-not-persisted");
        using var temp = new TemporaryDirectory();
        try
        {
            var storage = new StorageOptions { ConfigRoot = Path.Combine(temp.Path, "config") };
            await using var registry = new MediaSourceRegistry(storage,
                NullLogger<MediaSourceRegistry>.Instance,
                networkFactory: new StubNetworkFactory());
            await registry.StartAsync(CancellationToken.None);

            var result = await registry.CreateAsync(new MediaSourceDefinition(
                "jellyfin", "Jellyfin", "jellyfin", "https://media.example.local",
                DeviceId: "browser-1", SecretRef: "MFP_TEST_MEDIA_TOKEN"));

            Assert.Equal(MediaSourceOperationStatus.Success, result.Status);
            Assert.True(result.Source!.Connected);
            Assert.Equal("ready", result.Source.ImplementationStatus);
            Assert.Equal("browser-1", result.Source.DeviceId);
            Assert.Equal("/media/remote.mp4", result.Source.MediaState!.Path);
            Assert.Equal(2.5, result.Source.MediaState.PositionSeconds);
            Assert.Equal(MediaSourceOperationStatus.Success,
                (await registry.PlayAsync("jellyfin")).Status);
            Assert.Equal(MediaSourceOperationStatus.Success,
                (await registry.PauseAsync("jellyfin")).Status);
            Assert.Equal(MediaSourceOperationStatus.Success,
                (await registry.SeekAsync("jellyfin", TimeSpan.FromSeconds(4))).Status);
            Assert.Equal(MediaSourceOperationStatus.Invalid,
                (await registry.SeekAsync("jellyfin", TimeSpan.FromSeconds(-1))).Status);
            var updated = await registry.UpdateAsync("jellyfin", new MediaSourceDefinition(
                "jellyfin", "Renamed Jellyfin", "jellyfin", "https://media.example.local",
                DeviceId: "browser-1"));
            Assert.Equal(MediaSourceOperationStatus.Success, updated.Status);
            var persisted = await File.ReadAllTextAsync(Path.Combine(storage.ConfigRoot, "media-sources.json"));
            Assert.Contains("MFP_TEST_MEDIA_TOKEN", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("token-not-persisted", persisted, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_TEST_MEDIA_TOKEN", previous);
        }
    }

    [Theory]
    [InlineData("vlc")]
    [InlineData("mpc")]
    public async Task Media_source_registry_connects_http_player_adapters(string kind)
    {
        using var temp = new TemporaryDirectory();
        var storage = new StorageOptions { ConfigRoot = Path.Combine(temp.Path, "config") };
        await using var registry = new MediaSourceRegistry(storage,
            NullLogger<MediaSourceRegistry>.Instance,
            networkFactory: new StubNetworkFactory(),
            secretProvider: _ => "test-token");
        await registry.StartAsync(CancellationToken.None);

        var result = await registry.CreateAsync(new MediaSourceDefinition(
            kind, kind.ToUpperInvariant(), kind, "http://media.example.local",
            DeviceId: "remote-player"));

        Assert.Equal(MediaSourceOperationStatus.Success, result.Status);
        Assert.True(result.Source!.Connected);
        Assert.Equal("ready", result.Source.ImplementationStatus);
        Assert.Equal("/media/remote.mp4", result.Source.MediaState!.Path);
        Assert.Equal(2.5, result.Source.MediaState.PositionSeconds);
        Assert.Equal(MediaSourceOperationStatus.Success, (await registry.PlayAsync(kind)).Status);
        Assert.Equal(MediaSourceOperationStatus.Success, (await registry.PauseAsync(kind)).Status);
        Assert.Equal(MediaSourceOperationStatus.Success,
            (await registry.SeekAsync(kind, TimeSpan.FromSeconds(4))).Status);
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
                autoConnect = true,
                deviceId = "browser-session-1",
                secretRef = "MFP_TEST_MEDIA_TOKEN"
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ready", created.GetProperty("implementationStatus").GetString());
            Assert.False(created.GetProperty("connected").GetBoolean());
            Assert.Equal("browser-session-1", created.GetProperty("deviceId").GetString());
            Assert.DoesNotContain("secretRef", created.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("API key", created.GetProperty("lastError").GetString(),
                StringComparison.OrdinalIgnoreCase);

            var connect = await client.PostAsync("/api/v1/media-sources/remote-jellyfin/connect", null);
            Assert.Equal(HttpStatusCode.BadGateway, connect.StatusCode);
            Assert.Equal(HttpStatusCode.BadGateway,
                (await client.PostAsync("/api/v1/media-sources/remote-jellyfin/play", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync("/api/v1/media-sources/remote-jellyfin/seek",
                    new { positionSeconds = -1 })).StatusCode);

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
        var outputList = document.GetProperty("paths").GetProperty("/api/v1/output-targets").GetProperty("get");
        Assert.Equal("#/components/schemas/OutputTargetList", outputList
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString());
        var outputSchema = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("OutputTarget");
        var outputListSchema = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("OutputTargetList");
        Assert.Equal("array", outputListSchema.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/OutputTarget", outputListSchema
            .GetProperty("items").GetProperty("$ref").GetString());
        Assert.True(outputSchema.GetProperty("properties").TryGetProperty("coalescedFrames", out _));
        Assert.True(outputSchema.GetProperty("properties").TryGetProperty("droppedFrames", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources/{id}", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources/{id}/play", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources/{id}/pause", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/media-sources/{id}/seek", out _));
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
    public async Task Device_axis_settings_are_persisted_and_change_script_output()
    {
        var previousConfig = Environment.GetEnvironmentVariable("MFP_CONFIG_ROOT");
        var previousScripts = Environment.GetEnvironmentVariable("MFP_SCRIPTS_ROOT");
        using var temp = new TemporaryDirectory();
        Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", Path.Combine(temp.Path, "config"));
        Environment.SetEnvironmentVariable("MFP_SCRIPTS_ROOT", Path.Combine(temp.Path, "scripts"));
        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            using var client = factory.CreateClient();
            var update = await client.PutAsJsonAsync("/api/v1/device-axes/L0", new
            {
                axis = "L0",
                minimum = 0.2,
                maximum = 0.6,
                inverted = true,
                interpolation = "Linear",
                limitEnabled = true,
                limitMinimum = 0.3,
                limitMaximum = 0.8
            });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var configured = await update.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("L0", configured.GetProperty("axis").GetString());
            Assert.Equal(0.2, configured.GetProperty("minimum").GetDouble(), precision: 10);
            Assert.True(configured.GetProperty("inverted").GetBoolean());

            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/device-axes");
            Assert.Contains(list.EnumerateArray(), item => item.GetProperty("axis").GetString() == "L0");
            Assert.True(File.Exists(Path.Combine(temp.Path, "config", "device-axis-settings.json")));

            var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
                "{\"name\":\"axis.funscript\",\"axis\":\"L0\",\"content\":{\"actions\":[{\"at\":0,\"pos\":25},{\"at\":1000,\"pos\":75}]}}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, load.StatusCode);
            var status = await load.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0.5, status.GetProperty("axes").GetProperty("L0").GetDouble(), precision: 10);

            var delete = await client.DeleteAsync("/api/v1/device-axes/L0");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_CONFIG_ROOT", previousConfig);
            Environment.SetEnvironmentVariable("MFP_SCRIPTS_ROOT", previousScripts);
        }
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
            for (var attempt = 0; attempt < 8 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException) when (attempt < 7)
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 7)
                {
                    Thread.Sleep(25);
                }
            }
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

    private sealed class StubNetworkFactory : INetworkMediaSourceAdapterFactory
    {
        public NetworkMediaSourceAdapter Create(NetworkMediaSourceKind kind, Uri endpoint,
            string? token, string? deviceId = null, HttpMessageHandler? handler = null)
            => new(kind, endpoint, token, deviceId, new StubNetworkHandler(kind),
                new NetworkMediaSourceAdapterOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    ConnectTimeout = TimeSpan.FromSeconds(1),
                    RequestTimeout = TimeSpan.FromSeconds(1)
                });
    }

    private sealed class StubNetworkHandler : HttpMessageHandler
    {
        private readonly NetworkMediaSourceKind _kind;

        public StubNetworkHandler(NetworkMediaSourceKind kind) => _kind = kind;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var body = _kind switch
            {
                NetworkMediaSourceKind.Vlc => path switch
                {
                    "/requests/status.json" => "{\"apiversion\":4,\"state\":\"playing\",\"currentplid\":7,\"length\":12,\"time\":2.5,\"rate\":1}",
                    "/requests/playlist.json" => "{\"children\":[{\"type\":\"leaf\",\"current\":\"current\",\"uri\":\"file:///media/remote.mp4\"}]}",
                    _ => "{}"
                },
                NetworkMediaSourceKind.Mpc => path switch
                {
                    "/" => "<html>MPC-HC</html>",
                    "/variables.html" => "<html><p id=\"state\">2</p><p id=\"filepath\">/media/remote.mp4</p><p id=\"duration\">12000</p><p id=\"position\">2500</p><p id=\"playbackrate\">1</p></html>",
                    _ => "{}"
                },
                _ => path switch
                {
                    "/System/Ping" => "{}",
                    "/Sessions" => "[{\"Id\":\"session-1\",\"DeviceId\":\"browser-1\",\"PlayState\":{\"PositionTicks\":25000000,\"IsPaused\":false},\"NowPlayingItem\":{\"Id\":\"item-1\",\"Path\":\"/media/remote.mp4\",\"RunTimeTicks\":120000000}}]",
                    _ => "{}"
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
