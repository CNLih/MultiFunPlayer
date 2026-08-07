using MultiFunPlayer.Application;

namespace MultiFunPlayer.Server;

public sealed class EngineTicker : BackgroundService
{
    private readonly HeadlessPlayerEngine _engine;
    private readonly StateBroadcaster _broadcaster;
    private readonly ILogger<EngineTicker> _logger;
    private readonly TimeSpan _interval;
    private long _lastRevision = -1;

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
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var snapshot = _engine.Tick();
                OnSnapshotChanged(_engine, snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Engine tick failed");
            }
        }
    }

    private void OnSnapshotChanged(object? sender, EngineSnapshot snapshot)
    {
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
