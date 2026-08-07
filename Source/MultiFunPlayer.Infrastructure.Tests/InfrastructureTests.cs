using System.Text;
using MultiFunPlayer.Application;
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
