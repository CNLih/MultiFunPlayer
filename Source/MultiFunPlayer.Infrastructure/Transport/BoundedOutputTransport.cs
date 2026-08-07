using System.Threading.Channels;

namespace MultiFunPlayer.Infrastructure.Transport;

/// <summary>
/// Shared bounded queue and lifecycle for network/file transports. A failed write
/// completes the caller's task with a structured exception; it is never discarded.
/// </summary>
public abstract class BoundedOutputTransport : IOutputTransport
{
    private sealed record Envelope(byte[] Payload, TaskCompletionSource<bool> Completion);

    private readonly Channel<Envelope> _queue;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly OutputTransportOptions _options;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private int _connected;
    private int _disposed;

    protected BoundedOutputTransport(string id, OutputTransportOptions? options = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("A transport id is required.", nameof(id)) : id;
        _options = options ?? new OutputTransportOptions();
        _options.Validate();
        _queue = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public string Id { get; }
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    protected OutputTransportOptions Options => _options;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            using var timeout = CreateTimeout(cancellationToken, _options.ConnectTimeout);
            await ConnectCoreAsync(timeout.Token).ConfigureAwait(false);
            _workerCancellation = new CancellationTokenSource();
            _worker = ProcessQueueAsync(_workerCancellation.Token);
            Volatile.Write(ref _connected, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OutputTransportException(Id, "connect", exception);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected)
            throw new OutputTransportException(Id, "send", new InvalidOperationException("The transport is not connected."));

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Envelope(payload.ToArray(), completion)))
            throw new OutputTransportQueueFullException(Id);
        return new ValueTask(WaitForCompletionAsync(completion.Task, cancellationToken));
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected && _worker is null)
                return;

            Volatile.Write(ref _connected, 0);
            _workerCancellation?.Cancel();
            if (_worker is not null)
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            _worker = null;
            _workerCancellation?.Dispose();
            _workerCancellation = null;
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
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Dispose();
            _workerCancellation?.Dispose();
            _queue.Writer.TryComplete();
        }
    }

    protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);
    protected abstract ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    protected virtual Task DisconnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var timeout = CreateTimeout(cancellationToken, _options.SendTimeout);
                    await SendCoreAsync(envelope.Payload, timeout.Token).ConfigureAwait(false);
                    envelope.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException exception)
                {
                    envelope.Completion.TrySetException(new OutputTransportException(Id, "send", exception));
                }
                catch (Exception exception)
                {
                    envelope.Completion.TrySetException(new OutputTransportException(Id, "send", exception));
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_queue.Reader.TryRead(out var pending))
                pending.Completion.TrySetException(new OutputTransportException(Id, "dispose", new OperationCanceledException()));
        }
    }

    private static async Task WaitForCompletionAsync(Task completion, CancellationToken cancellationToken)
        => await completion.WaitAsync(cancellationToken).ConfigureAwait(false);

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(Id);
    }
}
