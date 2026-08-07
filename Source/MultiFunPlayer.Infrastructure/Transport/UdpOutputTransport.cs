using System.Net.Sockets;

namespace MultiFunPlayer.Infrastructure.Transport;

public sealed class UdpOutputTransport : BoundedOutputTransport
{
    private readonly string _host;
    private readonly int _port;
    private UdpClient? _client;

    public UdpOutputTransport(string id, string host, int port, OutputTransportOptions? options = null)
        : base(id, options)
    {
        _host = string.IsNullOrWhiteSpace(host) ? throw new ArgumentException("A host is required.", nameof(host)) : host;
        _port = port is < 1 or > 65535 ? throw new ArgumentOutOfRangeException(nameof(port)) : port;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        _client = new UdpClient();
        cancellationToken.ThrowIfCancellationRequested();
        _client.Connect(_host, _port);
    }

    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_client is null)
            throw new InvalidOperationException("UDP transport is not connected.");
        await _client.SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }
}
