namespace MultiFunPlayer.Infrastructure.Storage;

public sealed record ScriptDescriptor(string Name, string RelativePath, long Length, DateTimeOffset LastModifiedUtc);

/// <summary>Lists and reads script files below the configured scripts root.</summary>
public sealed class ScriptCatalog
{
    private readonly RootedFileStore _files;

    public ScriptCatalog(StorageOptions options)
        : this(new RootedFileStore(options.ScriptsRoot)) { }

    public ScriptCatalog(RootedFileStore files) => _files = files;

    public async Task<IReadOnlyList<ScriptDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        var files = await _files.ListAsync("*", cancellationToken).ConfigureAwait(false);
        return files.Where(IsScript)
            .Select(file => new ScriptDescriptor(Path.GetFileName(file.RelativePath), file.RelativePath,
                file.Length, file.LastModifiedUtc))
            .ToArray();
    }

    public Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (!IsScriptPath(relativePath))
            throw new ArgumentException("Only JSON/funscript files are supported.", nameof(relativePath));
        return _files.ReadTextAsync(relativePath, cancellationToken);
    }

    private static bool IsScript(StoredFile file) => IsScriptPath(file.RelativePath);

    private static bool IsScriptPath(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetExtension(path), ".funscript", StringComparison.OrdinalIgnoreCase);
}
