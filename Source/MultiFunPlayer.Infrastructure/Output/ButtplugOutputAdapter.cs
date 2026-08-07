using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>
/// Connection and actuator selection for a Buttplug protocol v3 server.
/// Secrets are not part of this record: authentication, when required by a
/// deployment, belongs to the separately managed WebSocket endpoint.
/// </summary>
public sealed record ButtplugOutputOptions
{
    public string SourceAxis { get; init; } = "L0";
    public string? DeviceName { get; init; }
    public uint? DeviceIndex { get; init; }
    public string ActuatorType { get; init; } = "Vibrate";
    public uint ActuatorIndex { get; init; }
    public int DurationMilliseconds { get; init; } = 50;
    public OutputTransportOptions Transport { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceAxis) || SourceAxis.Length > 64
            || SourceAxis.Any(character => !char.IsLetterOrDigit(character)
                && character is not ('_' or '-')))
            throw new ArgumentException("A valid source axis is required.", nameof(SourceAxis));
        if (!string.IsNullOrWhiteSpace(DeviceName) && DeviceName.Length > 256)
            throw new ArgumentException("Device name is too long.", nameof(DeviceName));
        if (string.IsNullOrWhiteSpace(ActuatorType) || ActuatorType.Length > 64
            || ActuatorType.Any(character => !char.IsLetterOrDigit(character)
                && character is not ('_' or '-')))
            throw new ArgumentException("A valid actuator type is required.", nameof(ActuatorType));
        if (DurationMilliseconds is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(DurationMilliseconds));
        (Transport ?? throw new ArgumentNullException(nameof(Transport))).Validate();
    }
}

/// <summary>Minimal device information advertised by a Buttplug server.</summary>
public sealed record ButtplugDeviceDescriptor(
    uint DeviceIndex,
    string DeviceName,
    IReadOnlyList<string> ActuatorTypes);

/// <summary>
/// Portable Buttplug WebSocket output. It implements only protocol-v3
/// messages observed at the wire boundary, and deliberately has no WPF,
/// Stylet, or Buttplug SDK dependency. The server remains responsible for
/// discovery and device safety policy.
/// </summary>
public sealed class ButtplugOutputAdapter : IOutputTargetAdapter, IAsyncDisposable
{
    private const int MessageVersion = 3;
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private readonly Uri _endpoint;
    private readonly ButtplugOutputOptions _options;
    private readonly Channel<OutputFrame> _queue;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _devicesGate = new();
    private readonly Dictionary<uint, ButtplugDeviceDescriptor> _devices = [];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveLoop;
    private Task? _worker;
    private TaskCompletionSource<int>? _serverInfo;
    private int _connected;
    private int _disposed;
    private int _messageId;

    public ButtplugOutputAdapter(string id, Uri endpoint,
        ButtplugOutputOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An output id is required.", nameof(id));
        Id = id;
        _endpoint = ValidateEndpoint(endpoint);
        _options = options ?? new ButtplugOutputOptions();
        _options.Validate();
        _queue = Channel.CreateBounded<OutputFrame>(new BoundedChannelOptions(_options.Transport.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public string Id { get; }
    public string Protocol => "buttplug";
    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public string? LastError { get; private set; }
    public event Action<Exception>? Faulted;

    public IReadOnlyList<ButtplugDeviceDescriptor> Devices
    {
        get
        {
            lock (_devicesGate)
                return _devices.Values.OrderBy(device => device.DeviceIndex).ToArray();
        }
    }

    public async ValueTask<MediaConnectionResult> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return new(true);

            await StopSessionCoreAsync(CancellationToken.None, sendStop: false).ConfigureAwait(false);
            ClearDevices();
            LastError = null;
            using var timeout = CreateTimeout(cancellationToken, _options.Transport.ConnectTimeout);
            var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);
                _socket = socket;
                _sessionCancellation = sessionCancellation;
                _serverInfo = NewServerInfoSource();
                _receiveLoop = ReceiveLoopAsync(socket, sessionCancellation.Token);

                await SendEnvelopeAsync("RequestServerInfo", new
                {
                    Id = NextMessageId(),
                    ClientName = "MultiFunPlayer",
                    MessageVersion
                }, timeout.Token).ConfigureAwait(false);

                var version = await _serverInfo.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (version < MessageVersion)
                    throw new InvalidDataException(
                        $"The Buttplug server only supports protocol version {version}; version {MessageVersion} is required.");

                Volatile.Write(ref _connected, 1);
                // Request the current inventory before starting a scan. This
                // covers already-connected devices while DeviceAdded handles
                // devices discovered during the scan.
                await SendEnvelopeAsync("RequestDeviceList", new { Id = NextMessageId() },
                    timeout.Token).ConfigureAwait(false);
                await SendEnvelopeAsync("StartScanning", new { Id = NextMessageId() },
                    timeout.Token).ConfigureAwait(false);
                _worker = ProcessQueueAsync(sessionCancellation.Token);
                if (!IsConnected)
                    throw new InvalidDataException(LastError ??
                        "The Buttplug server closed the protocol session during handshake.");
                return new(true);
            }
            catch
            {
                sessionCancellation.Cancel();
                socket.Dispose();
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                _sessionCancellation = null;
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _connected, 0);
            LastError = exception.Message;
            throw new OutputTransportException(Id, "connect", exception);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Publish(OutputFrame frame)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        if (!IsConnected)
            throw new OutputTransportException(Id, "send",
                new InvalidOperationException("The Buttplug adapter is not connected."));
        if (!_queue.Writer.TryWrite(frame))
            throw new OutputTransportQueueFullException(Id);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopSessionCoreAsync(cancellationToken, sendStop: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopSessionCoreAsync(CancellationToken.None, sendStop: true).ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            _sendGate.Dispose();
            _lifecycle.Dispose();
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var timeout = CreateTimeout(cancellationToken, _options.Transport.SendTimeout);
                    var device = SelectDevice();
                    if (device is null)
                        continue;
                    var value = ReadAxisValue(frame);
                    await SendActuatorCommandAsync(device, value, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    HandleFault(exception);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendActuatorCommandAsync(ButtplugDeviceDescriptor device,
        double value, CancellationToken cancellationToken)
    {
        var type = _options.ActuatorType;
        if (type.Equals("Linear", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Position", StringComparison.OrdinalIgnoreCase))
        {
            await SendEnvelopeAsync("LinearCmd", new
            {
                Id = NextMessageId(),
                DeviceIndex = device.DeviceIndex,
                Vectors = new[]
                {
                    new { Index = _options.ActuatorIndex,
                        Duration = _options.DurationMilliseconds,
                        Position = value }
                }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (type.Equals("Rotate", StringComparison.OrdinalIgnoreCase))
        {
            var speed = Math.Clamp(Math.Abs(value - 0.5) / 0.5, 0, 1);
            await SendEnvelopeAsync("RotateCmd", new
            {
                Id = NextMessageId(),
                DeviceIndex = device.DeviceIndex,
                Rotations = new[]
                {
                    new { Index = _options.ActuatorIndex,
                        Speed = speed,
                        Clockwise = value > 0.5 }
                }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendEnvelopeAsync("ScalarCmd", new
        {
            Id = NextMessageId(),
            DeviceIndex = device.DeviceIndex,
            Scalars = new[]
            {
                new { Index = _options.ActuatorIndex,
                    Scalar = value,
                    ActuatorType = type }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopSessionCoreAsync(CancellationToken cancellationToken, bool sendStop)
    {
        var socket = _socket;
        var sessionCancellation = _sessionCancellation;
        var worker = _worker;
        Volatile.Write(ref _connected, 0);
        _worker = null;
        _sessionCancellation = null;
        _serverInfo?.TrySetCanceled();
        _serverInfo = null;

        if (sendStop && socket is { State: WebSocketState.Open })
        {
            var devices = Devices;
            foreach (var device in devices)
            {
                try
                {
                    using var timeout = CreateTimeout(cancellationToken, _options.Transport.SendTimeout);
                    await SendEnvelopeAsync("StopDeviceCmd", new
                    {
                        Id = NextMessageId(),
                        DeviceIndex = device.DeviceIndex
                    }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or WebSocketException
                    or OperationCanceledException)
                {
                    // Disconnect remains best effort; the server will stop
                    // sending to the socket when its close frame is observed.
                }
            }
            try
            {
                using var timeout = CreateTimeout(cancellationToken, _options.Transport.SendTimeout);
                await SendEnvelopeAsync("StopScanning", new { Id = NextMessageId() },
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException
                or OperationCanceledException) { }
        }

        sessionCancellation?.Cancel();
        if (worker is not null)
        {
            try { await worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        _receiveLoop = null;
        if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException
                or OperationCanceledException) { }
        }
        socket?.Dispose();
        if (ReferenceEquals(_socket, socket))
            _socket = null;
        sessionCancellation?.Dispose();
        DrainQueue();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Buttplug messages must be JSON arrays.");
                foreach (var envelope in root.EnumerateArray())
                {
                    if (envelope.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Buttplug message envelopes must be objects.");
                    HandleEnvelope(envelope);
                }
            }
            if (!cancellationToken.IsCancellationRequested && IsConnected)
                HandleFault(new IOException("The Buttplug WebSocket closed unexpectedly."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                HandleFault(exception);
        }
    }

    private void HandleEnvelope(JsonElement envelope)
    {
        if (TryGetProperty(envelope, "ServerInfo", out var serverInfo))
        {
            var version = GetInt32(serverInfo, "MessageVersion")
                ?? throw new InvalidDataException("Buttplug ServerInfo is missing MessageVersion.");
            _serverInfo?.TrySetResult(version);
            return;
        }
        if (TryGetProperty(envelope, "DeviceAdded", out var deviceAdded))
        {
            var descriptor = ParseDevice(deviceAdded);
            lock (_devicesGate)
                _devices[descriptor.DeviceIndex] = descriptor;
            return;
        }
        if (TryGetProperty(envelope, "DeviceList", out var deviceList))
        {
            if (TryGetProperty(deviceList, "Devices", out var devices)
                && devices.ValueKind == JsonValueKind.Array)
            {
                lock (_devicesGate)
                {
                    foreach (var device in devices.EnumerateArray())
                    {
                        var descriptor = ParseDevice(device);
                        _devices[descriptor.DeviceIndex] = descriptor;
                    }
                }
            }
            return;
        }
        if (TryGetProperty(envelope, "DeviceRemoved", out var deviceRemoved))
        {
            if (GetUInt32(deviceRemoved, "DeviceIndex") is { } index)
            {
                lock (_devicesGate)
                    _devices.Remove(index);
            }
            return;
        }
        if (TryGetProperty(envelope, "Error", out var error))
        {
            var message = GetString(error, "ErrorMessage") ?? "Buttplug server returned an error.";
            _serverInfo?.TrySetException(new InvalidOperationException(message));
            if (IsConnected)
                HandleFault(new InvalidOperationException(message));
        }
    }

    private async ValueTask SendEnvelopeAsync(string name, object payload,
        CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The Buttplug WebSocket is not open.");
        var envelope = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [name] = payload
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { envelope }, JsonOptions);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var output = new ArrayBufferWriter<byte>();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Buttplug binary WebSocket messages are not supported.");
                if (output.WrittenCount + result.Count > MaxMessageBytes)
                    throw new InvalidDataException("Buttplug message exceeds the configured size limit.");
                output.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                    return output.WrittenMemory.ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ButtplugDeviceDescriptor? SelectDevice()
    {
        lock (_devicesGate)
        {
            if (_options.DeviceIndex is { } index)
                return _devices.TryGetValue(index, out var byIndex) ? byIndex : null;
            if (!string.IsNullOrWhiteSpace(_options.DeviceName))
                return _devices.Values.FirstOrDefault(device =>
                    string.Equals(device.DeviceName, _options.DeviceName, StringComparison.OrdinalIgnoreCase));
            return _devices.Values.OrderBy(device => device.DeviceIndex).FirstOrDefault();
        }
    }

    private double ReadAxisValue(OutputFrame frame)
    {
        if (!frame.Values.TryGetValue(_options.SourceAxis, out var value))
        {
            var pair = frame.Values.FirstOrDefault(item =>
                string.Equals(item.Key, _options.SourceAxis, StringComparison.OrdinalIgnoreCase));
            value = string.IsNullOrEmpty(pair.Key) ? 0 : pair.Value;
        }
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private void HandleFault(Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        Volatile.Write(ref _connected, 0);
        LastError = exception.Message;
        // Stop both background loops after an unsolicited protocol failure. The
        // lifecycle owner remains responsible for closing and disposing the
        // socket, so this method never waits on a loop that may be the caller.
        _sessionCancellation?.Cancel();
        try { Faulted?.Invoke(exception); }
        catch { }
    }

    private void ClearDevices()
    {
        lock (_devicesGate)
            _devices.Clear();
    }

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out _)) { }
    }

    private int NextMessageId()
    {
        var next = Interlocked.Increment(ref _messageId);
        if (next == int.MaxValue)
            Interlocked.Exchange(ref _messageId, 1);
        return next;
    }

    private static ButtplugDeviceDescriptor ParseDevice(JsonElement value)
    {
        var index = GetUInt32(value, "DeviceIndex")
            ?? throw new InvalidDataException("Buttplug device is missing DeviceIndex.");
        var name = GetString(value, "DeviceName") ?? $"Device {index}";
        var actuatorTypes = new List<string>();
        if (TryGetProperty(value, "DeviceMessages", out var messages)
            && messages.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in messages.EnumerateObject())
                actuatorTypes.Add(property.Name);
        }
        return new(index, name, actuatorTypes);
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property))
            return true;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }
            }
        }
        property = default;
        return false;
    }

    private static string? GetString(JsonElement value, string name)
        => TryGetProperty(value, name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static uint? GetUInt32(JsonElement value, string name)
        => TryGetProperty(value, name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt32(out var result) ? result : null;

    private static int? GetInt32(JsonElement value, string name)
        => TryGetProperty(value, name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var result) ? result : null;

    private static TaskCompletionSource<int> NewServerInfoSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException(
                "Buttplug output requires an absolute ws:// or wss:// endpoint without credentials or query data.",
                nameof(endpoint));
        return endpoint;
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
