using System.Text.Json;
using MultiFunPlayer.Application;

namespace MultiFunPlayer.Server;

public sealed record StatusResponse(
    string State,
    string? Script,
    double PositionSeconds,
    double DurationSeconds,
    IReadOnlyDictionary<string, double> Axes,
    long Revision);

public sealed record CapabilitiesResponse(
    string ApiVersion,
    int TickHz,
    string[] Commands,
    string[] Protocols,
    bool AuthenticationRequired,
    string WebSocketPath);

public sealed record SettingsResponse(
    int SchemaVersion,
    int TickHz,
    int MaxWebSocketClients,
    string ConfigRoot,
    string ScriptsRoot,
    string[] ScriptsRoots,
    string LogsRoot,
    string KeysRoot,
    bool AuthenticationRequired,
    FnosScriptAccessResponse FnosScriptAccess);

public sealed record FnosScriptAccessResponse(
    bool Available,
    string? SystemVersion,
    bool ExternalRootConfigured,
    bool ExternalRootActive,
    bool RestartRequired,
    int AuthorizedDirectoryCount,
    int MountedDirectoryCount,
    string ExternalRoot);

public sealed record SerialDeviceResponse(
    string StableId,
    string DevicePath,
    string? LinkTarget);

public sealed record MediaServerDiscoveryRequest(string? Kind, int TimeoutMilliseconds = 1_500);

public sealed record MediaServerDiscoveryResponse(
    string Kind,
    string? ServerId,
    string Name,
    string Endpoint,
    string SourceAddress,
    bool ApiKeyRequired,
    string ApiKeyUrl);

public sealed record ButtplugDeviceDiscoveryRequest(string? Endpoint, int ScanMilliseconds = 1_500);

public sealed record ButtplugDeviceDiscoveryResponse(
    uint DeviceIndex,
    string DeviceName,
    IReadOnlyList<string> ActuatorTypes);

public sealed record PairRequest(string? PairingToken, string? AuthToken);

public sealed record ScriptDescriptor(string Id, string Name, long Size, DateTimeOffset LastModifiedUtc);

public sealed record ScriptContentResponse(ScriptDescriptor Script, string Content);

public sealed record LoadScriptRequest(
    string? Id,
    string? Name,
    JsonElement Content,
    string? Axis);

public sealed record ScriptUploadRequest(string? Id, string? Content, bool Overwrite = false);
public sealed record ScriptFolderRequest(string? FolderPath);

public sealed record OutputTargetResponse(
    string Id,
    string Name,
    string Protocol,
    string Endpoint,
    string SourceAxis,
    int UpdateRateHz,
    bool Enabled,
    bool AutoConnect,
    bool Connected,
    string? LastError,
    long SentFrames,
    long DroppedFrames,
    long FailedFrames,
    long Reconnects,
    DateTimeOffset? LastSentAtUtc,
    string? DeviceName = null,
    uint? DeviceIndex = null,
    string ActuatorType = "Vibrate",
    uint ActuatorIndex = 0,
    int DurationMilliseconds = 50)
{
    public long CoalescedFrames { get; init; }
}

public sealed record OutputTargetRequest(
    string? Id,
    string? Name,
    string? Protocol,
    string? Endpoint,
    bool Enabled = true,
    bool AutoConnect = true,
    int BaudRate = 115200,
    string Parity = "None",
    int DataBits = 8,
    string StopBits = "One",
    bool DtrEnable = false,
    bool RtsEnable = false,
    string SourceAxis = "L0",
    int UpdateRateHz = 20,
    string? DeviceName = null,
    uint? DeviceIndex = null,
    string ActuatorType = "Vibrate",
    uint ActuatorIndex = 0,
    int DurationMilliseconds = 50);

public sealed record MediaSourceResponse(
    string Id,
    string Name,
    string Kind,
    string Endpoint,
    bool Enabled,
    bool AutoConnect,
    string Integration,
    string ImplementationStatus,
    bool Connected,
    string? LastError,
    MediaClockResponse? MediaState = null,
    string? DeviceId = null,
    MediaControlCapabilitiesResponse? Controls = null,
    bool HasSecretValue = false,
    string? SecretValueHint = null);

public sealed record MediaControlCapabilitiesResponse(
    bool CanPlayPause,
    bool CanSeek,
    bool CanSetPlaybackSpeed,
    bool CanLoop);

public sealed record MediaClockResponse(
    string? SessionId,
    string? DeviceId,
    string? MediaId,
    string? Path,
    bool Playing,
    double DurationSeconds,
    double PositionSeconds,
    double PlaybackSpeed,
    string? ContentType);

public sealed record MediaSourceRequest(
    string? Id,
    string? Name,
    string? Kind,
    string? Endpoint,
    bool Enabled = true,
    bool AutoConnect = true,
    string? DeviceId = null,
    string? SecretRef = null,
    string? SecretValue = null);

public sealed record SeekRequest(double PositionSeconds);

public sealed record PlaybackSpeedRequest(double PlaybackSpeed);

public sealed record RuntimeSourceRouteRequest(
    string? SourceId,
    string[]? OutputIds,
    bool LoopEnabled = false,
    string? ScriptId = null,
    string? ScriptMatchMode = "exact");

public sealed record RuntimeRoutesRequest(RuntimeSourceRouteRequest[]? Routes);

public sealed record RuntimeSourceRouteResponse(
    string SourceId,
    string[] OutputIds,
    bool LoopEnabled,
    string? ScriptId = null,
    string ScriptMatchMode = "global");

public static class ApiModelMapper
{
    public static StatusResponse ToStatus(EngineSnapshot snapshot)
        => new(snapshot.State.ToString(), snapshot.ScriptName,
            Math.Max(0, snapshot.Position.TotalSeconds),
            Math.Max(0, snapshot.Duration.TotalSeconds), snapshot.Values, snapshot.Revision);
}
