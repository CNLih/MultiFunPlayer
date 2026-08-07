namespace MultiFunPlayer.Application;

/// <summary>
/// Small host-neutral in-process bus used by headless hosts and tests. WPF
/// supplies a Stylet adapter instead, but both implementations share the same
/// typed contract and subscription lifetime semantics.
/// </summary>
public sealed class InMemoryApplicationMessageBus : IApplicationMessageBus, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<ISubscription>> _subscriptions = [];
    private bool _disposed;

    public IDisposable Subscribe<TMessage>(Action<TMessage> handler)
        where TMessage : IApplicationMessage
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription<TMessage>(this, handler);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_subscriptions.TryGetValue(typeof(TMessage), out var handlers))
                _subscriptions.Add(typeof(TMessage), handlers = []);
            handlers.Add(subscription);
        }
        return subscription;
    }

    public void Publish<TMessage>(TMessage message)
        where TMessage : IApplicationMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ISubscription[] handlers;
        lock (_gate)
        {
            ThrowIfDisposed();
            handlers = _subscriptions.TryGetValue(typeof(TMessage), out var registered)
                ? [.. registered]
                : [];
        }

        foreach (var handler in handlers)
            handler.Invoke(message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _subscriptions.Clear();
        }
    }

    private void Remove(ISubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscription.MessageType, out var handlers))
                return;
            handlers.Remove(subscription);
            if (handlers.Count == 0)
                _subscriptions.Remove(subscription.MessageType);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryApplicationMessageBus));
    }

    private interface ISubscription
    {
        Type MessageType { get; }
        void Invoke(IApplicationMessage message);
    }

    private sealed class Subscription<TMessage>(InMemoryApplicationMessageBus owner,
        Action<TMessage> handler) : ISubscription, IDisposable
        where TMessage : IApplicationMessage
    {
        private int _disposed;

        public Type MessageType => typeof(TMessage);

        public void Invoke(IApplicationMessage message)
        {
            if (Volatile.Read(ref _disposed) == 0)
                handler((TMessage)message);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Remove(this);
        }
    }
}
