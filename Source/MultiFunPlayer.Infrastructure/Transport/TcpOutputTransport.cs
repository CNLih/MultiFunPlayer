using System.Net.Sockets;

namespace MultiFunPlayer.Infrastructure.Transport;

public sealed class TcpOutputTransport : BoundedOutputTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpOutputTransport(string id, string host, int port, OutputTransportOptions? options = null)
        : base(id, options)
    {
        _host = string.IsNullOrWhiteSpace(host) ? throw new ArgumentException("A host is required.", nameof(host)) : host;
        _port = port is < 1 or > 65535 ? throw new ArgumentOutOfRangeException(nameof(port)) : port;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("TCP transport is not connected.");
        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _client?.Dispose();
        _stream = null;
        _client = null;
    }
}
