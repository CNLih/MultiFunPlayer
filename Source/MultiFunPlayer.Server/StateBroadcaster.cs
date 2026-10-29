using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using MultiFunPlayer.Application;

namespace MultiFunPlayer.Server;

/// <summary>
/// Each client owns a one-item channel. New snapshots replace stale pending
/// snapshots, so a slow browser cannot create an unbounded queue or delay the
/// engine tick.
/// </summary>
public sealed class StateBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly int _maxClients;

    public StateBroadcaster(IConfiguration configuration)
    {
        _maxClients = ServerOptions.ResolveMaxWebSocketClients(configuration);
    }

    public int ClientCount => _clients.Count;

    public bool TrySubscribe(out Subscription? subscription)
    {
        subscription = null;
        if (_clients.Count >= _maxClients)
            return false;

        var client = new Client();
        if (!_clients.TryAdd(client.Id, client))
            return false;
        subscription = new Subscription(this, client);
        return true;
    }

    public void Publish(EngineSnapshot snapshot)
    {
        foreach (var entry in _clients)
            entry.Value.WriteLatest(snapshot);
    }

    private void Remove(Client client)
    {
        if (_clients.TryRemove(client.Id, out _))
            client.Complete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly StateBroadcaster _owner;
        private readonly Client _client;
        private int _disposed;

        internal Subscription(StateBroadcaster owner, Client client)
        {
            _owner = owner;
            _client = client;
        }

        public ChannelReader<EngineSnapshot> Reader => _client.Reader;
        public EngineSnapshot? Initial => _client.Initial;

        public void SetInitial(EngineSnapshot snapshot) => _client.Initial = snapshot;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Remove(_client);
        }
    }

    internal sealed class Client
    {
        private readonly Channel<EngineSnapshot> _channel = Channel.CreateBounded<EngineSnapshot>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public Guid Id { get; } = Guid.NewGuid();
        public ChannelReader<EngineSnapshot> Reader => _channel.Reader;
        public EngineSnapshot? Initial { get; set; }

        public void WriteLatest(EngineSnapshot snapshot)
        {
            // DropOldest makes this non-blocking and keeps only the newest state.
            _channel.Writer.TryWrite(snapshot);
        }

        public void Complete() => _channel.Writer.TryComplete();
    }
}
