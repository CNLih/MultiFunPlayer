using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Application.Tests;

public sealed class EngineTests
{
    [Fact]
    public void ExplicitTicksProduceInterpolatedValuesWithoutWaiting()
    {
        using var engine = new HeadlessPlayerEngine(new FakeClock());
        var sink = new RecordingSink();
        engine.AddSink(sink);
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0), new(1, 1)])));
        engine.Play();

        var snapshot = engine.Tick(TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlayerState.Playing, snapshot.State);
        Assert.Equal(0.5, snapshot.Values["L0"], precision: 10);
        Assert.Equal(0.5, sink.Frames[^1].Values["L0"], precision: 10);
    }

    [Fact]
    public void InjectedClockAdvancesWhilePlayingAndPausesDeterministically()
    {
        var clock = new FakeClock();
        using var engine = new HeadlessPlayerEngine(clock);
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0), new(2, 1)])));
        engine.Play();

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, engine.Tick().Position.TotalSeconds, precision: 10);
        engine.Pause();
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, engine.Tick().Position.TotalSeconds, precision: 10);
        engine.Seek(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0.25, engine.Position.TotalSeconds, precision: 10);
    }

    [Fact]
    public void ReachingDurationPublishesCompletedState()
    {
        using var engine = new HeadlessPlayerEngine(new FakeClock());
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0), new(1, 1)])));
        engine.Play();

        var snapshot = engine.Tick(TimeSpan.FromSeconds(2));

        Assert.Equal(PlayerState.Completed, snapshot.State);
        Assert.Equal(1, snapshot.Position.TotalSeconds, precision: 10);
        Assert.Equal(1, snapshot.Values["L0"], precision: 10);
    }

    [Fact]
    public void AFailingSinkDoesNotPreventOtherSinksOrSnapshotEvents()
    {
        using var engine = new HeadlessPlayerEngine(new FakeClock());
        var recording = new RecordingSink();
        var failures = 0;
        var snapshots = 0;
        engine.AddSink(new ThrowingSink());
        engine.AddSink(recording);
        engine.SinkFailed += (_, _) => failures++;
        engine.SnapshotChanged += (_, _) => snapshots++;
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0), new(1, 1)])));
        engine.Play();
        engine.Tick(TimeSpan.FromMilliseconds(100));

        Assert.Equal(3, snapshots);
        Assert.Equal(3, failures);
        Assert.Equal(3, recording.Frames.Count);
    }

    private static ScriptResource Resource(string name, IEnumerable<Keyframe> keyframes)
        => new(name, name + ".funscript", new KeyframeCollection(keyframes), new ChapterCollection(), new BookmarkCollection());

    private sealed class FakeClock : IMediaClock
    {
        public TimeSpan Now { get; private set; }

        public void Advance(TimeSpan amount) => Now += amount;
    }

    private sealed class RecordingSink : IOutputSink
    {
        public List<OutputFrame> Frames { get; } = [];

        public void Publish(OutputFrame frame) => Frames.Add(frame);
    }

    private sealed class ThrowingSink : IOutputSink
    {
        public void Publish(OutputFrame frame) => throw new InvalidOperationException("test sink");
    }
}
