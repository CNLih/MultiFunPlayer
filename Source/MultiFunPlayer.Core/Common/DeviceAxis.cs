using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Common;

public sealed class DeviceAxis
{
    private readonly int _id;

    public string Name { get; }
    public double DefaultValue { get; }
    public string FriendlyName { get; }
    public IReadOnlyList<string> FunscriptNames { get; }

    public override string ToString() => Name;
    public override int GetHashCode() => _id;

    private DeviceAxis(DeviceAxisSettings settings)
    {
        _id = _count++;

        Name = settings.Name;
        DefaultValue = settings.DefaultValue;
        FriendlyName = settings.FriendlyName;
        FunscriptNames = settings.FunscriptNames;
    }

    private static int _count;
    private static int _outputMaximum;
    private static CompositeFormat _outputFormat;
    private static FrozenDictionary<string, DeviceAxis> _axisNameMap;
    public static ImmutableArray<DeviceAxis> All { get; private set; }

    public static bool IsInitialized => _count != 0;

    /// <summary>Initializes the portable host with the standard TCode axis set.</summary>
    public static void InitializeDefaults()
    {
        if (IsInitialized)
            return;

        InitializeFromDevice(new DeviceSettings
        {
            Name = "TCode-0.3",
            OutputPrecision = 4,
            IsDefault = true,
            Axes =
            [
                new() { Name = "L0", FriendlyName = "Up/Down", FunscriptNames = ["raw", "*", "stroke", "L0", "up"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "L1", FriendlyName = "Forward/Backward", FunscriptNames = ["surge", "L1", "forward"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "L2", FriendlyName = "Left/Right", FunscriptNames = ["sway", "L2", "left"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "R0", FriendlyName = "Twist", FunscriptNames = ["twist", "R0", "yaw"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "R1", FriendlyName = "Roll", FunscriptNames = ["roll", "R1"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "R2", FriendlyName = "Pitch", FunscriptNames = ["pitch", "R2"], Enabled = true, DefaultValue = 0.5 },
                new() { Name = "V0", FriendlyName = "Vibrate", FunscriptNames = ["vib", "V0"], Enabled = true, DefaultValue = 0 },
                new() { Name = "V1", FriendlyName = "Pump", FunscriptNames = ["pump", "lube", "V1"], Enabled = true, DefaultValue = 0 },
                new() { Name = "A0", FriendlyName = "Valve", FunscriptNames = ["valve", "A0"], Enabled = true, DefaultValue = 0 },
                new() { Name = "A1", FriendlyName = "Suction", FunscriptNames = ["suck", "A1"], Enabled = true, DefaultValue = 0 },
                new() { Name = "A2", FriendlyName = "Lube", FunscriptNames = ["lube", "A2"], Enabled = true, DefaultValue = 0 }
            ]
        });
    }

    public static DeviceAxis Parse(string name) => _axisNameMap.GetValueOrDefault(name, null);
    public static bool TryParse(string name, out DeviceAxis axis)
    {
        axis = Parse(name);
        return axis != null;
    }

    public static string ToString(DeviceAxis axis, double value) => $"{axis}{string.Format(null, _outputFormat, value * _outputMaximum)}";
    public static string ToString(DeviceAxis axis, double value, double interval) => $"{ToString(axis, value)}I{(int)Math.Floor(interval + 0.75)}";

    public static string ToString(IEnumerable<KeyValuePair<DeviceAxis, double>> values)
        => $"{values.Aggregate(string.Empty, (s, x) => $"{s} {ToString(x.Key, x.Value)}")}\n".TrimStart();
    public static string ToString(IEnumerable<KeyValuePair<DeviceAxis, double>> values, double interval)
        => $"{values.Aggregate(string.Empty, (s, x) => $"{s} {ToString(x.Key, x.Value, interval)}")}\n".TrimStart();

    public static bool IsValueDirty(double value, double lastValue)
        => Math.Abs(lastValue - value) * (_outputMaximum + 1) >= 1 || (double.IsFinite(value) ^ double.IsFinite(lastValue));
    public static bool IsValueDirty(double value, double lastValue, double epsilon)
        => Math.Abs(lastValue - value) >= epsilon || (double.IsFinite(value) ^ double.IsFinite(lastValue));

    internal static void InitializeFromDevice(DeviceSettings device)
    {
        if (_count != 0)
            throw new NotSupportedException();

        _outputMaximum = (int)(Math.Pow(10, device.OutputPrecision) - 1);
        _outputFormat = CompositeFormat.Parse($"{{0:{new string('0', device.OutputPrecision)}}}");

        ArgumentNullException.ThrowIfNull(device);

        All = device.Axes.Where(s => s.Enabled && Regex.IsMatch(s.Name, "^[A-Z][0-9]$"))
                         .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(s => new DeviceAxis(s))
                         .ToImmutableArray();

        _axisNameMap = All.ToFrozenDictionary(a => a.Name, a => a);
    }
}
