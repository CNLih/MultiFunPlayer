using MultiFunPlayer.Application;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>
/// Samples engine frames for a target without moving timing responsibility into
/// the engine. The wrapped sink still owns transport backpressure and errors.
/// </summary>
public sealed class RateLimitedOutputSink : IOutputSink, IOutputSinkDiagnostics,
    IOutputSinkErrorDiagnostics, IOutputSinkFaults, IDisposable, IAsyncDisposable
{
    // PeriodicTimer and NAS/container scheduling can arrive early. Keep the
    // configured rate as the target while avoiding drops caused only by up to
    // 10% scheduler jitter at the exact boundary.
    private const double SamplingToleranceRatio = 0.10;
    private readonly IOutputSink _inner;
    private readonly IOutputSinkFaults? _innerFaults;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeSpan _samplingTolerance;
    private TimeSpan? _lastPosition;
    private long _sentFrames;
    private long _droppedFrames;
    private long _failedFrames;
    private long _lastSentAtUtcTicks;
    private string? _lastError;
    private int _disposed;

    public RateLimitedOutputSink(IOutputSink inner, int updateRateHz)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (updateRateHz is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(updateRateHz), "Output rate must be between 1 and 20 Hz.");
        _innerFaults = inner as IOutputSinkFaults;
        if (_innerFaults is not null)
            _innerFaults.Faulted += HandleInnerFault;
        UpdateRateHz = updateRateHz;
        _minimumInterval = TimeSpan.FromSeconds(1d / updateRateHz);
        _samplingTolerance = TimeSpan.FromTicks(Math.Max(1,
            (long)(_minimumInterval.Ticks * SamplingToleranceRatio)));
    }

    public int UpdateRateHz { get; }

    public long SentFrames => Interlocked.Read(ref _sentFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long FailedFrames => Interlocked.Read(ref _failedFrames);
    public DateTimeOffset? LastSentAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSentAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
    public string? LastError => Volatile.Read(ref _lastError)
        ?? (_inner as IOutputSinkErrorDiagnostics)?.LastError;

    public event Action<Exception>? Faulted;

    public void Publish(OutputFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_lastPosition is { } previous
            && frame.Position >= previous
            && frame.Position - previous < _minimumInterval - _samplingTolerance)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        try
        {
            _inner.Publish(frame);
            Interlocked.Increment(ref _sentFrames);
            Interlocked.Exchange(ref _lastSentAtUtcTicks, DateTimeOffset.UtcNow.Ticks);
            Volatile.Write(ref _lastError, null);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }
        _lastPosition = frame.Position;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_innerFaults is not null)
            _innerFaults.Faulted -= HandleInnerFault;
        if (_inner is IDisposable disposable)
            disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_innerFaults is not null)
            _innerFaults.Faulted -= HandleInnerFault;
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

    private void HandleInnerFault(Exception exception) => RecordFailure(exception);

    private void RecordFailure(Exception exception)
    {
        Interlocked.Increment(ref _failedFrames);
        Volatile.Write(ref _lastError, exception.Message);
        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // Diagnostics subscribers must not terminate an output worker.
        }
    }
}
