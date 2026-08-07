namespace MultiFunPlayer.Infrastructure.Transport;

public interface IOutputTransport : IAsyncDisposable
{
    string Id { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record OutputTransportOptions
{
    public int QueueCapacity { get; init; } = 64;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (SendTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SendTimeout));
    }
}

public class OutputTransportException : IOException
{
    public OutputTransportException(string transportId, string operation, Exception innerException)
        : base($"Output transport '{transportId}' failed during {operation}.", innerException)
    {
        TransportId = transportId;
        Operation = operation;
    }

    public string TransportId { get; }
    public string Operation { get; }
}

public sealed class OutputTransportQueueFullException : OutputTransportException
{
    public OutputTransportQueueFullException(string transportId)
        : base(transportId, "queue", new InvalidOperationException("The bounded output queue is full.")) { }
}

public sealed class TCodeOutputException : OutputTransportException
{
    public TCodeOutputException(string transportId, string command, Exception innerException)
        : base(transportId, "tcode-send", innerException) => Command = command;

    public string Command { get; }
}
