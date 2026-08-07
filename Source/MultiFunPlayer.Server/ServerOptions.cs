namespace MultiFunPlayer.Server;

public sealed class ServerOptions
{
    public const string SectionName = "MultiFunPlayer";

    public string ScriptsPath { get; set; } = "scripts";

    /// <summary>Maximum host tick rate. Values above 20 are capped.</summary>
    public int TickHz { get; set; } = 20;

    public int MaxWebSocketClients { get; set; } = 16;

    public string? AuthToken { get; set; }
}
