namespace MultiFunPlayer.Server;

public sealed class ServerOptions
{
    public const string SectionName = "MultiFunPlayer";

    public string ScriptsPath { get; set; } = "scripts";

    /// <summary>Maximum host tick rate. Values above 20 are capped.</summary>
    public int TickHz { get; set; } = 20;

    public int MaxWebSocketClients { get; set; } = 16;

    public string? AuthToken { get; set; }

    public static int ResolveTickHz(IConfiguration configuration)
        => ResolveBoundedInt(configuration, "MFP_TICK_HZ", "MultiFunPlayer:TickHz", 20, 1, 20);

    public static int ResolveMaxWebSocketClients(IConfiguration configuration)
        => ResolveBoundedInt(configuration, "MFP_MAX_WEBSOCKET_CLIENTS", "MultiFunPlayer:MaxWebSocketClients", 16, 1, 128);

    public static int ResolveLogCapacity(IConfiguration configuration)
        => ResolveBoundedInt(configuration, "MFP_LOG_CAPACITY", "MultiFunPlayer:LogCapacity", 500, 50, 10_000);

    private static int ResolveBoundedInt(IConfiguration configuration, string environmentKey,
        string configurationKey, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(environmentKey);
        if (int.TryParse(raw, out var environmentValue))
            return Math.Clamp(environmentValue, minimum, maximum);
        return Math.Clamp(configuration.GetValue(configurationKey, fallback), minimum, maximum);
    }
}
