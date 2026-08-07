using System.Net.WebSockets;

namespace MultiFunPlayer.Infrastructure.Transport;

public sealed class WebSocketOutputTransport : BoundedOutputTransport
{
    private readonly Uri _uri;
    private ClientWebSocket? _socket;

    public WebSocketOutputTransport(string id, Uri uri, OutputTransportOptions? options = null)
        : base(id, options) => _uri = uri ?? throw new ArgumentNullException(nameof(uri));

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket transport is not connected.");
        await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_socket is { State: WebSocketState.Open })
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // A peer restart may have already torn down the connection. Cleanup
            // must remain idempotent and should not fail host shutdown.
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
        }
    }
}
