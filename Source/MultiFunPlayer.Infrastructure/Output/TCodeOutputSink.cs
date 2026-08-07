using System.Text;
using System.Threading.Channels;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>
/// Application output sink backed by a bounded asynchronous transport queue.
/// Formatting stays on the engine thread, while physical writes run on one
/// target-owned worker so a slow target cannot stall other output targets.
/// </summary>
public sealed class TCodeOutputSink : IOutputSink, IOutputSinkErrorDiagnostics,
    IOutputSinkFaults, IDisposable, IAsyncDisposable
{
    private const int DefaultQueueCapacity = 64;
    private readonly IOutputTransport _transport;
    private readonly TCodeFormattingOptions _formatting;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Task _worker;
    private string? _lastError;
    private Exception? _fault;
    private int _disposed;

    public TCodeOutputSink(IOutputTransport transport, TCodeFormattingOptions? formatting = null,
        int queueCapacity = DefaultQueueCapacity)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _formatting = formatting ?? new TCodeFormattingOptions();
        _formatting.Validate();
        if (queueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = ProcessQueueAsync(_workerCancellation.Token);
    }

    public string TransportId => _transport.Id;

    public string? LastError => Volatile.Read(ref _lastError);

    public event Action<Exception>? Faulted;

    public void Publish(OutputFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        var command = TCodeFormatter.Format(frame, _formatting);
        if (Volatile.Read(ref _fault) is not null)
            throw CreateFault(command);
        if (!_transport.IsConnected)
            throw new TCodeOutputException(_transport.Id, command,
                new InvalidOperationException("The output transport is not connected."));
        if (!_queue.Writer.TryWrite(command))
            throw new TCodeOutputException(_transport.Id, command,
                new OutputTransportQueueFullException(_transport.Id));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.Writer.TryComplete();
        _workerCancellation.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal shutdown path for a pending write.
        }
        finally
        {
            _workerCancellation.Dispose();
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _queue.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                try
                {
                    await _transport.SendAsync(Encoding.UTF8.GetBytes(command), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetFault(exception is TCodeOutputException
                        ? exception
                        : new TCodeOutputException(_transport.Id, command, exception));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal shutdown path.
        }
    }

    private void SetFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is not null)
            return;
        Volatile.Write(ref _lastError, exception.Message);
        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // Diagnostics subscribers must not terminate the output worker.
        }
    }

    private TCodeOutputException CreateFault(string command)
        => new(_transport.Id, command, Volatile.Read(ref _fault)
            ?? new InvalidOperationException("The output worker has faulted."));
}
