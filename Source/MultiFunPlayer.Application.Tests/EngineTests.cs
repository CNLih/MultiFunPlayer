using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Application.Tests;

public sealed class EngineTests
{
    [Fact]
    public void IdleTicksDoNotAllocateSnapshotsOrRepublishOutputButCommandsStillDo()
    {
        using var engine = new HeadlessPlayerEngine(new FakeClock());
        var sink = new RecordingSink();
        engine.AddSink(sink);
        var empty = engine.Current;
        Assert.Same(empty, engine.Tick());
        Assert.Empty(sink.Frames);
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0), new(2, 1)])));
        var ready = engine.Current;
        for (var index = 0; index < 100; index++) Assert.Same(ready, engine.Tick());
        Assert.Single(sink.Frames);
        engine.Play();
        engine.Pause();
        var paused = engine.Current;
        var sent = sink.Frames.Count;
        for (var index = 0; index < 100; index++) Assert.Same(paused, engine.Tick());
        Assert.Equal(sent, sink.Frames.Count);
        engine.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(sent + 1, sink.Frames.Count);
        Assert.Equal(0.5, sink.Frames[^1].Values["L0"], precision: 10);
    }

    [Fact]
    public void ConfigurationSnapshotsRemainStableWhenPoliciesChange()
    {
        using var engine = new HeadlessPlayerEngine();
        engine.SetAxisConfiguration(new AxisConfiguration("L0", Minimum: 0.1));
        var before = engine.AxisConfigurations;
        Assert.Same(before, engine.AxisConfigurations);
        engine.SetAxisConfiguration(new AxisConfiguration("L0", Minimum: 0.2));
        Assert.Equal(0.1, before["L0"].Minimum);
        Assert.Equal(0.2, engine.AxisConfigurations["L0"].Minimum);
    }

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

    [Fact]
    public void AxisConfigurationIsAppliedToEvaluatedOutput()
    {
        using var engine = new HeadlessPlayerEngine(new FakeClock());
        engine.Load(new AxisScript("L0", Resource("stroke", [new(0, 0.25), new(1, 0.75)])));
        engine.SetAxisConfiguration(new AxisConfiguration("L0",
            Minimum: 0.2, Maximum: 0.6, Inverted: true,
            Interpolation: InterpolationType.Step, LimitEnabled: true,
            LimitMinimum: 0.3, LimitMaximum: 0.8));
        engine.Play();

        var snapshot = engine.Tick(TimeSpan.FromMilliseconds(750));

        // Step keeps 0.25, inversion makes 0.75, and range maps it to 0.5.
        Assert.Equal(0.5, snapshot.Values["L0"], precision: 10);
        Assert.Equal(InterpolationType.Step, engine.AxisConfigurations["L0"].Interpolation);
    }

    [Fact]
    public void AxisConfigurationRejectsUnsafeRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AxisConfiguration("L0", Minimum: 0.8, Maximum: 0.2).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AxisConfiguration("L0", LimitMinimum: 0.9, LimitMaximum: 0.9).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AxisConfiguration("L0", MaximumTravel: 0).Normalize());
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
