using MultiFunPlayer.Application;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>
/// Samples engine frames for a target without moving timing responsibility into
/// the engine. The wrapped sink still owns transport backpressure and errors.
/// </summary>
public sealed class RateLimitedOutputSink : IOutputSink, IDisposable, IAsyncDisposable
{
    private readonly IOutputSink _inner;
    private readonly TimeSpan _minimumInterval;
    private TimeSpan? _lastPosition;
    private int _disposed;

    public RateLimitedOutputSink(IOutputSink inner, int updateRateHz)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (updateRateHz is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(updateRateHz), "Output rate must be between 1 and 20 Hz.");
        UpdateRateHz = updateRateHz;
        _minimumInterval = TimeSpan.FromSeconds(1d / updateRateHz);
    }

    public int UpdateRateHz { get; }

    public void Publish(OutputFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_lastPosition is { } previous
            && frame.Position >= previous
            && frame.Position - previous < _minimumInterval)
            return;
        _inner.Publish(frame);
        _lastPosition = frame.Position;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_inner is IDisposable disposable)
            disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
