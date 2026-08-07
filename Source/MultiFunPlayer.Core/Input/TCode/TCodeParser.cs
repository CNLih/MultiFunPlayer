using MultiFunPlayer.Common;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Input.TCode;

/// <summary>
/// Parses newline-delimited TCode commands without requiring an event aggregator or a transport.
/// </summary>
public sealed partial class TCodeParser
{
    private readonly SplittingStringBuffer _buffer = new('\n');
    private readonly Dictionary<string, int> _unsignedAxisStates = [];
    private readonly Dictionary<string, int> _signedAxisStates = [];

    public IReadOnlyList<IInputGesture> Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        _buffer.Push(input);
        var gestures = new List<IInputGesture>();
        foreach (var command in _buffer.Consume())
            ParseCommand(command, gestures);

        return gestures;
    }

    private static void ParseCommand(string command, ICollection<IInputGesture> gestures)
    {
        foreach (var match in ButtonRegex.Matches(command).Where(m => m.Success))
        {
            var button = match.Groups["button"].Value;
            var state = int.Parse(match.Groups["state"].Value) == 1;
            gestures.Add(TCodeButtonGesture.Create(button, state));
        }

        foreach (var match in UnsignedAxisRegex.Matches(command).Where(m => m.Success))
            CreateAxisGesture(_unsignedAxisStates, match, ushort.MinValue, ushort.MaxValue, gestures);

        foreach (var match in SignedAxisRegex.Matches(command).Where(m => m.Success))
            CreateAxisGesture(_signedAxisStates, match, short.MinValue, short.MaxValue, gestures);
    }

    private static void CreateAxisGesture(Dictionary<string, int> states, Match match, double minValue, double maxValue, ICollection<IInputGesture> gestures)
    {
        var axis = match.Groups["axis"].Value;
        var value = int.Parse(match.Groups["value"].Value);
        states.TryAdd(axis, 0);

        var delta = value - states[axis];
        if (delta == 0)
            return;

        var valueDecimal = MathUtils.UnLerp(minValue, maxValue, value);
        var deltaDecimal = MathUtils.UnLerp(minValue, maxValue, delta);
        gestures.Add(TCodeAxisGesture.Create(axis, valueDecimal, deltaDecimal, 0));
        states[axis] = value;
    }

    [GeneratedRegex("#(?<button>.+?):(?<state>0|1)")]
    private static partial Regex ButtonRegex { get; }

    [GeneratedRegex(@"@(?<axis>.+?):(?<value>\d{1,5})")]
    private static partial Regex UnsignedAxisRegex { get; }

    [GeneratedRegex(@"\$(?<axis>.+?):(?<value>-?\d{1,5})")]
    private static partial Regex SignedAxisRegex { get; }
}
