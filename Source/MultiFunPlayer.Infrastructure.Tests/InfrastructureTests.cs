using System.Text;
using System.IO.Ports;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Configuration;
using MultiFunPlayer.Infrastructure.Output;
using MultiFunPlayer.Infrastructure.Serial;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task RootedStoreRejectsPathTraversal()
    {
        using var temp = new TemporaryDirectory();
        var store = new RootedFileStore(temp.Path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadTextAsync("../outside.json"));
        Assert.Throws<ArgumentException>(() => store.Resolve(Path.Combine(temp.Path, "outside.json")));
    }

    [Fact]
    public async Task JsonStoreRoundTripsAndCatalogListsScripts()
    {
        using var temp = new TemporaryDirectory();
        var store = new RootedFileStore(temp.Path);
        var value = new SampleSettings("demo", 42);
        await store.WriteJsonAsync("nested/settings.json", value);

        var loaded = await store.ReadJsonAsync<SampleSettings>("nested/settings.json");
        Assert.Equal(value, loaded);

        await store.WriteTextAsync("demo.funscript", "{\"actions\":[]}");
        await store.WriteTextAsync("ignored.txt", "ignore");
        var catalog = new ScriptCatalog(store);
        var scripts = await catalog.ListAsync();
        Assert.Equal(2, scripts.Count);
        var script = Assert.Single(scripts.Where(x => x.RelativePath == "demo.funscript"));
        Assert.Equal("demo.funscript", script.RelativePath);
        Assert.Equal("{\"actions\":[]}", await catalog.ReadAsync(script.RelativePath));
    }

    [Fact]
    public async Task FileTransportWritesPayload()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "output.tcode");
        await using var transport = new FileOutputTransport("file-test", path);
        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("L0500\n"));
        await transport.DisconnectAsync();

        Assert.Equal("L0500\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TCodeSinkFormatsValuesAndSurfacesTransportFailure()
    {
        await using var transport = new RecordingTransport("fake");
        await transport.ConnectAsync();
        var sink = new TCodeOutputSink(transport);
        sink.Publish(new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 }));
        Assert.Equal("L0500\n", Assert.Single(transport.Payloads));

        await transport.DisposeAsync();
        var exception = Assert.Throws<TCodeOutputException>(() => sink.Publish(
            new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 })));
        Assert.Equal("fake", exception.TransportId);
    }

    [Fact]
    public async Task BoundedTransportCanRecoverAfter_a_single_write_failure()
    {
        await using var transport = new RecordingTransport("recoverable");
        await transport.ConnectAsync();
        transport.FailWrites = true;

        await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await transport.SendAsync(Encoding.UTF8.GetBytes("first")));

        transport.FailWrites = false;
        await transport.SendAsync(Encoding.UTF8.GetBytes("second"));

        Assert.Equal("second", Assert.Single(transport.Payloads));
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task BoundedTransportReports_connect_timeout()
    {
        await using var transport = new BlockingConnectTransport("connect-timeout",
            new OutputTransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(30) });

        var exception = await Assert.ThrowsAsync<OutputTransportException>(() => transport.ConnectAsync());

        Assert.Equal("connect", exception.Operation);
    }

    [Fact]
    public async Task BoundedTransportReports_send_timeout()
    {
        await using var transport = new BlockingWriteTransport("send-timeout",
            new OutputTransportOptions { SendTimeout = TimeSpan.FromMilliseconds(30) });
        await transport.ConnectAsync();

        var exception = await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await transport.SendAsync(Encoding.UTF8.GetBytes("blocked")));

        Assert.Equal("send", exception.Operation);
    }

    [Fact]
    public async Task BoundedTransportCan_disconnect_and_reconnect()
    {
        await using var transport = new RecordingTransport("reconnect");
        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("one"));
        await transport.DisconnectAsync();
        Assert.False(transport.IsConnected);

        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("two"));

        Assert.Equal(["one", "two"], transport.Payloads);
    }

    [Fact]
    public void LinuxSerialEnumeratorUsesStableByIdNames()
    {
        using var temp = new TemporaryDirectory();
        var device = Path.Combine(temp.Path, "ttyACM0");
        File.WriteAllText(device, string.Empty);
        var link = Path.Combine(temp.Path, "usb-MultiFunPlayer_0001-if00");
        File.CreateSymbolicLink(link, device);

        var ports = new LinuxSerialPortEnumerator(temp.Path).Enumerate();

        var port = Assert.Single(ports);
        Assert.Equal("usb-MultiFunPlayer_0001-if00", port.StableId);
        Assert.Equal(link, port.DevicePath);
        Assert.Equal(device, port.LinkTarget);
    }

    [Fact]
    public void StorageOptionsReadsPersistentKeyRootAliases()
    {
        var options = StorageOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["MFP_KEYS_PATH"] = "/var/lib/mfp/keys",
        });

        Assert.Equal("/var/lib/mfp/keys", options.KeysRoot);
    }

    [Fact]
    public async Task SerialTransportKeepsStableDevicePathAndValidatesLineSettings()
    {
        var settings = new SerialPortSettings
        {
            BaudRate = 230400,
            Parity = Parity.Even,
            DataBits = 7,
            StopBits = StopBits.Two,
            DtrEnable = true,
        };

        await using var transport = new SerialOutputTransport("serial-test",
            "/dev/serial/by-id/usb-MultiFunPlayer_0001-if00", settings);

        Assert.Equal("/dev/serial/by-id/usb-MultiFunPlayer_0001-if00", transport.DevicePath);
        Assert.Equal(230400, transport.Settings.BaudRate);
        Assert.Equal(Parity.Even, transport.Settings.Parity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialPortSettings { DataBits = 9 }.Validate());
    }

    [Fact]
    public void ConfigurationMigrationExtractsPortableFieldsAndDropsSecrets()
    {
        var result = new ConfigurationMigrationService().Read(
            "{\"ConfigVersion\":44,\"Device\":{\"SelectedDevice\":\"TCode-0.3\",\"Devices\":[{\"Name\":\"TCode-0.3\",\"Axes\":[{\"Name\":\"L0\",\"Enabled\":true,\"DefaultValue\":1.4}]}]},\"OutputTarget\":{\"Items\":[{\"Type\":\"TcpOutputTarget\",\"Endpoint\":\"host:9000\",\"Enabled\":true,\"ApiKey\":\"do-not-copy\"}]},\"MediaSource\":{\"Selected\":\"Internal\",\"RemoteEndpoint\":\"C:\\\\Media\\\\Root\"}}");

        Assert.True(result.Success);
        Assert.NotNull(result.Configuration);
        Assert.Equal(44, result.Configuration!.SourceConfigVersion);
        Assert.Equal("TCode-0.3", result.Configuration.SelectedDevice);
        var axis = Assert.Single(result.Configuration.Axes);
        Assert.Equal(1, axis.DefaultValue);
        Assert.Equal("tcp", Assert.Single(result.Configuration.Outputs).Protocol);
        Assert.Equal("C:/Media/Root", Assert.Single(result.Configuration.MediaSources).Endpoint);
        Assert.NotEmpty(result.Configuration.Warnings);
    }

    [Fact]
    public void ConfigurationMigrationRejectsMalformedJsonWithoutThrowing()
    {
        var result = new ConfigurationMigrationService().Read("not-json");

        Assert.False(result.Success);
        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
    }

    private sealed record SampleSettings(string Name, int Count);

    private sealed class RecordingTransport(string id) : BoundedOutputTransport(id)
    {
        public List<string> Payloads { get; } = [];
        public bool FailWrites { get; set; }

        protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (FailWrites)
                throw new IOException("fake send failure");
            Payloads.Add(Encoding.UTF8.GetString(payload.Span));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingConnectTransport(string id, OutputTransportOptions options)
        : BoundedOutputTransport(id, options)
    {
        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class BlockingWriteTransport(string id, OutputTransportOptions options)
        : BoundedOutputTransport(id, options)
    {
        protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mfp-infra-" + Guid.NewGuid().ToString("N"));
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
