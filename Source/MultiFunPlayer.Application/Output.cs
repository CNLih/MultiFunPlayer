namespace MultiFunPlayer.Application;

/// <summary>A complete, normalized output update for one engine tick.</summary>
public sealed record OutputFrame(
    TimeSpan Position,
    IReadOnlyDictionary<string, double> Values,
    double? IntervalMilliseconds = null)
{
    public OutputFrame(TimeSpan position, IReadOnlyDictionary<string, double> values, TimeSpan interval)
        : this(position, values, interval.TotalMilliseconds) { }
}

/// <summary>
/// Synchronous boundary for output adapters. Adapters that need asynchronous
/// I/O should enqueue the frame and return; the engine never owns adapter
/// threads or timing.
/// </summary>
public interface IOutputSink
{
    void Publish(OutputFrame frame);
}

/// <summary>
/// Optional, thread-safe counters exposed by an output sink for diagnostics.
/// Sent frames are frames accepted by the wrapped sink; transport adapters may
/// still complete their physical write asynchronously.
/// </summary>
public interface IOutputSinkDiagnostics
{
    long SentFrames { get; }
    long DroppedFrames { get; }
    long FailedFrames { get; }
    DateTimeOffset? LastSentAtUtc { get; }
}

/// <summary>Optional failure detail for sinks that can expose the latest write error.</summary>
public interface IOutputSinkErrorDiagnostics
{
    string? LastError { get; }
}

/// <summary>Optional asynchronous fault notifications for queued output sinks.</summary>
public interface IOutputSinkFaults
{
    event Action<Exception>? Faulted;
}

public sealed record OutputSinkError(IOutputSink Sink, OutputFrame Frame, Exception Exception);
