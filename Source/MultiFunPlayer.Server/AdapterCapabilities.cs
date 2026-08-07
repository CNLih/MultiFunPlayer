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
            new("tcp", "direct", "planned", ["linux", "windows", "fnOS"], "Portable stream transport; server output adapter is not wired yet."),
            new("udp", "direct", "planned", ["linux", "windows", "fnOS"], "Portable datagram transport; server output adapter is not wired yet."),
            new("websocket", "direct", "planned", ["linux", "windows", "fnOS"], "Portable WebSocket transport; state WebSocket is available, output adapter is not wired yet."),
            new("serial", "direct", "planned", ["linux", "windows", "fnOS"], "Requires a host serial device and permission; no server output adapter is wired yet."),
            new("file", "direct", "planned", ["linux", "windows", "fnOS"], "Uses a configured writable directory; no server output adapter is wired yet.")
        ],
        [
            new("output.tcp", "output", "direct", "planned", "tcp", [], "Maps to the existing TCP output design after the output boundary is extracted."),
            new("output.udp", "output", "direct", "planned", "udp", [], "Maps to the existing UDP output design after the output boundary is extracted."),
            new("output.websocket", "output", "direct", "planned", "websocket", [], "Maps to the existing WebSocket output design after the output boundary is extracted."),
            new("output.serial", "output", "direct", "planned", "serial", [], "Requires serial-device discovery, permissions, and a platform adapter."),
            new("output.file", "output", "direct", "planned", "file", [], "Requires a configured storage volume and a platform-neutral writer."),
            new("output.pipe", "output", "unsupported", "unsupported", null, [], "The current named-pipe output is retained by the Windows host and is not a fnOS contract."),
            new("output.buttplug", "output", "external-proxy", "planned", "websocket", ["buttplug-server"], "Requires a separately managed Buttplug server; no fake CRUD is exposed here."),
            new("output.the-handy", "output", "unsupported", "unsupported", "https", [], "The existing WPF/HTTP implementation has not been ported to the headless host."),
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
            new("media.ofs", "media", "direct", "planned", "websocket", [], "A portable WebSocket protocol is plausible but the current server has no OFS adapter.")
        ]);
}
