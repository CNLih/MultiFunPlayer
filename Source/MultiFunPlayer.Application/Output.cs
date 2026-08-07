namespace MultiFunPlayer.Application;

/// <summary>A complete, normalized output update for one engine tick.</summary>
public sealed record OutputFrame(TimeSpan Position, IReadOnlyDictionary<string, double> Values);

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
    string? LastError { get; }
}

public sealed record OutputSinkError(IOutputSink Sink, OutputFrame Frame, Exception Exception);
