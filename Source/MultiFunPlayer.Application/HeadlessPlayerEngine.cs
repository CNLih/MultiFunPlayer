using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Application;

/// <summary>
/// Deterministic player coordinator. It has no timer or background thread:
/// hosts call <see cref="Tick()"/> from their scheduler, or pass a position to
/// <see cref="Tick(TimeSpan)"/> in tests and event-driven hosts.
/// </summary>
public sealed class HeadlessPlayerEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly ScriptEngine _scripts;
    private readonly IMediaClock _clock;
    private readonly List<IOutputSink> _sinks = [];
    private PlayerState _state = PlayerState.Empty;
    private TimeSpan _position;
    private TimeSpan _clockAnchor;
    private long _revision;
    private bool _disposed;

    public HeadlessPlayerEngine(IMediaClock? clock = null, ScriptEngine? scripts = null)
    {
        _clock = clock ?? new SystemClock();
        _scripts = scripts ?? new ScriptEngine();
        Current = NewSnapshot();
    }

    public event EventHandler<EngineSnapshot>? SnapshotChanged;

    public event EventHandler<OutputSinkError>? SinkFailed;

    public EngineSnapshot Current { get; private set; }

    public PlayerState State => Current.State;

    public TimeSpan Position => Current.Position;

    public TimeSpan Duration => Current.Duration;

    public void Load(IScriptResource resource, string axisName = "default", InterpolationType interpolation = InterpolationType.Linear)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Load([new AxisScript(axisName, resource, interpolation)]);
    }

    public void Load(AxisScript track) => Load([track]);

    public void Load(IEnumerable<AxisScript> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _scripts.Load(tracks);
            _position = TimeSpan.Zero;
            _clockAnchor = _clock.Now;
            _state = _scripts.Tracks.Count == 0 ? PlayerState.Empty : PlayerState.Ready;
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    public void Load(IReadOnlyDictionary<DeviceAxis, IScriptResource> resources, InterpolationType interpolation = InterpolationType.Linear)
    {
        ArgumentNullException.ThrowIfNull(resources);
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _scripts.Load(resources, interpolation);
            _position = TimeSpan.Zero;
            _clockAnchor = _clock.Now;
            _state = _scripts.Tracks.Count == 0 ? PlayerState.Empty : PlayerState.Ready;
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    public void Play()
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_scripts.Tracks.Count == 0)
                return;
            if (_state is PlayerState.Ready or PlayerState.Paused or PlayerState.Stopped or PlayerState.Completed)
            {
                if (_state == PlayerState.Completed)
                    _position = TimeSpan.Zero;
                _clockAnchor = _clock.Now - _position;
                _state = PlayerState.Playing;
            }
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    public void Pause()
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == PlayerState.Playing)
            {
                _position = PositionFromClockLocked();
                _clockAnchor = _clock.Now;
                _state = PlayerState.Paused;
            }
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    public void Stop()
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_scripts.Tracks.Count > 0)
            {
                _position = TimeSpan.Zero;
                _state = PlayerState.Stopped;
                _clockAnchor = _clock.Now;
            }
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    public void Seek(TimeSpan position)
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _position = ClampPosition(position);
            if (_state == PlayerState.Completed && _position < _scripts.Duration)
                _state = PlayerState.Paused;
            _clockAnchor = _clock.Now;
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
    }

    /// <summary>Advance using the injected monotonic clock.</summary>
    public EngineSnapshot Tick()
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == PlayerState.Playing)
            {
                _position = PositionFromClockLocked();
                _clockAnchor = _clock.Now;
                if (_scripts.Duration > TimeSpan.Zero && _position >= _scripts.Duration)
                {
                    _position = _scripts.Duration;
                    _state = PlayerState.Completed;
                }
            }
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
        return snapshot;
    }

    /// <summary>Advance to an explicit position without relying on wall time.</summary>
    public EngineSnapshot Tick(TimeSpan position)
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _position = ClampPosition(position);
            if (_state == PlayerState.Playing)
                _clockAnchor = _clock.Now;
            if (_scripts.Duration > TimeSpan.Zero && _position >= _scripts.Duration)
            {
                _position = _scripts.Duration;
                _state = PlayerState.Completed;
            }
            else if (_state == PlayerState.Completed)
                _state = PlayerState.Paused;
            snapshot = SetSnapshotLocked();
        }
        Publish(snapshot);
        return snapshot;
    }

    public IDisposable AddSink(IOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            ThrowIfDisposed();
            _sinks.Add(sink);
        }
        return new SinkRegistration(this, sink);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _sinks.Clear();
        }
    }

    private EngineSnapshot NewSnapshot()
        => new(_state, _scripts.ScriptName, _position, _scripts.Duration,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase), _revision);

    private EngineSnapshot SetSnapshotLocked()
    {
        var frame = _scripts.Evaluate(_position);
        Current = new EngineSnapshot(_state, _scripts.ScriptName, frame.Position,
            _scripts.Duration, frame.Values, ++_revision);
        return Current;
    }

    private TimeSpan PositionFromClockLocked()
        => ClampPosition(_position + (_clock.Now - _clockAnchor));

    private TimeSpan ClampPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        if (_scripts.Duration > TimeSpan.Zero && position > _scripts.Duration)
            return _scripts.Duration;
        return position;
    }

    private void Publish(EngineSnapshot snapshot)
    {
        OutputFrame frame = new(snapshot.Position, snapshot.Values);
        IOutputSink[] sinks;
        lock (_gate)
            sinks = [.. _sinks];

        foreach (var sink in sinks)
        {
            try
            {
                sink.Publish(frame);
            }
            catch (Exception exception)
            {
                RaiseSinkFailed(new OutputSinkError(sink, frame, exception));
            }
        }

        var handlers = SnapshotChanged;
        if (handlers is not null)
            foreach (EventHandler<EngineSnapshot> handler in handlers.GetInvocationList())
                handler(this, snapshot);
    }

    private void RaiseSinkFailed(OutputSinkError error)
    {
        var handlers = SinkFailed;
        if (handlers is not null)
            foreach (EventHandler<OutputSinkError> handler in handlers.GetInvocationList())
                handler(this, error);
    }

    private void RemoveSink(IOutputSink sink)
    {
        lock (_gate)
            _sinks.Remove(sink);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeadlessPlayerEngine));
    }

    private sealed class SinkRegistration(HeadlessPlayerEngine owner, IOutputSink sink) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.RemoveSink(sink);
        }
    }
}
