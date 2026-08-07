using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Output;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Server;

public enum OutputTargetOperationStatus
{
    Success,
    Invalid,
    NotFound,
    Conflict,
    Unavailable,
    Failed
}

public sealed record OutputTargetOperation(
    OutputTargetOperationStatus Status,
    OutputTargetResponse? Target = null,
    string? Error = null);

public sealed record OutputTargetDefinition(
    string Id,
    string Name,
    string Protocol,
    string Endpoint,
    bool Enabled = true,
    bool AutoConnect = true,
    int BaudRate = 115200,
    string Parity = "None",
    int DataBits = 8,
    string StopBits = "One",
    bool DtrEnable = false,
    bool RtsEnable = false);

/// <summary>
/// Owns configured output transports and their engine sink registrations.
/// Definitions are persisted below the administrator-controlled config root;
/// runtime connection failures are reported without stopping the host.
/// </summary>
public sealed class OutputTargetRegistry : IHostedService, IAsyncDisposable
{
    private const string StoreFile = "output-targets.json";
    private static readonly HashSet<string> SupportedProtocols =
        new(StringComparer.OrdinalIgnoreCase) { "tcp", "udp", "websocket", "file", "serial" };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RuntimeTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageOptions _storage;
    private readonly HeadlessPlayerEngine _engine;
    private readonly ILogger<OutputTargetRegistry> _logger;
    private readonly RootedFileStore? _config;
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public OutputTargetRegistry(StorageOptions storage, HeadlessPlayerEngine engine,
        ILogger<OutputTargetRegistry> logger)
    {
        _storage = storage;
        _engine = engine;
        _logger = logger;
        try
        {
            _config = new RootedFileStore(storage.ConfigRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Output target persistence is unavailable.");
        }
    }

    public IReadOnlyList<OutputTargetResponse> List()
    {
        _gate.Wait();
        try
        {
            return _targets.Values.Select(ToResponse).OrderBy(target => target.Id,
                StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return;
            _started = true;
            if (_config is null)
                return;

            List<OutputTargetDefinition>? definitions;
            try
            {
                definitions = await _config.ReadJsonAsync<List<OutputTargetDefinition>>(StoreFile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                definitions = [];
            }
            catch (JsonException exception)
            {
                _logger.LogError(exception, "Ignoring malformed output target configuration.");
                definitions = [];
            }

            foreach (var definition in definitions ?? [])
            {
                var validation = ValidateDefinition(definition);
                if (validation is not null)
                {
                    _logger.LogWarning("Ignoring output target {TargetId}: {Reason}", definition.Id, validation);
                    continue;
                }

                var runtime = new RuntimeTarget(definition);
                _targets.Add(definition.Id, runtime);
                if (definition.Enabled && definition.AutoConnect)
                    await ConnectLockedAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutputTargetOperation> CreateAsync(OutputTargetDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateDefinition(definition);
        if (validation is not null)
            return new(OutputTargetOperationStatus.Invalid, Error: validation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_targets.ContainsKey(definition.Id))
                return new(OutputTargetOperationStatus.Conflict,
                    Error: "An output target with this id already exists.");
            if (_config is null)
                return new(OutputTargetOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");

            var runtime = new RuntimeTarget(definition);
            _targets.Add(definition.Id, runtime);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            if (definition.Enabled && definition.AutoConnect)
                await ConnectLockedAsync(runtime, cancellationToken).ConfigureAwait(false);
            return new(OutputTargetOperationStatus.Success, ToResponse(runtime));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _targets.Remove(definition.Id);
            return new(OutputTargetOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OutputTargetOperation> UpdateAsync(string id, OutputTargetDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(id, definition.Id, StringComparison.OrdinalIgnoreCase))
            return new(OutputTargetOperationStatus.Invalid, Error: "The route id must match the target id.");
        var validation = ValidateDefinition(definition);
        if (validation is not null)
            return new(OutputTargetOperationStatus.Invalid, Error: validation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.TryGetValue(id, out var current))
                return new(OutputTargetOperationStatus.NotFound, Error: "Output target not found.");
            if (_config is null)
                return new(OutputTargetOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");

            await DisconnectLockedAsync(current, cancellationToken).ConfigureAwait(false);
            var replacement = new RuntimeTarget(definition);
            _targets[id] = replacement;
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            if (definition.Enabled && definition.AutoConnect)
                await ConnectLockedAsync(replacement, cancellationToken).ConfigureAwait(false);
            return new(OutputTargetOperationStatus.Success, ToResponse(replacement));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(OutputTargetOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OutputTargetOperation> DeleteAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.Remove(id, out var target))
                return new(OutputTargetOperationStatus.NotFound, Error: "Output target not found.");
            await DisconnectLockedAsync(target, cancellationToken).ConfigureAwait(false);
            if (_config is null)
                return new(OutputTargetOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            return new(OutputTargetOperationStatus.Success);
        }
        catch (IOException exception)
        {
            return new(OutputTargetOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<OutputTargetOperation> ConnectAsync(string id, CancellationToken cancellationToken = default)
        => RunLifecycleAsync(id, connect: true, cancellationToken);

    public Task<OutputTargetOperation> DisconnectAsync(string id, CancellationToken cancellationToken = default)
        => RunLifecycleAsync(id, connect: false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    private async Task<OutputTargetOperation> RunLifecycleAsync(string id, bool connect,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.TryGetValue(id, out var target))
                return new(OutputTargetOperationStatus.NotFound, Error: "Output target not found.");
            var connected = true;
            if (connect)
                connected = await ConnectLockedAsync(target, cancellationToken).ConfigureAwait(false);
            else
                await DisconnectLockedAsync(target, cancellationToken).ConfigureAwait(false);
            return new(connected ? OutputTargetOperationStatus.Success : OutputTargetOperationStatus.Failed,
                ToResponse(target), connected ? null : target.LastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped)
                return;
            _stopped = true;
            foreach (var target in _targets.Values)
                await DisconnectLockedAsync(target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ConnectLockedAsync(RuntimeTarget target, CancellationToken cancellationToken)
    {
        if (target.Transport?.IsConnected == true)
        {
            target.LastError = null;
            return true;
        }

        IOutputTransport? transport = null;
        try
        {
            transport = CreateTransport(target.Definition);
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var sink = new TCodeOutputSink(transport);
            target.Registration = _engine.AddSink(sink);
            target.Transport = transport;
            target.Sink = sink;
            target.LastError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SocketException or InvalidOperationException or ArgumentException or UriFormatException)
        {
            target.LastError = exception.Message;
            _logger.LogWarning(exception, "Output target {TargetId} failed to connect.", target.Definition.Id);
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static async Task DisconnectLockedAsync(RuntimeTarget target, CancellationToken cancellationToken)
    {
        target.Registration?.Dispose();
        target.Registration = null;
        if (target.Sink is not null)
            await target.Sink.DisposeAsync().ConfigureAwait(false);
        else if (target.Transport is not null)
            await target.Transport.DisposeAsync().ConfigureAwait(false);
        target.Sink = null;
        target.Transport = null;
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new IOException("The configured root is not writable.");
        var definitions = _targets.Values.Select(target => target.Definition)
            .OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        await _config.WriteJsonAsync(StoreFile, definitions, cancellationToken).ConfigureAwait(false);
    }

    private IOutputTransport CreateTransport(OutputTargetDefinition definition)
    {
        var options = new OutputTransportOptions();
        return definition.Protocol.ToLowerInvariant() switch
        {
            "tcp" => CreateHostPortTransport(definition, tcp: true, options),
            "udp" => CreateHostPortTransport(definition, tcp: false, options),
            "websocket" => new WebSocketOutputTransport(definition.Id, ParseWebSocketUri(definition.Endpoint), options),
            "file" => new FileOutputTransport(definition.Id, ResolveFileEndpoint(definition.Endpoint), options),
            "serial" => new SerialOutputTransport(definition.Id, ValidateSerialEndpoint(definition.Endpoint),
                CreateSerialSettings(definition), options),
            _ => throw new ArgumentException("Unsupported output protocol.", nameof(definition))
        };
    }

    private IOutputTransport CreateHostPortTransport(OutputTargetDefinition definition, bool tcp,
        OutputTransportOptions options)
    {
        var (host, port) = ParseHostPort(definition.Endpoint);
        return tcp
            ? new TcpOutputTransport(definition.Id, host, port, options)
            : new UdpOutputTransport(definition.Id, host, port, options);
    }

    private string ResolveFileEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || Path.IsPathRooted(endpoint)
            || endpoint.Contains('\0') || endpoint.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("File output paths must be relative to the logs root.", nameof(endpoint));
        var root = Path.GetFullPath(_storage.LogsRoot);
        var path = Path.GetFullPath(Path.Combine(root, endpoint));
        if (!IsWithin(path, root) || IsReparsePoint(path))
            throw new UnauthorizedAccessException("File output path escapes the logs root.");
        return path;
    }

    private static string ValidateSerialEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Contains('\0')
            || (!endpoint.StartsWith("/dev/serial/by-id/", StringComparison.Ordinal)
                && !endpoint.StartsWith("/dev/tty", StringComparison.Ordinal)
                && !endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Serial output requires a stable Linux device path or COM name.", nameof(endpoint));
        return endpoint;
    }

    private static SerialPortSettings CreateSerialSettings(OutputTargetDefinition definition)
    {
        if (!Enum.TryParse<Parity>(definition.Parity, true, out var parity)
            || !Enum.TryParse<StopBits>(definition.StopBits, true, out var stopBits))
            throw new ArgumentException("Invalid serial line settings.", nameof(definition));
        var settings = new SerialPortSettings
        {
            BaudRate = definition.BaudRate,
            Parity = parity,
            DataBits = definition.DataBits,
            StopBits = stopBits,
            DtrEnable = definition.DtrEnable,
            RtsEnable = definition.RtsEnable
        };
        settings.Validate();
        return settings;
    }

    private static Uri ParseWebSocketUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("WebSocket output requires a ws:// or wss:// endpoint.", nameof(endpoint));
        return uri;
    }

    private static (string Host, int Port) ParseHostPort(string endpoint)
    {
        if (!Uri.TryCreate($"tcp://{endpoint}", UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("TCP/UDP output requires host:port.", nameof(endpoint));
        return (uri.Host, uri.Port);
    }

    private string? ValidateDefinition(OutputTargetDefinition definition)
    {
        var basic = Validate(definition);
        if (basic is not null)
            return basic;
        try
        {
            switch (definition.Protocol.ToLowerInvariant())
            {
                case "tcp":
                case "udp":
                    ParseHostPort(definition.Endpoint);
                    break;
                case "websocket":
                    ParseWebSocketUri(definition.Endpoint);
                    break;
                case "file":
                    ResolveFileEndpoint(definition.Endpoint);
                    break;
                case "serial":
                    ValidateSerialEndpoint(definition.Endpoint);
                    CreateSerialSettings(definition);
                    break;
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException
            or UriFormatException)
        {
            return exception.Message;
        }
    }

    private static string? Validate(OutputTargetDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Id.Length > 64
            || definition.Id.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            return "id must contain only letters, numbers, '-' or '_', and be at most 64 characters.";
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 120)
            return "name is required and must be at most 120 characters.";
        if (!SupportedProtocols.Contains(definition.Protocol))
            return "protocol must be tcp, udp, websocket, file, or serial.";
        if (string.IsNullOrWhiteSpace(definition.Endpoint) || definition.Endpoint.Length > 2048)
            return "endpoint is required and must be at most 2048 characters.";
        return null;
    }

    private static bool IsWithin(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
        => (File.Exists(path) || Directory.Exists(path))
            && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static OutputTargetResponse ToResponse(RuntimeTarget target)
        => new(target.Definition.Id, target.Definition.Name, target.Definition.Protocol,
            target.Definition.Endpoint, target.Definition.Enabled, target.Definition.AutoConnect,
            target.Transport?.IsConnected == true, target.LastError);

    private sealed class RuntimeTarget(OutputTargetDefinition definition)
    {
        public OutputTargetDefinition Definition { get; } = definition;
        public IOutputTransport? Transport { get; set; }
        public TCodeOutputSink? Sink { get; set; }
        public IDisposable? Registration { get; set; }
        public string? LastError { get; set; }
    }
}
