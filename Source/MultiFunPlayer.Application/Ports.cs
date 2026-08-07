using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Application;

/// <summary>
/// Marker for messages that cross the portable application boundary. The
/// desktop host adapts these messages to its legacy Stylet bus while the Web
/// host can use a different transport without bringing Stylet into the
/// portable projects.
/// </summary>
public interface IApplicationMessage;

public interface IApplicationCommand : IApplicationMessage;

public interface IApplicationEvent : IApplicationMessage;

/// <summary>
/// Synchronous, typed in-process message boundary. Implementations decide how
/// handlers are scheduled; the portable contract deliberately does not expose
/// a UI dispatcher or a framework event aggregator.
/// </summary>
public interface IApplicationMessageBus
{
    IDisposable Subscribe<TMessage>(Action<TMessage> handler)
        where TMessage : IApplicationMessage;

    void Publish<TMessage>(TMessage message)
        where TMessage : IApplicationMessage;
}

/// <summary>Structured failure that can be rendered by any host.</summary>
public sealed record ApplicationError(
    string Code,
    string Operation,
    string Source,
    string Message,
    bool IsTransient = false,
    Exception? Exception = null)
{
    public string? ExceptionType => Exception?.GetType().FullName;

    public static ApplicationError FromException(string code, string operation,
        string source, Exception exception, bool isTransient = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(code, operation, source,
            string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message,
            isTransient, exception);
    }
}

/// <summary>Host-owned presentation/retention boundary for application errors.</summary>
public interface IOperationErrorSink
{
    void Report(ApplicationError error);
}

/// <summary>
/// Compatibility alias for callers that use the broader application naming.
/// New portable code should depend on <see cref="IOperationErrorSink"/>.
/// </summary>
public interface IApplicationErrorSink : IOperationErrorSink;

/// <summary>Safe default for hosts that do not provide an error presenter.</summary>
public sealed class NullApplicationErrorSink : IApplicationErrorSink
{
    public static NullApplicationErrorSink Instance { get; } = new();

    private NullApplicationErrorSink() { }

    public void Report(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
    }
}

/// <summary>Host-neutral confirmation boundary for operations requiring user consent.</summary>
public interface IApplicationPrompt
{
    ValueTask<bool> ConfirmAsync(string code, string title, string message,
        CancellationToken cancellationToken = default);
}

/// <summary>Non-interactive hosts deny prompts instead of opening a UI dialog.</summary>
public sealed class DenyApplicationPrompt : IApplicationPrompt
{
    public static DenyApplicationPrompt Instance { get; } = new();

    private DenyApplicationPrompt() { }

    public ValueTask<bool> ConfirmAsync(string code, string title, string message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

public sealed record MediaSeekCommand(TimeSpan Position) : IApplicationCommand;
public sealed record MediaPlayPauseCommand(bool ShouldBePlaying) : IApplicationCommand;
public sealed record MediaChangePathCommand(string Path) : IApplicationCommand;
public sealed record MediaChangeSpeedCommand(double Speed) : IApplicationCommand;

public sealed record SyncRequestCommand(IReadOnlyList<DeviceAxis>? Axes = null) : IApplicationCommand;
public sealed record ReloadScriptsCommand(IReadOnlyList<DeviceAxis>? Axes = null) : IApplicationCommand;

public sealed record MediaSpeedChangedEvent(double Speed) : IApplicationEvent;
public sealed record MediaPositionChangedEvent(TimeSpan Position, bool ForceSeek = false) : IApplicationEvent;
public sealed record MediaPlayingChangedEvent(bool IsPlaying) : IApplicationEvent;
public sealed record MediaPathChangedEvent(string Path, bool ReloadScripts = true, object? Context = null) : IApplicationEvent;
public sealed record MediaDurationChangedEvent(TimeSpan Duration) : IApplicationEvent;
public sealed record MediaResetEvent : IApplicationEvent;
public sealed record ChangeScriptEvent(IReadOnlyDictionary<DeviceAxis, IScriptResource> Scripts) : IApplicationEvent;

/// <summary>Portable description of a media clock/control integration.</summary>
public sealed record MediaSourceDescriptor(
    string Id,
    string Name,
    string Kind,
    string Endpoint,
    string Integration,
    string ImplementationStatus);

public sealed record MediaConnectionResult(bool Connected, string? Error = null);

/// <summary>
/// Adapter boundary for network or companion-process media integrations. The
/// host owns lifecycle and persistence; adapters only translate remote state.
/// </summary>
public interface IMediaSourceAdapter
{
    string Kind { get; }

    ValueTask<MediaConnectionResult> ConnectAsync(MediaSourceDescriptor source,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Portable output adapter boundary; timing remains engine-owned.</summary>
public interface IOutputTargetAdapter : IOutputSink, IAsyncDisposable
{
    string Protocol { get; }
    bool IsConnected { get; }
    string? LastError { get; }

    event Action<Exception>? Faulted;

    ValueTask<MediaConnectionResult> ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Versioned configuration persistence without a UI model dependency.</summary>
public interface IConfigurationStore
{
    ValueTask<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default);

    ValueTask WriteAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}

/// <summary>Secret persistence; implementations must protect values at rest.</summary>
public interface ISecretStore
{
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record StoredFileDescriptor(string RelativePath, long Length, DateTimeOffset LastModifiedUtc);

/// <summary>Administrator-rooted file access for scripts and diagnostics.</summary>
public interface IFileStore
{
    ValueTask<IReadOnlyList<StoredFileDescriptor>> ListAsync(string searchPattern = "*",
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);
}

public sealed record SerialDeviceDescriptor(string StableId, string DevicePath, string? LinkTarget);

/// <summary>OS-specific discovery only; opening and writing remain separate.</summary>
public interface ISerialDeviceDiscovery
{
    IReadOnlyList<SerialDeviceDescriptor> Enumerate(CancellationToken cancellationToken = default);
}
