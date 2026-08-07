using System.Diagnostics;

namespace MultiFunPlayer.Application;

/// <summary>
/// Monotonic media time. Implementations are deliberately small so a host can
/// replace them with a media player's clock or a deterministic test clock.
/// </summary>
public interface IMediaClock
{
    TimeSpan Now { get; }
}

/// <summary>System-backed monotonic clock used by a server host.</summary>
public interface ISystemClock : IMediaClock
{
}

public sealed class SystemClock : ISystemClock
{
    private readonly long _started = Stopwatch.GetTimestamp();

    public TimeSpan Now
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _started;
            return TimeSpan.FromSeconds(elapsed / (double)Stopwatch.Frequency);
        }
    }
}
