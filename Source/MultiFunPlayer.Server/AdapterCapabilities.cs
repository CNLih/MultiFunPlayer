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
            new("output.buttplug", "output", "external-proxy", "planned", "websocket", ["buttplug-server"], "Requires a separately managed Buttplug server; no fake CRUD is exposed here."),
            new("output.the-handy", "output", "direct", "ready", "https", ["MFP_THE_HANDY_CONNECTION_KEY"], "HTTP HDSP v2 adapter; the key is read from the host environment and never persisted or returned."),
            new("media.internal", "media", "direct", "contract-only", "file", [], "Headless script playback is available; a media-source repository and media clock adapter are still pending."),
            new("media.emby", "media", "direct", "planned", "https", [], "Network API integration is portable but not implemented in this server slice."),
            new("media.jellyfin", "media", "direct", "planned", "https", [], "Network API integration is portable but not implemented in this server slice."),
            new("media.plex", "media", "direct", "planned", "https", [], "Network API integration is portable but not implemented in this server slice."),
            new("media.mpc", "media", "external-proxy", "blocked", "process-ipc", ["windows-player"], "The current implementation relies on a Windows player process; use an external proxy until a Linux control path is defined."),
            new("media.mpv", "media", "external-proxy", "blocked", "process-ipc", ["mpv"], "The current implementation discovers/starts a local process and is not used by the headless server."),
            new("media.vlc", "media", "external-proxy", "blocked", "process-ipc", ["vlc"], "The current implementation is coupled to local player control and is not used by the headless server."),
            new("media.potplayer", "media", "unsupported", "unsupported", "process-ipc", ["windows-player"], "Windows-only process and window integration is not a Linux/fnOS target."),
            new("media.whirligig", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.deovr", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.heresphere", "media", "external-proxy", "blocked", "tcp", ["windows-player"], "The existing adapter assumes a local player process; no server adapter is wired."),
            new("media.ofs", "media", "direct", "ready", "websocket", [], "Implements the event/command WebSocket envelope observed in the desktop OFS adapter; no unobserved capability handshake is assumed.")
        ]);
}
