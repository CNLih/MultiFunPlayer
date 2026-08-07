using System.Text.Json;
using MultiFunPlayer.Infrastructure.Media;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public enum MediaSourceOperationStatus
{
    Success,
    Invalid,
    NotFound,
    Conflict,
    Unavailable,
    Failed
}

public sealed record MediaSourceOperation(
    MediaSourceOperationStatus Status,
    MediaSourceResponse? Source = null,
    string? Error = null);

public sealed record MediaSourceDefinition(
    string Id,
    string Name,
    string Kind,
    string Endpoint,
    bool Enabled = true,
    bool AutoConnect = true);

/// <summary>
/// Persists media source configuration without persisting credentials. The
/// internal source is ready in the headless host; network and Windows-player
/// entries retain an explicit capability state until their adapter is wired.
/// </summary>
public sealed class MediaSourceRegistry : IHostedService, IAsyncDisposable
{
    private const string StoreFile = "media-sources.json";
    private static readonly IReadOnlyDictionary<string, (string Integration, string Implementation)> Kinds
        = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["internal"] = ("direct", "ready"),
            ["emby"] = ("direct", "planned"),
            ["jellyfin"] = ("direct", "planned"),
            ["plex"] = ("direct", "planned"),
            ["ofs"] = ("direct", "ready"),
            ["mpv"] = ("external-proxy", "blocked"),
            ["vlc"] = ("external-proxy", "blocked"),
            ["mpc"] = ("external-proxy", "blocked"),
            ["potplayer"] = ("unsupported", "unsupported"),
            ["whirligig"] = ("external-proxy", "blocked"),
            ["deovr"] = ("external-proxy", "blocked"),
            ["heresphere"] = ("external-proxy", "blocked")
        };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RuntimeSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly RootedFileStore? _config;
    private readonly ILogger<MediaSourceRegistry> _logger;
    private readonly IOfsMediaSourceAdapterFactory _ofsFactory;
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public MediaSourceRegistry(StorageOptions storage, ILogger<MediaSourceRegistry> logger,
        IOfsMediaSourceAdapterFactory? ofsFactory = null)
    {
        _logger = logger;
        _ofsFactory = ofsFactory ?? new OfsMediaSourceAdapterFactory();
        try
        {
            _config = new RootedFileStore(storage.ConfigRoot);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Media source persistence is unavailable.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Media source persistence is unavailable.");
        }
    }

    public IReadOnlyList<MediaSourceResponse> List()
    {
        _gate.Wait();
        try
        {
            return _sources.Values.Select(ToResponse).OrderBy(source => source.Id,
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

            List<MediaSourceDefinition>? definitions;
            try
            {
                definitions = await _config.ReadJsonAsync<List<MediaSourceDefinition>>(StoreFile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                definitions = [];
            }
            catch (JsonException exception)
            {
                _logger.LogError(exception, "Ignoring malformed media source configuration.");
                definitions = [];
            }

            foreach (var definition in definitions ?? [])
            {
                var validation = Validate(definition);
                if (validation is not null)
                {
                    _logger.LogWarning("Ignoring media source {SourceId}: {Reason}", definition.Id, validation);
                    continue;
                }
                var source = new RuntimeSource(definition);
                _sources.Add(definition.Id, source);
                if (definition.Enabled && definition.AutoConnect)
                    await ConnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            foreach (var source in _sources.Values)
                await DisconnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MediaSourceOperation> CreateAsync(MediaSourceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(definition);
        if (validation is not null)
            return new(MediaSourceOperationStatus.Invalid, Error: validation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sources.ContainsKey(definition.Id))
                return new(MediaSourceOperationStatus.Conflict, Error: "A media source with this id already exists.");
            if (_config is null)
                return new(MediaSourceOperationStatus.Unavailable, Error: "The configured root is not writable.");
            var source = new RuntimeSource(definition);
            _sources.Add(definition.Id, source);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            if (definition.Enabled && definition.AutoConnect)
                await ConnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
            // Persistence is successful even when an optional network adapter
            // is unavailable. The lifecycle endpoint reports that connection
            // failure separately without losing the configured source.
            return new(MediaSourceOperationStatus.Success, ToResponse(source), source.LastError);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _sources.Remove(definition.Id);
            return new(MediaSourceOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MediaSourceOperation> UpdateAsync(string id, MediaSourceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(id, definition.Id, StringComparison.OrdinalIgnoreCase))
            return new(MediaSourceOperationStatus.Invalid, Error: "The route id must match the source id.");
        var validation = Validate(definition);
        if (validation is not null)
            return new(MediaSourceOperationStatus.Invalid, Error: validation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sources.ContainsKey(id))
                return new(MediaSourceOperationStatus.NotFound, Error: "Media source not found.");
            if (_config is null)
                return new(MediaSourceOperationStatus.Unavailable, Error: "The configured root is not writable.");
            await DisconnectLockedAsync(_sources[id], cancellationToken).ConfigureAwait(false);
            var source = new RuntimeSource(definition);
            _sources[id] = source;
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            if (definition.Enabled && definition.AutoConnect)
                await ConnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
            return new(MediaSourceOperationStatus.Success, ToResponse(source), source.LastError);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(MediaSourceOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MediaSourceOperation> DeleteAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sources.TryGetValue(id, out var source))
                return new(MediaSourceOperationStatus.NotFound, Error: "Media source not found.");
            if (_config is null)
                return new(MediaSourceOperationStatus.Unavailable, Error: "The configured root is not writable.");
            await DisconnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
            _sources.Remove(id);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
            return new(MediaSourceOperationStatus.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(MediaSourceOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<MediaSourceOperation> ConnectAsync(string id, CancellationToken cancellationToken = default)
        => RunLifecycleAsync(id, true, cancellationToken);

    public Task<MediaSourceOperation> DisconnectAsync(string id, CancellationToken cancellationToken = default)
        => RunLifecycleAsync(id, false, cancellationToken);

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

    private async Task<MediaSourceOperation> RunLifecycleAsync(string id, bool connect,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sources.TryGetValue(id, out var source))
                return new(MediaSourceOperationStatus.NotFound, Error: "Media source not found.");
            if (connect)
                await ConnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
            else
                await DisconnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
            return new(source.LastError is null ? MediaSourceOperationStatus.Success : MediaSourceOperationStatus.Failed,
                ToResponse(source), source.LastError);
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
            foreach (var source in _sources.Values)
                await DisconnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectLockedAsync(RuntimeSource source, CancellationToken cancellationToken)
    {
        var capability = Kinds[source.Definition.Kind];
        if (capability.Implementation == "ready")
        {
            if (source.Definition.Kind.Equals("ofs", StringComparison.OrdinalIgnoreCase))
            {
                await DisconnectLockedAsync(source, cancellationToken).ConfigureAwait(false);
                OfsWebSocketMediaAdapter? adapter = null;
                try
                {
                    adapter = _ofsFactory.Create(new Uri(source.Definition.Endpoint));
                    source.Adapter = adapter;
                    adapter.MessageReceived += message => HandleOfsMessage(source, message);
                    adapter.ConnectionStateChanged += state =>
                    {
                        if (state == OfsConnectionState.Disconnected)
                            source.Connected = false;
                        else if (state == OfsConnectionState.Faulted)
                            source.Connected = false;
                    };
                    await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    source.Connected = true;
                    source.LastError = null;
                    return;
                }
                catch (Exception exception)
                {
                    source.Connected = false;
                    source.LastError = exception is OfsAdapterException ofs
                        ? $"OFS WebSocket {ofs.Operation} failed: {ofs.InnerException?.Message ?? ofs.Message}"
                        : $"OFS WebSocket connection failed: {exception.Message}";
                    _logger.LogWarning(exception, "OFS media source {SourceId} could not connect to {Endpoint}.",
                        source.Definition.Id, source.Definition.Endpoint);
                    source.Adapter = null;
                    if (adapter is not null)
                    {
                        try { await adapter.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception disposeException)
                        {
                            _logger.LogDebug(disposeException, "OFS adapter cleanup failed for {SourceId}.",
                                source.Definition.Id);
                        }
                    }
                    return;
                }
            }
            source.Connected = true;
            source.LastError = null;
            return;
        }

        source.Connected = false;
        source.LastError = capability.Implementation == "blocked"
            ? "This source requires an external player/proxy and is not available in the headless host."
            : "The network media adapter is described but not wired in this server version.";
    }

    private static void HandleOfsMessage(RuntimeSource source, OfsMediaEvent message)
    {
        if (message.Kind == OfsMediaEventKind.ProtocolError)
        {
            source.LastError = message.Error;
            return;
        }

        source.LastError = null;
        source.LastMediaState = message.State;
    }

    private static async Task DisconnectLockedAsync(RuntimeSource source, CancellationToken cancellationToken)
    {
        source.Connected = false;
        var adapter = source.Adapter;
        source.Adapter = null;
        if (adapter is not null)
        {
            try { await adapter.DisconnectAsync(cancellationToken).ConfigureAwait(false); }
            finally { await adapter.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new IOException("The configured root is not writable.");
        var definitions = _sources.Values.Select(source => source.Definition)
            .OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        await _config.WriteJsonAsync(StoreFile, definitions, cancellationToken).ConfigureAwait(false);
    }

    private static string? Validate(MediaSourceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Id.Length > 64
            || definition.Id.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            return "id must contain only letters, numbers, '-' or '_', and be at most 64 characters.";
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 120)
            return "name is required and must be at most 120 characters.";
        if (!Kinds.ContainsKey(definition.Kind))
            return "kind is not a supported media source capability.";
        if (definition.Endpoint.Length > 2048 || definition.Endpoint.Contains('\0'))
            return "endpoint is invalid or too long.";
        if (definition.Kind.Equals("internal", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(definition.Endpoint))
            return "a network endpoint is required for this source kind.";
        if (!Uri.TryCreate(definition.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo))
            return "network endpoint must use http(s) or ws(s) without embedded credentials.";
        if (definition.Endpoint.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || definition.Endpoint.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || definition.Endpoint.Contains("api-key=", StringComparison.OrdinalIgnoreCase))
            return "credentials must not be embedded in the endpoint.";
        return null;
    }

    private static MediaSourceResponse ToResponse(RuntimeSource source)
    {
        var capability = Kinds[source.Definition.Kind];
        return new(source.Definition.Id, source.Definition.Name, source.Definition.Kind,
            source.Definition.Endpoint, source.Definition.Enabled, source.Definition.AutoConnect,
            capability.Integration, capability.Implementation, source.Connected, source.LastError);
    }

    private sealed class RuntimeSource(MediaSourceDefinition definition)
    {
        public MediaSourceDefinition Definition { get; } = definition;
        public bool Connected { get; set; }
        public string? LastError { get; set; }
        public OfsWebSocketMediaAdapter? Adapter { get; set; }
        public OfsMediaState? LastMediaState { get; set; }
    }
}
