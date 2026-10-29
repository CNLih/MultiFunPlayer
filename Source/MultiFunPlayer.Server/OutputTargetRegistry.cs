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
    bool RtsEnable = false,
    string SourceAxis = "L0",
    int UpdateRateHz = 20,
    string? DeviceName = null,
    uint? DeviceIndex = null,
    string ActuatorType = "Vibrate",
    uint ActuatorIndex = 0,
    int DurationMilliseconds = 50);

/// <summary>
/// Owns configured output transports and their engine sink registrations.
/// Definitions are persisted below the administrator-controlled config root;
/// runtime connection failures are reported without stopping the host.
/// </summary>
public sealed class OutputTargetRegistry : IHostedService, IAsyncDisposable
{
    private const string StoreFile = "output-targets.json";
    private static readonly HashSet<string> SupportedProtocols =
        new(StringComparer.OrdinalIgnoreCase)
        { "tcp", "udp", "websocket", "file", "serial", "the-handy", "buttplug" };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RuntimeTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runtimeRoutedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageOptions _storage;
    private readonly HeadlessPlayerEngine _engine;
    private readonly ILogger<OutputTargetRegistry> _logger;
    private readonly int _engineTickHz;
    private readonly Func<string?> _theHandyConnectionKeyProvider;
    private readonly Func<HttpMessageHandler?>? _theHandyHandlerFactory;
    private readonly RootedFileStore? _config;
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public OutputTargetRegistry(StorageOptions storage, HeadlessPlayerEngine engine,
        ILogger<OutputTargetRegistry> logger, Func<string?>? theHandyConnectionKeyProvider = null,
        Func<HttpMessageHandler?>? theHandyHandlerFactory = null,
        IConfiguration? configuration = null)
    {
        _storage = storage;
        _engine = engine;
        _logger = logger;
        _engineTickHz = configuration is null ? 20 : ServerOptions.ResolveTickHz(configuration);
        _theHandyConnectionKeyProvider = theHandyConnectionKeyProvider
            ?? (() => Environment.GetEnvironmentVariable("MFP_THE_HANDY_CONNECTION_KEY"));
        _theHandyHandlerFactory = theHandyHandlerFactory;
        try
        {
            _config = new RootedFileStore(storage.ConfigRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Output target persistence is unavailable.");
        }
    }

    public event Action? Changed;

    public bool HasConnectedRoute(IEnumerable<string> outputIds)
    {
        _gate.Wait();
        try { return outputIds.Any(id => _targets.TryGetValue(id, out var target)
            && target.Sink is not null && (target.Transport?.IsConnected == true || target.Adapter?.IsConnected == true)); }
        finally { _gate.Release(); }
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

    public bool Contains(string id)
    {
        _gate.Wait();
        try { return _targets.ContainsKey(id); }
        finally { _gate.Release(); }
    }

    public async Task SetRuntimeRoutedOutputsAsync(IEnumerable<string> outputIds,
        CancellationToken cancellationToken = default)
    {
        var routed = new HashSet<string>(outputIds, StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _runtimeRoutedOutputs.Clear();
            _runtimeRoutedOutputs.UnionWith(routed);
            foreach (var target in _targets.Values)
            {
                if (_runtimeRoutedOutputs.Contains(target.Definition.Id))
                {
                    target.Registration?.Dispose();
                    target.Registration = null;
                }
                else
                {
                    RegisterWithEngine(target);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool PublishRouted(string outputId, OutputFrame frame)
    {
        _gate.Wait();
        try
        {
            if (!_targets.TryGetValue(outputId, out var target) || target.Sink is null
                || !_runtimeRoutedOutputs.Contains(outputId))
                return false;
            target.Sink.Publish(frame);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public int PublishPose(OutputFrame frame, string? targetId = null)
    {
        _gate.Wait();
        try
        {
            var sent = 0;
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                if (_targets.TryGetValue(targetId, out var target) && target.Sink is not null)
                {
                    target.Sink.Publish(frame);
                    sent++;
                }
                return sent;
            }

            foreach (var target in _targets.Values)
            {
                if (target.Sink is not null && (target.Transport?.IsConnected == true || target.Adapter?.IsConnected == true))
                {
                    try
                    {
                        target.Sink.Publish(frame);
                        sent++;
                    }
                    catch { }
                }
            }
            return sent;
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

    public async Task<OutputTargetOperation> TestAsync(OutputTargetDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateDefinition(definition);
        if (validation is not null)
            return new(OutputTargetOperationStatus.Invalid, Error: validation);
        var target = new RuntimeTarget(definition with { Enabled = true, AutoConnect = false });
        try
        {
            var connected = await ConnectLockedAsync(target, cancellationToken, registerSink: false)
                .ConfigureAwait(false);
            return new(connected ? OutputTargetOperationStatus.Success : OutputTargetOperationStatus.Failed,
                ToResponse(target), connected ? null : target.LastError);
        }
        finally
        {
            await DisconnectLockedAsync(target, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<OutputTargetOperation> TestAsync(string id,
        CancellationToken cancellationToken = default)
    {
        OutputTargetDefinition definition;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.TryGetValue(id, out var target))
                return new(OutputTargetOperationStatus.NotFound, Error: "Output target not found.");
            definition = target.Definition;
        }
        finally
        {
            _gate.Release();
        }
        return await TestAsync(definition, cancellationToken).ConfigureAwait(false);
    }

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

    private async Task<bool> ConnectLockedAsync(RuntimeTarget target, CancellationToken cancellationToken,
        bool registerSink = true)
    {
        if (target.LastError is null
            && (target.Transport?.IsConnected == true || target.Adapter?.IsConnected == true))
        {
            target.LastError = null;
            return true;
        }

        if (target.Transport is not null || target.Adapter is not null || target.Sink is not null)
            await DisconnectLockedAsync(target, cancellationToken).ConfigureAwait(false);

        IOutputTransport? transport = null;
        IOutputTargetAdapter? adapter = null;
        try
        {
            if (string.Equals(target.Definition.Protocol, "the-handy", StringComparison.OrdinalIgnoreCase))
            {
                var connectionKey = _theHandyConnectionKeyProvider();
                if (string.IsNullOrWhiteSpace(connectionKey))
                    throw new InvalidOperationException(
                        "MFP_THE_HANDY_CONNECTION_KEY is required for The Handy output.");
                adapter = new TheHandyOutputAdapter(target.Definition.Id,
                    ParseTheHandyUri(target.Definition.Endpoint), connectionKey,
                    string.IsNullOrWhiteSpace(target.Definition.SourceAxis) ? "L0" : target.Definition.SourceAxis,
                    _theHandyHandlerFactory?.Invoke());
                var result = await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Connected)
                    throw new InvalidOperationException(result.Error ?? "The Handy connection failed.");
                adapter.Faulted += exception =>
                {
                    target.LastError = exception.Message;
                    target.Registration?.Dispose();
                    target.Registration = null;
                };
                target.Adapter = adapter;
                if (registerSink)
                {
                    var sink = new RateLimitedOutputSink(adapter, target.Definition.UpdateRateHz,
                        _engineTickHz);
                    target.Sink = sink;
                    RegisterWithEngine(target);
                }
            }
            else if (string.Equals(target.Definition.Protocol, "buttplug", StringComparison.OrdinalIgnoreCase))
            {
                adapter = new ButtplugOutputAdapter(target.Definition.Id,
                    ParseButtplugUri(target.Definition.Endpoint), new ButtplugOutputOptions
                    {
                        SourceAxis = string.IsNullOrWhiteSpace(target.Definition.SourceAxis)
                            ? "L0" : target.Definition.SourceAxis,
                        DeviceName = target.Definition.DeviceName,
                        DeviceIndex = target.Definition.DeviceIndex,
                        ActuatorType = target.Definition.ActuatorType,
                        ActuatorIndex = target.Definition.ActuatorIndex,
                        DurationMilliseconds = target.Definition.DurationMilliseconds
                    });
                var result = await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Connected)
                    throw new InvalidOperationException(result.Error ?? "The Buttplug connection failed.");
                adapter.Faulted += exception =>
                {
                    target.LastError = exception.Message;
                    target.Registration?.Dispose();
                    target.Registration = null;
                };
                target.Adapter = adapter;
                if (registerSink)
                {
                    var sink = new RateLimitedOutputSink(adapter, target.Definition.UpdateRateHz,
                        _engineTickHz);
                    target.Sink = sink;
                    RegisterWithEngine(target);
                }
            }
            else
            {
                transport = CreateTransport(target.Definition);
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                target.Transport = transport;
                if (registerSink)
                {
                    var sink = new RateLimitedOutputSink(new TCodeOutputSink(transport),
                        target.Definition.UpdateRateHz, _engineTickHz);
                    sink.Faulted += exception =>
                    {
                        target.LastError = exception.Message;
                        target.Registration?.Dispose();
                        target.Registration = null;
                        // The sink owns disposal of the failed transport. Mark it
                        // unavailable so the next connect call rebuilds both.
                        target.Transport = null;
                    };
                    target.Sink = sink;
                    RegisterWithEngine(target);
                }
            }
            if (target.HasConnected)
                target.Reconnects++;
            target.HasConnected = true;
            Changed?.Invoke();
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
            if (adapter is not null)
                await adapter.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private void RegisterWithEngine(RuntimeTarget target)
    {
        if (target.Sink is not null && target.Registration is null
            && !_runtimeRoutedOutputs.Contains(target.Definition.Id))
        {
            target.Registration = _engine.AddSink(target.Sink);
            var snapshot = _engine.Current;
            if (snapshot.Values.Count > 0)
                target.Sink.Publish(new OutputFrame(snapshot.Position, snapshot.Values));
        }
    }

    private async Task DisconnectLockedAsync(RuntimeTarget target, CancellationToken cancellationToken)
    {
        CaptureDiagnostics(target);
        target.Registration?.Dispose();
        target.Registration = null;
        if (target.Sink is not null)
        {
            if (target.Sink is IAsyncDisposable asyncSink)
                await asyncSink.DisposeAsync().ConfigureAwait(false);
            else if (target.Sink is IDisposable disposableSink)
                disposableSink.Dispose();
        }
        else if (target.Adapter is not null)
            await target.Adapter.DisposeAsync().ConfigureAwait(false);
        else if (target.Transport is not null)
            await target.Transport.DisposeAsync().ConfigureAwait(false);
        target.Sink = null;
        target.Adapter = null;
        target.Transport = null;
        Changed?.Invoke();
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
                case "the-handy":
                    ParseTheHandyUri(definition.Endpoint);
                    ValidateSourceAxis(definition.SourceAxis);
                    break;
                case "buttplug":
                    ParseButtplugUri(definition.Endpoint);
                    new ButtplugOutputOptions
                    {
                        SourceAxis = definition.SourceAxis,
                        DeviceName = definition.DeviceName,
                        DeviceIndex = definition.DeviceIndex,
                        ActuatorType = definition.ActuatorType,
                        ActuatorIndex = definition.ActuatorIndex,
                        DurationMilliseconds = definition.DurationMilliseconds
                    }.Validate();
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
            return "protocol must be tcp, udp, websocket, file, serial, the-handy, or buttplug.";
        if (string.IsNullOrWhiteSpace(definition.Endpoint) || definition.Endpoint.Length > 2048)
            return "endpoint is required and must be at most 2048 characters.";
        if (ValidateSourceAxis(definition.SourceAxis) is not null)
            return "sourceAxis must contain only letters, numbers, '-' or '_', and be at most 64 characters.";
        if (definition.UpdateRateHz is < 1 or > 20)
            return "updateRateHz must be between 1 and 20.";
        return null;
    }

    private static Uri ParseTheHandyUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException(
                "The Handy output requires an absolute http(s) endpoint without credentials.", nameof(endpoint));
        return uri;
    }

    private static Uri ParseButtplugUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException(
                "Buttplug output requires an absolute ws(s) endpoint without credentials or query data.",
                nameof(endpoint));
        return uri;
    }

    private static string? ValidateSourceAxis(string sourceAxis)
    {
        // Empty means "all axes": for TCode-family transports the full frame is
        // sent anyway, so an empty source axis is valid. Single-channel devices
        // (The Handy / Buttplug) resolve an empty value to L0 at connect time.
        if (string.IsNullOrWhiteSpace(sourceAxis))
            return null;
        if (sourceAxis.Length > 64
            || sourceAxis.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-')))
            return "invalid source axis";
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
    {
        var diagnostics = target.Sink as IOutputSinkDiagnostics;
        var errorDiagnostics = target.Sink as IOutputSinkErrorDiagnostics;
        var sentFrames = target.SentFrames + (diagnostics?.SentFrames ?? 0);
        var droppedFrames = target.DroppedFrames + (diagnostics?.DroppedFrames ?? 0);
        var failedFrames = target.FailedFrames + (diagnostics?.FailedFrames ?? 0);
        var lastSentAtUtc = diagnostics?.LastSentAtUtc is { } activeLastSent
            && (target.LastSentAtUtc is null || activeLastSent > target.LastSentAtUtc.Value)
            ? activeLastSent
            : target.LastSentAtUtc;
        return new(target.Definition.Id, target.Definition.Name, target.Definition.Protocol,
            target.Definition.Endpoint, target.Definition.SourceAxis,
            target.Definition.UpdateRateHz,
            target.Definition.Enabled, target.Definition.AutoConnect,
            target.Transport?.IsConnected == true || target.Adapter?.IsConnected == true,
            target.Adapter?.LastError ?? errorDiagnostics?.LastError ?? target.LastError,
            sentFrames, droppedFrames, failedFrames, target.Reconnects, lastSentAtUtc,
            target.Definition.DeviceName, target.Definition.DeviceIndex,
            target.Definition.ActuatorType, target.Definition.ActuatorIndex,
            target.Definition.DurationMilliseconds)
        {
            CoalescedFrames = droppedFrames
        };
    }

    private static void CaptureDiagnostics(RuntimeTarget target)
    {
        if (target.Sink is not IOutputSinkDiagnostics diagnostics)
        {
            if (target.Sink is IOutputSinkErrorDiagnostics errorOnly
                && errorOnly.LastError is { } error)
                target.LastError = error;
            return;
        }
        target.SentFrames += diagnostics.SentFrames;
        target.DroppedFrames += diagnostics.DroppedFrames;
        target.FailedFrames += diagnostics.FailedFrames;
        if (diagnostics.LastSentAtUtc is { } lastSent
            && (target.LastSentAtUtc is null || lastSent > target.LastSentAtUtc.Value))
            target.LastSentAtUtc = lastSent;
        if (target.Sink is IOutputSinkErrorDiagnostics errorDiagnostics
            && errorDiagnostics.LastError is { } lastError)
            target.LastError = lastError;
    }

    private sealed class RuntimeTarget(OutputTargetDefinition definition)
    {
        public OutputTargetDefinition Definition { get; } = definition;
        public IOutputTransport? Transport { get; set; }
        public IOutputTargetAdapter? Adapter { get; set; }
        public IOutputSink? Sink { get; set; }
        public IDisposable? Registration { get; set; }
        public string? LastError { get; set; }
        public bool HasConnected { get; set; }
        public long Reconnects { get; set; }
        public long SentFrames { get; set; }
        public long DroppedFrames { get; set; }
        public long FailedFrames { get; set; }
        public DateTimeOffset? LastSentAtUtc { get; set; }
    }
}
