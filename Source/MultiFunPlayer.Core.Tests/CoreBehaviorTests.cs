using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Core.Tests;

public sealed class CoreBehaviorTests
{
    static CoreBehaviorTests()
    {
        DeviceAxis.InitializeFromDevice(new DeviceSettings
        {
            Name = "test-device",
            OutputPrecision = 3,
            Axes =
            [
                new() { Name = "L0", FriendlyName = "Stroke", FunscriptNames = ["*", "stroke", "L0"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "R2", FriendlyName = "Pitch", FunscriptNames = ["pitch", "R2"], Enabled = true, DefaultValue = 0.5 }
            ]
        });
    }

    [Fact]
    public void AxisFormattingAndScriptMatchingUseConfiguredPrecision()
    {
        var axis = DeviceAxis.Parse("L0");

        Assert.NotNull(axis);
        Assert.Equal("L0500", DeviceAxis.ToString(axis, 0.5));
        Assert.Equal(new[] { axis }, DeviceAxisUtils.FindAxesMatchingName("clip.funscript"));
        Assert.Equal(new[] { DeviceAxis.Parse("R2") }, DeviceAxisUtils.FindAxesMatchingName("clip.pitch.funscript"));
    }

    [Theory]
    [InlineData(InterpolationType.Linear, 0.5)]
    [InlineData(InterpolationType.Pchip, 0.5)]
    [InlineData(InterpolationType.Makima, 0.5)]
    [InlineData(InterpolationType.Step, 0)]
    public void KeyframesInterpolateAtSegment(InterpolationType type, double expected)
    {
        var keyframes = new KeyframeCollection([new(0, 0), new(1, 1)]);

        Assert.Equal(expected, keyframes.Interpolate(0, 0.5, type), precision: 10);
    }

    [Fact]
    public void FunscriptReaderProducesNormalizedKeyframes()
    {
        var result = FunscriptReader.Default.FromText("clip.funscript", "/tmp", "{\"actions\":[{\"at\":1000,\"pos\":125}]}");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Resource.Keyframes.Count);
        Assert.Equal(1, result.Resource.Keyframes[0].Position);
        Assert.Equal(1, result.Resource.Keyframes[0].Value);
    }

    [Fact]
    public void TCodeParserReturnsButtonAndChangedAxisGestures()
    {
        var parser = new TCodeParser();

        var first = parser.Parse("#btn:1@L0:1000\n");
        var second = parser.Parse("@L0:2000\n");

        Assert.Collection(first,
            gesture => Assert.IsType<TCodeButtonGesture>(gesture),
            gesture => Assert.IsType<TCodeAxisGesture>(gesture));
        var axis = Assert.IsType<TCodeAxisGesture>(Assert.Single(second));
        Assert.Equal("L0", axis.Axis);
        Assert.True(axis.Value > 0);
        Assert.True(axis.Delta > 0);
    }

    [Fact]
    public void RelativeAxisGestureDataAppliesDelta()
    {
        var data = AxisInputGestureData.Relative(0.25, 0.1);

        Assert.Equal(0.75, data.ApplyTo(0.5));
        Assert.True(data.IsRelative);
    }
}
