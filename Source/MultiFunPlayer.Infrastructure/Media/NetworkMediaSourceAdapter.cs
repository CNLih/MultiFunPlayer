using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using MultiFunPlayer.Application;

namespace MultiFunPlayer.Infrastructure.Media;

/// <summary>Network-native media protocols supported by the headless host.</summary>
public enum NetworkMediaSourceKind
{
    Emby,
    Jellyfin,
    Plex,
    Vlc,
    Mpc
}

public enum NetworkMediaConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

/// <summary>
/// A normalized media clock projection. The adapter only reports what the
/// remote server actually returned; an empty state is valid when a server has
/// no active playback session.
/// </summary>
public sealed record NetworkMediaState(
    string? SessionId,
    string? DeviceId,
    string? MediaId,
    string? Path,
    bool Playing,
    TimeSpan Duration,
    TimeSpan Position,
    double PlaybackSpeed,
    string? ContentType = null)
{
    public static NetworkMediaState Empty { get; } = new(
        null, null, null, null, false, TimeSpan.Zero, TimeSpan.Zero, 1d);
}

public sealed record NetworkMediaSourceAdapterOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (MaxResponseBytes < 1024 || MaxResponseBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
    }
}

public interface INetworkMediaSourceAdapterFactory
{
    NetworkMediaSourceAdapter Create(NetworkMediaSourceKind kind, Uri endpoint,
        string? token, string? deviceId = null,
        HttpMessageHandler? handler = null);
}

public sealed class NetworkMediaSourceAdapterFactory(NetworkMediaSourceAdapterOptions? options = null)
    : INetworkMediaSourceAdapterFactory
{
    private readonly NetworkMediaSourceAdapterOptions _options = options ?? new();

    public NetworkMediaSourceAdapter Create(NetworkMediaSourceKind kind, Uri endpoint,
        string? token, string? deviceId = null, HttpMessageHandler? handler = null)
        => new(kind, endpoint, token, deviceId, handler, _options);
}

/// <summary>
/// Portable HTTP adapter for Emby, Jellyfin, Plex, VLC, and MPC-HC/BE.
///
/// Connection validates the protocol's session/timeline endpoint and then
/// starts bounded polling. It does not claim a media clock merely because a
/// server health endpoint answered. Credentials are supplied by the host and
/// never included in the endpoint or persisted definition.
/// </summary>
public sealed class NetworkMediaSourceAdapter : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };
    private static readonly Regex MpcVariableRegex = new(
        @"<p\s+id=['""](?<name>[^'""]{1,64})['""]\s*>(?<value>.*?)</p>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly NetworkMediaSourceKind _kind;
    private readonly Uri _endpoint;
    private readonly string? _token;
    private readonly string? _deviceId;
    private readonly NetworkMediaSourceAdapterOptions _options;
    private readonly HttpClient _client;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private NetworkMediaState _current = NetworkMediaState.Empty;
    private NetworkMediaConnectionState _connectionState = NetworkMediaConnectionState.Disconnected;
    private string? _lastError;
    private string? _plexMetadataKey;
    private string? _plexPath;
    private TimeSpan _plexDuration;
    private string? _plexType;
    private long _plexCommandId;
    private long _vlcPlaylistId = long.MinValue;
    private string? _vlcPath;
    private int _vlcApiVersion;
    private int _disposed;

    public NetworkMediaSourceAdapter(NetworkMediaSourceKind kind, Uri endpoint,
        string? token, string? deviceId = null, HttpMessageHandler? handler = null,
        NetworkMediaSourceAdapterOptions? options = null)
    {
        _kind = kind;
        _endpoint = ValidateEndpoint(endpoint);
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        _options = options ?? new();
        _options.Validate();
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MultiFunPlayer", "headless"));
    }

    public NetworkMediaSourceKind Kind => _kind;
    public Uri Endpoint => _endpoint;
    public string Protocol => _kind.ToString().ToLowerInvariant();
    public NetworkMediaConnectionState ConnectionState
    {
        get { lock (_stateGate) return _connectionState; }
    }
    public bool IsConnected => ConnectionState == NetworkMediaConnectionState.Connected;
    public NetworkMediaState Current
    {
        get { lock (_stateGate) return _current; }
    }
    public string? LastError
    {
        get { lock (_stateGate) return _lastError; }
    }

    public event Action<NetworkMediaState>? StateChanged;
    public event Action<NetworkMediaConnectionState>? ConnectionStateChanged;
    public event Action<Exception>? Faulted;

    public async ValueTask<MediaConnectionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return new(true);
            SetConnectionState(NetworkMediaConnectionState.Connecting, null);
            if (_kind is NetworkMediaSourceKind.Emby or NetworkMediaSourceKind.Jellyfin
                && string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("An API key is required through the configured secret environment variable.");
            if (_kind == NetworkMediaSourceKind.Plex && string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("A Plex token is required through the configured secret environment variable.");

            await StopPollAsync(CancellationToken.None).ConfigureAwait(false);
            _plexMetadataKey = null;
            _plexPath = null;
            _plexDuration = TimeSpan.Zero;
            _plexType = null;
            _vlcPlaylistId = long.MinValue;
            _vlcPath = null;
            _vlcApiVersion = 0;
            var state = await PollOnceAsync(cancellationToken, includeHandshake: true).ConfigureAwait(false);
            SetCurrent(state);
            _pollCancellation = new CancellationTokenSource();
            _pollTask = PollLoopAsync(_pollCancellation.Token);
            SetConnectionState(NetworkMediaConnectionState.Connected, null);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetConnectionState(NetworkMediaConnectionState.Disconnected, null);
            throw;
        }
        catch (Exception exception)
        {
            SetConnectionState(NetworkMediaConnectionState.Faulted, exception.Message);
            throw new NetworkMediaSourceAdapterException(Protocol, "connect", exception);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopPollAsync(cancellationToken).ConfigureAwait(false);
            SetConnectionState(NetworkMediaConnectionState.Disconnected, null);
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
                await StopPollAsync(CancellationToken.None).ConfigureAwait(false);
                SetConnectionState(NetworkMediaConnectionState.Disconnected, null);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        finally
        {
            _client.Dispose();
            _lifecycle.Dispose();
        }
    }

    public Task SendPlayAsync(bool playing, CancellationToken cancellationToken = default)
        => SendPlaybackCommandAsync(playing, cancellationToken);

    public Task SendSeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero || !double.IsFinite(position.TotalMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(position));
        return _kind switch
        {
            NetworkMediaSourceKind.Emby or NetworkMediaSourceKind.Jellyfin
                => SendJsonSessionCommandAsync("Seek", new Dictionary<string, string>
                {
                    ["SeekPositionTicks"] = Ticks(position).ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, cancellationToken),
            NetworkMediaSourceKind.Plex => SendPlexCommandAsync("seekTo", new Dictionary<string, string>
            {
                ["offset"] = ((long)position.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken),
            NetworkMediaSourceKind.Vlc => SendVlcCommandAsync("seek", new Dictionary<string, string>
            {
                ["val"] = ((long)position.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken),
            NetworkMediaSourceKind.Mpc => SendMpcCommandAsync(-1, new Dictionary<string, string>
            {
                ["position"] = position.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported media source kind.")
        };
    }

    private Task SendPlaybackCommandAsync(bool playing, CancellationToken cancellationToken)
        => _kind switch
        {
            NetworkMediaSourceKind.Emby or NetworkMediaSourceKind.Jellyfin
                => SendJsonSessionCommandAsync(playing ? "Unpause" : "Pause", null, cancellationToken),
            NetworkMediaSourceKind.Plex
                => SendPlexCommandAsync(playing ? "play" : "pause", null, cancellationToken),
            NetworkMediaSourceKind.Vlc
                => SendVlcCommandAsync(playing ? "pl_forceresume" : "pl_forcepause", null, cancellationToken),
            NetworkMediaSourceKind.Mpc
                => SendMpcCommandAsync(playing ? 887 : 888, null, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported media source kind.")
        };

    private async Task SendJsonSessionCommandAsync(string command, IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var sessionId = Current.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("The media server has no active playback session.");
        var suffix = query is null || query.Count == 0
            ? string.Empty
            : "?" + string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = CreateRequest(HttpMethod.Post, $"/Sessions/{Uri.EscapeDataString(sessionId)}/Playing/{command}{suffix}");
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPlexCommandAsync(string command, IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var type = _plexType ?? Current.ContentType;
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("The Plex server has no active playback timeline.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["commandID"] = Interlocked.Increment(ref _plexCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (query is not null)
            foreach (var pair in query)
                values[pair.Key] = pair.Value;
        var path = "/player/playback/" + command + "?" + string.Join("&",
            values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendVlcCommandAsync(string command, IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["command"] = command };
        if (query is not null)
            foreach (var pair in query)
                values[pair.Key] = pair.Value;
        var path = "/requests/status.json?" + string.Join("&",
            values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMpcCommandAsync(int command, IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wm_command"] = command.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (query is not null)
            foreach (var pair in query)
                values[pair.Key] = pair.Value;
        var path = "/command.html?" + string.Join("&",
            values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var state = await PollOnceAsync(cancellationToken, includeHandshake: false).ConfigureAwait(false);
                SetCurrent(state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetConnectionState(NetworkMediaConnectionState.Faulted, exception.Message);
            Faulted?.Invoke(exception);
        }
    }

    private async Task<NetworkMediaState> PollOnceAsync(CancellationToken cancellationToken, bool includeHandshake)
        => _kind switch
        {
            NetworkMediaSourceKind.Emby or NetworkMediaSourceKind.Jellyfin
                => await PollJsonServerAsync(cancellationToken, includeHandshake).ConfigureAwait(false),
            NetworkMediaSourceKind.Plex => await PollPlexAsync(cancellationToken).ConfigureAwait(false),
            NetworkMediaSourceKind.Vlc => await PollVlcAsync(cancellationToken, includeHandshake).ConfigureAwait(false),
            NetworkMediaSourceKind.Mpc => await PollMpcAsync(cancellationToken, includeHandshake).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported media source kind.")
        };

    private async Task<NetworkMediaState> PollJsonServerAsync(CancellationToken cancellationToken,
        bool includeHandshake)
    {
        if (includeHandshake)
        {
            using var ping = CreateRequest(HttpMethod.Get, "/System/Ping");
            using var pingResponse = await SendCheckedAsync(ping, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var payload = await ReadBoundedAsync(pingResponse, cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0 && !LooksLikeJsonObject(payload))
                throw new InvalidDataException("The media server ping response was not valid JSON.");
        }

        using var request = CreateRequest(HttpMethod.Get, "/Sessions");
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The media server sessions response was not an array.");

        foreach (var session in document.RootElement.EnumerateArray())
        {
            var sessionDevice = GetString(session, "DeviceId");
            if (_deviceId is not null && !string.Equals(_deviceId, sessionDevice, StringComparison.Ordinal))
                continue;
            var item = GetObject(session, "NowPlayingItem");
            if (item is null)
                continue;
            var path = GetString(item.Value, "Path");
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var playState = GetObject(session, "PlayState");
            var position = TicksToTime(playState is null ? 0 : GetInt64(playState.Value, "PositionTicks"));
            var duration = TicksToTime(GetInt64(item.Value, "RunTimeTicks"));
            var paused = playState is not null && GetBoolean(playState.Value, "IsPaused", true);
            var speed = playState is null ? 1d : GetDouble(playState.Value, "PlaybackRate", 1d);
            return new(GetString(session, "Id"), sessionDevice,
                GetString(item.Value, "Id"), path, !paused, duration, position,
                speed > 0 && double.IsFinite(speed) ? speed : 1d);
        }

        return NetworkMediaState.Empty;
    }

    private async Task<NetworkMediaState> PollPlexAsync(CancellationToken cancellationToken)
    {
        var commandId = Interlocked.Increment(ref _plexCommandId);
        using var request = CreateRequest(HttpMethod.Get,
            $"/player/timeline/poll?wait=0&commandID={commandId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        var document = ParseXml(bytes);
        var timeline = document.DocumentElement?.ChildNodes.OfType<XmlNode>()
            .Where(node => string.Equals(node.Name, "Timeline", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(node => !string.Equals(GetAttribute(node, "state"), "stopped", StringComparison.OrdinalIgnoreCase));
        if (timeline is null)
        {
            _plexMetadataKey = null;
            _plexPath = null;
            _plexDuration = TimeSpan.Zero;
            _plexType = null;
            return NetworkMediaState.Empty;
        }

        _plexType = GetAttribute(timeline, "type");
        var key = GetAttribute(timeline, "key");
        if (!string.IsNullOrWhiteSpace(key) && !string.Equals(key, _plexMetadataKey, StringComparison.Ordinal))
        {
            _plexMetadataKey = key;
            var metadata = await ReadPlexMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            _plexPath = metadata.Path;
            _plexDuration = metadata.Duration;
        }

        var position = ParseMilliseconds(GetAttribute(timeline, "time"));
        var duration = ParseMilliseconds(GetAttribute(timeline, "duration"));
        if (duration == TimeSpan.Zero)
            duration = _plexDuration;
        var state = GetAttribute(timeline, "state");
        var rate = ParseDouble(GetAttribute(timeline, "rate"), 1d);
        var clientId = GetAttribute(timeline, "machineIdentifier") ?? _deviceId;
        return new(key, clientId, key, _plexPath, string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase),
            duration, position, rate > 0 && double.IsFinite(rate) ? rate : 1d, _plexType);
    }

    private async Task<(string? Path, TimeSpan Duration)> ReadPlexMetadataAsync(string key,
        CancellationToken cancellationToken)
    {
        if (!key.StartsWith("/", StringComparison.Ordinal) || key.Contains("//", StringComparison.Ordinal))
            throw new InvalidDataException("The Plex timeline metadata key was not a local path.");
        using var request = CreateRequest(HttpMethod.Get, key);
        using var response = await SendCheckedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        var document = ParseXml(bytes);
        var part = document.SelectSingleNode("//*[local-name()='Part']")
            ?? throw new InvalidDataException("The Plex metadata response did not contain a Part element.");
        var path = GetAttribute(part, "file");
        var duration = ParseMilliseconds(GetAttribute(part, "duration"));
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("The Plex metadata Part did not contain a file path.");
        return (path, duration);
    }

    private async Task<NetworkMediaState> PollVlcAsync(CancellationToken cancellationToken,
        bool includeHandshake)
    {
        using var request = CreateRequest(HttpMethod.Get, "/requests/status.json");
        using var response = await SendCheckedAsync(request,
            includeHandshake ? _options.ConnectTimeout : _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The VLC status response was not a JSON object.");

        var root = document.RootElement;
        var apiVersion = GetInt64(root, "apiversion");
        if (includeHandshake)
        {
            if (apiVersion is not (3 or 4))
                throw new InvalidDataException($"Unsupported VLC API version '{apiVersion}'.");
            _vlcApiVersion = (int)apiVersion;
        }
        else if (_vlcApiVersion is not (3 or 4))
        {
            throw new InvalidDataException("The VLC API version was not established.");
        }

        var playlistId = GetInt64(root, "currentplid");
        var state = GetString(root, "state");
        if (playlistId < 0 || string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            ResetVlcState();
            return NetworkMediaState.Empty;
        }

        if (playlistId != _vlcPlaylistId || string.IsNullOrWhiteSpace(_vlcPath))
        {
            using var playlistRequest = CreateRequest(HttpMethod.Get, "/requests/playlist.json");
            using var playlistResponse = await SendCheckedAsync(playlistRequest,
                _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            var playlistBytes = await ReadBoundedAsync(playlistResponse, cancellationToken).ConfigureAwait(false);
            using var playlist = JsonDocument.Parse(playlistBytes, JsonOptions);
            var path = FindVlcCurrentPath(playlist.RootElement);
            if (string.IsNullOrWhiteSpace(path))
            {
                ResetVlcState();
                return NetworkMediaState.Empty;
            }
            _vlcPlaylistId = playlistId;
            _vlcPath = path;
        }

        var duration = SecondsToTime(GetDouble(root, "length", 0d));
        var position = _vlcApiVersion == 3
            ? SecondsToTime(GetDouble(root, "position", 0d) * duration.TotalSeconds)
            : SecondsToTime(GetDouble(root, "time", 0d));
        var speed = GetDouble(root, "rate", 1d);
        return new(playlistId.ToString(System.Globalization.CultureInfo.InvariantCulture), _deviceId,
            playlistId.ToString(System.Globalization.CultureInfo.InvariantCulture), _vlcPath,
            string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase), duration, position,
            speed > 0 && double.IsFinite(speed) ? speed : 1d);
    }

    private async Task<NetworkMediaState> PollMpcAsync(CancellationToken cancellationToken,
        bool includeHandshake)
    {
        if (includeHandshake)
        {
            using var handshake = CreateRequest(HttpMethod.Get, "/");
            using var handshakeResponse = await SendCheckedAsync(handshake, _options.ConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            _ = await ReadBoundedAsync(handshakeResponse, cancellationToken).ConfigureAwait(false);
        }

        using var request = CreateRequest(HttpMethod.Get, "/variables.html");
        using var response = await SendCheckedAsync(request, _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        var html = StrictUtf8.GetString(bytes);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MpcVariableRegex.Matches(html))
        {
            var name = match.Groups["name"].Value;
            var value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            if (name.Length <= 64 && value.Length <= 4096)
                variables[name] = value;
        }

        variables.TryGetValue("filepath", out var path);
        if (string.IsNullOrWhiteSpace(path))
            return NetworkMediaState.Empty;
        var playing = variables.TryGetValue("state", out var state)
            && int.TryParse(state, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var stateValue)
            && stateValue == 2;
        var duration = variables.TryGetValue("duration", out var durationText)
            ? ParseMilliseconds(durationText) : TimeSpan.Zero;
        var position = variables.TryGetValue("position", out var positionText)
            ? ParseMilliseconds(positionText) : TimeSpan.Zero;
        var speed = variables.TryGetValue("playbackrate", out var speedText)
            ? ParseDouble(speedText.Replace(',', '.'), 1d) : 1d;
        return new(null, _deviceId, path, path, playing, duration, position,
            speed > 0 && double.IsFinite(speed) ? speed : 1d);
    }

    private static string? FindVlcCurrentPath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var isCurrentLeaf = GetString(element, "type") is { } type
                && string.Equals(type, "leaf", StringComparison.OrdinalIgnoreCase)
                && GetString(element, "current") is { } current
                && string.Equals(current, "current", StringComparison.OrdinalIgnoreCase);
            if (isCurrentLeaf && GetString(element, "uri") is { } uri)
                return NormalizeVlcPath(uri);
            foreach (var property in element.EnumerateObject())
            {
                var found = FindVlcCurrentPath(property.Value);
                if (found is not null)
                    return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindVlcCurrentPath(item);
                if (found is not null)
                    return found;
            }
        }
        return null;
    }

    private static string? NormalizeVlcPath(string value)
    {
        if (value.Length == 0 || value.Length > 4096)
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : uri.ToString();
        return Uri.UnescapeDataString(value);
    }

    private void ResetVlcState()
    {
        _vlcPlaylistId = long.MinValue;
        _vlcPath = null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        switch (_kind)
        {
            case NetworkMediaSourceKind.Emby:
                request.Headers.TryAddWithoutValidation("X-Emby-Token", _token);
                break;
            case NetworkMediaSourceKind.Jellyfin:
                request.Headers.TryAddWithoutValidation("X-Emby-Token", _token);
                request.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", _token);
                break;
            case NetworkMediaSourceKind.Plex:
                request.Headers.TryAddWithoutValidation("X-Plex-Token", _token);
                request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", _deviceId ?? "mfp-headless");
                request.Headers.TryAddWithoutValidation("X-Plex-Product", "MultiFunPlayer");
                request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Linux");
                request.Headers.TryAddWithoutValidation("X-Plex-Provides", "controller");
                if (_deviceId is not null)
                    request.Headers.TryAddWithoutValidation("X-Plex-Target-Client-Identifier", _deviceId);
                break;
            case NetworkMediaSourceKind.Vlc when _token is not null:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _token)));
                break;
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpRequestMessage request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"{Protocol} returned HTTP {status}.");
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Protocol} request timed out after {timeout.TotalSeconds:0.###} seconds.");
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > _options.MaxResponseBytes)
                throw new InvalidDataException($"{Protocol} response exceeded {_options.MaxResponseBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task StopPollAsync(CancellationToken cancellationToken)
    {
        var cancellation = _pollCancellation;
        var task = _pollTask;
        _pollCancellation = null;
        _pollTask = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (TimeoutException) { }
        }
        cancellation.Dispose();
    }

    private void SetCurrent(NetworkMediaState state)
    {
        lock (_stateGate)
        {
            _current = state;
            _lastError = null;
        }
        var handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (Action<NetworkMediaState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch { /* observers must not stop the polling loop */ }
        }
    }

    private void SetConnectionState(NetworkMediaConnectionState state, string? error)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _connectionState != state || !string.Equals(_lastError, error, StringComparison.Ordinal);
            _connectionState = state;
            _lastError = error;
        }
        if (!changed)
            return;
        var handlers = ConnectionStateChanged;
        if (handlers is not null)
            foreach (Action<NetworkMediaConnectionState> handler in handlers.GetInvocationList())
                try { handler(state); } catch { }
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("A network media endpoint must use http(s) without embedded credentials.", nameof(endpoint));
        return endpoint;
    }

    private static long Ticks(TimeSpan value)
        => checked((long)(value.TotalMilliseconds * 10_000));

    private static TimeSpan TicksToTime(long ticks)
        => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(ticks, TimeSpan.MaxValue.Ticks));

    private static bool LooksLikeJsonObject(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes).TrimStart();
        return text.Length == 0 || text[0] is '{' or '[';
    }

    private static JsonElement? GetObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? GetString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long GetInt64(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? number : 0;

    private static bool GetBoolean(JsonElement parent, string name, bool fallback)
        => parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;

    private static double GetDouble(JsonElement parent, string name, double fallback)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : fallback;

    private static XmlDocument ParseXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }

    private static string? GetAttribute(XmlNode node, string name)
        => node.Attributes?[name]?.Value;

    private static TimeSpan ParseMilliseconds(string? value)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds > 0 ? TimeSpan.FromMilliseconds(Math.Min(milliseconds, TimeSpan.MaxValue.TotalMilliseconds)) : TimeSpan.Zero;

    private static double ParseDouble(string? value, double fallback)
        => double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static TimeSpan SecondsToTime(double seconds)
        => double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds))
            : TimeSpan.Zero;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(NetworkMediaSourceAdapter));
    }
}

public sealed class NetworkMediaSourceAdapterException(string protocol, string operation, Exception innerException)
    : Exception($"{protocol} {operation} failed: {innerException.Message}", innerException)
{
    public string Protocol { get; } = protocol;
    public string Operation { get; } = operation;
}
