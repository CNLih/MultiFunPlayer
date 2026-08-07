using MultiFunPlayer.Common;
using MultiFunPlayer.Script;
using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Tests;

public sealed class ScriptParsingGoldenTests
{
    [Fact]
    public void FunscriptReaderParsesMetadataAndClampsActions()
    {
        var result = FunscriptReader.Default.FromText("sample.funscript", "fixture", """
            {
              "actions": [
                { "at": 0, "pos": 0 },
                { "at": 500, "pos": 50 },
                { "at": 1000, "pos": 120 },
                { "at": -1, "pos": 10 },
                { "at": 1000, "pos": 10 }
              ],
              "metadata": {
                "chapters": [{ "name": "Intro", "startTime": "00:00:00", "endTime": "00:00:00.500" }],
                "bookmarks": [{ "name": "Beat", "time": "00:00:00.500" }]
              }
            }
            """);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsMultiAxis);
        Assert.Equal("sample.funscript", result.Resource.Name);
        Assert.Equal("fixture", result.Resource.Source);
        Assert.Equal([new Keyframe(0, 0), new Keyframe(0.5, 0.5), new Keyframe(1, 1)], result.Resource.Keyframes);
        Assert.Equal([new Chapter("Intro", 0, 0.5)], result.Resource.Chapters);
        Assert.Equal([new Bookmark("Beat", 0.5)], result.Resource.Bookmarks);
    }

    [Fact]
    public void FunscriptReaderMapsMultiAxisActions()
    {
        TestDeviceAxisFixture.EnsureInitialized();

        var result = FunscriptReader.Default.FromText("multi.funscript", "fixture", """
            {
              "actions": [{ "at": 0, "pos": 25 }],
              "axes": [{ "id": "R2", "actions": [{ "at": 1000, "pos": 75 }] }]
            }
            """);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsMultiAxis);
        Assert.Collection(result.Resources.OrderBy(x => x.Key.Name),
            item =>
            {
                Assert.Equal("L0", item.Key.Name);
                Assert.Equal(new Keyframe(0, 0.25), item.Value.Keyframes.Single());
            },
            item =>
            {
                Assert.Equal("R2", item.Key.Name);
                Assert.Equal(new Keyframe(1, 0.75), item.Value.Keyframes.Single());
            });
    }

    [Fact]
    public void CsvReaderIsCultureInvariantAndSkipsMalformedRows()
    {
        var result = CsvReader.Default.FromText("sample.csv", "fixture", """
            position;value
            0;0
            0,5;1,2
            -1;0
            malformed
            """);

        Assert.True(result.IsSuccess);
        Assert.Equal([new Keyframe(0, 0), new Keyframe(0.5, 1)], result.Resource.Keyframes);
    }
}

public sealed class InterpolationGoldenTests
{
    private static readonly KeyframeCollection Keyframes =
        [new(0, 0), new(1, 1), new(2, 2), new(3, 3)];

    [Theory]
    [InlineData(InterpolationType.Linear, 0.25, 0.25)]
    [InlineData(InterpolationType.Step, 0.25, 0)]
    [InlineData(InterpolationType.Pchip, 1.5, 1.5)]
    [InlineData(InterpolationType.Makima, 1.5, 1.5)]
    public void KeyframeInterpolationMatchesGoldenValues(InterpolationType type, double position, double expected)
    {
        var index = Keyframes.SearchForIndexBefore(position);
        Assert.Equal(expected, Keyframes.Interpolate(index, position, type), precision: 12);
    }

    [Fact]
    public void KeyframeCollectionOrdersAndRejectsDuplicatePositions()
    {
        var keyframes = new KeyframeCollection([new(2, 0.2), new(0, 0), new(1, 0.1)]);
        keyframes.Add(1, 0.9);

        Assert.Equal([new Keyframe(0, 0), new Keyframe(1, 0.1), new Keyframe(2, 0.2)], keyframes);
    }
}

public sealed class AxisAndTCodeGoldenTests
{
    public AxisAndTCodeGoldenTests() => TestDeviceAxisFixture.EnsureInitialized();

    [Fact]
    public void DeviceAxisMapsAndFormatsTCodeValues()
    {
        var l0 = DeviceAxis.Parse("L0");
        var r2 = DeviceAxis.Parse("R2");

        Assert.NotNull(l0);
        Assert.NotNull(r2);
        Assert.Equal("L05000", DeviceAxis.ToString(l0, 0.5));
        Assert.Equal("R21000I17", DeviceAxis.ToString(r2, 0.1, 16.25));
        Assert.Equal("L00000 R21000", DeviceAxis.ToString(new Dictionary<DeviceAxis, double>
        {
            [l0] = 0,
            [r2] = 0.1
        }));
    }

    [Fact]
    public void FakeClockAndRecordingSinkCaptureDeterministicTrace()
    {
        var clock = new FakeClock();
        var sink = new RecordingOutputSink();
        var axis = DeviceAxis.Parse("L0");

        sink.Write(clock.Now, DeviceAxis.ToString(axis, 0));
        sink.Write(clock.Advance(TimeSpan.FromMilliseconds(500)), DeviceAxis.ToString(axis, 1, 500));

        Assert.Equal([
            new OutputSample(DateTimeOffset.UnixEpoch, "L00000"),
            new OutputSample(DateTimeOffset.UnixEpoch.AddMilliseconds(500), "L09999I500")
        ], sink.Samples);
    }
}

public sealed class BaselineFixtureTests
{
    [Fact]
    public void SanitizedConfigurationFixtureHasNoSecretValues()
    {
        var fixture = JObject.Parse(File.ReadAllText(FixturePath("config.sample.json")));

        Assert.Equal(44, fixture.Value<int>("ConfigVersion"));
        Assert.Equal("TCode-0.3", fixture.SelectToken("Device.SelectedDevice")?.Value<string>());
        Assert.DoesNotContain("password", fixture.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", fixture.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtocolTraceFixtureContainsSanitizedRepresentativeMessages()
    {
        var fixture = JObject.Parse(File.ReadAllText(FixturePath("protocol-traces.json")));
        var traces = fixture["traces"] as JArray;

        Assert.NotNull(traces);
        Assert.Contains(traces, trace => trace.Value<string>("protocol") == "tcode" && trace.Value<string>("payload") == "L05000I100");
        Assert.Contains(traces, trace => trace.Value<string>("protocol") == "vlc-http" && trace.Value<int>("status") == 200);
        Assert.DoesNotContain("127.0.0.1", fixture.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
