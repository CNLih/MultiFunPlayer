using System.Globalization;
using System.Text;
using MultiFunPlayer.Application;
using MultiFunPlayer.Common;

namespace MultiFunPlayer.Infrastructure.Output;

public sealed record TCodeFormattingOptions
{
    public int OutputPrecision { get; init; } = 3;
    public bool IncludeTrailingNewline { get; init; } = true;

    public void Validate()
    {
        if (OutputPrecision is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(OutputPrecision));
    }
}

/// <summary>Converts normalized engine values to the TCode command representation.</summary>
public static class TCodeFormatter
{
    public static string Format(OutputFrame frame, TCodeFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= new TCodeFormattingOptions();
        options.Validate();

        // DeviceAxis owns the canonical format once the host has initialized its
        // device profile. The fallback keeps the headless host usable before that.
        if (!DeviceAxis.All.IsDefaultOrEmpty)
        {
            var known = new List<KeyValuePair<DeviceAxis, double>>();
            var unknown = new List<(string Name, double Value)>();
            foreach (var (name, value) in frame.Values)
            {
                var normalized = Normalize(value);
                if (DeviceAxis.TryParse(name, out var axis))
                    known.Add(new(axis, normalized));
                else
                    unknown.Add((name, normalized));
            }

            var parts = new List<string>(known.Count + unknown.Count);
            if (known.Count > 0)
                parts.AddRange(DeviceAxis.ToString(known).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            parts.AddRange(unknown.Select(x => FormatAxis(x.Name, x.Value, options.OutputPrecision)));
            return Finish(string.Join(' ', parts), options.IncludeTrailingNewline);
        }

        return Finish(string.Join(' ', frame.Values.Select(x => FormatAxis(x.Key, Normalize(x.Value), options.OutputPrecision))),
            options.IncludeTrailingNewline);
    }

    private static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static string FormatAxis(string name, double value, int precision)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An axis name is required.", nameof(name));
        var maximum = (int)Math.Pow(10, precision) - 1;
        var output = (int)Math.Round(value * maximum, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{name}{output.ToString($"D{precision}", CultureInfo.InvariantCulture)}");
    }

    private static string Finish(string command, bool newline) => newline ? command + "\n" : command;
}
