using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>
/// Headless The Handy HDSP adapter. The request paths and payloads mirror the
/// existing desktop adapter, while the connection key is supplied by the host
/// rather than persisted with an output target definition.
/// </summary>
public sealed class TheHandyOutputAdapter : IOutputTargetAdapter, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly string _connectionKey;
    private readonly string _sourceAxis;
    private readonly OutputTransportOptions _options;
    private readonly Channel<OutputFrame> _queue;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private int _connected;
    private int _disposed;

    public TheHandyOutputAdapter(string id, Uri endpoint, string connectionKey,
        string sourceAxis = "L0", HttpMessageHandler? handler = null,
        OutputTransportOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A target id is required.", nameof(id));
        Id = id;
        _baseUri = ValidateEndpoint(endpoint);
        _connectionKey = string.IsNullOrWhiteSpace(connectionKey)
            ? throw new ArgumentException("A The Handy connection key is required.", nameof(connectionKey))
            : connectionKey;
        _sourceAxis = ValidateSourceAxis(sourceAxis);
        _options = options ?? new OutputTransportOptions();
        _options.Validate();
        _queue = Channel.CreateBounded<OutputFrame>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("X-Connection-Key", _connectionKey);
    }

    public string Id { get; }
    public string Protocol => "the-handy";
    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public string? LastError { get; private set; }

    public async ValueTask<MediaConnectionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return new(true);

            try
            {
                var connected = await GetObjectAsync("connected", _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!connected.TryGetProperty("connected", out var connectedValue)
                    || connectedValue.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("The Handy reports that the device is not connected.");

                var info = await GetObjectAsync("info", _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!info.TryGetProperty("fwStatus", out var firmwareStatus)
                    || firmwareStatus.ValueKind != JsonValueKind.Number
                    || !firmwareStatus.TryGetInt32(out var status))
                    throw new InvalidDataException("The Handy info response did not contain fwStatus.");
                if (status == 1)
                    throw new InvalidOperationException("The Handy firmware requires an update.");

                var mode = await PutObjectAsync("mode", new { mode = 2 }, _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!TryGetResultCode(mode, out var resultCode))
                    throw new InvalidDataException("The Handy mode response did not contain result.");
                if (resultCode == -1)
                    throw new InvalidOperationException("The Handy rejected HDSP mode 2.");

                // The desktop implementation samples this endpoint for clock
                // calibration. One probe keeps server startup bounded while
                // still rejecting an endpoint that cannot serve the API.
                await GetObjectAsync("servertime", _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);

                // A failed send can leave the receive worker completing just
                // after IsConnected becomes false. Always stop that old worker
                // before installing a new one so reconnect cannot leak tasks or
                // race two movement writers.
                await StopWorkerAsync(CancellationToken.None).ConfigureAwait(false);
                DrainQueue();
                _workerCancellation = new CancellationTokenSource();
                _worker = ProcessQueueAsync(_workerCancellation.Token);
                LastError = null;
                Volatile.Write(ref _connected, 1);
                return new(true);
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
                new InvalidOperationException("The Handy adapter is not connected."));
        if (!frame.Values.TryGetValue(_sourceAxis, out var value))
        {
            var pair = frame.Values.FirstOrDefault(item =>
                string.Equals(item.Key, _sourceAxis, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(pair.Key))
                return;
            value = pair.Value;
        }

        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var snapshot = new OutputFrame(frame.Position,
            new Dictionary<string, double>(frame.Values, StringComparer.OrdinalIgnoreCase));
        if (!_queue.Writer.TryWrite(snapshot))
            throw new OutputTransportQueueFullException(Id);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _client.Dispose();
            _queue.Writer.TryComplete();
            _lifecycle.Dispose();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _connected, 0);
            await StopWorkerAsync(cancellationToken).ConfigureAwait(false);
            DrainQueue();
        }
        finally
        {
            _lifecycle.Release();
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
                    var value = ReadAxisValue(frame);
                    var payload = new
                    {
                        immediateResponse = true,
                        stopOnTarget = true,
                        duration = 50,
                        position = value * 100
                    };
                    var result = await PutObjectAsync("hdsp/xpt", payload, _options.SendTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!TryGetResultCode(result, out var resultCode))
                        throw new InvalidDataException("The Handy movement response did not contain result.");
                    if (resultCode == -1)
                        throw new InvalidOperationException("The Handy rejected the movement command.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _connected, 0);
                    LastError = exception.Message;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private double ReadAxisValue(OutputFrame frame)
    {
        if (!frame.Values.TryGetValue(_sourceAxis, out var value))
        {
            var pair = frame.Values.FirstOrDefault(item =>
                string.Equals(item.Key, _sourceAxis, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(pair.Key))
                return 0;
            value = pair.Value;
        }
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private async Task StopWorkerAsync(CancellationToken cancellationToken)
    {
        var worker = _worker;
        var workerCancellation = _workerCancellation;
        if (worker is null && workerCancellation is null)
            return;

        workerCancellation?.Cancel();
        if (worker is not null && worker.Id != Task.CurrentId)
        {
            try { await worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        _worker = null;
        workerCancellation?.Dispose();
        _workerCancellation = null;
    }

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out _)) { }
    }

    private async ValueTask<JsonElement> GetObjectAsync(string path, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeout(cancellationToken, timeout);
        using var response = await _client.GetAsync(PathUri(path), timeoutSource.Token)
            .ConfigureAwait(false);
        return await ReadObjectAsync(response, timeoutSource.Token).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> PutObjectAsync(string path, object payload, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeout(cancellationToken, timeout);
        using var response = await _client.PutAsJsonAsync(PathUri(path), payload, JsonOptions,
            timeoutSource.Token).ConfigureAwait(false);
        return await ReadObjectAsync(response, timeoutSource.Token).ConfigureAwait(false);
    }

    private static async ValueTask<JsonElement> ReadObjectAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The Handy API returned {(int)response.StatusCode}.");
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The Handy API returned a non-object response.");
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"The Handy API returned an error: {error}");
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Handy API returned malformed JSON.", exception);
        }
    }

    private static bool TryGetResultCode(JsonElement response, out int resultCode)
    {
        if (response.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Number
            && result.TryGetInt32(out resultCode))
            return true;
        resultCode = default;
        return false;
    }

    private Uri PathUri(string path) => new(_baseUri, path);

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0)
            throw new ArgumentException("The Handy endpoint must be an absolute http(s) URL without credentials.", nameof(endpoint));
        var text = endpoint.ToString();
        return new Uri(text.EndsWith('/') ? text : text + '/', UriKind.Absolute);
    }

    private static string ValidateSourceAxis(string sourceAxis)
    {
        if (string.IsNullOrWhiteSpace(sourceAxis) || sourceAxis.Length > 64
            || sourceAxis.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("The Handy source axis is invalid.", nameof(sourceAxis));
        return sourceAxis;
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
