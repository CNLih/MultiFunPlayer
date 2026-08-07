namespace MultiFunPlayer.Application;

/// <summary>Lifecycle of a headless player instance.</summary>
public enum PlayerState
{
    Empty,
    Ready,
    Playing,
    Paused,
    Stopped,
    Completed
}
