namespace MultiFunPlayer.Infrastructure.Capabilities;

public sealed record InfrastructureCapability(
    string Id,
    string DisplayName,
    bool Available,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> ConfigurationKeys);

public static class InfrastructureCapabilities
{
    public static IReadOnlyList<InfrastructureCapability> All { get; } =
    [
        new("storage", "Rooted JSON/file storage", true, ["linux", "windows", "container"],
            ["MFP_CONFIG_ROOT", "MFP_SCRIPTS_ROOT", "MFP_LOGS_ROOT"]),
        new("tcp", "TCode over TCP", true, ["linux", "windows", "container"], []),
        new("udp", "TCode over UDP", true, ["linux", "windows", "container"], []),
        new("websocket", "TCode over WebSocket", true, ["linux", "windows", "container"], []),
        new("ofs-websocket", "OFS media events over WebSocket", true,
            ["linux", "windows", "container"], []),
        new("file", "TCode file sink", true, ["linux", "windows", "container"], []),
        new("the-handy", "The Handy HDSP over HTTP", true, ["linux", "windows", "container"],
            ["MFP_THE_HANDY_CONNECTION_KEY"]),
        new("serial-enumeration", "Linux stable serial device discovery", true, ["linux", "container"],
            ["MFP_SERIAL_BY_ID_ROOT"]),
        new("serial", "TCode over serial", true, ["linux", "windows", "container"],
            ["MFP_SERIAL_BY_ID_ROOT"])
    ];
}
