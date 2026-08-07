namespace MultiFunPlayer.Application;

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
