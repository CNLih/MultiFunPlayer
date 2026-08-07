namespace MultiFunPlayer.Server;

/// <summary>
/// Versioned, read-only integration inventory. "Direct" describes the
/// intended transport boundary, not a claim that an adapter is already wired
/// into the headless host; ImplementationStatus is explicit for that reason.
/// </summary>
public sealed record AdapterCapabilitiesResponse(
    string ApiVersion,
    string SchemaVersion,
    IReadOnlyList<TransportCapability> Transports,
    IReadOnlyList<AdapterCapability> Adapters);

public sealed record TransportCapability(
    string Id,
    string Integration,
    string ImplementationStatus,
    string[] Platforms,
    string Notes);

public sealed record AdapterCapability(
    string Id,
    string Kind,
    string Integration,
    string ImplementationStatus,
    string? Transport,
    string[] RequiredServices,
    string Notes);

public static class AdapterCapabilityCatalog
{
    public static AdapterCapabilitiesResponse Current { get; } = new(
        "v1",
        "1.0",
        [
            new("tcp", "direct", "ready", ["linux", "windows", "fnOS"], "Portable stream transport; registry lifecycle is wired and bounded."),
            new("udp", "direct", "ready", ["linux", "windows", "fnOS"], "Portable datagram transport; registry lifecycle is wired and bounded."),
            new("websocket", "direct", "ready", ["linux", "windows", "fnOS"], "Portable WebSocket output transport; endpoint availability remains external."),
            new("buttplug", "direct", "ready", ["linux", "windows", "fnOS"], "Portable Buttplug protocol-v3 WebSocket output; a reachable Buttplug server remains an external dependency."),
            new("serial", "direct", "ready", ["linux", "windows", "fnOS"], "Requires a host serial device and permission; stable paths are validated."),
            new("file", "direct", "ready", ["linux", "windows", "fnOS"], "Uses the configured writable logs directory and bounded writer."),
            new("the-handy", "direct", "ready", ["linux", "windows", "fnOS"], "HTTP HDSP v2 output; requires MFP_THE_HANDY_CONNECTION_KEY and a reachable device."),
            new("ofs-websocket", "direct", "ready", ["linux", "windows", "fnOS"], "OFS WebSocket media adapter; endpoint and protocol validation remain explicit.")
        ],
        [
            new("output.tcp", "output", "direct", "ready", "tcp", [], "Bounded TCP transport is wired through the output registry."),
            new("output.udp", "output", "direct", "ready", "udp", [], "Bounded UDP transport is wired through the output registry."),
            new("output.websocket", "output", "direct", "ready", "websocket", [], "Bounded WebSocket transport is wired through the output registry."),
            new("output.serial", "output", "direct", "ready", "serial", [], "Requires serial-device discovery, permissions, and a stable device path."),
            new("output.file", "output", "direct", "ready", "file", [], "Uses the configured logs volume and rejects path escape."),
            new("output.pipe", "output", "unsupported", "unsupported", null, [], "The current named-pipe output is retained by the Windows host and is not a fnOS contract."),
            new("output.buttplug", "output", "direct", "ready", "websocket", ["buttplug-server"], "Protocol-v3 WebSocket adapter with bounded output queue; device discovery and actuator selection are server-side."),
            new("output.the-handy", "output", "direct", "ready", "https", ["MFP_THE_HANDY_CONNECTION_KEY"], "HTTP HDSP v2 adapter; the key is read from the host environment and never persisted or returned."),
            new("media.internal", "media", "direct", "ready", "file", [], "Headless script playback and the internal media clock are wired through the server registry."),
            new("media.emby", "media", "direct", "ready", "https", ["MFP_EMBY_API_KEY"], "Reads /System/Ping and /Sessions; the API key is resolved from the host environment."),
            new("media.jellyfin", "media", "direct", "ready", "https", ["MFP_JELLYFIN_API_KEY"], "Reads /System/Ping and /Sessions; the API key is resolved from the host environment."),
            new("media.plex", "media", "direct", "ready", "https", ["MFP_PLEX_TOKEN"], "Reads the timeline and metadata APIs; the token is resolved from the host environment."),
            new("media.mpc", "media", "direct", "ready", "http", [], "Polls the MPC-HC/BE /variables.html contract and sends play, pause, and seek commands over HTTP; the player must expose its web interface."),
            new("media.mpv", "media", "external-proxy", "blocked", "process-ipc", ["mpv"], "The current implementation discovers/starts a local process and is not used by the headless server."),
            new("media.vlc", "media", "direct", "ready", "http", [], "Polls VLC /requests/status.json and /requests/playlist.json and sends the documented HTTP commands; the optional password is read from MFP_VLC_PASSWORD when configured."),
            new("media.potplayer", "media", "unsupported", "unsupported", "process-ipc", ["windows-player"], "Windows-only process and window integration is not a Linux/fnOS target."),
            new("media.whirligig", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.deovr", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.heresphere", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.ofs", "media", "direct", "ready", "websocket", [], "Implements the event/command WebSocket envelope observed in the desktop OFS adapter; no unobserved capability handshake is assumed.")
        ]);
}
