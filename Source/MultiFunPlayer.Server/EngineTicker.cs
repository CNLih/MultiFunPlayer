using MultiFunPlayer.Application;
using System.Threading.Channels;

namespace MultiFunPlayer.Server;

public sealed class EngineTicker : BackgroundService
{
    private readonly HeadlessPlayerEngine _engine;
    private readonly StateBroadcaster _broadcaster;
    private readonly ILogger<EngineTicker> _logger;
    private readonly TimeSpan _interval;
    private long _lastRevision = -1;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public EngineTicker(HeadlessPlayerEngine engine, StateBroadcaster broadcaster,
        IConfiguration configuration, ILogger<EngineTicker> logger)
    {
        _engine = engine;
        _broadcaster = broadcaster;
        _logger = logger;
        var hz = ServerOptions.ResolveTickHz(configuration);
        _interval = TimeSpan.FromSeconds(1d / hz);
        _engine.SnapshotChanged += OnSnapshotChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (_wake.Reader.TryRead(out _)) { }
                if (_engine.State != PlayerState.Playing)
                {
                    await _wake.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }
                using var timer = new PeriodicTimer(_interval);
                while (_engine.State == PlayerState.Playing
                    && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    try { _engine.Tick(); }
                    catch (Exception exception) { _logger.LogError(exception, "Engine tick failed"); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void OnSnapshotChanged(object? sender, EngineSnapshot snapshot)
    {
        if (snapshot.State == PlayerState.Playing)
            _wake.Writer.TryWrite(true);
        if (snapshot.Revision != Interlocked.Read(ref _lastRevision))
        {
            Interlocked.Exchange(ref _lastRevision, snapshot.Revision);
            _broadcaster.Publish(snapshot);
        }
    }

    public override void Dispose()
    {
        _engine.SnapshotChanged -= OnSnapshotChanged;
        base.Dispose();
    }
}
