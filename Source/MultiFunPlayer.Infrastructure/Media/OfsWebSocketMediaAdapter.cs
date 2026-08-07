using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MultiFunPlayer.Infrastructure.Media;

/// <summary>
/// Connection state exposed by the portable OFS adapter. OFS does not expose a
/// capability handshake in the protocol used by the desktop client, so a
/// successful WebSocket upgrade is the only connection capability asserted.
/// </summary>
public enum OfsConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

public enum OfsMediaEventKind
{
    MediaChanged,
    PlaybackChanged,
    DurationChanged,
    PositionChanged,
    PlaybackSpeedChanged,
    FunscriptChanged,
    FunscriptRemoved,
    ProjectChanged,
    ProtocolError
}

public sealed record OfsMediaState(
    string? Path,
    bool Playing,
    TimeSpan Duration,
    TimeSpan Position,
    double PlaybackSpeed)
{
    public static OfsMediaState Empty { get; } = new(null, false, TimeSpan.Zero, TimeSpan.Zero, 1d);
}

/// <summary>
/// A normalized projection of one OFS event. Funscript JSON is kept as text so
/// the application layer can choose the appropriate script reader and axis
/// mapping without making the transport depend on WPF types.
/// </summary>
public sealed record OfsMediaEvent(
    OfsMediaEventKind Kind,
    OfsMediaState State,
    string? Path = null,
    string? ScriptName = null,
    string? FunscriptJson = null,
    string? Error = null);

public sealed record OfsWebSocketAdapterOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxMessageBytes { get; init; } = 1 * 1024 * 1024;
    public int ReceiveBufferBytes { get; init; } = 16 * 1024;

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (CloseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CloseTimeout));
        if (KeepAliveInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval));
        if (MaxMessageBytes < 256 || MaxMessageBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        if (ReceiveBufferBytes < 256 || ReceiveBufferBytes > MaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferBytes));
    }
}

public interface IOfsMediaSourceAdapterFactory
{
    OfsWebSocketMediaAdapter Create(Uri endpoint);
}

public sealed class OfsMediaSourceAdapterFactory(OfsWebSocketAdapterOptions? options = null)
    : IOfsMediaSourceAdapterFactory
{
    private readonly OfsWebSocketAdapterOptions _options = options ?? new();

    public OfsWebSocketMediaAdapter Create(Uri endpoint)
        => new(endpoint, _options);
}

/// <summary>
/// Portable client for the OFS WebSocket event/command envelope observed in
/// the original OfsMediaSource. It intentionally implements only that known
/// protocol and does not invent a handshake or remote API.
/// </summary>
public sealed class OfsWebSocketMediaAdapter : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _gate = new();
    private readonly Uri _endpoint;
    private readonly OfsWebSocketAdapterOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private OfsConnectionState _state;
    private OfsMediaState _current = OfsMediaState.Empty;
    private string? _lastError;
    private int _disposed;

    public OfsWebSocketMediaAdapter(Uri endpoint, OfsWebSocketAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("OFS endpoint must use ws:// or wss://.", nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Credentials must not be embedded in an OFS endpoint.", nameof(endpoint));

        _endpoint = endpoint;
        _options = options ?? new();
        _options.Validate();
        _state = OfsConnectionState.Disconnected;
    }

    public Uri Endpoint => _endpoint;

    public OfsConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    public OfsMediaState Current
    {
        get { lock (_gate) return _current; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Raised for recognized OFS events and protocol errors.</summary>
    public event Action<OfsMediaEvent>? MessageReceived;

    public event Action<OfsConnectionState>? ConnectionStateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_state == OfsConnectionState.Connected)
                return;
            if (_state == OfsConnectionState.Connecting)
                throw new InvalidOperationException("The OFS adapter is already connecting.");
            _lastError = null;
            _state = OfsConnectionState.Connecting;
        }
        RaiseConnectionState(OfsConnectionState.Connecting);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        var socket = new ClientWebSocket
        {
            Options = { KeepAliveInterval = _options.KeepAliveInterval }
        };

        try
        {
            await socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfDisposedLocked(socket);
                _socket = socket;
                _receiveCancellation = new CancellationTokenSource();
                _state = OfsConnectionState.Connected;
                _receiveTask = ReceiveLoopAsync(socket, _receiveCancellation.Token);
            }
            RaiseConnectionState(OfsConnectionState.Connected);
        }
        catch (OperationCanceledException exception)
        {
            socket.Dispose();
            TransitionToFaulted(exception.Message);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new OfsAdapterException("connect", _endpoint, exception);
        }
        catch (Exception exception)
        {
            socket.Dispose();
            TransitionToFaulted(exception.Message);
            throw new OfsAdapterException("connect", _endpoint, exception);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        CancellationTokenSource? receiveCancellation;
        Task? receiveTask;
        lock (_gate)
        {
            if (_state == OfsConnectionState.Disconnected && _socket is null)
                return;
            socket = _socket;
            receiveCancellation = _receiveCancellation;
            receiveTask = _receiveTask;
            _socket = null;
            _receiveCancellation = null;
            _receiveTask = null;
            _state = OfsConnectionState.Disconnected;
            _lastError = null;
        }

        receiveCancellation?.Cancel();
        if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CloseTimeout);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (WebSocketException) { }
            catch (InvalidOperationException) { }
        }

        if (receiveTask is not null && receiveTask.Id != Task.CurrentId)
        {
            try { await receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }

        socket?.Dispose();
        receiveCancellation?.Dispose();
        RaiseConnectionState(OfsConnectionState.Disconnected);
    }

    public Task SendPlayAsync(bool playing, CancellationToken cancellationToken = default)
        => SendCommandAsync("change_play", new { playing }, cancellationToken);

    public Task SendSeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero || !double.IsFinite(position.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(position));
        return SendCommandAsync("change_time", new { time = position.TotalSeconds }, cancellationToken);
    }

    public Task SendPlaybackSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(speed) || speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed));
        return SendCommandAsync("change_playbackspeed", new { speed }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { _sendGate.Dispose(); }
    }

    private async Task SendCommandAsync(string name, object data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ClientWebSocket socket;
        lock (_gate)
        {
            socket = _socket ?? throw new InvalidOperationException("The OFS adapter is not connected.");
            if (socket.State != WebSocketState.Open)
                throw new InvalidOperationException("The OFS WebSocket is not open.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "command",
            name,
            data
        }, JsonOptions);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            TransitionToFaulted(exception.Message);
            throw new OfsAdapterException("send", _endpoint, exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferBytes);
        var message = new ArrayBufferWriter<byte>(Math.Min(_options.ReceiveBufferBytes, _options.MaxMessageBytes));
        var discard = false;
        var messageBytes = 0;
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    discard = true;
                }
                else if (!discard)
                {
                    if (result.Count > _options.MaxMessageBytes - messageBytes)
                    {
                        RaiseProtocolError("OFS message exceeds the configured size limit.");
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (WebSocketException) { }
                        break;
                    }

                    var span = message.GetSpan(result.Count);
                    buffer.AsSpan(0, result.Count).CopyTo(span);
                    message.Advance(result.Count);
                    messageBytes += result.Count;
                }

                if (!result.EndOfMessage)
                    continue;

                if (discard)
                {
                    RaiseProtocolError("OFS binary WebSocket messages are not supported.");
                    discard = false;
                    messageBytes = 0;
                    message = new ArrayBufferWriter<byte>(Math.Min(_options.ReceiveBufferBytes, _options.MaxMessageBytes));
                    continue;
                }

                try
                {
                    var text = StrictUtf8.GetString(message.WrittenSpan);
                    HandleMessage(text);
                }
                catch (DecoderFallbackException exception)
                {
                    RaiseProtocolError($"OFS message is not valid UTF-8: {exception.Message}");
                }
                catch (JsonException exception)
                {
                    RaiseProtocolError($"OFS message is not valid JSON: {exception.Message}");
                }

                messageBytes = 0;
                message = new ArrayBufferWriter<byte>(Math.Min(_options.ReceiveBufferBytes, _options.MaxMessageBytes));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failure = exception;
            RaiseProtocolError($"OFS WebSocket receive failed: {exception.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await HandleReceiveEndedAsync(socket, failure).ConfigureAwait(false);
        }
    }

    private void HandleMessage(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetString(root, "type", out var type)
            || !string.Equals(type, "event", StringComparison.OrdinalIgnoreCase))
        {
            RaiseProtocolError("OFS messages must be event envelopes with type=event.");
            return;
        }

        if (!TryGetString(root, "name", out var name)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            RaiseProtocolError("OFS event envelopes require a name and object data.");
            return;
        }

        switch (name.ToLowerInvariant())
        {
            case "media_change":
                var path = GetOptionalBoundedString(data, "path", 4096);
                UpdateState(OfsMediaEventKind.MediaChanged, state => state with { Path = path }, path: path);
                break;
            case "play_change":
                if (!TryGetBoolean(data, "playing", out var playing))
                {
                    RaiseProtocolError("play_change requires boolean data.playing.");
                    return;
                }
                UpdateState(OfsMediaEventKind.PlaybackChanged, state => state with { Playing = playing });
                break;
            case "duration_change":
                if (!TryGetFiniteNumber(data, "duration", out var duration) || duration < 0)
                {
                    RaiseProtocolError("duration_change requires a finite non-negative data.duration.");
                    return;
                }
                UpdateState(OfsMediaEventKind.DurationChanged,
                    state => state with { Duration = TimeSpan.FromSeconds(duration) });
                break;
            case "time_change":
                if (!TryGetFiniteNumber(data, "time", out var position) || position < 0)
                {
                    RaiseProtocolError("time_change requires a finite non-negative data.time.");
                    return;
                }
                UpdateState(OfsMediaEventKind.PositionChanged,
                    state => state with { Position = TimeSpan.FromSeconds(position) });
                break;
            case "playbackspeed_change":
                if (!TryGetFiniteNumber(data, "speed", out var speed) || speed <= 0)
                {
                    RaiseProtocolError("playbackspeed_change requires a finite positive data.speed.");
                    return;
                }
                UpdateState(OfsMediaEventKind.PlaybackSpeedChanged,
                    state => state with { PlaybackSpeed = speed });
                break;
            case "funscript_change":
                var scriptName = GetBoundedScriptName(data, "name");
                if (scriptName is null || !data.TryGetProperty("funscript", out var funscript)
                    || funscript.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    RaiseProtocolError("funscript_change requires a valid name and object/array data.funscript.");
                    return;
                }
                Emit(new OfsMediaEvent(OfsMediaEventKind.FunscriptChanged, Current,
                    ScriptName: scriptName, FunscriptJson: funscript.GetRawText()));
                break;
            case "funscript_remove":
                var removedName = GetBoundedScriptName(data, "name");
                if (removedName is null)
                {
                    RaiseProtocolError("funscript_remove requires a non-empty data.name.");
                    return;
                }
                Emit(new OfsMediaEvent(OfsMediaEventKind.FunscriptRemoved, Current,
                    ScriptName: removedName));
                break;
            case "project_change":
                lock (_gate)
                    _current = OfsMediaState.Empty;
                Emit(new OfsMediaEvent(OfsMediaEventKind.ProjectChanged, Current));
                break;
            default:
                RaiseProtocolError($"Unsupported OFS event '{name}'.");
                break;
        }
    }

    private void UpdateState(OfsMediaEventKind kind, Func<OfsMediaState, OfsMediaState> update,
        string? path = null)
    {
        OfsMediaState state;
        lock (_gate)
        {
            _current = update(_current);
            _lastError = null;
            state = _current;
        }
        Emit(new OfsMediaEvent(kind, state, path));
    }

    private async Task HandleReceiveEndedAsync(ClientWebSocket socket, Exception? failure)
    {
        bool changed;
        OfsConnectionState next;
        lock (_gate)
        {
            if (!ReferenceEquals(socket, _socket))
                return;
            _socket = null;
            _receiveCancellation?.Dispose();
            _receiveCancellation = null;
            _receiveTask = null;
            next = failure is null ? OfsConnectionState.Disconnected : OfsConnectionState.Faulted;
            changed = _state != next;
            _state = next;
            _lastError = failure?.Message;
        }
        if (changed)
            RaiseConnectionState(next);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void TransitionToFaulted(string error)
    {
        bool changed;
        lock (_gate)
        {
            _lastError = error;
            changed = _state != OfsConnectionState.Faulted;
            _state = OfsConnectionState.Faulted;
        }
        if (changed)
            RaiseConnectionState(OfsConnectionState.Faulted);
    }

    private void RaiseProtocolError(string error)
    {
        lock (_gate)
            _lastError = error;
        Emit(new OfsMediaEvent(OfsMediaEventKind.ProtocolError, Current, Error: error));
    }

    private void Emit(OfsMediaEvent message)
    {
        var handlers = MessageReceived;
        if (handlers is null)
            return;
        foreach (Action<OfsMediaEvent> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch { /* A subscriber must not terminate the receive loop. */ }
        }
    }

    private void RaiseConnectionState(OfsConnectionState state)
    {
        var handlers = ConnectionStateChanged;
        if (handlers is null)
            return;
        foreach (Action<OfsConnectionState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch { /* A subscriber must not terminate connection cleanup. */ }
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string? GetOptionalBoundedString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException($"data.{name} must be a string.");
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw new JsonException($"data.{name} is too long or contains control characters.");
        return value;
    }

    private static string? GetBoundedScriptName(JsonElement element, string name)
    {
        var value = GetOptionalBoundedString(element, name, 256);
        if (value is null)
            return null;
        if (!Path.HasExtension(value)
            || !string.Equals(Path.GetExtension(value), ".funscript", StringComparison.OrdinalIgnoreCase))
            value += ".funscript";
        return value;
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (element.TryGetProperty(name, out property) && property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetFiniteNumber(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OfsWebSocketMediaAdapter));
    }

    private void ThrowIfDisposedLocked(ClientWebSocket socket)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            socket.Dispose();
            throw new ObjectDisposedException(nameof(OfsWebSocketMediaAdapter));
        }
    }
}

public sealed class OfsAdapterException(string operation, Uri endpoint, Exception innerException)
    : IOException($"OFS WebSocket operation '{operation}' failed for '{endpoint}'.", innerException)
{
    public string Operation { get; } = operation;
    public Uri Endpoint { get; } = endpoint;
}
