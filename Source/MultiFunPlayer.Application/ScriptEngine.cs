using System.Collections.ObjectModel;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Application;

/// <summary>One axis and the script resource that drives it.</summary>
public sealed record AxisScript(
    string AxisName,
    IScriptResource Resource,
    InterpolationType Interpolation = InterpolationType.Linear);

/// <summary>
/// Platform-independent script evaluator. A resource is kept intact so the
/// existing script readers can feed the application layer without conversion.
/// </summary>
public sealed class ScriptEngine
{
    private readonly Dictionary<string, AxisScript> _tracks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AxisScript> Tracks => _tracks.Values;

    public string? ScriptName { get; private set; }

    public TimeSpan Duration { get; private set; }

    public void Clear()
    {
        _tracks.Clear();
        ScriptName = null;
        Duration = TimeSpan.Zero;
    }

    public void Load(IScriptResource resource, string axisName = "default", InterpolationType interpolation = InterpolationType.Linear)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Load([new AxisScript(axisName, resource, interpolation)]);
    }

    public void Load(IEnumerable<AxisScript> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        _tracks.Clear();
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.AxisName))
                throw new ArgumentException("An axis name is required.", nameof(tracks));
            ArgumentNullException.ThrowIfNull(track.Resource);
            _tracks[track.AxisName] = track;
        }

        ScriptName = _tracks.Values.Select(x => x.Resource.Name).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var durationSeconds = _tracks.Values
            .Select(x => x.Resource.Keyframes)
            .Where(x => x is not null && x.Count > 0)
            .Select(x => x[^1].Position)
            .Where(x => double.IsFinite(x) && x >= 0)
            .DefaultIfEmpty(0)
            .Max();
        Duration = TimeSpan.FromSeconds(durationSeconds);
    }

    public void Load(IReadOnlyDictionary<DeviceAxis, IScriptResource> resources, InterpolationType interpolation = InterpolationType.Linear)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Load(resources.Select(x => new AxisScript(x.Key.Name, x.Value, interpolation)));
    }

    public OutputFrame Evaluate(TimeSpan position)
    {
        var seconds = position.TotalSeconds;
        if (!double.IsFinite(seconds))
            seconds = 0;
        seconds = Math.Max(0, seconds);
        if (Duration > TimeSpan.Zero)
            seconds = Math.Min(seconds, Duration.TotalSeconds);

        var values = new Dictionary<string, double>(_tracks.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (axis, track) in _tracks)
            values[axis] = Evaluate(track.Resource.Keyframes, seconds, track.Interpolation);

        return new OutputFrame(TimeSpan.FromSeconds(seconds),
            new ReadOnlyDictionary<string, double>(values));
    }

    private static double Evaluate(KeyframeCollection? keyframes, double position, InterpolationType interpolation)
    {
        if (keyframes is null || keyframes.Count == 0)
            return 0;
        if (keyframes.Count == 1 || position <= keyframes[0].Position)
            return keyframes[0].Value;
        if (position >= keyframes[^1].Position)
            return keyframes[^1].Value;

        var index = Math.Clamp(keyframes.SearchForIndexBefore(position), 0, keyframes.Count - 2);
        return keyframes.Interpolate(index, position, interpolation);
    }
}
