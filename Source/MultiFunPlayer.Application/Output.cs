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

public sealed record OutputSinkError(IOutputSink Sink, OutputFrame Frame, Exception Exception);
