using System.Text;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Output;

/// <summary>Application output sink backed by any bounded transport.</summary>
public sealed class TCodeOutputSink : IOutputSink, IDisposable, IAsyncDisposable
{
    private readonly IOutputTransport _transport;
    private readonly TCodeFormattingOptions _formatting;
    private int _disposed;

    public TCodeOutputSink(IOutputTransport transport, TCodeFormattingOptions? formatting = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _formatting = formatting ?? new TCodeFormattingOptions();
        _formatting.Validate();
    }

    public string TransportId => _transport.Id;

    public void Publish(OutputFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        var command = TCodeFormatter.Format(frame, _formatting);
        try
        {
            _transport.SendAsync(Encoding.UTF8.GetBytes(command)).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new TCodeOutputException(_transport.Id, command, exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        return _transport.DisposeAsync();
    }
}
