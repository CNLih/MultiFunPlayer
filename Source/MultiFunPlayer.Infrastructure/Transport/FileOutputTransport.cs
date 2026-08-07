namespace MultiFunPlayer.Infrastructure.Transport;

public sealed class FileOutputTransport : BoundedOutputTransport
{
    private readonly string _path;
    private FileStream? _stream;

    public FileOutputTransport(string id, string path, OutputTransportOptions? options = null)
        : base(id, options)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.CompletedTask;
    }

    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("File transport is not connected.");
        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
    }
}
