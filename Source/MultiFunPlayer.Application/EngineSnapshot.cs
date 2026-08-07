namespace MultiFunPlayer.Application;

/// <summary>Serializable state published after commands and deterministic ticks.</summary>
public sealed record EngineSnapshot(
    PlayerState State,
    string? ScriptName,
    TimeSpan Position,
    TimeSpan Duration,
    IReadOnlyDictionary<string, double> Values,
    long Revision);
