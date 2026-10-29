using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Server.Tests;

[Collection("Server environment")]
public sealed class IdleSchedulingTests
{
    [Fact]
    public async Task EngineSleepsWhileIdleAndResumesOnPlay()
    {
        using var engine = new HeadlessPlayerEngine();
        var configuration = new ConfigurationBuilder().Build();
        var broadcaster = new StateBroadcaster(configuration);
        using var ticker = new EngineTicker(engine, broadcaster, configuration, NullLogger<EngineTicker>.Instance);
        await ticker.StartAsync(CancellationToken.None);
        try
        {
            var revision = engine.Current.Revision;
            await Task.Delay(160);
            Assert.Equal(revision, engine.Current.Revision);
            engine.Load(new ScriptResource("test", "test.funscript",
                new KeyframeCollection([new(0, 0), new(10, 1)]), new ChapterCollection(), new BookmarkCollection()), "L0");
            engine.Play();
            await WaitUntil(() => engine.Position > TimeSpan.Zero);
            engine.Pause();
            revision = engine.Current.Revision;
            await Task.Delay(160);
            Assert.Equal(revision, engine.Current.Revision);
            var position = engine.Position;
            engine.Play();
            await WaitUntil(() => engine.Position > position);
        }
        finally { await ticker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RoutedOutputSleepsPausedAndWakesForSeekAndReconnect()
    {
        DeviceAxis.InitializeDefaults();
        var directory = Directory.CreateTempSubdirectory("mfp-idle-");
        try
        {
            var storage = new StorageOptions { ConfigRoot = Path.Combine(directory.FullName, "config"),
                ScriptsRoot = Path.Combine(directory.FullName, "scripts"), LogsRoot = Path.Combine(directory.FullName, "logs") };
            using var engine = new HeadlessPlayerEngine();
            await using var sources = new MediaSourceRegistry(storage, NullLogger<MediaSourceRegistry>.Instance);
            await using var outputs = new OutputTargetRegistry(storage, engine, NullLogger<OutputTargetRegistry>.Instance);
            var catalog = new ScriptCatalog(storage.ScriptsRoot);
            var evaluator = new RuntimeScriptEvaluator(catalog, NullLogger<RuntimeScriptEvaluator>.Instance);
            await using var routes = new RuntimeRoutingRegistry(storage, sources, outputs, catalog,
                new RuntimeScriptMatcher(catalog), NullLogger<RuntimeRoutingRegistry>.Instance);
            await sources.CreateAsync(new MediaSourceDefinition("internal", "Internal", "internal", "", AutoConnect: true));
            await outputs.CreateAsync(new OutputTargetDefinition("file", "File", "file", "idle.tcode", AutoConnect: true));
            engine.Load(new ScriptResource("test", "test.funscript",
                new KeyframeCollection([new(0, 0), new(10, 1)]), new ChapterCollection(), new BookmarkCollection()), "L0");
            await routes.SetAsync([new RuntimeSourceRouteDefinition("internal", ["file"])]);
            using var ticker = new RuntimeRouteTicker(routes, sources, outputs, engine, evaluator,
                new ConfigurationBuilder().Build(), NullLogger<RuntimeRouteTicker>.Instance);
            long Sent() => outputs.List().Single().SentFrames;
            await ticker.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntil(() => Sent() > 0);
                var sent = Sent();
                await Task.Delay(160);
                Assert.Equal(sent, Sent());
                engine.Seek(TimeSpan.FromSeconds(2));
                await WaitUntil(() => Sent() > sent);
                await outputs.DisconnectAsync("file");
                sent = Sent();
                await outputs.ConnectAsync("file");
                await WaitUntil(() => Sent() > sent);
            }
            finally { await ticker.StopAsync(CancellationToken.None); }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void ScriptFailuresBackOffAndUnusedScriptsAreEvicted()
    {
        var directory = Directory.CreateTempSubdirectory("mfp-script-cache-");
        try
        {
            var catalog = new ScriptCatalog(directory.FullName);
            var evaluator = new RuntimeScriptEvaluator(catalog, NullLogger<RuntimeScriptEvaluator>.Instance);
            var policies = new Dictionary<string, AxisConfiguration>();
            Assert.False(evaluator.TryEvaluate("late.funscript", TimeSpan.Zero, policies, out _));
            File.WriteAllText(Path.Combine(directory.FullName, "late.funscript"), "{\"actions\":[{\"at\":0,\"pos\":20}]}");
            Assert.False(evaluator.TryEvaluate("late.funscript", TimeSpan.Zero, policies, out _));
            evaluator.Invalidate("late.funscript");
            Assert.True(evaluator.TryEvaluate("late.funscript", TimeSpan.Zero, policies, out var first));
            Assert.Equal(0.2, first.Values["L0"]);
            evaluator.RetainScripts(new HashSet<string>());
            File.WriteAllText(Path.Combine(directory.FullName, "late.funscript"), "{\"actions\":[{\"at\":0,\"pos\":80}]}");
            Assert.True(evaluator.TryEvaluate("late.funscript", TimeSpan.Zero, policies, out var next));
            Assert.Equal(0.8, next.Values["L0"]);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task AxisRegistryShutdownCanBeRepeatedAfterDisposal()
    {
        DeviceAxis.InitializeDefaults();
        var directory = Directory.CreateTempSubdirectory("mfp-stop-");
        try
        {
            using var engine = new HeadlessPlayerEngine();
            var registry = new DeviceAxisRegistry(new StorageOptions { ConfigRoot = directory.FullName },
                engine, NullLogger<DeviceAxisRegistry>.Instance);
            await registry.StartAsync(CancellationToken.None);
            await registry.DisposeAsync();
            await registry.StopAsync(CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
