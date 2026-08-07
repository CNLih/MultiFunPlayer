namespace MultiFunPlayer.Tests;

internal sealed class FakeClock
{
    public FakeClock(DateTimeOffset? start = null) => Now = start ?? DateTimeOffset.UnixEpoch;

    public DateTimeOffset Now { get; private set; }

    public DateTimeOffset Advance(TimeSpan elapsed)
    {
        Now += elapsed;
        return Now;
    }
}

internal sealed record OutputSample(DateTimeOffset Timestamp, string Payload);

internal sealed class RecordingOutputSink
{
    private readonly List<OutputSample> _samples = [];

    public IReadOnlyList<OutputSample> Samples => _samples;

    public void Write(DateTimeOffset timestamp, string payload)
        => _samples.Add(new OutputSample(timestamp, payload));
}
