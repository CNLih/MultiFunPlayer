using MultiFunPlayer.Common;

namespace MultiFunPlayer.Application;

/// <summary>
/// Portable per-device-axis output policy. Values are normalized to [0, 1]
/// by the script evaluator before this policy is applied.
/// </summary>
public sealed record AxisConfiguration(
    string Axis,
    double Minimum = 0,
    double Maximum = 1,
    bool Inverted = false,
    InterpolationType Interpolation = InterpolationType.Linear,
    bool LimitEnabled = false,
    double LimitMinimum = 0,
    double LimitMaximum = 1,
    double? MaximumTravel = null)
{
    public static AxisConfiguration Default(string axis)
        => new(axis);

    public AxisConfiguration Normalize()
    {
        if (string.IsNullOrWhiteSpace(Axis))
            throw new ArgumentException("An axis name is required.", nameof(Axis));
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum)
            || Minimum < 0 || Maximum > 1 || Minimum >= Maximum)
            throw new ArgumentOutOfRangeException(nameof(Minimum),
                "Axis range must satisfy 0 <= minimum < maximum <= 1.");
        if (!Enum.IsDefined(Interpolation))
            throw new ArgumentOutOfRangeException(nameof(Interpolation),
                "The interpolation mode is not supported.");
        if (!double.IsFinite(LimitMinimum) || !double.IsFinite(LimitMaximum)
            || LimitMinimum < 0 || LimitMaximum > 1 || LimitMinimum >= LimitMaximum)
            throw new ArgumentOutOfRangeException(nameof(LimitMinimum),
                "Axis limit must satisfy 0 <= limitMinimum < limitMaximum <= 1.");
        if (MaximumTravel is not null
            && (!double.IsFinite(MaximumTravel.Value) || MaximumTravel.Value <= 0
                || MaximumTravel.Value > 1_000_000))
            throw new ArgumentOutOfRangeException(nameof(MaximumTravel),
                "Maximum travel must be greater than 0 and no more than 1000000.");
        return this;
    }

    /// <summary>Applies limit, inversion, and configured output range.</summary>
    public double Apply(double value)
    {
        if (!double.IsFinite(value))
            value = 0;
        value = MathUtils.Clamp01(value);
        if (Inverted)
            value = 1 - value;
        if (LimitEnabled)
            value = MathUtils.ClampSafe(value, LimitMinimum, LimitMaximum);
        return MathUtils.Lerp(Minimum, Maximum, value);
    }
}
