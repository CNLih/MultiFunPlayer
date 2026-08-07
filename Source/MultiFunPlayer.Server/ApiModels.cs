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
    string LogsRoot,
    string KeysRoot,
    bool AuthenticationRequired);

public sealed record SerialDeviceResponse(
    string StableId,
    string DevicePath,
    string? LinkTarget);

public sealed record PairRequest(string? PairingToken, string? AuthToken);

public sealed record ScriptDescriptor(string Id, string Name, long Size, DateTimeOffset LastModifiedUtc);

public sealed record ScriptContentResponse(ScriptDescriptor Script, string Content);

public sealed record LoadScriptRequest(
    string? Id,
    string? Name,
    JsonElement Content,
    string? Axis);

public sealed record ScriptUploadRequest(string? Id, string? Content, bool Overwrite = false);

public sealed record OutputTargetResponse(
    string Id,
    string Name,
    string Protocol,
    string Endpoint,
    string SourceAxis,
    bool Enabled,
    bool AutoConnect,
    bool Connected,
    string? LastError);

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
    string SourceAxis = "L0");

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
    string? LastError);

public sealed record MediaSourceRequest(
    string? Id,
    string? Name,
    string? Kind,
    string? Endpoint,
    bool Enabled = true,
    bool AutoConnect = true);

public sealed record SeekRequest(double PositionSeconds);

public static class ApiModelMapper
{
    public static StatusResponse ToStatus(EngineSnapshot snapshot)
        => new(snapshot.State.ToString(), snapshot.ScriptName,
            Math.Max(0, snapshot.Position.TotalSeconds),
            Math.Max(0, snapshot.Duration.TotalSeconds), snapshot.Values, snapshot.Revision);
}
