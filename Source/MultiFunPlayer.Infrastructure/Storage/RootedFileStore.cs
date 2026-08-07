using System.Text.Json;

namespace MultiFunPlayer.Infrastructure.Storage;

public sealed record StoredFile(string RelativePath, long Length, DateTimeOffset LastModifiedUtc);

/// <summary>JSON/file access constrained to one explicitly configured root.</summary>
public sealed class RootedFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;

    public RootedFileStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A storage root is required.", nameof(root));

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public Task<IReadOnlyList<StoredFile>> ListAsync(string searchPattern = "*", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = EnumerateFilesSafe(_root, searchPattern)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => new StoredFile(Relative(file.FullName), file.Length, file.LastWriteTimeUtc))
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredFile>>(files);
    }

    public async Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath, mustExist: true);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = Resolve(relativePath, mustExist: false);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath, mustExist: true);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteTextAsync(string relativePath, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var path = Resolve(relativePath, mustExist: false);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
    }

    public string Resolve(string relativePath, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
            throw new ArgumentException("The path must be a non-empty relative path.", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!IsWithinRoot(fullPath))
            throw new UnauthorizedAccessException("The path escapes the configured storage root.");

        RejectReparsePoints(fullPath);
        if (mustExist && !File.Exists(fullPath))
            throw new FileNotFoundException("The requested file does not exist.", relativePath);
        return fullPath;
    }

    private bool IsWithinRoot(string path)
        => string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    private void RejectReparsePoints(string path)
    {
        var current = path;
        while (IsWithinRoot(current) && !string.Equals(current, _root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                    throw new UnauthorizedAccessException("Symbolic links and reparse points are not allowed in storage paths.");
            }
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static bool IsReparsePoint(string path)
        => File.Exists(path) || Directory.Exists(path)
            ? File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            : false;

    private static IEnumerable<string> EnumerateFilesSafe(string root, string searchPattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
                if (!IsReparsePoint(file))
                    yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                if (!IsReparsePoint(child))
                    pending.Push(child);
        }
    }

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
}
